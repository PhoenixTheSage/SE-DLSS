using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ClientPlugin.Dlss;

internal static class NgxNative
{
    internal const int QualityMaxPerf = 0;
    internal const int QualityBalanced = 1;
    internal const int QualityMaxQuality = 2;
    internal const int QualityUltraPerformance = 3;
    internal const int QualityUltraQuality = 4;
    internal const int QualityDlaa = 5;

    [StructLayout(LayoutKind.Sequential)]
    internal struct InitArgs
    {
        public IntPtr Device;
        [MarshalAs(UnmanagedType.LPWStr)] public string DllSearchPath;
        [MarshalAs(UnmanagedType.LPWStr)] public string LogPath;
        [MarshalAs(UnmanagedType.LPWStr)] public string DebugLogPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EvalArgs
    {
        public IntPtr DeviceContext;
        public IntPtr Color;
        public IntPtr Depth;
        public IntPtr MotionVectors;
        public IntPtr Output;
        public IntPtr Exposure;
        public float JitterX;
        public float JitterY;
        public float MvScaleX;
        public float MvScaleY;
        public int Reset;
        public float Sharpness;
        public uint RenderWidth;
        public uint RenderHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MvArgs
    {
        public IntPtr Device;
        public IntPtr DeviceContext;
        public IntPtr Depth;
        public uint Width;
        public uint Height;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] InvViewProj;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] UnjitteredViewProj;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] PrevViewProj;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int InitDelegate(ref InitArgs args);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int IsSupportedDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int SetModeDelegate(int quality, uint outWidth, uint outHeight, out uint renderWidth, out uint renderHeight, out float sharpness, int preset);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int EvaluateDelegate(ref EvalArgs args);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr GenerateMvDelegate(ref MvArgs args);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int UpsampleDepthDelegate(IntPtr device, IntPtr context, IntPtr srcDepth, IntPtr destDepth);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ShutdownDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr LastErrorDelegate();

    internal static InitDelegate Init;
    internal static IsSupportedDelegate IsSupported;
    internal static SetModeDelegate SetMode;
    internal static EvaluateDelegate Evaluate;
    internal static GenerateMvDelegate GenerateCameraMotionVectors;
    internal static UpsampleDepthDelegate UpsampleDepth;
    internal static ShutdownDelegate Shutdown;
    internal static LastErrorDelegate LastErrorPtr;

    private static IntPtr module;

    internal static bool TryLoad(string directory, out string error)
    {
        error = null;
        if (module != IntPtr.Zero)
            return true;

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            error = "native search directory is missing";
            return false;
        }

        var path = Path.Combine(directory, "SeDlssNgx.dll");
        if (!File.Exists(path))
        {
            error = "SeDlssNgx.dll was not found in " + directory;
            return false;
        }

        module = LoadLibrary(path);
        if (module == IntPtr.Zero)
        {
            error = "LoadLibrary failed for SeDlssNgx.dll (Win32 " + Marshal.GetLastWin32Error() + ")";
            return false;
        }

        Init = Get<InitDelegate>("SeDlss_Init");
        IsSupported = Get<IsSupportedDelegate>("SeDlss_IsSupported");
        SetMode = Get<SetModeDelegate>("SeDlss_SetMode");
        Evaluate = Get<EvaluateDelegate>("SeDlss_Evaluate");
        GenerateCameraMotionVectors = Get<GenerateMvDelegate>("SeDlss_GenerateCameraMotionVectors");
        UpsampleDepth = Get<UpsampleDepthDelegate>("SeDlss_UpsampleDepth");
        Shutdown = Get<ShutdownDelegate>("SeDlss_Shutdown");
        LastErrorPtr = Get<LastErrorDelegate>("SeDlss_LastError");
        if (Init == null || IsSupported == null || SetMode == null || Evaluate == null || Shutdown == null)
        {
            error = "SeDlssNgx.dll is missing required exports";
            return false;
        }

        return true;
    }

    internal static string LastError()
    {
        if (LastErrorPtr == null)
            return "native library not loaded";
        var ptr = LastErrorPtr();
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
    }

    private static T Get<T>(string name) where T : class
    {
        var ptr = GetProcAddress(module, name);
        return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
}
