using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NETFRAMEWORK
using System.Runtime.ExceptionServices;
using System.Security;
#endif
using SharpDX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;
using Resource = SharpDX.Direct3D11.Resource;

namespace ClientPlugin.Dlss;

/// <summary>
/// NGX D3D11 session: init, create DLSS feature, evaluate, shutdown.
/// </summary>
internal static class NgxApi
{
    internal const int QualityMaxPerf = 0;
    internal const int QualityBalanced = 1;
    internal const int QualityMaxQuality = 2;
    internal const int QualityUltraPerformance = 3;
    internal const int QualityDlaa = 5;

    private const int FlagMvLowRes = 1 << 1;
    private const int FlagDepthInverted = 1 << 3;
    private const int FlagAutoExposure = 1 << 6;
    // Anomaly velocity is unjittered pixel-space at internal res: MVJittered off, MVLowRes on.
    private const int CreateFlags = FlagDepthInverted | FlagAutoExposure | FlagMvLowRes;
    private const int PresetF = 6;
    private const int PresetJ = 10;
    private const int PresetK = 11;
    private const int PresetM = 13;
    private const ulong SteamAppId = 0x244850;

    private static readonly object Gate = new();

    private static NgxDriver _driver;
    private static NgxFeatureInfo _featureInfo;
    private static Device _device;
    private static IntPtr _devicePtr;
    private static NgxParameter _capabilityParams;
    private static NgxParameter _evalParams;
    private static IntPtr _dlss;
    private static bool _initialized;
    private static bool _supported;
    private static int _quality = -1;
    private static int _preset = -1;
    private static uint _outW;
    private static uint _outH;
    private static int _createFlags = -1;
    private static string _lastEvalLog;
    private static bool _loggedEvalInputs;
    private static IntPtr _projectIdNative;
    private static IntPtr _engineVersionNative;
    private static IntPtr _logPathNative;

    internal static string LastError { get; private set; } = "not initialized";
    internal static bool IsInitialized => _initialized;
    internal static bool IsSupported => _supported;
    internal static bool HasFeature => _dlss != IntPtr.Zero;

    internal static void SetError(string text)
    {
        LastError = string.IsNullOrEmpty(text) ? "unknown error" : text;
    }

    internal static bool Init(Device device, string dllSearchPath, string logPath)
    {
        lock (Gate)
        {
            if (_initialized)
                return true;
            if (device == null || device.IsDisposed)
            {
                SetError("Init requires a D3D11 device");
                return false;
            }

            DebugLog.Write("Init search=" + dllSearchPath + " abi=oleaut-6");

            var driver = new NgxDriver();
            if (!driver.TryLoad(out var loadError))
            {
                SetError(loadError);
                DebugLog.Write(loadError);
                driver.Dispose();
                return false;
            }

            DebugLog.Write("loaded NGX from " + driver.LoadedFrom);
            _driver = driver;
            _device = device;
            _devicePtr = device.NativePointer;

            var paths = new List<string>();
            if (!string.IsNullOrEmpty(dllSearchPath))
                paths.Add(dllSearchPath);
            var ngxCore = NgxDriver.ReadNgxCoreDir();
            if (!string.IsNullOrEmpty(ngxCore))
                paths.Add(ngxCore);
            _featureInfo = NgxFeatureInfo.Create(paths);
            DebugLog.Write(_featureInfo.Describe());

            var log = string.IsNullOrEmpty(logPath) ? "." : logPath;
            var info = _featureInfo.Pointer;
            EnsureNativeStrings(log);
            int result;
            var entry = _driver.HasInitProject ? "Init_ProjectID"
                : _driver.HasInitProjectSdk ? "Init_with_ProjectID"
                : _driver.HasInitExt ? "Init_Ext"
                : "Init";
            var callPath = _driver.HasInitProject || !_driver.HasInitProjectSdk
                ? "oleaut-6"
                : "sdk-delegate";
            DebugLog.Write("calling NGX D3D11 " + entry + " via " + callPath +
                           " device=0x" + _devicePtr.ToInt64().ToString("X") +
                           " info=0x" + info.ToInt64().ToString("X") +
                           " ver=0x" + NgxDriver.VersionApi.ToString("X") +
                           " log=" + log);
            try
            {
                result = CallNgxInit(info);
                GC.KeepAlive(device);
            }
            catch (Exception e)
            {
                SetError("NGX D3D11 " + entry + " threw: " + e.GetType().Name + ": " + e.Message);
                DebugLog.Write(LastError);
                FailInitCleanup(unloadDriver: !IsAccessViolation(e));
                return false;
            }

            if (NgxResult.Failed(result))
            {
                if (result != NgxResult.FailAccessViolation)
                    SetError("NVSDK_NGX_D3D11_" + entry + " failed (0x" + ((uint)result).ToString("X8") + ")");
                DebugLog.Write(LastError);
                FailInitCleanup(unloadDriver: result != NgxResult.FailAccessViolation);
                return false;
            }

            DebugLog.Write("NGX D3D11 init ok");
            DebugLog.Write(NgxLog.HasMessages
                ? "NGX log callback live last=" + NgxLog.LastLine
                : "NGX log callback installed, no messages during init");
            result = _driver.GetCapabilityParameters(out var capsPtr);
            if (NgxResult.Failed(result) || capsPtr == IntPtr.Zero)
            {
                SetError("GetCapabilityParameters failed");
                FailInitCleanup(shutdownNgx: true);
                return false;
            }

            _capabilityParams = NgxParameter.FromNative(capsPtr);
            if (_driver.AllocateParameters != null &&
                !NgxResult.Failed(_driver.AllocateParameters(out var evalPtr)) &&
                evalPtr != IntPtr.Zero)
                _evalParams = NgxParameter.FromNative(evalPtr);

            _capabilityParams.Get(NgxNames.SuperSamplingAvailable, out int available);
            _supported = available != 0;
            _initialized = true;
            if (_supported)
            {
                SetError("initialized from " + _driver.LoadedFrom);
                DebugLog.Write(LastError);
            }
            else
            {
                SetError("NGX initialized but Super Sampling is not available");
                DebugLog.Write(LastError);
            }

            return true;
        }
    }

