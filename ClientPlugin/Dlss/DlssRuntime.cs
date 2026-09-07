using System;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using VRage.Render11.Common;
using VRage.Render11.Resources;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Dlss;

public static class DlssRuntime
{
    public static string LastBindingEvidence { get; private set; }
    internal static string BindingContext { get; private set; }
    private static string _lastVelocitySource;
    private static long _evaluateAttempt;
    private static long _renderFrame;
    private static int _evaluatedWidth, _evaluatedHeight;
    internal static void RecordBinding(string evidence)
    {
        LastBindingEvidence = BindingContext + " " + evidence;
        DebugLog.WriteFrame(LastBindingEvidence);
    }
    public static int InternalWidth { get; private set; }
    public static int InternalHeight { get; private set; }
    public static int OutputWidth { get; private set; }
    public static int OutputHeight { get; private set; }
    public static bool LastEvaluateFailed { get; private set; }
    public static bool UsedExternalVelocity { get; private set; }
    public static bool UsedReactiveMask { get; private set; }
    public static bool EvaluatedThisFrame { get; set; }
    public static bool LastEvaluateWasHdr { get; private set; }
    public static int EvaluateCount { get; private set; }
    public static IBorrowedDepthStencilTexture OutputDepthThisFrame { get; private set; }
    private static bool _outputDepthReady;
    private static ICustomTexture _ldrTexture;
    private static PersistentLdrTarget _ldrOutput;
    private static ICustomTexture _hdrTexture;
    private static PersistentLdrTarget _hdrOutput;

    private static bool _configChanged = true;
    private static bool _resetHistory = true;
    private static volatile bool _pluginsReady;
    private static int _consecutiveEvaluateFails;
    private static Vector2I _cachedOutput;
#if DEBUG
    private static string _lastPrepareLog;
#endif
    private static readonly float[] InvViewProj = new float[16];
    private static readonly float[] UnjitteredViewProj = new float[16];
    private static readonly float[] PrevViewProj = new float[16];

    public static bool WantsDlss
    {
        get
        {
            var config = Config.Current;
            if (config == null || config.AntiAliasing != AntiAliasingChoice.DLSS)
                return false;
            GpuSupport.TryProbe();
            if (!GpuSupport.CanAttemptDlss)
                return false;
            if (NgxHost.SupportKnown && !NgxHost.IsSupported)
                return false;
            return true;
        }
    }

    public static bool IsLive => WantsDlss && NgxHost.IsReady && !MyRender11.MultisamplingEnabled;

    /// <summary>
    /// HdrRender (or another Display tenant) owns present. Do not intercept
    /// <c>CopyToRT</c> or LDR billboards onto the scRGB swapchain.
    /// </summary>
    public static bool ShouldYieldPresentPath =>
        AnomalyHook.HasDisplayTenant || IsHdrSwapchainLive;

