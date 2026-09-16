using ClientPlugin.RichHud;
using ClientPlugin.Settings;
using Sandbox.Engine.Platform.VideoMode;
using Sandbox.Graphics.GUI;
using VRageRender;

namespace ClientPlugin.Dlss;

internal static class GameAntiAliasing
{
    public const long DlssComboKey = AntiAliasingHandshake.GraphicsComboKey;

    private static MyGuiControlCombobox _graphicsCombo;
    private static MyGuiControlCombobox _pluginCombo;
    private static AntiAliasingChoice _graphicsOpenedWith;
    private static bool _graphicsCommitted;
    private static bool _suppress;
    private static bool _suppressPeer;
    private static bool _peerSynced;
    private static int _alignTries;

    public static void FillAndBindPluginCombo(MyGuiControlCombobox combo)
    {
        EnsurePluginItems(combo);
        BindPluginCombo(combo);
    }

    public static void BindPluginCombo(MyGuiControlCombobox combo)
    {
        if (ReferenceEquals(_pluginCombo, combo))
        {
            if (combo != null)
            {
                EnsurePluginItems(combo);
                SelectCombo(combo, ToPluginKey(DisplayedChoice(Config.Current.AntiAliasing)));
            }
            return;
        }

        if (_pluginCombo != null)
            _pluginCombo.ItemSelected -= OnPluginComboSelected;

        _pluginCombo = combo;
        if (combo == null)
            return;

        EnsurePluginItems(combo);
        SelectCombo(combo, ToPluginKey(DisplayedChoice(Config.Current.AntiAliasing)));
        combo.ItemSelected += OnPluginComboSelected;
    }

    public static void BindGraphicsCombo(MyGuiControlCombobox combo)
    {
        GpuSupport.TryProbe();
        if (!ReferenceEquals(_graphicsCombo, combo))
        {
            if (_graphicsCombo != null)
                _graphicsCombo.ItemSelected -= OnGraphicsComboSelected;

            _graphicsCombo = combo;
            _graphicsCommitted = false;
            if (combo != null)
            {
                _graphicsOpenedWith = DisplayedChoice(Config.Current.AntiAliasing);
                combo.ItemSelected += OnGraphicsComboSelected;
            }
        }

        AfterGraphicsWrite(combo);
    }

    public static void AfterGraphicsWrite(MyGuiControlCombobox combo)
    {
        EnsureOwnGraphicsItem(combo);
        if (combo == null)
            return;

        var fromGame = FromGraphicsKey(combo.GetSelectedKey());
        var displayed = DisplayedChoice(Config.Current.AntiAliasing);
        if (fromGame == AntiAliasingChoice.Off && IsUpscaler(displayed))
        {
            SelectCombo(combo, ToGraphicsKey(displayed));
            return;
        }

        SetChoice(fromGame, applyGame: false, save: false);
    }

    public static void OnGraphicsScreenClosed()
    {
        if (_graphicsCombo == null)
            return;
        if (!_graphicsCommitted)
            SetChoice(_graphicsOpenedWith, applyGame: false, save: false);

        _graphicsCombo.ItemSelected -= OnGraphicsComboSelected;
        _graphicsCombo = null;
        _graphicsCommitted = false;
    }

    public static void OnGraphicsOk()
    {
        _graphicsCommitted = true;
        if (_graphicsCombo == null)
            return;
        SetChoice(FromGraphicsKey(_graphicsCombo.GetSelectedKey()), applyGame: true, save: true);
    }

    public static void RemapDlssKey(MyGuiControlCombobox combo, ref MyGraphicsSettings graphicsSettings)
    {
        if (combo == null || combo.GetSelectedKey() != DlssComboKey)
            return;
        var perf = graphicsSettings.PerformanceSettings;
        var rs = perf.RenderSettings;
        rs.AntialiasingMode = MyAntialiasingMode.NONE;
        perf.RenderSettings = rs;
        graphicsSettings.PerformanceSettings = perf;
    }

    public static void AlignConfigWithGame()
    {
        if (_graphicsCombo != null)
            return;
        var resolved = ResolveExclusive();
        if (DisplayedChoice(Config.Current.AntiAliasing) == resolved)
            return;
        SetChoice(resolved, applyGame: false, save: true);
    }

    public static void CoerceUnsupported()
    {
        GpuSupport.TryProbe();
        if (Config.Current.AntiAliasing != AntiAliasingChoice.DLSS || GpuSupport.CanOfferDlss)
            return;
        SetChoice(AntiAliasingChoice.Off, applyGame: true, save: true);
    }

    public static void AlignWithPeer()
    {
        if (_peerSynced)
            return;
        GpuSupport.TryProbe();
        var resolved = ResolveExclusive();
        var displayed = DisplayedChoice(Config.Current.AntiAliasing);
        if (displayed != resolved)
            SetChoice(resolved, applyGame: true, save: true);
        else if (Config.Current.AntiAliasing != resolved)
            SetChoice(resolved, applyGame: false, save: true);
        else if (!_suppressPeer)
            SyncPeer(resolved);

        if (PeerAntiAliasing.Present || ++_alignTries >= 2)
            _peerSynced = true;
    }

