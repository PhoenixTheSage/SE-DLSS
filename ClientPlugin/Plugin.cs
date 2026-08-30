using System.IO;
using System.Reflection;
using ClientPlugin.Dlss;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Layouts;
using HarmonyLib;
using Sandbox.Graphics.GUI;
using VRage.Plugins;
using VRage.Utils;

#if !LOCAL_BUILD
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
#endif

namespace ClientPlugin;

// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    public const string Name = "SpaceEngineersDLSS";
    public static Plugin Instance { get; private set; }
    private SettingsGenerator settingsGenerator;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
        Instance = this;
        Instance.settingsGenerator = new SettingsGenerator();
        DebugLog.Open();
        NgxHost.AddSearchPath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
        DebugLog.Write("Init search=" + NgxHost.SearchPathSummary());

        var harmony = new Harmony(Name);
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        MyLog.Default.WriteLine("DLSS plugin initialized");
        DebugLog.Write("Harmony patched, plugin initialized");
    }

    public void Dispose()
    {
        DebugLog.Write("Dispose");
        DlssRuntime.Shutdown();
        DebugLog.Close();
        Instance = null;
    }

    public void Update()
    {
    }

    // ReSharper disable once UnusedMember.Global
    public void OpenConfigDialog()
    {
        GameAntiAliasing.AlignConfigWithGame();
        Instance.settingsGenerator.SetLayout<Simple>();
        Instance.settingsGenerator.Dialog.RecreateControls(true);
        MyGuiSandbox.AddScreen(Instance.settingsGenerator.Dialog);
    }

    public void LoadAssets(string folder)
    {
        NgxHost.AddSearchPath(folder);
        MyLog.Default.WriteLine("DLSS asset folder: " + folder);
        DebugLog.Write("LoadAssets " + folder);
    }
}
