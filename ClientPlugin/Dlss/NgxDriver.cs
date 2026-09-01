using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClientPlugin.Dlss;

/// <summary>
/// Loads the NVIDIA driver <c>_nvngx.dll</c> and binds D3D11 NGX C exports.
/// </summary>
internal sealed class NgxDriver : IDisposable
{
    internal const int VersionApi = 0x0000015;
    internal const int EngineTypeCustom = 0;
    internal const int FeatureSuperSampling = 1;
    internal const string ProjectId = "8e4c2a71-6b9d-4f13-9c1a-7f2e5b90d4c3";
    internal const string EngineVersion = "1.0.0";

    private const uint LoadWithAlteredSearchPath = 0x00000008;

    // Init_ProjectID / Init_Ext / Init are private driver-core exports. They
    // are invoked through NgxOleInvoker so the core sees a native caller
    // module. Init_with_ProjectID is the public SDK ABI if a native SDK shim
    // happens to export it.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int InitProjectSdkFn(
        IntPtr projectId, int engineType, IntPtr engineVersion, IntPtr logPath,
        IntPtr device, IntPtr featureInfo, int version);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int Shutdown1Delegate(IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetCapsDelegate(out IntPtr parameters);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int AllocParamsDelegate(out IntPtr parameters);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int DestroyParamsDelegate(IntPtr parameters);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int CreateFeatureDelegate(IntPtr context, int feature, IntPtr parameters, out IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ReleaseFeatureDelegate(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int EvaluateDelegate(IntPtr context, IntPtr handle, IntPtr parameters, IntPtr callback);

    private IntPtr _module;
    private IntPtr _initProject;
    private IntPtr _initProjectSdk;
    private IntPtr _initExt;
    private IntPtr _initApp;

    internal string LoadedFrom { get; private set; }
    internal IntPtr InitProjectPtr => _initProject;
    internal IntPtr InitProjectSdkPtr => _initProjectSdk;
    internal IntPtr InitExtPtr => _initExt;
    internal IntPtr InitAppPtr => _initApp;
    internal bool HasInitProject => _initProject != IntPtr.Zero;
    internal bool HasInitProjectSdk => InitProjectSdk != null;
    internal bool HasInitExt => _initExt != IntPtr.Zero;
    internal bool HasInitApp => _initApp != IntPtr.Zero;
    internal InitProjectSdkFn InitProjectSdk { get; private set; }
    internal Shutdown1Delegate Shutdown1 { get; private set; }
    internal GetCapsDelegate GetCapabilityParameters { get; private set; }
    internal AllocParamsDelegate AllocateParameters { get; private set; }
    internal DestroyParamsDelegate DestroyParameters { get; private set; }
    internal CreateFeatureDelegate CreateFeature { get; private set; }
    internal ReleaseFeatureDelegate ReleaseFeature { get; private set; }
    internal EvaluateDelegate EvaluateFeature { get; private set; }

    internal bool TryLoad(out string error)
    {
        error = null;
        if (_module != IntPtr.Zero)
            return true;

        var lastError = 0;
        string loadedFrom = null;
        foreach (var path in EnumerateCandidates())
        {
            var module = TryLoadModule(path);
            if (module != IntPtr.Zero)
            {
                _module = module;
                loadedFrom = path;
                break;
            }

            var err = Marshal.GetLastWin32Error();
            if (err != 0)
                lastError = err;
        }

        if (_module == IntPtr.Zero)
        {
            var ngxCore = ReadNgxCoreDir();
            error = "failed to load _nvngx.dll (Win32 " + lastError + "). NGXCore=" +
                    (string.IsNullOrEmpty(ngxCore) ? "(registry missing)" : ngxCore);
            return false;
        }

        LoadedFrom = loadedFrom;
        _initProject = GetProcAddress(_module, "NVSDK_NGX_D3D11_Init_ProjectID");
        _initProjectSdk = GetProcAddress(_module, "NVSDK_NGX_D3D11_Init_with_ProjectID");
        _initExt = GetProcAddress(_module, "NVSDK_NGX_D3D11_Init_Ext");
        _initApp = GetProcAddress(_module, "NVSDK_NGX_D3D11_Init");
        if (_initProjectSdk != IntPtr.Zero)
            InitProjectSdk = Marshal.GetDelegateForFunctionPointer<InitProjectSdkFn>(_initProjectSdk);
        Shutdown1 = LoadFn<Shutdown1Delegate>("NVSDK_NGX_D3D11_Shutdown1");
        GetCapabilityParameters = LoadFn<GetCapsDelegate>("NVSDK_NGX_D3D11_GetCapabilityParameters");
        AllocateParameters = LoadFn<AllocParamsDelegate>("NVSDK_NGX_D3D11_AllocateParameters");
        DestroyParameters = LoadFn<DestroyParamsDelegate>("NVSDK_NGX_D3D11_DestroyParameters");
        CreateFeature = LoadFn<CreateFeatureDelegate>("NVSDK_NGX_D3D11_CreateFeature");
        ReleaseFeature = LoadFn<ReleaseFeatureDelegate>("NVSDK_NGX_D3D11_ReleaseFeature");
        EvaluateFeature = LoadFn<EvaluateDelegate>("NVSDK_NGX_D3D11_EvaluateFeature")
                          ?? LoadFn<EvaluateDelegate>("NVSDK_NGX_D3D11_EvaluateFeature_C");

        var hasInit = HasInitProject || HasInitProjectSdk || HasInitExt || HasInitApp;
        if (!hasInit || Shutdown1 == null || GetCapabilityParameters == null ||
            CreateFeature == null || ReleaseFeature == null || EvaluateFeature == null)
        {
            error = "NGX driver exports are missing (init=" +
                    (HasInitProject ? 1 : 0) + "/" +
                    (HasInitProjectSdk ? 1 : 0) + "/" +
                    (HasInitExt ? 1 : 0) + "/" +
                    (HasInitApp ? 1 : 0) + " shut=" + (Shutdown1 != null ? 1 : 0) +
                    " caps=" + (GetCapabilityParameters != null ? 1 : 0) +
                    " create=" + (CreateFeature != null ? 1 : 0) +
                    " release=" + (ReleaseFeature != null ? 1 : 0) +
                    " eval=" + (EvaluateFeature != null ? 1 : 0) + ") from " + loadedFrom;
            Dispose();
            return false;
        }

        return true;
    }

    internal static IntPtr TryPreloadDlss(string directory)
    {
        if (string.IsNullOrEmpty(directory))
            return IntPtr.Zero;
        var path = Path.Combine(directory, "nvngx_dlss.dll");
        if (!File.Exists(path))
            return IntPtr.Zero;
        return TryLoadModule(path);
    }

    internal static string ReadNgxCoreDir()
    {
#if NET
        if (!OperatingSystem.IsWindows())
            return null;
#endif
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(@"SOFTWARE\NVIDIA Corporation\Global\NGXCore");
            var value = key?.GetValue("FullPath") as string;
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _initProject = IntPtr.Zero;
        _initProjectSdk = IntPtr.Zero;
        _initExt = IntPtr.Zero;
        _initApp = IntPtr.Zero;
        InitProjectSdk = null;
        Shutdown1 = null;
        GetCapabilityParameters = null;
        AllocateParameters = null;
        DestroyParameters = null;
        CreateFeature = null;
        ReleaseFeature = null;
        EvaluateFeature = null;
        LoadedFrom = null;
        if (_module == IntPtr.Zero)
            return;
        FreeLibrary(_module);
        _module = IntPtr.Zero;
    }

    private T LoadFn<T>(string name) where T : class
    {
        var ptr = GetProcAddress(_module, name);
        return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrEmpty(sys))
        {
            yield return Path.Combine(sys, "_nvngx.dll");
            yield return Path.Combine(sys, "nvngx.dll");
        }

        var ngxCore = ReadNgxCoreDir();
        if (!string.IsNullOrEmpty(ngxCore))
        {
            yield return Path.Combine(ngxCore, "_nvngx.dll");
            yield return Path.Combine(ngxCore, "nvngx.dll");
        }

        if (!string.IsNullOrEmpty(sys))
        {
            var repo = Path.Combine(sys, "DriverStore", "FileRepository");
            if (Directory.Exists(repo))
            {
                string[] dirs;
                try
                {
                    dirs = Directory.GetDirectories(repo, "nv*");
                }
                catch
                {
                    dirs = [];
                }

                foreach (var dir in dirs)
                    yield return Path.Combine(dir, "_nvngx.dll");
            }
        }

        yield return "_nvngx.dll";
        yield return "nvngx.dll";
    }

    private static IntPtr TryLoadModule(string path)
    {
        if (string.IsNullOrEmpty(path))
            return IntPtr.Zero;
        try
        {
            if (path != "_nvngx.dll" && path != "nvngx.dll" && !File.Exists(path))
                return IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }

        var module = LoadLibraryEx(path, IntPtr.Zero, LoadWithAlteredSearchPath);
        return module != IntPtr.Zero ? module : LoadLibrary(path);
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);
}