    public static void ApplyFromConfig()
    {
        var gs = MyVideoSettingsManager.CurrentGraphicsSettings;
        var perf = gs.PerformanceSettings;
        var rs = perf.RenderSettings;
        var wanted = Config.Current.AntiAliasing == AntiAliasingChoice.FXAA
            ? MyAntialiasingMode.FXAA
            : MyAntialiasingMode.NONE;
        if (rs.AntialiasingMode == wanted)
            return;

        rs.AntialiasingMode = wanted;
        perf.RenderSettings = rs;
        gs.PerformanceSettings = perf;
        MyVideoSettingsManager.Apply(gs);
    }

    public static void ApplyFromUi(AntiAliasingChoice choice)
    {
        SetChoice(choice, applyGame: true, save: true);
    }

    public static void ApplyFromPeer(string choiceName)
    {
        var previous = _suppressPeer;
        _suppressPeer = true;
        try
        {
            SetChoice(FromName(choiceName), applyGame: true, save: true);
        }
        finally
        {
            _suppressPeer = previous;
        }
    }

    public static void Reset()
    {
        if (_pluginCombo != null)
            _pluginCombo.ItemSelected -= OnPluginComboSelected;
        if (_graphicsCombo != null)
            _graphicsCombo.ItemSelected -= OnGraphicsComboSelected;

        _pluginCombo = null;
        _graphicsCombo = null;
        _graphicsOpenedWith = default(AntiAliasingChoice);
        _graphicsCommitted = false;
        _suppress = false;
        _suppressPeer = false;
        _peerSynced = false;
        _alignTries = 0;
    }

    public static AntiAliasingChoice DisplayedChoice(AntiAliasingChoice choice)
    {
        if (choice == AntiAliasingChoice.DLSS && !GpuSupport.CanOfferDlss)
            return AntiAliasingChoice.Off;
        if (choice == AntiAliasingChoice.FRS && !(PeerAntiAliasing.Present && PeerAntiAliasing.CanOffer))
            return PeerAntiAliasing.Present ? AntiAliasingChoice.Off : choice;
        return choice;
    }

    public static string ChoiceName(AntiAliasingChoice choice)
    {
        switch (choice)
        {
            case AntiAliasingChoice.FXAA: return AntiAliasingHandshake.ChoiceFxaa;
            case AntiAliasingChoice.DLSS: return AntiAliasingHandshake.ChoiceDlss;
            case AntiAliasingChoice.FRS: return AntiAliasingHandshake.ChoiceFrs;
            default: return AntiAliasingHandshake.ChoiceOff;
        }
    }

    private static void OnPluginComboSelected()
    {
        if (_suppress || _pluginCombo == null)
            return;
        SetChoice(FromPluginKey(_pluginCombo.GetSelectedKey()), applyGame: true, save: false);
    }

    private static void OnGraphicsComboSelected()
    {
        if (_suppress || _graphicsCombo == null)
            return;
        SetChoice(FromGraphicsKey(_graphicsCombo.GetSelectedKey()), applyGame: false, save: false);
    }

    private static void SetChoice(AntiAliasingChoice choice, bool applyGame, bool save)
    {
        choice = Sanitize(choice);
        if (Config.Current.AntiAliasing != choice)
        {
            var previousSuppress = Config.SuppressApply;
            Config.SuppressApply = Config.SuppressApply || !applyGame;
            try
            {
                Config.Current.AntiAliasing = choice;
            }
            finally
            {
                Config.SuppressApply = previousSuppress;
            }
        }
        else if (applyGame)
            ApplyFromConfig();

        var displayed = DisplayedChoice(Config.Current.AntiAliasing);
        SelectCombo(_pluginCombo, ToPluginKey(displayed));
        SelectCombo(_graphicsCombo, ToGraphicsKey(displayed));
        AnomalyTerminalHook.TryRefresh();
        DebugLog.Write("SetChoice " + choice + " apply=" + applyGame + " save=" + save);
        if (save)
            ConfigStorage.Save(Config.Current);
        if (!_suppressPeer)
            SyncPeer(displayed);
    }

    private static AntiAliasingChoice Sanitize(AntiAliasingChoice choice)
    {
        if (choice == AntiAliasingChoice.DLSS && !GpuSupport.CanOfferDlss)
            return AntiAliasingChoice.Off;
        if (choice == AntiAliasingChoice.FRS && PeerAntiAliasing.Present && !PeerAntiAliasing.CanOffer)
            return AntiAliasingChoice.Off;
        return choice;
    }