    internal static bool SetMode(int quality, uint outWidth, uint outHeight, int preset,
        out uint renderWidth, out uint renderHeight, out float sharpness)
    {
        renderWidth = outWidth;
        renderHeight = outHeight;
        sharpness = 0f;
        lock (Gate)
        {
            if (!_initialized || !_supported || _capabilityParams == null || _device == null)
            {
                SetError("NGX is not initialized");
                return false;
            }

            if (outWidth == 0 || outHeight == 0)
            {
                SetError("invalid output size");
                return false;
            }

            var params_ = _capabilityParams;
            params_.Set(NgxNames.Width, outWidth);
            params_.Set(NgxNames.Height, outHeight);
            params_.Set(NgxNames.PerfQualityValue, quality);
            params_.Set(NgxNames.RtxValue, 0);
            ApplyHintPresets(params_, preset);

            uint renderW = outWidth;
            uint renderH = outHeight;
            if (params_.TryInvokeOptimalSettings())
            {
                params_.Get(NgxNames.OutWidth, out renderW);
                params_.Get(NgxNames.OutHeight, out renderH);
                params_.Get(NgxNames.Sharpness, out sharpness);
            }

            if (renderW == 0 || renderH == 0)
            {
                SetError("DLSS optimal settings returned a zero size");
                return false;
            }

            if (_dlss != IntPtr.Zero && _quality == quality && _preset == preset &&
                _outW == outWidth && _outH == outHeight && _createFlags == CreateFlags)
            {
                renderWidth = renderW;
                renderHeight = renderH;
                return true;
            }

            ReleaseDlss();
            params_.Set(NgxNames.Width, renderW);
            params_.Set(NgxNames.Height, renderH);
            params_.Set(NgxNames.OutWidth, outWidth);
            params_.Set(NgxNames.OutHeight, outHeight);
            params_.Set(NgxNames.PerfQualityValue, quality);
            ApplyHintPresets(params_, preset);
            params_.Set(NgxNames.CreateFlags, CreateFlags);
            params_.Set(NgxNames.EnableOutputSubrects, 0);

            DeviceContext ctx;
            try
            {
                ctx = _device.ImmediateContext;
            }
            catch (Exception e)
            {
                SetError("failed to get immediate context: " + e.GetType().Name);
                return false;
            }

            if (ctx == null)
            {
                SetError("failed to get immediate context");
                return false;
            }

            var result = _driver.CreateFeature(ctx.NativePointer, NgxDriver.FeatureSuperSampling,
                params_.Pointer, out _dlss);
            if (NgxResult.Failed(result) || _dlss == IntPtr.Zero)
            {
                _dlss = IntPtr.Zero;
                SetError("CreateFeature SuperSampling failed (0x" + ((uint)result).ToString("X8") + ")");
                DebugLog.Write(LastError);
                return false;
            }

            _quality = quality;
            _preset = preset;
            _createFlags = CreateFlags;
            _outW = outWidth;
            _outH = outHeight;
            renderWidth = renderW;
            renderHeight = renderH;
            SetError("DLSS feature created");
            DebugLog.Write("CreateFeature ok quality=" + quality + " preset=" + preset +
                           " out=" + outWidth + "x" + outHeight + " render=" + renderW + "x" + renderH);
            if (NgxLog.HasMessages)
                DebugLog.Write("CreateFeature ngx=" + NgxLog.LastLines(4));
            return true;
        }
    }

