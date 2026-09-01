using System;
using System.Collections.Generic;
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
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
#endif

namespace ClientPlugin;

// ReSharper disable once UnusedType.Global
public sealed class Plugin : IPlugin
{
    public const string Name = "SpaceEngineersDLSS";
    public static Plugin Instance { get; private set; }

    private SettingsGenerator settingsGenerator;
    private Harmony harmony;
    private Harmony deviceHarmony;
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
        deviceHarmony = new Harmony(DeviceDisposePatch.HarmonyId);
        DeviceDisposePatch.Apply(deviceHarmony);
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
        // Leave deviceHarmony applied so NGX can shut down when the D3D device
        // is disposed after this plugin. Process exit reclaims the patch.
        DlssRuntime.Shutdown();
        GpuSupport.Reset();
        settingsGenerator = null;
        if (ReferenceEquals(Instance, this))
            Instance = null;
        DebugLog.Close();
    }

    public void Update()
    {
        if (disposed)
            return;
        // Pulsar finishes every plugin Init before the first Update. NGX D3D11
        // init must not overlap Anomaly (or other plugins) Harmony.PatchAll.
        DlssRuntime.NotifyPluginsReady();
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
    public void LoadAssets(IReadOnlyDictionary<string, string> assets)
    {
        if (disposed || assets == null)
            return;

        foreach (var pair in assets)
            AddAssetSearchPath(pair.Value, pair.Key);
    }

    // Older Pulsar still calls this when an asset is named AssetFolder.
    // ReSharper disable once UnusedMember.Global
    public void LoadAssets(string folder)
    {
        AddAssetSearchPath(folder, null);
    }

    private void AddAssetSearchPath(string path, string name)
    {
        if (disposed || string.IsNullOrEmpty(path))
            return;

        if (File.Exists(path))
            path = Path.GetDirectoryName(path);

        NgxHost.AddSearchPath(path);
        var label = string.IsNullOrEmpty(name) ? path : name + "=" + path;
        MyLog.Default.WriteLine("DLSS asset: " + label);
        DebugLog.Write("LoadAssets " + label);
    }
}
