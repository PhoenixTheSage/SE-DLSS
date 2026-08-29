using System;
using System.Collections.Generic;
using System.IO;
using VRage.Utils;

namespace ClientPlugin.Dlss;

public static class NgxHost
{
    public static bool IsLoaded { get; private set; }
    public static bool IsSupported { get; private set; }
    public static bool IsReady { get; private set; }
    public static string LastError { get; internal set; } = "not initialized";

    private static readonly List<string> SearchPaths = new List<string>();
    private static bool nativeLoaded;
    private static uint lastOutW;
    private static uint lastOutH;
    private static int lastQuality = int.MinValue;
    private static int lastPreset = int.MinValue;

    public static void AddSearchPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        path = Path.GetFullPath(path);
        if (!SearchPaths.Contains(path))
            SearchPaths.Add(path);
    }

    public static string SearchPathSummary()
    {
        return SearchPaths.Count == 0 ? "(none)" : string.Join("; ", SearchPaths);
    }

    public static bool TryInit(IntPtr device, string logPath)
    {
        if (IsLoaded)
            return IsSupported;
        if (device == IntPtr.Zero)
        {
            LastError = "D3D11 device is not ready";
            return false;
        }

        if (!nativeLoaded)
        {
            string loadError = "SeDlssNgx.dll was not found";
            foreach (var path in SearchPaths)
            {
                if (NgxNative.TryLoad(path, out loadError))
                {
                    nativeLoaded = true;
                    break;
                }
            }
            if (!nativeLoaded)
            {
                LastError = loadError;
                MyLog.Default.Warning("DLSS: " + LastError);
                return false;
            }
        }

        var searchPath = FindDlssDllDirectory();
        var args = new NgxNative.InitArgs
        {
            Device = device,
            DllSearchPath = searchPath,
            LogPath = string.IsNullOrEmpty(logPath) ? searchPath : logPath
        };
        if (NgxNative.Init(ref args) == 0)
        {
            LastError = NgxNative.LastError();
            MyLog.Default.Warning("DLSS: NGX init failed: " + LastError);
            return false;
        }

        IsLoaded = true;
        IsSupported = NgxNative.IsSupported() != 0;
        LastError = NgxNative.LastError();
        return IsSupported;
    }

    public static bool TrySetMode(DlssMode mode, uint outputWidth, uint outputHeight, out uint renderWidth, out uint renderHeight)
    {
        renderWidth = outputWidth;
        renderHeight = outputHeight;
        if (!IsSupported)
            return false;

        var quality = ToNgxQuality(mode);
        var preset = ToNgxPreset(Config.Current.Model);
        if (IsReady && lastQuality == quality && lastPreset == preset && lastOutW == outputWidth && lastOutH == outputHeight)
        {
            renderWidth = (uint)DlssRuntime.InternalWidth;
            renderHeight = (uint)DlssRuntime.InternalHeight;
            return true;
        }

        float sharpness;
        if (NgxNative.SetMode(quality, outputWidth, outputHeight, out renderWidth, out renderHeight, out sharpness, preset) == 0)
        {
            IsReady = false;
            LastError = NgxNative.LastError();
            return false;
        }

        lastQuality = quality;
        lastPreset = preset;
        lastOutW = outputWidth;
        lastOutH = outputHeight;
        IsReady = true;
        LastError = NgxNative.LastError();
        return true;
    }

    public static bool Evaluate(IntPtr context, IntPtr color, IntPtr depth, IntPtr motionVectors, IntPtr output,
        float jitterX, float jitterY, int reset, float sharpness, uint renderWidth, uint renderHeight)
    {
        if (!IsReady)
            return false;
        var args = new NgxNative.EvalArgs
        {
            DeviceContext = context,
            Color = color,
            Depth = depth,
            MotionVectors = motionVectors,
            Output = output,
            JitterX = jitterX,
            JitterY = jitterY,
            MvScaleX = 1f,
            MvScaleY = 1f,
            Reset = reset,
            Sharpness = sharpness,
            RenderWidth = renderWidth,
            RenderHeight = renderHeight
        };
        if (NgxNative.Evaluate(ref args) == 0)
        {
            LastError = NgxNative.LastError();
            return false;
        }
        LastError = NgxNative.LastError();
        return true;
    }

    public static IntPtr GenerateCameraMotionVectors(IntPtr device, IntPtr context, IntPtr depth, uint width, uint height,
        float[] invViewProj, float[] unjitteredViewProj, float[] prevViewProj)
    {
        if (!nativeLoaded || NgxNative.GenerateCameraMotionVectors == null)
            return IntPtr.Zero;
        var args = new NgxNative.MvArgs
        {
            Device = device,
            DeviceContext = context,
            Depth = depth,
            Width = width,
            Height = height,
            InvViewProj = invViewProj,
            UnjitteredViewProj = unjitteredViewProj,
            PrevViewProj = prevViewProj
        };
        return NgxNative.GenerateCameraMotionVectors(ref args);
    }

    public static void Shutdown()
    {
        if (nativeLoaded && NgxNative.Shutdown != null)
            NgxNative.Shutdown();
        IsLoaded = false;
        IsSupported = false;
        IsReady = false;
        lastQuality = int.MinValue;
        lastPreset = int.MinValue;
        LastError = "shutdown";
    }

    public static float FallbackScale(DlssMode mode)
    {
        switch (mode)
        {
            case DlssMode.DLAA: return 1f;
            case DlssMode.Quality: return 2f / 3f;
            case DlssMode.Balanced: return 0.58f;
            case DlssMode.Performance: return 0.5f;
            case DlssMode.UltraPerformance: return 1f / 3f;
            default: return 2f / 3f;
        }
    }

    private static int ToNgxQuality(DlssMode mode)
    {
        switch (mode)
        {
            case DlssMode.Performance: return NgxNative.QualityMaxPerf;
            case DlssMode.Balanced: return NgxNative.QualityBalanced;
            case DlssMode.Quality: return NgxNative.QualityMaxQuality;
            case DlssMode.UltraPerformance: return NgxNative.QualityUltraPerformance;
            case DlssMode.DLAA: return NgxNative.QualityDlaa;
            default: return NgxNative.QualityMaxQuality;
        }
    }

    private static int ToNgxPreset(DlssModel model)
    {
        switch (model)
        {
            case DlssModel.TransformerJ: return 10;
            case DlssModel.TransformerK: return 11;
            case DlssModel.TransformerL: return 12;
            case DlssModel.TransformerM: return 13;
            default: return 0;
        }
    }

    private static string FindDlssDllDirectory()
    {
        foreach (var path in SearchPaths)
        {
            if (File.Exists(Path.Combine(path, "nvngx_dlss.dll")))
                return path;
        }
        return SearchPaths.Count > 0 ? SearchPaths[0] : Environment.CurrentDirectory;
    }
}
