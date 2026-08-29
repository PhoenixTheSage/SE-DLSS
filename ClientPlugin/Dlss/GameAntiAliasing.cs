using ClientPlugin.Settings;
using Sandbox.Engine.Platform.VideoMode;
using Sandbox.Graphics.GUI;
using VRageRender;

namespace ClientPlugin.Dlss;

internal static class GameAntiAliasing
{
    public const long DlssComboKey = 100;

    private static MyGuiControlCombobox graphicsCombo;
    private static MyGuiControlCombobox pluginCombo;
    private static AntiAliasingChoice graphicsOpenedWith;
    private static bool graphicsCommitted;
    private static bool suppress;

    public static void BindPluginCombo(MyGuiControlCombobox combo)
    {
        pluginCombo = combo;
        if (combo == null)
            return;
        SelectCombo(combo, ToPluginKey(Config.Current.AntiAliasing));
        combo.ItemSelected += OnPluginComboSelected;
    }

    public static void BindGraphicsCombo(MyGuiControlCombobox combo)
    {
        if (combo != null && graphicsCombo != combo)
        {
            graphicsCombo = combo;
            graphicsCommitted = false;
            graphicsOpenedWith = Config.Current.AntiAliasing;
            combo.ItemSelected += OnGraphicsComboSelected;
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
            key == (long)MyAntialiasingMode.NONE)
        {
            SelectCombo(combo, DlssComboKey);
            return;
        }

        SetChoice(FromGraphicsKey(key), applyGame: false, save: false);
    }

    public static void OnGraphicsScreenClosed()
    {
        if (graphicsCombo == null)
            return;
        if (!graphicsCommitted)
            SetChoice(graphicsOpenedWith, applyGame: false, save: false);
        graphicsCombo = null;
        graphicsCommitted = false;
    }

    public static void OnGraphicsOk()
    {
        graphicsCommitted = true;
        if (graphicsCombo != null)
            SetChoice(FromGraphicsKey(graphicsCombo.GetSelectedKey()), applyGame: true, save: true);
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
        if (graphicsCombo != null)
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

    private static void OnPluginComboSelected()
    {
        if (suppress || pluginCombo == null)
            return;
        SetChoice(FromPluginKey(pluginCombo.GetSelectedKey()), applyGame: true, save: false);
    }

    private static void OnGraphicsComboSelected()
    {
        if (suppress || graphicsCombo == null)
            return;
        SetChoice(FromGraphicsKey(graphicsCombo.GetSelectedKey()), applyGame: false, save: false);
    }

    private static void SetChoice(AntiAliasingChoice choice, bool applyGame, bool save)
    {
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

        DlssRuntime.NotifyConfigChanged();
        SelectCombo(pluginCombo, ToPluginKey(Config.Current.AntiAliasing));
        SelectCombo(graphicsCombo, ToGraphicsKey(Config.Current.AntiAliasing));
        if (save)
            ConfigStorage.Save(Config.Current);
    }

    private static AntiAliasingChoice ResolveFromGame()
    {
        var mode = MyVideoSettingsManager.CurrentGraphicsSettings.PerformanceSettings.RenderSettings.AntialiasingMode;
        if (mode == MyAntialiasingMode.FXAA)
            return AntiAliasingChoice.FXAA;
        if (Config.Current.AntiAliasing == AntiAliasingChoice.DLSS)
            return AntiAliasingChoice.DLSS;
        return AntiAliasingChoice.Off;
    }

    private static void EnsureDlssItem(MyGuiControlCombobox combo)
    {
        if (combo == null)
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
        suppress = true;
        try
        {
            combo.SelectItemByKey(key, sendEvent: false);
        }
        finally
        {
            suppress = false;
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
        if (key == DlssComboKey)
            return AntiAliasingChoice.DLSS;
        if (key == (long)MyAntialiasingMode.FXAA)
            return AntiAliasingChoice.FXAA;
        return AntiAliasingChoice.Off;
    }

    private static AntiAliasingChoice FromPluginKey(long key)
    {
        if (key == (long)AntiAliasingChoice.DLSS)
            return AntiAliasingChoice.DLSS;
        if (key == (long)AntiAliasingChoice.FXAA)
            return AntiAliasingChoice.FXAA;
        return AntiAliasingChoice.Off;
    }
}