    public static bool IsHdrSwapchainLive
    {
        get
        {
            var backbuffer = MyRender11.Backbuffer;
            if (backbuffer?.Resource == null)
                return false;
            try
            {
                using var tex = backbuffer.Resource.QueryInterface<Texture2D>();
                var format = tex.Description.Format;
                return format == Format.R16G16B16A16_Float ||
                       format == Format.R16G16B16A16_UNorm;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void NotifyPluginsReady()
    {
        if (_pluginsReady)
            return;
        _pluginsReady = true;
        AnomalyHook.Probe();
        AnomalyHook.ClaimUpscale();
        DebugLog.Write("plugins ready; NGX init allowed");
    }

    public static void NotifyConfigChanged()
    {
        _configChanged = true;
        _resetHistory = true;
        _consecutiveEvaluateFails = 0;
        LastEvaluateFailed = false;
        Jitter.Reset();
        AnomalyHook.InvalidateHistory();
        DisableConsoleDrs();
        NgxHost.AllowRetry();
        DebugLog.Write(
            "NotifyConfigChanged aa=" + (Config.Current != null ? Config.Current.AntiAliasing.ToString() : "?") +
            " mode=" + (Config.Current != null ? Config.Current.Mode.ToString() : "?") +
            " model=" + (Config.Current != null ? Config.Current.Model.ToString() : "?"));
    }

    public static void Shutdown()
    {
        DebugLog.Write("DlssRuntime.Shutdown");
        NgxHost.Shutdown();
        Jitter.Reset();
        try
        {
            ReleaseOutputDepth();
        }
        catch (Exception e)
        {
            DebugLog.Write("ReleaseOutputDepth during shutdown: " + e);
        }
        try
        {
            ReleaseLdrOutput();
        }
        catch (Exception e)
        {
            DebugLog.Write("ReleaseLdrOutput during shutdown: " + e);
        }
        try
        {
            ReleaseHdrOutput();
        }
        catch (Exception e)
        {
            DebugLog.Write("ReleaseHdrOutput during shutdown: " + e);
        }
        InternalWidth = InternalHeight = OutputWidth = OutputHeight = 0;
        _cachedOutput = default(Vector2I);
        _configChanged = true;
        _resetHistory = true;
        LastEvaluateFailed = false;
        UsedExternalVelocity = false;
        UsedReactiveMask = false;
        EvaluatedThisFrame = false;
        LastEvaluateWasHdr = false;
        EvaluateCount = 0;
        LastBindingEvidence = BindingContext = _lastVelocitySource = null;
        _evaluateAttempt = _renderFrame = 0;
        _evaluatedWidth = _evaluatedHeight = 0;
        _consecutiveEvaluateFails = 0;
        _pluginsReady = false;
        AnomalyHook.Reset();
#if DEBUG
        _lastPrepareLog = null;
#endif
    }

    public static void ApplyInternalResolution()
    {
        var target = DesiredInternalResolution();
        if (target.X <= 0 || target.Y <= 0)
            return;
        if (MyRender11.ResolutionI == target)
            return;

        // Keen's SetDRS resizes GBuffer/HBAO without using the console DRS Present path.
        DisableConsoleDrs();
        DebugLog.Write("SetDRS internal " + MyRender11.ResolutionI + " -> " + target);
        MyRender11.SetDRS(target);
        PinViewportToInternal();
    }

    public static void RestoreOutputResolution()
    {
        DisableConsoleDrs();
        var output = OutputResolution();
        if (output.X <= 0 || output.Y <= 0)
            return;
        if (MyRender11.ResolutionI != output)
        {
            DebugLog.Write("SetDRS output " + MyRender11.ResolutionI + " -> " + output);
            MyRender11.SetDRS(output);
        }
        RestoreViewportToOutput();
    }

    public static void DisableConsoleDrs()
    {
        var settings = MyRender11.Settings;
        if (settings.User.DRScaling)
        {
            var user = settings.User;
            user.DRScaling = false;
            settings.User = user;
            MyRender11.Settings = settings;
        }
        if (MyRender11.DebugOverrides.EnableDRS)
            MyRender11.DebugOverrides.EnableDRS = false;
    }

    public static void PinViewportToInternal()
    {
        var size = InternalWidth > 0 && InternalHeight > 0
            ? new Vector2I(InternalWidth, InternalHeight)
            : MyRender11.ResolutionI;
        if (size is { X: > 0, Y: > 0 })
            MyRender11.ViewportResolution = size;
    }

    public static void RestoreViewportToOutput()
    {
        var output = OutputResolution();
        if (output is { X: > 0, Y: > 0 })
            MyRender11.ViewportResolution = output;
    }

    public static void ApplyOutputSpace()
    {
        var output = OutputResolution();
        if (output.X <= 0 || output.Y <= 0)
            return;
        MyRender11.ViewportResolution = output;
        var data = MyCommon.FrameConstantsData;
        if ((int)data.Screen.Resolution.X == output.X && (int)data.Screen.Resolution.Y == output.Y)
            return;
        data.Screen.Resolution = new Vector2(output.X, output.Y);
        MyCommon.FrameConstantsData = data;
        var mapping = MyMapping.MapDiscard(MyCommon.FrameConstants);
        try
        {
            mapping.WriteAndPosition(ref MyCommon.FrameConstantsData);
        }
        finally
        {
            mapping.Unmap();
        }
    }

    public static bool SettingsMatchOutput(int width, int height)
    {
        var output = OutputResolution();
        return width == output.X && height == output.Y && output.X > 0;
    }

    public static bool SwapchainMatchesOutput()
    {
        var output = OutputResolution();
        var dxgi = SwapchainBufferSize();
        return output.X > 0 && dxgi.X == output.X && dxgi.Y == output.Y;
    }

    public static Vector2I OutputPixelSize()
    {
        return OutputResolution();
    }

    public static void SnapshotOutputSize()
    {
        RememberNativeOutput();
    }

    // Backbuffer.Size follows internal ResolutionI after SetDRS; HUD targets need the DXGI size.
    public static bool TryGetHudTargetSize(IRtvBindable target, out Vector2I size)
    {
        size = OutputPixelSize();
        if (target == null || size.X <= 0 || size.Y <= 0)
            return false;
        if (ReferenceEquals(target, MyRender11.Backbuffer))
            return true;
        return target.Size.X == size.X && target.Size.Y == size.Y;
    }

    public static void BeginFrameResources()
    {
        _outputDepthReady = false;
        UsedReactiveMask = false;
        _renderFrame++;
        AnomalyHook.BeginFrame();
        LastEvaluateWasHdr = false;
    }

    public static void ReleaseOutputDepth()
    {
        OutputDepthThisFrame?.Release();
        OutputDepthThisFrame = null;
        _outputDepthReady = false;
    }

    public static IBorrowedCustomTexture AcquireLdrOutput()
    {
        var output = OutputResolution();
        if (output.X <= 0 || output.Y <= 0)
            return null;
        if (_ldrOutput != null && _ldrOutput.Size.X == output.X && _ldrOutput.Size.Y == output.Y)
            return _ldrOutput;

        ReleaseLdrOutput();
        _ldrTexture = MyManagers.CustomTextures.CreateTexture("DLSS.LdrUpscale", output.X, output.Y);
        if (_ldrTexture == null)
            return null;
        _ldrOutput = new PersistentLdrTarget(_ldrTexture);
        DebugLog.Write("LDR output " + output.X + "x" + output.Y);
        return _ldrOutput;
    }

    public static void ReleaseLdrOutput()
    {
        _ldrOutput = null;
        if (_ldrTexture != null)
            MyManagers.CustomTextures.DisposeTex(ref _ldrTexture);
    }

    public static IBorrowedCustomTexture AcquireHdrOutput()
    {
        var output = OutputResolution();
        if (output.X <= 0 || output.Y <= 0)
            return null;
        if (_hdrOutput != null && _hdrOutput.Size.X == output.X && _hdrOutput.Size.Y == output.Y)
            return _hdrOutput;

        ReleaseHdrOutput();
        _hdrTexture = MyManagers.CustomTextures.CreateTexture("DLSS.HdrUpscale", output.X, output.Y);
        if (_hdrTexture == null)
            return null;
        _hdrOutput = new PersistentLdrTarget(_hdrTexture);
        DebugLog.Write("HDR output " + output.X + "x" + output.Y + " fmt=" + _hdrOutput.Format);
        return _hdrOutput;
    }

    public static void ReleaseHdrOutput()
    {
        _hdrOutput = null;
        if (_hdrTexture != null)
            MyManagers.CustomTextures.DisposeTex(ref _hdrTexture);
    }

    /// <summary>
    /// Pre-tonemap <c>hdrColor</c> → output-sized dest, then
    /// <c>NotifyUpscaleComplete(rc, dest)</c>. Skips HdrRender's scRGB
    /// <c>MyToneMapping.Run</c> result.
    /// </summary>
    public static bool TryEvaluateHdrDisplay()
    {
        if (!IsLive || EvaluatedThisFrame)
            return false;

        ISrvBindable source = null;
        if (!AnomalyHook.TryGetHdrColor(InternalWidth, InternalHeight, out source))
            source = MyGBuffer.Main?.LBuffer;
        if (source == null)
        {
            DebugLog.Write("HDR evaluate missing hdrColor/LBuffer");
            return false;
        }

        var dest = AcquireHdrOutput();
        if (dest == null)
            return false;

        if (!TryEvaluate(dest, source))
        {
            DebugLog.Write("ToneMapping HDR evaluate failed src=" + source.Size + " dest=" + dest.Size);
            return false;
        }

        EvaluatedThisFrame = true;
        LastEvaluateWasHdr = true;
        ApplyOutputSpace();
        try
        {
            AnomalyHook.NotifyUpscaleComplete(MyRender11.RC, dest);
        }
        finally
        {
            MyRender11.RC?.ClearState();
        }

        DebugLog.WriteFrame("ToneMapping HDR evaluate src=" + source.Size + " dest=" + dest.Size);
        return true;
    }

    public static IBorrowedDepthStencilTexture TryAcquireOutputDepth(IDepthStencil source, Vector2I size)
    {
        if (source == null || size.X <= 0 || size.Y <= 0)
            return null;

        var sizeOk = OutputDepthThisFrame != null &&
            OutputDepthThisFrame.Size.X == size.X &&
            OutputDepthThisFrame.Size.Y == size.Y;
        if (sizeOk && _outputDepthReady)
            return OutputDepthThisFrame;

        if (!sizeOk)
        {
            ReleaseOutputDepth();
            var dest = MyManagers.RwTexturesPool.BorrowDepthStencil(
                "DLSS.LdrDepth", size.X, size.Y, IsHqDepth(source));
            if (dest == null || dest.Resource == null)
                return null;
            OutputDepthThisFrame = dest;
        }

        var rc = MyRender11.RC;
        var device = MyRender11.DeviceInstance;
        if (rc?.DeviceContext == null || device == null || source.Resource == null ||
            OutputDepthThisFrame.Resource == null)
        {
            ReleaseOutputDepth();
            return null;
        }

        bool upsampled;
        rc.ClearState();
        try
        {
            upsampled = NgxHost.TryUpsampleDepth(
                device,
                rc.DeviceContext,
                source.Resource,
                OutputDepthThisFrame.Resource);
        }
        finally
        {
            rc.ClearState();
        }

        if (!upsampled)
        {
            ReleaseOutputDepth();
            return null;
        }

        _outputDepthReady = true;
        return OutputDepthThisFrame;
    }

    private static bool IsHqDepth(IDepthStencil source)
    {
        if (source.Resource is not Texture2D tex)
            return true;
        var format = tex.Description.Format;
        return format == SharpDX.DXGI.Format.R32G8X24_Typeless ||
               format == SharpDX.DXGI.Format.D32_Float_S8X24_UInt;
    }

    public static bool TryPrepareFrame()
    {
        if (!_pluginsReady)
            return false;
        if (!WantsDlss)
        {
            if (Config.Current != null && Config.Current.AntiAliasing == AntiAliasingChoice.DLSS)
            {
                if (GpuSupport.Probed && !GpuSupport.IsNvidia)
                    NgxHost.LastError = GpuSupport.UnsupportedReason;
                else if (NgxHost.SupportKnown && !NgxHost.IsSupported && string.IsNullOrEmpty(NgxHost.LastError))
                    NgxHost.LastError = "NGX reports Super Sampling is not available on this GPU";
            }
            else if (!NgxHost.IsLoaded)
                NgxHost.LastError = "DLSS is not the selected anti-aliasing mode";
            return false;
        }
        DisableConsoleDrs();
        if (MyRender11.MultisamplingEnabled)
        {
            NgxHost.LastError = "DLSS cannot run while MSAA is enabled. Set anti-aliasing to Off, FXAA, or DLSS.";
            return false;
        }

        var device = MyRender11.DeviceInstance;
        if (device == null)
        {
            NgxHost.LastError = "D3D11 device is not ready";
            return false;
        }

        try
        {
            if (!NgxHost.IsLoaded && !NgxHost.TryInit(device, MyFileLogPath()))
                return false;
        }
        catch (Exception e)
        {
            NgxHost.LastError = "NGX init threw: " + e.GetType().Name + ": " + e.Message;
            MyLog.Default.Error("DLSS: " + NgxHost.LastError);
            DebugLog.Write(NgxHost.LastError);
            return false;
        }
        if (!NgxHost.IsSupported)
            return false;

        RememberNativeOutput();
        var output = OutputResolution();
        OutputWidth = output.X;
        OutputHeight = output.Y;
        if (OutputWidth <= 0 || OutputHeight <= 0)
            return false;

        if (!NgxHost.TrySetMode(
                Config.Current.Mode,
                (uint)OutputWidth,
                (uint)OutputHeight,
                out var renderW,
                out var renderH))
        {
            var scale = NgxHost.FallbackScale(Config.Current.Mode);
            renderW = (uint)Math.Max(1, MathHelper.RoundToInt(OutputWidth * scale));
            renderH = (uint)Math.Max(1, MathHelper.RoundToInt(OutputHeight * scale));
        }

        InternalWidth = (int)renderW;
        InternalHeight = (int)renderH;
#if DEBUG
        var prepare = "TryPrepareFrame live=" + IsLive + " ready=" + NgxHost.IsReady +
                      " " + InternalWidth + "x" + InternalHeight + " -> " + OutputWidth + "x" + OutputHeight +
                      " " + (NgxHost.LastError ?? "");
        if (_lastPrepareLog != prepare)
        {
            _lastPrepareLog = prepare;
            DebugLog.Write(prepare);
        }
#endif
        return NgxHost.IsReady;
    }

    public static Vector2I DesiredInternalResolution()
    {
        if (InternalWidth > 0 && InternalHeight > 0)
            return new Vector2I(InternalWidth, InternalHeight);
        var output = OutputResolution();
        var scale = NgxHost.FallbackScale(Config.Current.Mode);
        return new Vector2I(
            Math.Max(1, MathHelper.RoundToInt(output.X * scale)),
            Math.Max(1, MathHelper.RoundToInt(output.Y * scale)));
    }

    public static Vector2I OutputResolution()
    {
        // Backbuffer.Size is internal after SetDRS; DXGI and device settings retain the output size.
        if (_cachedOutput is { X: > 0, Y: > 0 })
            return _cachedOutput;
        RememberNativeOutput();
        if (_cachedOutput is { X: > 0, Y: > 0 })
            return _cachedOutput;
        if (MyRender11.m_swapchain is { } swap)
        {
            var mode = swap.Description.ModeDescription;
            if (mode is { Width: > 0, Height: > 0 })
                return new Vector2I(mode.Width, mode.Height);
        }
        var settings = MyRender11.DeviceSettings;
        if (TryNativeSize(settings.BackBufferWidth, settings.BackBufferHeight, out var candidate))
            return candidate;
        return MyRender11.ViewportResolution;
    }

    public static Vector2I SwapchainBufferSize()
    {
        try
        {
            if (MyRender11.Backbuffer?.Resource is Texture2D tex)
            {
                var desc = tex.Description;
                if (desc is { Width: > 0, Height: > 0 })
                    return new Vector2I(desc.Width, desc.Height);
            }
        }
        catch (Exception e)
        {
            DebugLog.WriteFrame("Swapchain buffer query failed: " + e.GetType().Name + ": " + e.Message);
        }
        return default(Vector2I);
    }

    private static void RememberNativeOutput()
    {
        var dxgi = SwapchainBufferSize();
        if (dxgi is { X: > 0, Y: > 0 })
            _cachedOutput = dxgi;
    }

    private static bool TryNativeSize(int width, int height, out Vector2I native)
    {
        native = default(Vector2I);
        if (width <= 0 || height <= 0)
            return false;
        if (InternalWidth > 0 && width == InternalWidth && height == InternalHeight)
            return false;
        native = new Vector2I(width, height);
        return true;
    }

    public static bool TryEvaluate(IResource destination, ISrvBindable source)
    {
        if (!IsLive || _consecutiveEvaluateFails >= 3)
        {
            DebugLog.WriteFrame("TryEvaluate skipped live=" + IsLive + " fails=" + _consecutiveEvaluateFails);
            return false;
        }
        LastEvaluateFailed = false;

        var gbuffer = MyGBuffer.Main;
        if (gbuffer == null || gbuffer.ResolvedDepthStencil == null || destination == null || source == null)
        {
            DebugLog.Write("TryEvaluate missing gbuffer/depth/source/dest");
            return false;
        }

        var rc = MyRender11.RC;
        var device = MyRender11.DeviceInstance;
        if (rc == null || device == null || rc.DeviceContext == null)
            return false;

        var depth = gbuffer.ResolvedDepthStencil.Resource;
        var color = source.Resource;
        var output = destination.Resource;
        if (depth == null || color == null || output == null)
            return false;

        try
        {
            var mvec = IntPtr.Zero;
            var allowAnomaly = Config.Current?.UseAnomalyMotionVectors ?? true;
            var externalMv = IntPtr.Zero;
            var externalHistory = false;
            var usedExternal = allowAnomaly && AnomalyHook.TryGetLive(
                InternalWidth, InternalHeight, out externalMv, out externalHistory);
            var rejection = allowAnomaly ? AnomalyHook.SelectionReason : "integration disabled";
            var textureEvidence = "";
            if (usedExternal && !DlssD3d.ValidateVelocity(device, externalMv, InternalWidth, InternalHeight,
                    out textureEvidence))
            {
                usedExternal = false;
                rejection = "incompatible texture/device: " + textureEvidence;
            }
            var selectedSource = usedExternal ? "Anomaly/" + AnomalyHook.SelectedSource : "camera";
            var historyValid = Jitter.HasPrevious;
            if (usedExternal)
            {
                mvec = externalMv;
                historyValid = externalHistory;
            }
            else
            {
                if (allowAnomaly)
                    AnomalyHook.NoteCameraFallback();
                if (Jitter.HasPrevious)
                {
                    Jitter.CopyToArray(Jitter.JitteredInvViewProjection, InvViewProj);
                    Jitter.CopyToArray(Jitter.UnjitteredViewProjection, UnjitteredViewProj);
                    Jitter.CopyToArray(Jitter.PreviousViewProjection, PrevViewProj);
                    mvec = NgxHost.GenerateCameraMotionVectors(
                        device,
                        rc.DeviceContext,
                        depth,
                        (uint)InternalWidth,
                        (uint)InternalHeight,
                        InvViewProj,
                        UnjitteredViewProj,
                        PrevViewProj);
                }
            }

            var reactive = IntPtr.Zero;
            var usedReactive = AnomalyHook.TryGetReactiveMask(InternalWidth, InternalHeight, out reactive);
            UsedReactiveMask = usedReactive;

            var sourceChanged = selectedSource != _lastVelocitySource;
            _lastVelocitySource = selectedSource;
            UsedExternalVelocity = usedExternal;
            var cameraCut = Jitter.ConsumeCameraCut();
            if (cameraCut)
                AnomalyHook.InvalidateHistory();
            var motionVectorsFailed = !usedExternal && Jitter.HasPrevious && mvec == IntPtr.Zero;
            var resetReason = VelocityAcceptance.ResetReason(_resetHistory, _configChanged, historyValid,
                motionVectorsFailed, sourceChanged, cameraCut);
            if (_evaluatedWidth != InternalWidth || _evaluatedHeight != InternalHeight)
                resetReason = resetReason == "none" ? "resolution-change" : resetReason + ",resolution-change";
            _evaluatedWidth = InternalWidth;
            _evaluatedHeight = InternalHeight;
            var reset = resetReason == "none" ? 0 : 1;
            BindingContext = "Evaluate #" + (++_evaluateAttempt) + " frame=" + _renderFrame + " source=" + selectedSource +
                " fallback=" + (usedExternal ? "none" : rejection) + " reset=" + resetReason +
                " producerTexture=" + (usedExternal ? textureEvidence : "local") +
                " producerFrame=unavailable";
            LastBindingEvidence = BindingContext + " pending NGX binding";
            _configChanged = false;
            _resetHistory = false;

            var ok = NgxHost.Evaluate(
                device,
                rc.DeviceContext,
                color,
                depth,
                mvec,
                output,
                Jitter.OffsetX,
                Jitter.OffsetY,
                reset,
                Config.Current.Sharpness,
                (uint)InternalWidth,
                (uint)InternalHeight,
                usedReactive ? reactive : IntPtr.Zero);
            if (!ok)
            {
                _resetHistory = true;
                LastEvaluateFailed = true;
                _consecutiveEvaluateFails++;
                MyLog.Default.Warning("DLSS evaluate failed: " + NgxHost.LastError);
                DebugLog.Write("TryEvaluate fail #" + _consecutiveEvaluateFails +
                               " dest=" + destination.Size + " src=" + source.Size + " " + NgxHost.LastError);
                if (_consecutiveEvaluateFails >= 3)
                    MyLog.Default.Warning("DLSS: stopping evaluate until anti-aliasing settings change");
            }
            else
            {
                _consecutiveEvaluateFails = 0;
                EvaluateCount++;
                DebugLog.WriteFrame("TryEvaluate ok dest=" + destination.Size.X + "x" + destination.Size.Y +
                                    " src=" + source.Size.X + "x" + source.Size.Y +
                                    " reset=" + reset +
                                    " mv=" + (usedExternal ? "anomaly" : mvec != IntPtr.Zero ? "camera" : "none") +
                                    " reactive=" + (usedReactive ? "anomaly" : "none"));
            }

            return ok;
        }
        catch (Exception e)
        {
            _resetHistory = true;
            LastEvaluateFailed = true;
            _consecutiveEvaluateFails = 3;
            NgxHost.LastError = e.GetType().Name + ": " + e.Message;
            MyLog.Default.Error("DLSS evaluate threw: " + e);
            DebugLog.Write("TryEvaluate threw " + e);
            return false;
        }
        finally
        {
            // Native passes bypass Keen's D3D11 state cache.
            rc.ClearState();
        }
    }

    private static string MyFileLogPath()
    {
        try
        {
            return VRage.FileSystem.MyFileSystem.UserDataPath;
        }
        catch (Exception e)
        {
            DebugLog.Write("User-data path lookup failed: " + e.GetType().Name + ": " + e.Message);
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }
}
