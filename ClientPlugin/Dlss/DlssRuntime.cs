using System;
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
    public static bool EvaluatedHdrThisFrame { get; set; }

    private static bool configChanged = true;
    private static bool resetHistory = true;
    private static int consecutiveEvaluateFails;
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
    }

    public static void Shutdown()
    {
        NgxHost.Shutdown();
        Jitter.Reset();
        InternalWidth = InternalHeight = OutputWidth = OutputHeight = 0;
        LastEvaluateFailed = false;
        consecutiveEvaluateFails = 0;
    }

    public static void ApplyInternalResolution()
    {
        DisableConsoleDrs();
        var target = DesiredInternalResolution();
        if (target.X <= 0 || target.Y <= 0)
            return;
        if (MyRender11.ResolutionI == target)
            return;

        // SetDRS is Keen's GBuffer/HBAO resize (screenshots use it too). It is not the
        // console DRS feature: it does not touch DRScaling, Present, or PSNative.dll.
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
            MyRender11.SetDRS(output);
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

    public static bool TryPrepareFrame()
    {
        if (!WantsDlss)
        {
            if (!NgxHost.IsLoaded)
                NgxHost.LastError = "DLSS is not the selected anti-aliasing mode";
            return false;
        }
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
        // BackBufferResolution and MyBackbuffer.Size both alias m_resolution, so after
        // SetDRS they report the internal size, not the DXGI swapchain.
        var settings = MyRender11.DeviceSettings;
        if (settings.BackBufferWidth > 0 && settings.BackBufferHeight > 0)
            return new Vector2I(settings.BackBufferWidth, settings.BackBufferHeight);
        var swap = MyRender11.m_swapchain;
        if (swap != null)
        {
            var mode = swap.Description.ModeDescription;
            if (mode.Width > 0 && mode.Height > 0)
                return new Vector2I(mode.Width, mode.Height);
        }
        return MyRender11.ViewportResolution;
    }

    public static bool TryEvaluate(IRtvBindable destination, ISrvBindable source)
    {
        if (!IsLive || consecutiveEvaluateFails >= 3)
            return false;
        LastEvaluateFailed = false;

        var gbuffer = MyGBuffer.Main;
        if (gbuffer == null || gbuffer.ResolvedDepthStencil == null || destination == null || source == null)
            return false;

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

            int reset = resetHistory || configChanged || !Jitter.HasPrevious ? 1 : 0;
            configChanged = false;
            resetHistory = false;

            bool ok = NgxHost.Evaluate(
                rc.DeviceContext.NativePointer,
                color.NativePointer,
                depth.NativePointer,
                mvec,
                output.NativePointer,
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
                if (consecutiveEvaluateFails >= 3)
                    MyLog.Default.Warning("DLSS: stopping evaluate until anti-aliasing settings change");
            }
            else
            {
                consecutiveEvaluateFails = 0;
            }

            return ok;
        }
        catch (Exception e)
        {
            LastEvaluateFailed = true;
            consecutiveEvaluateFails = 3;
            NgxHost.LastError = e.GetType().Name + ": " + e.Message;
            MyLog.Default.Error("DLSS evaluate threw: " + e);
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
