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
    public static bool SupportKnown { get; private set; }
    public static string LastError { get; internal set; } = "not initialized";
    public static int CurrentPresetHint =>
        ToNgxPreset(Config.Current != null ? Config.Current.Model : DlssModel.LatestModel);

    private static readonly List<string> SearchPaths = [];
    private static readonly float[] MvMatrices = new float[48];
    private static bool _nativeLoaded;
    private static bool _ngxTornDown;
    private static IntPtr _device;
    private static bool _initBlocked;
    private static bool _gpuRejected;
    private static uint _lastOutW;
    private static uint _lastOutH;
    private static int _lastQuality = int.MinValue;
    private static int _lastPreset = int.MinValue;

    public static void AddSearchPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        path = Path.GetFullPath(path);
        if (!SearchPaths.Contains(path))
        {
            SearchPaths.Add(path);
            DebugLog.Write("search path " + path);
            if (!IsLoaded && !_gpuRejected)
                _initBlocked = false;
        }
    }

    public static string SearchPathSummary()
    {
        return SearchPaths.Count == 0 ? "(none)" : string.Join("; ", SearchPaths);
    }

    public static bool TryInit(IntPtr device, string logPath)
    {
        if (IsLoaded)
            return IsSupported;
        if (_initBlocked)
            return false;
        if (device == IntPtr.Zero)
        {
            LastError = "D3D11 device is not ready";
            DebugLog.Write("TryInit: " + LastError);
            return false;
        }

        GpuSupport.TryProbe();
        if (GpuSupport.Probed && !GpuSupport.IsNvidia)
        {
            LastError = GpuSupport.UnsupportedReason;
            SupportKnown = true;
            _gpuRejected = true;
            _initBlocked = true;
            IsSupported = false;
            DebugLog.Write("TryInit blocked: " + LastError);
            return false;
        }
        if (!GpuSupport.CanAttemptDlss)
        {
            LastError = GpuSupport.UnsupportedReason;
            return false;
        }

        if (!_nativeLoaded)
        {
            var loadError = "SeDlssNgx.dll was not found";
            foreach (var path in SearchPaths)
            {
                DebugLog.Write("TryLoad " + path);
                if (NgxNative.TryLoad(path, out loadError))
                {
                    _nativeLoaded = true;
                    DebugLog.Write("loaded SeDlssNgx.dll from " + path);
                    break;
                }
                DebugLog.Write("TryLoad failed: " + loadError);
            }
            if (!_nativeLoaded)
            {
                LastError = loadError;
                MyLog.Default.Warning("DLSS: " + LastError);
                DebugLog.Write("TryInit native load failed: " + LastError);
                _initBlocked = true;
                SupportKnown = true;
                IsSupported = false;
                return false;
            }
        }

        var searchPath = FindDlssDllDirectory();
        var args = new NgxNative.InitArgs
        {
            Device = device,
            DllSearchPath = searchPath,
            LogPath = string.IsNullOrEmpty(logPath) ? searchPath : logPath,
            DebugLogPath = DebugLog.NativeFilePath
        };
        DebugLog.Write(
            "NGX Init dllSearch=" + searchPath +
            " log=" + args.LogPath +
            " debug=" + (args.DebugLogPath ?? "(none)"));
        if (NgxNative.Init(ref args) == 0)
        {
            LastError = NgxNative.LastError();
            MyLog.Default.Warning("DLSS: NGX init failed: " + LastError);
            DebugLog.Write("NGX Init failed: " + LastError);
            _initBlocked = true;
            SupportKnown = true;
            IsSupported = false;
            return false;
        }

        IsLoaded = true;
        _device = device;
        _ngxTornDown = false;
        IsSupported = NgxNative.IsSupported() != 0;
        SupportKnown = true;
        LastError = NgxNative.LastError();
        if (!IsSupported)
        {
            _initBlocked = true;
            MyLog.Default.Warning("DLSS: " + LastError);
        }
        DebugLog.Write("NGX Init ok supported=" + IsSupported + " " + LastError);
        return IsSupported;
    }

    public static bool TrySetMode(
        DlssMode mode,
        uint outputWidth,
        uint outputHeight,
        out uint renderWidth,
        out uint renderHeight)
    {
        renderWidth = outputWidth;
        renderHeight = outputHeight;
        if (!IsSupported)
        {
            DebugLog.Write("TrySetMode skipped: not supported");
            return false;
        }

        var quality = ToNgxQuality(mode);
        var preset = ToNgxPreset(Config.Current.Model);
        if (IsReady && _lastQuality == quality && _lastPreset == preset &&
            _lastOutW == outputWidth && _lastOutH == outputHeight)
        {
            renderWidth = (uint)DlssRuntime.InternalWidth;
            renderHeight = (uint)DlssRuntime.InternalHeight;
            return true;
        }

        DebugLog.Write("SetMode quality=" + quality + " preset=" + preset +
                       " out=" + outputWidth + "x" + outputHeight);
        if (NgxNative.SetMode(
                quality,
                outputWidth,
                outputHeight,
                out renderWidth,
                out renderHeight,
                out var sharpness,
                preset) == 0)
        {
            IsReady = false;
            LastError = NgxNative.LastError();
            DebugLog.Write("SetMode failed: " + LastError);
            return false;
        }

        _lastQuality = quality;
        _lastPreset = preset;
        _lastOutW = outputWidth;
        _lastOutH = outputHeight;
        IsReady = true;
        LastError = NgxNative.LastError();
        DebugLog.Write(
            "SetMode ok render=" + renderWidth + "x" + renderHeight +
            " sharpness=" + sharpness + " " + LastError);
        return true;
    }

    public static bool Evaluate(IntPtr context, IntPtr color, IntPtr depth, IntPtr motionVectors, IntPtr output,
        IntPtr exposure, float jitterX, float jitterY, int reset, float sharpness, uint renderWidth, uint renderHeight)
    {
        if (!IsReady || _gpuRejected)
            return false;
        var args = new NgxNative.EvalArgs
        {
            DeviceContext = context,
            Color = color,
            Depth = depth,
            MotionVectors = motionVectors,
            Output = output,
            Exposure = exposure,
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
            DebugLog.Write("Evaluate failed reset=" + reset + " jitter=" + jitterX + "," + jitterY +
                           " render=" + renderWidth + "x" + renderHeight + " mv=" + (motionVectors != IntPtr.Zero) +
                           " " + LastError);
            return false;
        }
        LastError = NgxNative.LastError();
        DebugLog.WriteFrame("Evaluate ok reset=" + reset +
                            " render=" + renderWidth + "x" + renderHeight + " mv=" + (motionVectors != IntPtr.Zero));
        return true;
    }

    public static IntPtr GenerateCameraMotionVectors(
        IntPtr device,
        IntPtr context,
        IntPtr depth,
        uint width,
        uint height,
        float[] invViewProj, float[] unjitteredViewProj, float[] prevViewProj)
    {
        if (_gpuRejected || !_nativeLoaded || NgxNative.GenerateCameraMotionVectors == null)
            return IntPtr.Zero;
        Array.Copy(invViewProj, 0, MvMatrices, 0, 16);
        Array.Copy(unjitteredViewProj, 0, MvMatrices, 16, 16);
        Array.Copy(prevViewProj, 0, MvMatrices, 32, 16);
        var args = new NgxNative.MvArgs
        {
            Device = device,
            DeviceContext = context,
            Depth = depth,
            Width = width,
            Height = height,
            Matrices = MvMatrices
        };
        var mv = NgxNative.GenerateCameraMotionVectors(ref args);
        if (mv == IntPtr.Zero)
            DebugLog.Write(
                "GenerateCameraMotionVectors failed " + width + "x" + height +
                " " + (NgxNative.LastError() ?? LastError));
        return mv;
    }

    public static bool TryUpsampleDepth(IntPtr device, IntPtr context, IntPtr srcDepth, IntPtr destDepth)
    {
        if (_gpuRejected || !_nativeLoaded || NgxNative.UpsampleDepth == null)
            return false;
        if (device == IntPtr.Zero || context == IntPtr.Zero || srcDepth == IntPtr.Zero || destDepth == IntPtr.Zero)
            return false;
        if (NgxNative.UpsampleDepth(device, context, srcDepth, destDepth) == 0)
        {
            DebugLog.Write("UpsampleDepth failed " + (NgxNative.LastError() ?? LastError));
            return false;
        }
        return true;
    }

    public static void AllowRetry()
    {
        if (_gpuRejected)
            return;
        _initBlocked = false;
        SupportKnown = IsLoaded;
    }

    /// <summary>
    /// NGX D3D11 shutdown must run before the device is released, on the thread
    /// that owns it. Plugin.Dispose is neither: it runs on the game thread after
    /// (or while) Keen tears D3D down, and NVSDK_NGX_D3D11_Shutdown1 then AVs.
    /// Native teardown happens in <see cref="OnDeviceDisposing"/>.
    /// </summary>
    public static void Shutdown()
    {
        DebugLog.Write(
            "NgxHost.Shutdown loaded=" + IsLoaded + " ready=" + IsReady +
            " ngxTornDown=" + _ngxTornDown);
        ResetSessionFlags();
    }

    public static void OnDeviceDisposing(IntPtr device)
    {
        if (!_nativeLoaded || _ngxTornDown || NgxNative.Shutdown == null)
            return;
        if (device == IntPtr.Zero)
            return;
        if (_device != IntPtr.Zero && device != _device)
            return;

        DebugLog.Write("NgxHost.OnDeviceDisposing");
        try
        {
            NgxNative.Shutdown();
        }
        catch (Exception e)
        {
            MyLog.Default.Error("DLSS native shutdown failed: " + e);
        }

        _ngxTornDown = true;
        _device = IntPtr.Zero;
        ResetSessionFlags();
    }

    private static void ResetSessionFlags()
    {
        IsLoaded = false;
        IsSupported = false;
        IsReady = false;
        SupportKnown = false;
        _initBlocked = false;
        _gpuRejected = false;
        _lastOutW = 0;
        _lastOutH = 0;
        _lastQuality = int.MinValue;
        _lastPreset = int.MinValue;
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
            case DlssModel.CnnF: return 6;
            default: return 11;
        }
    }

    private static string FindDlssDllDirectory()
    {
        foreach (var path in SearchPaths)
            if (File.Exists(Path.Combine(path, "nvngx_dlss.dll")))
                return path;
        return SearchPaths.Count > 0 ? SearchPaths[0] : Environment.CurrentDirectory;
    }
}
