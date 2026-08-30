using System;
using SharpDX.Direct3D11;
using VRage.Render11.Common;
using VRage.Render11.Resources;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Dlss;

public static class DlssRuntime
{
    public static int InternalWidth { get; private set; }
    public static int InternalHeight { get; private set; }
    public static int OutputWidth { get; private set; }
    public static int OutputHeight { get; private set; }
    public static bool LastEvaluateFailed { get; private set; }
    public static bool EvaluatedThisFrame { get; set; }
    public static IBorrowedDepthStencilTexture OutputDepthThisFrame { get; private set; }
    private static bool _outputDepthReady;
    private static ICustomTexture _ldrTexture;
    private static PersistentLdrTarget _ldrOutput;

    private static bool _configChanged = true;
    private static bool _resetHistory = true;
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

    public static void NotifyConfigChanged()
    {
        _configChanged = true;
        _resetHistory = true;
        _consecutiveEvaluateFails = 0;
        LastEvaluateFailed = false;
        Jitter.Reset();
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
        ReleaseOutputDepth();
        ReleaseLdrOutput();
        InternalWidth = InternalHeight = OutputWidth = OutputHeight = 0;
        _cachedOutput = default(Vector2I);
        _configChanged = true;
        _resetHistory = true;
        LastEvaluateFailed = false;
        EvaluatedThisFrame = false;
        _consecutiveEvaluateFails = 0;
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
                device.NativePointer,
                rc.DeviceContext.NativePointer,
                source.Resource.NativePointer,
                OutputDepthThisFrame.Resource.NativePointer);
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

        if (!NgxHost.IsLoaded && !NgxHost.TryInit(device.NativePointer, MyFileLogPath()))
            return false;
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
            if (Jitter.HasPrevious)
            {
                Jitter.CopyToArray(Jitter.JitteredInvViewProjection, InvViewProj);
                Jitter.CopyToArray(Jitter.UnjitteredViewProjection, UnjitteredViewProj);
                Jitter.CopyToArray(Jitter.PreviousViewProjection, PrevViewProj);
                mvec = NgxHost.GenerateCameraMotionVectors(
                    device.NativePointer,
                    rc.DeviceContext.NativePointer,
                    depth.NativePointer,
                    (uint)InternalWidth,
                    (uint)InternalHeight,
                    InvViewProj,
                    UnjitteredViewProj,
                    PrevViewProj);
            }

            var motionVectorsFailed = Jitter.HasPrevious && mvec == IntPtr.Zero;
            var reset = _resetHistory || _configChanged || !Jitter.HasPrevious || motionVectorsFailed ||
                        Jitter.ConsumeCameraCut()
                ? 1
                : 0;
            _configChanged = false;
            _resetHistory = false;

            var ok = NgxHost.Evaluate(
                rc.DeviceContext.NativePointer,
                color.NativePointer,
                depth.NativePointer,
                mvec,
                output.NativePointer,
                IntPtr.Zero,
                Jitter.OffsetX,
                Jitter.OffsetY,
                reset,
                Config.Current.Sharpness,
                (uint)InternalWidth,
                (uint)InternalHeight);
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
                DebugLog.WriteFrame("TryEvaluate ok dest=" + destination.Size.X + "x" + destination.Size.Y +
                                    " src=" + source.Size.X + "x" + source.Size.Y +
                                    " reset=" + reset + " mv=" + (mvec != IntPtr.Zero));
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