    internal static bool Evaluate(
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
        lock (Gate)
        {
        if (!_initialized || _dlss == IntPtr.Zero || context == null || color == null || output == null ||
            depth == null)
        {
            SetError("Evaluate missing device, color, depth, or output");
            return false;
        }

        var parameters = _evalParams ?? _capabilityParams;
        if (parameters == null)
        {
            SetError("no NGX parameter map");
            return false;
        }

        var motion = DlssD3d.EnsureMotionOrZero(device, context, motionVectors, renderWidth, renderHeight);
        if (!DlssD3d.PrepareEvalOutput(device, output, out var evalOutput, out var copyBack, out var destDesc))
            return false;

        parameters.Reset();
        parameters.SetD3D11(NgxNames.Color, color.NativePointer);
        parameters.SetD3D11(NgxNames.Output, evalOutput);
        parameters.SetD3D11(NgxNames.Depth, depth.NativePointer);
        if (motion != IntPtr.Zero)
            parameters.SetD3D11(NgxNames.MotionVectors, motion);
        if (biasCurrentColorMask != IntPtr.Zero)
            parameters.SetD3D11(NgxNames.BiasCurrentColorMask, biasCurrentColorMask);
        var colorGet = parameters.GetD3D11(NgxNames.Color, out var colorParameter);
        var outputGet = parameters.GetD3D11(NgxNames.Output, out var outputParameter);
        var depthGet = parameters.GetD3D11(NgxNames.Depth, out var depthParameter);
        var motionGet = parameters.GetD3D11(NgxNames.MotionVectors, out var motionParameter);
        parameters.Set(NgxNames.JitterOffsetX, jitterX);
        parameters.Set(NgxNames.JitterOffsetY, jitterY);
        parameters.Set(NgxNames.Sharpness, sharpness);
        parameters.Set(NgxNames.Reset, reset);
        parameters.Set(NgxNames.MvScaleX, 1f);
        parameters.Set(NgxNames.MvScaleY, 1f);
        parameters.Set(NgxNames.RenderSubrectWidth, renderWidth);
        parameters.Set(NgxNames.RenderSubrectHeight, renderHeight);

        var packet = DlssD3d.DescribeContext(device, context) +
                     " render=" + renderWidth + "x" + renderHeight +
                     " jitter=" + jitterX.ToString("0.###") + "," + jitterY.ToString("0.###") +
                     " reset=" + reset +
                     " sharp=" + sharpness.ToString("0.###") +
                     " color=" + DlssD3d.Describe(color) +
                     " depth=" + DlssD3d.Describe(depth) +
                     " mv=" + DlssD3d.Describe(motion) +
                     " bias=" + DlssD3d.Describe(biasCurrentColorMask) +
                     " dest=" + destDesc +
                     " evalOut=" + DlssD3d.Describe(evalOutput, copyBack ? null : output) +
                     " copyBack=" + (copyBack ? 1 : 0) +
                     " sameColorOut=" + (color.NativePointer == evalOutput ? 1 : 0) +
                     " feature=0x" + _dlss.ToInt64().ToString("X") +
                     " params=0x" + parameters.Pointer.ToInt64().ToString("X") +
                     " initDev=0x" + _devicePtr.ToInt64().ToString("X") +
                     " map=color:0x" + colorParameter.ToInt64().ToString("X") +
                     "/0x" + ((uint)colorGet).ToString("X8") +
                     " output:0x" + outputParameter.ToInt64().ToString("X") +
                     "/0x" + ((uint)outputGet).ToString("X8") +
                     " depth:0x" + depthParameter.ToInt64().ToString("X") +
                     "/0x" + ((uint)depthGet).ToString("X8") +
                     " mv:0x" + motionParameter.ToInt64().ToString("X") +
                     "/0x" + ((uint)motionGet).ToString("X8");
        if (!_loggedEvalInputs)
        {
            _loggedEvalInputs = true;
            DebugLog.Write("Evaluate inputs " + packet);
        }

        DlssD3d.UnbindPipeline(context);
        var evaluate = _driver?.EvaluateFeature;
        if (evaluate == null)
        {
            SetError("EvaluateFeature export is unavailable");
            return false;
        }
        var result = evaluate(context.NativePointer, _dlss, parameters.Pointer, IntPtr.Zero);
        if (NgxResult.Failed(result))
        {
            SetError("EvaluateFeature failed (0x" + ((uint)result).ToString("X8") + " " +
                     NgxResult.Name(result) + ") " + packet + " ngx=" + NgxLog.LastLines(6));
            DebugLog.Write(LastError);
            return false;
        }

        if (copyBack)
            DlssD3d.CopyEvalOutput(context, output);

        var evalLog = "Evaluate ok render=" + renderWidth + "x" + renderHeight + " copyBack=" +
                      (copyBack ? 1 : 0) + " dest=" + destDesc;
        if (!string.Equals(_lastEvalLog, evalLog, StringComparison.Ordinal))
        {
            _lastEvalLog = evalLog;
            DebugLog.Write(evalLog);
        }

        SetError("ok");
        return true;
        }
    }