    private static AntiAliasingChoice ResolveExclusive()
    {
        var gameFxaa = MyVideoSettingsManager.CurrentGraphicsSettings.PerformanceSettings.RenderSettings.AntialiasingMode
                       == MyAntialiasingMode.FXAA;
        var own = DisplayedChoice(Config.Current.AntiAliasing);
        var peer = FromName(PeerAntiAliasing.GetChoice());
        var dlssWanted = own == AntiAliasingChoice.DLSS || peer == AntiAliasingChoice.DLSS;
        if (dlssWanted && !GpuSupport.CanOfferDlss)
            dlssWanted = false;
        var frsWanted = own == AntiAliasingChoice.FRS || peer == AntiAliasingChoice.FRS;
        if (frsWanted && !(PeerAntiAliasing.Present && PeerAntiAliasing.CanOffer))
            frsWanted = false;

        if (dlssWanted && frsWanted)
            return PeerAntiAliasing.CanOffer ? AntiAliasingChoice.FRS : AntiAliasingChoice.DLSS;
        if (dlssWanted)
            return AntiAliasingChoice.DLSS;
        if (frsWanted)
            return AntiAliasingChoice.FRS;
        if (gameFxaa || own == AntiAliasingChoice.FXAA || peer == AntiAliasingChoice.FXAA)
            return AntiAliasingChoice.FXAA;
        return AntiAliasingChoice.Off;
    }

    private static void SyncPeer(AntiAliasingChoice choice)
    {
        if (!PeerAntiAliasing.Present)
            return;
        var name = ChoiceName(choice);
        if (PeerAntiAliasing.GetChoice() == name)
            return;
        PeerAntiAliasing.ApplyChoice(name);
    }

    private static bool IsUpscaler(AntiAliasingChoice choice)
    {
        return choice == AntiAliasingChoice.DLSS || choice == AntiAliasingChoice.FRS;
    }

    private static void EnsurePluginItems(MyGuiControlCombobox combo)
    {
        if (combo == null)
            return;
        AddItem(combo, (long)AntiAliasingChoice.Off, "Off");
        AddItem(combo, (long)AntiAliasingChoice.FXAA, "FXAA");
        if (GpuSupport.CanOfferDlss)
            AddItem(combo, (long)AntiAliasingChoice.DLSS, "DLSS");
        if (PeerAntiAliasing.Present && PeerAntiAliasing.CanOffer)
            AddItem(combo, (long)AntiAliasingChoice.FRS, "FRS");
    }

    private static void EnsureOwnGraphicsItem(MyGuiControlCombobox combo)
    {
        if (combo == null || !GpuSupport.CanOfferDlss)
            return;
        AddItem(combo, DlssComboKey, "DLSS");
    }

    private static void AddItem(MyGuiControlCombobox combo, long key, string name)
    {
        if (combo.TryGetItemByKey(key) == null)
            combo.AddItem(key, name);
    }

    private static void SelectCombo(MyGuiControlCombobox combo, long key)
    {
        if (combo == null)
            return;
        if (combo.GetSelectedKey() == key)
            return;
        _suppress = true;
        try
        {
            combo.SelectItemByKey(key, sendEvent: false);
        }
        finally
        {
            _suppress = false;
        }
    }

    private static long ToGraphicsKey(AntiAliasingChoice choice)
    {
        switch (choice)
        {
            case AntiAliasingChoice.DLSS: return DlssComboKey;
            case AntiAliasingChoice.FRS: return PeerAntiAliasing.GraphicsComboKey;
            case AntiAliasingChoice.FXAA: return (long)MyAntialiasingMode.FXAA;
            default: return (long)MyAntialiasingMode.NONE;
        }
    }

    private static long ToPluginKey(AntiAliasingChoice choice)
    {
        return (long)choice;
    }

    private static AntiAliasingChoice FromGraphicsKey(long key)
    {
        if (key == DlssComboKey)
            return AntiAliasingChoice.DLSS;
        if (key == PeerAntiAliasing.GraphicsComboKey)
            return AntiAliasingChoice.FRS;
        if (key == (long)MyAntialiasingMode.FXAA)
            return AntiAliasingChoice.FXAA;
        return AntiAliasingChoice.Off;
    }

    private static AntiAliasingChoice FromPluginKey(long key)
    {
        if (key == (long)AntiAliasingChoice.DLSS)
            return AntiAliasingChoice.DLSS;
        if (key == (long)AntiAliasingChoice.FRS)
            return AntiAliasingChoice.FRS;
        if (key == (long)AntiAliasingChoice.FXAA)
            return AntiAliasingChoice.FXAA;
        return AntiAliasingChoice.Off;
    }

    private static AntiAliasingChoice FromName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return AntiAliasingChoice.Off;
        if (name == AntiAliasingHandshake.ChoiceDlss)
            return AntiAliasingChoice.DLSS;
        if (name == AntiAliasingHandshake.ChoiceFrs)
            return AntiAliasingChoice.FRS;
        if (name == AntiAliasingHandshake.ChoiceFxaa)
            return AntiAliasingChoice.FXAA;
        return AntiAliasingChoice.Off;
    }
}
