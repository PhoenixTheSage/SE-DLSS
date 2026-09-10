using System.Globalization;
using System.Text;

namespace ClientPlugin.Dlss;

public static class DlssStatus
{
    public static string CurrentText
    {
        get
        {
            var sb = new StringBuilder();
            AppendGpu(sb);
            sb.AppendLine();
            AppendNgx(sb);
            sb.AppendLine();
            AppendConfig(sb);
            sb.AppendLine();
            AppendEvaluate(sb);
            sb.AppendLine();
            AnomalyHook.AppendStatus(sb);
            sb.AppendLine();
            AppendPaths(sb);
            return sb.ToString();
        }
    }

    static void AppendGpu(StringBuilder sb)
    {
        sb.Append("GPU  ").Append(GpuSupport.StatusLine);
        sb.Append(" · eligible ").AppendLine(Yes(GpuSupport.CanOfferDlss));
    }

    static void AppendNgx(StringBuilder sb)
    {
        sb.Append("NGX  ");
        if (NgxHost.IsReady)
            sb.Append("ready");
        else if (NgxHost.IsLoaded && NgxHost.IsSupported)
            sb.Append("loaded, not ready");
        else if (NgxHost.IsLoaded)
            sb.Append("loaded, unsupported");
        else
            sb.Append("not loaded");
        sb.Append(" · ").Append(NgxHost.FeatureIsHdr ? "IsHDR" : "SDR");
        sb.AppendLine(" · oleaut-6");

        var err = NgxHost.LastError;
        if (!string.IsNullOrEmpty(err) && !(NgxHost.IsReady && err == "not initialized"))
            sb.Append("     ").AppendLine(err);

        if (NgxLog.HasMessages)
            sb.Append("     log ").AppendLine(NgxLog.LastLine);
    }

    static void AppendConfig(StringBuilder sb)
    {
        var cfg = Config.Current;
        sb.Append("AA   ").Append(cfg.AntiAliasing);
        sb.Append(" · ").Append(cfg.Mode);
        sb.Append(" · ").Append(cfg.Model);
        sb.Append(" (preset ").Append(NgxHost.CurrentPresetHint).Append(')');
        sb.Append(" · sharp ").AppendLine(cfg.Sharpness.ToString("0.00", CultureInfo.InvariantCulture));
        sb.Append("Res  ").Append(DlssRuntime.InternalWidth).Append('x').Append(DlssRuntime.InternalHeight);
        sb.Append(" → ").Append(DlssRuntime.OutputWidth).Append('x').Append(DlssRuntime.OutputHeight).AppendLine();
    }

    static void AppendEvaluate(StringBuilder sb)
    {
        sb.Append("Eval ");
        if (DlssRuntime.EvaluateCount <= 0)
            sb.AppendLine("none");
        else
        {
            sb.Append(DlssRuntime.LastEvaluateWasHdr ? "HDR" : "LDR");
            sb.Append(" ×").Append(DlssRuntime.EvaluateCount);
            if (!string.IsNullOrEmpty(DlssRuntime.LastEvaluatePath))
                sb.Append(" · ").Append(DlssRuntime.LastEvaluatePath);
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(DlssRuntime.LastEvaluateColorDesc))
            sb.Append("     color ").AppendLine(DlssRuntime.LastEvaluateColorDesc);
        if (!string.IsNullOrEmpty(DlssRuntime.LastEvaluateDestDesc))
            sb.Append("     dest  ").AppendLine(DlssRuntime.LastEvaluateDestDesc);

        sb.Append("     jitter ")
            .Append(Jitter.OffsetX.ToString("0.###", CultureInfo.InvariantCulture))
            .Append(',')
            .Append(Jitter.OffsetY.ToString("0.###", CultureInfo.InvariantCulture));
        sb.Append(" · swapchain HDR ").Append(Yes(DlssRuntime.IsHdrSwapchainLive));
        sb.Append(" · want HDR ").AppendLine(Yes(DlssRuntime.WantsHdrEvaluate));

        if (DlssRuntime.LastEvaluateFailed)
            sb.AppendLine("     last evaluate failed; falling back to a stretch blit");
    }

    static void AppendPaths(StringBuilder sb)
    {
        sb.AppendLine("Paths");
        NgxHost.AppendSearchPaths(sb, "     ");
#if DEBUG
        if (!string.IsNullOrEmpty(DebugLog.FilePath))
            sb.Append("     debug ").AppendLine(DebugLog.FilePath);
#endif
    }

    static string Yes(bool value) => value ? "yes" : "no";
}
