using ClientPlugin.Settings;
using Sandbox.Engine.Platform.VideoMode;
using Sandbox.Graphics.GUI;
using VRageRender;

namespace ClientPlugin.Dlss;

internal static class GameAntiAliasing
{
    public const long DlssComboKey = 100;

    private static MyGuiControlCombobox _graphicsCombo;
    private static MyGuiControlCombobox _pluginCombo;
    private static AntiAliasingChoice _graphicsOpenedWith;
    private static bool _graphicsCommitted;
    private static bool _suppress;

    public static void BindPluginCombo(MyGuiControlCombobox combo)
    {
        if (ReferenceEquals(_pluginCombo, combo))
        {
            if (combo != null)
                SelectCombo(combo, ToPluginKey(DisplayedChoice(Config.Current.AntiAliasing)));
            return;
        }

        if (_pluginCombo != null)
            _pluginCombo.ItemSelected -= OnPluginComboSelected;

        _pluginCombo = combo;
        if (combo == null)
            return;

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
                _graphicsOpenedWith = Config.Current.AntiAliasing;
                combo.ItemSelected += OnGraphicsComboSelected;
            }
        }

        AfterGraphicsWrite(combo);
    }

    public static void AfterGraphicsWrite(MyGuiControlCombobox combo)
    {
        EnsureDlssItem(combo);
        if (combo == null)
            return;

        var key = combo.GetSelectedKey();
        if (Config.Current.AntiAliasing == AntiAliasingChoice.DLSS &&
            !GpuSupport.CanOfferDlss)
        {
            SelectCombo(combo, (long)MyAntialiasingMode.NONE);
            return;
        }
        if (Config.Current.AntiAliasing == AntiAliasingChoice.DLSS &&
            key == (long)MyAntialiasingMode.NONE)
        {
            SelectCombo(combo, DlssComboKey);
            return;
        }

        SetChoice(FromGraphicsKey(key), applyGame: false, save: false);
    }

    public static void OnGraphicsScreenClosed()
    {
        if (_graphicsCombo == null)
            return;
        if (!_graphicsCommitted &&
            !(_graphicsOpenedWith == AntiAliasingChoice.DLSS && !GpuSupport.CanOfferDlss))
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
        var key = _graphicsCombo.GetSelectedKey();
        var choice = FromGraphicsKey(key);
        if (choice != AntiAliasingChoice.DLSS &&
            _graphicsOpenedWith == AntiAliasingChoice.DLSS &&
            !GpuSupport.CanOfferDlss &&
            key == (long)MyAntialiasingMode.NONE)
            return;
        SetChoice(choice, applyGame: true, save: true);
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
        var resolved = ResolveFromGame();
        if (Config.Current.AntiAliasing == resolved)
            return;
        Config.SuppressApply = true;
        try
        {
            Config.Current.AntiAliasing = resolved;
        }
        finally
        {
            Config.SuppressApply = false;
        }
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
        if (choice == AntiAliasingChoice.DLSS && !GpuSupport.CanOfferDlss)
            choice = AntiAliasingChoice.Off;
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

        SelectCombo(_pluginCombo, ToPluginKey(DisplayedChoice(Config.Current.AntiAliasing)));
        SelectCombo(_graphicsCombo, ToGraphicsKey(DisplayedChoice(Config.Current.AntiAliasing)));
        DebugLog.Write("SetChoice " + choice + " apply=" + applyGame + " save=" + save);
        if (save)
            ConfigStorage.Save(Config.Current);
    }

    private static AntiAliasingChoice ResolveFromGame()
    {
        var mode = MyVideoSettingsManager.CurrentGraphicsSettings.PerformanceSettings.RenderSettings.AntialiasingMode;
        return mode == MyAntialiasingMode.FXAA
            ? AntiAliasingChoice.FXAA
            : Config.Current.AntiAliasing == AntiAliasingChoice.DLSS
                ? AntiAliasingChoice.DLSS
                : AntiAliasingChoice.Off;
    }

    private static AntiAliasingChoice DisplayedChoice(AntiAliasingChoice choice)
    {
        if (choice == AntiAliasingChoice.DLSS && !GpuSupport.CanOfferDlss)
            return AntiAliasingChoice.Off;
        return choice;
    }

    private static void EnsureDlssItem(MyGuiControlCombobox combo)
    {
        if (combo == null || !GpuSupport.CanOfferDlss)
            return;
        if (combo.TryGetItemByKey(DlssComboKey) == null)
            combo.AddItem(DlssComboKey, "DLSS");
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
        return key switch
        {
            DlssComboKey => AntiAliasingChoice.DLSS,
            (long)MyAntialiasingMode.FXAA => AntiAliasingChoice.FXAA,
            _ => AntiAliasingChoice.Off,
        };
    }

    private static AntiAliasingChoice FromPluginKey(long key)
    {
        return key switch
        {
            (long)AntiAliasingChoice.DLSS => AntiAliasingChoice.DLSS,
            (long)AntiAliasingChoice.FXAA => AntiAliasingChoice.FXAA,
            _ => AntiAliasingChoice.Off,
        };
    }
}
