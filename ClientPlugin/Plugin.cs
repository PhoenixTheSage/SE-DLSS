using System;
using System.IO;
using System.Reflection;
using ClientPlugin.Dlss;
using ClientPlugin.Patches;
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
public sealed class Plugin : IPlugin
{
    public const string Name = "SpaceEngineersDLSS";
    public static Plugin Instance { get; private set; }

    private SettingsGenerator settingsGenerator;
    private Harmony harmony;
    private bool disposed;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
        disposed = false;
        Instance = this;
        settingsGenerator = new SettingsGenerator();
        DebugLog.Open();
        NgxHost.AddSearchPath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
        DebugLog.Write("Init search=" + NgxHost.SearchPathSummary());

        harmony = new Harmony(Name);
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        GpuSupport.TryProbe();
        MyLog.Default.WriteLine("DLSS plugin initialized. GPU: " + GpuSupport.StatusLine);
        DebugLog.Write("Harmony patched, plugin initialized GPU=" + GpuSupport.StatusLine);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        DebugLog.Write("Dispose");
        GameAntiAliasing.Reset();
        BillboardOutputPass.Reset();
        try
        {
            harmony?.UnpatchAll(Name);
        }
        catch (Exception e)
        {
            MyLog.Default.Error("DLSS failed to remove Harmony patches: " + e);
        }
        harmony = null;
        DlssRuntime.Shutdown();
        GpuSupport.Reset();
        settingsGenerator = null;
        if (ReferenceEquals(Instance, this))
            Instance = null;
        DebugLog.Close();
    }

    public void Update()
    {
    }

    // ReSharper disable once UnusedMember.Global
    public void OpenConfigDialog()
    {
        var generator = settingsGenerator;
        if (disposed || generator == null)
            return;

        GpuSupport.TryProbe();
        GameAntiAliasing.AlignConfigWithGame();
        generator.SetLayout<Simple>();
        generator.Dialog.RecreateControls(true);
        MyGuiSandbox.AddScreen(generator.Dialog);
    }

    // ReSharper disable once UnusedMember.Global
    public void LoadAssets(string folder)
    {
        if (disposed)
            return;

        NgxHost.AddSearchPath(folder);
        MyLog.Default.WriteLine("DLSS asset folder: " + folder);
        DebugLog.Write("LoadAssets " + folder);
    }
}
