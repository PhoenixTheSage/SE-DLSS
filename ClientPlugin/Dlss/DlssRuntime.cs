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
    private static bool outputDepthReady;
    private static ICustomTexture ldrTexture;
    private static PersistentLdrTarget ldrOutput;

    private static bool configChanged = true;
    private static bool resetHistory = true;
    private static int consecutiveEvaluateFails;
    private static Vector2I cachedOutput;
#if DEBUG
    private static string lastPrepareLog;
#endif
    private static readonly float[] invViewProj = new float[16];
    private static readonly float[] unjitteredViewProj = new float[16];
    private static readonly float[] prevViewProj = new float[16];

    public static bool WantsDlss
    {
        get
        {
            var config = Config.Current;
            return config != null && config.AntiAliasing == AntiAliasingChoice.DLSS;
        }
    }

    public static bool IsLive => WantsDlss && NgxHost.IsReady && !MyRender11.MultisamplingEnabled;

    public static void NotifyConfigChanged()
    {
        configChanged = true;
        resetHistory = true;
        consecutiveEvaluateFails = 0;
        LastEvaluateFailed = false;
        Jitter.Reset();
        DisableConsoleDrs();
        DebugLog.Write("NotifyConfigChanged aa=" + (Config.Current != null ? Config.Current.AntiAliasing.ToString() : "?") +
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
        cachedOutput = default(Vector2I);
        LastEvaluateFailed = false;
        consecutiveEvaluateFails = 0;
    }

    public static void ApplyInternalResolution()
    {
        var target = DesiredInternalResolution();
        if (target.X <= 0 || target.Y <= 0)
            return;
        if (MyRender11.ResolutionI == target)
            return;

        // SetDRS is Keen's GBuffer/HBAO resize (screenshots use it too). It is not the
        // console DRS feature: it does not touch DRScaling, Present, or PSNative.dll.
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
        if (size.X > 0 && size.Y > 0)
            MyRender11.ViewportResolution = size;
    }

    public static void RestoreViewportToOutput()
    {
        var output = OutputResolution();
        if (output.X > 0 && output.Y > 0)
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
        mapping.WriteAndPosition(ref MyCommon.FrameConstantsData);
        mapping.Unmap();
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

    // Backbuffer.Size aliases internal ResolutionI after SetDRS. HUD must use
    // the DXGI size, and must not composite onto an internal scene RT.
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
        outputDepthReady = false;
    }

    public static void ReleaseOutputDepth()
    {
        OutputDepthThisFrame?.Release();
        OutputDepthThisFrame = null;
        outputDepthReady = false;
    }

    public static IBorrowedCustomTexture AcquireLdrOutput()
    {
        var output = OutputResolution();
        if (output.X <= 0 || output.Y <= 0)
            return null;
        if (ldrOutput != null && ldrOutput.Size.X == output.X && ldrOutput.Size.Y == output.Y)
            return ldrOutput;

        ReleaseLdrOutput();
        ldrTexture = MyManagers.CustomTextures.CreateTexture("DLSS.LdrUpscale", output.X, output.Y, 1, 0);
        if (ldrTexture == null)
            return null;
        ldrOutput = new PersistentLdrTarget(ldrTexture);
        DebugLog.Write("LDR output " + output.X + "x" + output.Y);
        return ldrOutput;
    }

    public static void ReleaseLdrOutput()
    {
        ldrOutput = null;
        if (ldrTexture != null)
            MyManagers.CustomTextures.DisposeTex(ref ldrTexture);
    }

    public static IBorrowedDepthStencilTexture TryAcquireOutputDepth(IDepthStencil source, Vector2I size)
    {
        if (source == null || size.X <= 0 || size.Y <= 0)
            return null;

        bool sizeOk = OutputDepthThisFrame != null &&
            OutputDepthThisFrame.Size.X == size.X &&
            OutputDepthThisFrame.Size.Y == size.Y;
        if (sizeOk && outputDepthReady)
            return OutputDepthThisFrame;

        if (!sizeOk)
        {
            ReleaseOutputDepth();
            var dest = MyManagers.RwTexturesPool.BorrowDepthStencil(
                "DLSS.LdrDepth", size.X, size.Y, IsHqDepth(source), 1, 0);
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

        rc.ClearState();
        if (!NgxHost.TryUpsampleDepth(
                device.NativePointer,
                rc.DeviceContext.NativePointer,
                source.Resource.NativePointer,
                OutputDepthThisFrame.Resource.NativePointer))
        {
            ReleaseOutputDepth();
            return null;
        }

        rc.ClearState();
        outputDepthReady = true;
        return OutputDepthThisFrame;
    }

    private static bool IsHqDepth(IDepthStencil source)
    {
        var tex = source.Resource as Texture2D;
        if (tex == null)
            return true;
        var format = tex.Description.Format;
        return format == SharpDX.DXGI.Format.R32G8X24_Typeless ||
               format == SharpDX.DXGI.Format.D32_Float_S8X24_UInt;
    }

    public static bool TryPrepareFrame()
    {
        if (!WantsDlss)
        {
            if (!NgxHost.IsLoaded)
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

        uint renderW;
        uint renderH;
        if (!NgxHost.TrySetMode(Config.Current.Mode, (uint)OutputWidth, (uint)OutputHeight, out renderW, out renderH))
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
        if (lastPrepareLog != prepare)
        {
            lastPrepareLog = prepare;
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
        // MyBackbuffer.Size aliases m_resolution (internal after SetDRS). The DXGI
        // texture and swapchain mode stay at the real output, including DLAA 1:1.
        if (cachedOutput.X > 0 && cachedOutput.Y > 0)
            return cachedOutput;
        RememberNativeOutput();
        if (cachedOutput.X > 0 && cachedOutput.Y > 0)
            return cachedOutput;
        var swap = MyRender11.m_swapchain;
        if (swap != null)
        {
            var mode = swap.Description.ModeDescription;
            if (mode.Width > 0 && mode.Height > 0)
                return new Vector2I(mode.Width, mode.Height);
        }
        Vector2I candidate;
        var settings = MyRender11.DeviceSettings;
        if (TryNativeSize(settings.BackBufferWidth, settings.BackBufferHeight, out candidate))
            return candidate;
        return MyRender11.ViewportResolution;
    }

    public static Vector2I SwapchainBufferSize()
    {
        try
        {
            var tex = MyRender11.Backbuffer?.Resource as Texture2D;
            if (tex != null)
            {
                var desc = tex.Description;
                if (desc.Width > 0 && desc.Height > 0)
                    return new Vector2I(desc.Width, desc.Height);
            }
        }
        catch
        {
            // ignored
        }
        return default(Vector2I);
    }

    private static void RememberNativeOutput()
    {
        var dxgi = SwapchainBufferSize();
        if (dxgi.X > 0 && dxgi.Y > 0)
            cachedOutput = dxgi;
    }

    private static bool TryNativeSize(Vector2I size, out Vector2I native)
    {
        return TryNativeSize(size.X, size.Y, out native);
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

    public static bool TryEvaluate(IResource destination, ISrvBindable source, ISrvBindable exposure)
    {
        if (!IsLive || consecutiveEvaluateFails >= 3)
        {
            DebugLog.WriteFrame("TryEvaluate skipped live=" + IsLive + " fails=" + consecutiveEvaluateFails);
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
            IntPtr mvec = IntPtr.Zero;
            if (Jitter.HasPrevious)
            {
                Jitter.CopyToArray(Jitter.JitteredInvViewProjection, invViewProj);
                Jitter.CopyToArray(Jitter.UnjitteredViewProjection, unjitteredViewProj);
                Jitter.CopyToArray(Jitter.PreviousViewProjection, prevViewProj);
                mvec = NgxHost.GenerateCameraMotionVectors(
                    device.NativePointer,
                    rc.DeviceContext.NativePointer,
                    depth.NativePointer,
                    (uint)InternalWidth,
                    (uint)InternalHeight,
                    invViewProj,
                    unjitteredViewProj,
                    prevViewProj);
            }

            int reset = resetHistory || configChanged || !Jitter.HasPrevious || Jitter.ConsumeCameraCut() ? 1 : 0;
            configChanged = false;
            resetHistory = false;

            bool ok = NgxHost.Evaluate(
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
                LastEvaluateFailed = true;
                consecutiveEvaluateFails++;
                MyLog.Default.Warning("DLSS evaluate failed: " + NgxHost.LastError);
                DebugLog.Write("TryEvaluate fail #" + consecutiveEvaluateFails +
                               " dest=" + destination.Size + " src=" + source.Size + " " + NgxHost.LastError);
                if (consecutiveEvaluateFails >= 3)
                    MyLog.Default.Warning("DLSS: stopping evaluate until anti-aliasing settings change");
            }
            else
            {
                consecutiveEvaluateFails = 0;
                DebugLog.WriteFrame("TryEvaluate ok dest=" + destination.Size.X + "x" + destination.Size.Y +
                                    " src=" + source.Size.X + "x" + source.Size.Y +
                                    " reset=" + reset + " mv=" + (mvec != IntPtr.Zero));
            }

            return ok;
        }
        catch (Exception e)
        {
            LastEvaluateFailed = true;
            consecutiveEvaluateFails = 3;
            NgxHost.LastError = e.GetType().Name + ": " + e.Message;
            MyLog.Default.Error("DLSS evaluate threw: " + e);
            DebugLog.Write("TryEvaluate threw " + e);
            return false;
        }
        finally
        {
            // NGX and the motion-vector pass write the D3D11 context behind Keen's state cache.
            rc.ClearState();
        }
    }

    private static string MyFileLogPath()
    {
        try
        {
            return VRage.FileSystem.MyFileSystem.UserDataPath;
        }
        catch
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }
}