    internal static void Shutdown()
    {
        lock (Gate)
        {
            DebugLog.Write("NgxApi.Shutdown");
            try
            {
                ReleaseDlss();
                DlssD3d.Release();
                if (_evalParams != null && _driver?.DestroyParameters != null)
                {
                    try
                    {
                        _driver.DestroyParameters(_evalParams.Pointer);
                    }
                    catch
                    {
                        // ignored
                    }
                }

                _evalParams = null;
                _capabilityParams = null;
                NgxParameter.ClearCache();
                FreeNativeStrings();
                if (_initialized && _driver?.Shutdown1 != null && _devicePtr != IntPtr.Zero)
                {
                    try
                    {
                        _driver.Shutdown1(_devicePtr);
                    }
                    catch (Exception e)
                    {
                        DebugLog.Write("NGX Shutdown1: " + e.GetType().Name + ": " + e.Message);
                    }
                }
            }
            finally
            {
                _initialized = false;
                _supported = false;
                _device = null;
                _devicePtr = IntPtr.Zero;
                _driver?.Dispose();
                _driver = null;
                _featureInfo?.Dispose();
                _featureInfo = null;
                _lastEvalLog = null;
                _loggedEvalInputs = false;
                NgxLog.Clear();
                SetError("shutdown");
            }
        }
    }

    private static void FailInitCleanup(bool shutdownNgx = false, bool unloadDriver = true)
    {
        if (shutdownNgx && _driver?.Shutdown1 != null && _devicePtr != IntPtr.Zero)
        {
            try
            {
                _driver.Shutdown1(_devicePtr);
            }
            catch
            {
                // ignored
            }
        }

        _capabilityParams = null;
        _evalParams = null;
        _device = null;
        _devicePtr = IntPtr.Zero;
        _initialized = false;
        _supported = false;
        if (unloadDriver)
        {
            _driver?.Dispose();
            _driver = null;
            _featureInfo?.Dispose();
            _featureInfo = null;
            FreeNativeStrings();
        }
        else
        {
            // Native code already faulted; do not FreeLibrary or free blobs it may hold.
            _driver = null;
            _featureInfo = null;
        }
    }

    private static void ReleaseDlss()
    {
        if (_dlss != IntPtr.Zero && _driver?.ReleaseFeature != null)
        {
            try
            {
                _driver.ReleaseFeature(_dlss);
            }
            catch
            {
                // ignored
            }
        }

        _dlss = IntPtr.Zero;
        _quality = -1;
        _preset = -1;
        _createFlags = -1;
        _outW = _outH = 0;
    }

    private static int CallNgxInit(IntPtr info)
    {
        if (_driver.HasInitProject)
            return InvokeInitProject(info);
        if (_driver.HasInitProjectSdk)
            return InvokeInitProjectSdk(info);
        if (_driver.HasInitExt)
            return InvokeInitExt(info);
        return InvokeInitApp();
    }

