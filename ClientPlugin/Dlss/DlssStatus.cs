using System.Text;

namespace ClientPlugin.Dlss;

public static class DlssStatus
{
    public static string CurrentText
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine(NgxHost.LastError ?? "not initialized");
            sb.AppendLine();
            sb.Append("GPU: ").AppendLine(GpuSupport.StatusLine);
            sb.Append("DLSS eligible: ").AppendLine(GpuSupport.CanOfferDlss ? "yes" : "no");
            sb.Append("Anti-aliasing: ").Append(Config.Current.AntiAliasing).AppendLine();
            sb.Append("NGX loaded: ").AppendLine(NgxHost.IsLoaded ? "yes" : "no");
            sb.Append("DLSS supported: ").AppendLine(NgxHost.IsSupported ? "yes" : "no");
            sb.Append("Feature ready: ").AppendLine(NgxHost.IsReady ? "yes" : "no");
            sb.Append("Mode: ").Append(Config.Current.Mode).AppendLine();
            sb.Append("Model: ")
                .Append(Config.Current.Model)
                .Append(" (NGX preset ")
                .Append(NgxHost.CurrentPresetHint)
                .AppendLine(")");
            sb.Append("Internal resolution: ")
                .Append(DlssRuntime.InternalWidth)
                .Append(" x ")
                .Append(DlssRuntime.InternalHeight)
                .AppendLine();
            sb.Append("Output resolution: ")
                .Append(DlssRuntime.OutputWidth)
                .Append(" x ")
                .Append(DlssRuntime.OutputHeight)
                .AppendLine();
            sb.Append("LDR evaluate this frame: ").AppendLine(DlssRuntime.EvaluatedThisFrame ? "yes" : "no");
            AnomalyVelocity.AppendStatus(sb);
            sb.Append("Search paths: ").AppendLine(NgxHost.SearchPathSummary());
            sb.AppendLine("NGX ABI: oleaut-6");
#if DEBUG
            if (!string.IsNullOrEmpty(DebugLog.FilePath))
                sb.Append("Debug log: ").AppendLine(DebugLog.FilePath);
#endif
            if (NgxLog.HasMessages)
                sb.Append("NGX log: ").AppendLine(NgxLog.LastLine);
            if (DlssRuntime.LastEvaluateFailed)
                sb.AppendLine("Last evaluate failed; falling back to a stretch blit.");
            return sb.ToString();
        }
    }
}
