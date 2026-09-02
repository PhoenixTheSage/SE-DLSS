using System;
using System.Collections.Generic;
using System.IO;
using SharpDX.Direct3D11;
using VRage.Utils;
using Device = SharpDX.Direct3D11.Device;
using Resource = SharpDX.Direct3D11.Resource;

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
    private static bool _ngxTornDown;
    private static IntPtr _device;
    private static Device _deviceOwner;
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

    public static bool TryInit(Device device, string logPath)
    {
        if (IsLoaded)
            return IsSupported;
        if (_initBlocked)
            return false;
        if (device == null || device.IsDisposed)
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

        var searchPath = FindDlssDllDirectory();
        var log = string.IsNullOrEmpty(logPath) ? searchPath : logPath;
        DebugLog.Write("NGX Init dllSearch=" + searchPath + " log=" + log);
        try
        {
            if (!NgxApi.Init(device, searchPath, log))
            {
                LastError = NgxApi.LastError;
                MyLog.Default.Warning("DLSS: NGX init failed: " + LastError);
                DebugLog.Write("NGX Init failed: " + LastError);
                _initBlocked = true;
                SupportKnown = true;
                IsSupported = false;
                return false;
            }
        }
        catch (Exception e)
        {
            LastError = "NGX init threw: " + e.GetType().Name + ": " + e.Message;
            MyLog.Default.Error("DLSS: " + LastError);
            DebugLog.Write(LastError);
            _initBlocked = true;
            SupportKnown = true;
            IsSupported = false;
            return false;
        }

        IsLoaded = true;
        _device = device.NativePointer;
        _deviceOwner = device;
        _ngxTornDown = false;
        IsSupported = NgxApi.IsSupported;
        SupportKnown = true;
        LastError = NgxApi.LastError;
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
        if (!NgxApi.SetMode(quality, outputWidth, outputHeight, preset,
                out renderWidth, out renderHeight, out var sharpness))
        {
            IsReady = false;
            LastError = NgxApi.LastError;
            DebugLog.Write("SetMode failed: " + LastError);
            return false;
        }

        _lastQuality = quality;
        _lastPreset = preset;
        _lastOutW = outputWidth;
        _lastOutH = outputHeight;
        IsReady = true;
        LastError = NgxApi.LastError;
        DebugLog.Write(
            "SetMode ok render=" + renderWidth + "x" + renderHeight +
            " sharpness=" + sharpness + " " + LastError);
        return true;
    }

    public static bool Evaluate(
        Device device,
        DeviceContext context,
        Resource color,
        Resource depth,
        IntPtr motionVectors,
        Resource output,
        float jitterX,
        float jitterY,
        int reset,
        float sharpness,
        uint renderWidth,
        uint renderHeight,
        IntPtr biasCurrentColorMask = default(IntPtr))
    {
        if (!IsReady || _gpuRejected)
            return false;
        if (!NgxApi.Evaluate(device, context, color, depth, motionVectors, output,
                jitterX, jitterY, reset, sharpness, renderWidth, renderHeight, biasCurrentColorMask))
        {
            LastError = NgxApi.LastError;
            DebugLog.Write("Evaluate failed reset=" + reset + " jitter=" + jitterX + "," + jitterY +
                           " render=" + renderWidth + "x" + renderHeight + " mv=" + (motionVectors != IntPtr.Zero) +
                           " " + LastError);
            return false;
        }

        LastError = NgxApi.LastError;
        DebugLog.WriteFrame("Evaluate ok reset=" + reset +
                            " render=" + renderWidth + "x" + renderHeight + " mv=" + (motionVectors != IntPtr.Zero));
        return true;
    }

    public static IntPtr GenerateCameraMotionVectors(
        Device device,
        DeviceContext context,
        Resource depth,
        uint width,
        uint height,
        float[] invViewProj, float[] unjitteredViewProj, float[] prevViewProj)
    {
        if (_gpuRejected || !NgxApi.IsInitialized)
            return IntPtr.Zero;
        var mv = DlssD3d.GenerateCameraMotionVectors(
            device, context, depth, width, height,
            invViewProj, unjitteredViewProj, prevViewProj);
        if (mv == IntPtr.Zero)
            DebugLog.Write(
                "GenerateCameraMotionVectors failed " + width + "x" + height +
                " " + (NgxApi.LastError ?? LastError));
        return mv;
    }

    public static bool TryUpsampleDepth(Device device, DeviceContext context, Resource srcDepth, Resource destDepth)
    {
        if (_gpuRejected || !NgxApi.IsInitialized)
            return false;
        if (device == null || context == null || srcDepth == null || destDepth == null)
            return false;
        if (!DlssD3d.TryUpsampleDepth(device, context, srcDepth, destDepth))
        {
            DebugLog.Write("UpsampleDepth failed " + (NgxApi.LastError ?? LastError));
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
    /// Teardown happens in <see cref="OnDeviceDisposing"/>.
    /// </summary>
    public static void Shutdown()
    {
        DebugLog.Write(
            "NgxHost.Shutdown loaded=" + IsLoaded + " ready=" + IsReady +
            " ngxTornDown=" + _ngxTornDown);
        ResetSessionFlags();
    }

    public static void OnDeviceDisposing(Device device)
    {
        // DeviceChild.Device returns a temporary SharpDX Device wrapper for the
        // same native pointer. Disposing such a wrapper must not tear down NGX.
        if (device == null || !ReferenceEquals(device, _deviceOwner))
            return;
        if (!NgxApi.IsInitialized || _ngxTornDown)
            return;
        var pointer = device.NativePointer;
        if (pointer == IntPtr.Zero)
            return;
        if (_device != IntPtr.Zero && pointer != _device)
            return;

        DebugLog.Write("NgxHost.OnDeviceDisposing");
        try
        {
            NgxApi.Shutdown();
        }
        catch (Exception e)
        {
            MyLog.Default.Error("DLSS NGX shutdown failed: " + e);
        }

        _ngxTornDown = true;
        _device = IntPtr.Zero;
        _deviceOwner = null;
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
            case DlssMode.Performance: return NgxApi.QualityMaxPerf;
            case DlssMode.Balanced: return NgxApi.QualityBalanced;
            case DlssMode.Quality: return NgxApi.QualityMaxQuality;
            case DlssMode.UltraPerformance: return NgxApi.QualityUltraPerformance;
            case DlssMode.DLAA: return NgxApi.QualityDlaa;
            default: return NgxApi.QualityMaxQuality;
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