    private static void EnsureNativeStrings(string logPath)
    {
        if (_projectIdNative == IntPtr.Zero)
            _projectIdNative = Marshal.StringToHGlobalAnsi(NgxDriver.ProjectId);
        if (_engineVersionNative == IntPtr.Zero)
            _engineVersionNative = Marshal.StringToHGlobalAnsi(NgxDriver.EngineVersion);
        FreePtr(ref _logPathNative);
        _logPathNative = Marshal.StringToHGlobalUni(logPath);
    }

    private static void FreeNativeStrings()
    {
        FreePtr(ref _projectIdNative);
        FreePtr(ref _engineVersionNative);
        FreePtr(ref _logPathNative);
    }

    private static void FreePtr(ref IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
            return;
        Marshal.FreeHGlobal(ptr);
        ptr = IntPtr.Zero;
    }

    private static bool IsAccessViolation(Exception e)
    {
        return e is AccessViolationException || e is SEHException;
    }

#if NETFRAMEWORK
    [HandleProcessCorruptedStateExceptions]
    [SecurityCritical]
#endif
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int InvokeInitProject(IntPtr info)
    {
        try
        {
            return NgxOleInvoker.InitProject(
                _driver.InitProjectPtr,
                _projectIdNative,
                NgxDriver.EngineTypeCustom,
                _engineVersionNative,
                _logPathNative,
                _devicePtr,
                NgxDriver.VersionApi,
                info);
        }
        catch (Exception e)
        {
            return NativeInitFailed(e);
        }
    }

#if NETFRAMEWORK
    [HandleProcessCorruptedStateExceptions]
    [SecurityCritical]
#endif
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int InvokeInitProjectSdk(IntPtr info)
    {
        try
        {
            return _driver.InitProjectSdk(
                _projectIdNative, NgxDriver.EngineTypeCustom, _engineVersionNative,
                _logPathNative, _devicePtr, info, NgxDriver.VersionApi);
        }
        catch (Exception e)
        {
            return NativeInitFailed(e);
        }
    }

#if NETFRAMEWORK
    [HandleProcessCorruptedStateExceptions]
    [SecurityCritical]
#endif
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int InvokeInitExt(IntPtr info)
    {
        try
        {
            // Private driver-core ABI: app, path, device, version, feature info.
            return NgxOleInvoker.InitExt(
                _driver.InitExtPtr,
                SteamAppId,
                _logPathNative,
                _devicePtr,
                NgxDriver.VersionApi,
                info);
        }
        catch (Exception e)
        {
            return NativeInitFailed(e);
        }
    }

#if NETFRAMEWORK
    [HandleProcessCorruptedStateExceptions]
    [SecurityCritical]
#endif
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int InvokeInitApp()
    {
        try
        {
            // Private driver-core ABI: app, path, device, version.
            return NgxOleInvoker.Init(
                _driver.InitAppPtr,
                SteamAppId,
                _logPathNative,
                _devicePtr,
                NgxDriver.VersionApi);
        }
        catch (Exception e)
        {
            return NativeInitFailed(e);
        }
    }

    private static int NativeInitFailed(Exception e)
    {
        var av = IsAccessViolation(e);
        SetError("NGX D3D11 init " + (av ? "access violation" : "threw") + ": " +
                 e.GetType().Name + ": " + e.Message);
        DebugLog.Write(LastError);
        return av ? NgxResult.FailAccessViolation : NgxResult.Fail;
    }

    private static void ApplyHintPresets(NgxParameter parameters, int preset)
    {
        uint k = PresetK;
        uint dlaa = k, quality = k, balanced = k, perf = k, ultra = k, ultraQ = k;
        if (IsForcedRenderPreset(preset))
        {
            var forced = (uint)preset;
            dlaa = quality = balanced = perf = ultra = ultraQ = forced;
        }

        parameters.Set(NgxNames.PresetDlaa, dlaa);
        parameters.Set(NgxNames.PresetQuality, quality);
        parameters.Set(NgxNames.PresetBalanced, balanced);
        parameters.Set(NgxNames.PresetPerformance, perf);
        parameters.Set(NgxNames.PresetUltraPerformance, ultra);
        parameters.Set(NgxNames.PresetUltraQuality, ultraQ);
    }

    private static bool IsForcedRenderPreset(int preset)
    {
        return preset is >= 1 and <= PresetF || preset is >= PresetJ and <= PresetM;
    }
}
