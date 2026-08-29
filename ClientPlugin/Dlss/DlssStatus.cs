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
            sb.Append("Anti-aliasing: ").AppendLine(Config.Current.AntiAliasing.ToString());
            sb.Append("NGX loaded: ").AppendLine(NgxHost.IsLoaded ? "yes" : "no");
            sb.Append("DLSS supported: ").AppendLine(NgxHost.IsSupported ? "yes" : "no");
            sb.Append("Feature ready: ").AppendLine(NgxHost.IsReady ? "yes" : "no");
            sb.Append("Mode: ").AppendLine(Config.Current.Mode.ToString());
            sb.Append("Model: ").AppendLine(Config.Current.Model.ToString());
            sb.Append("Internal resolution: ").Append(DlssRuntime.InternalWidth).Append(" x ").AppendLine(DlssRuntime.InternalHeight.ToString());
            sb.Append("Output resolution: ").Append(DlssRuntime.OutputWidth).Append(" x ").AppendLine(DlssRuntime.OutputHeight.ToString());
            sb.Append("HDR evaluate: ").AppendLine(DlssRuntime.EvaluatedHdrThisFrame ? "yes" : "no");
            sb.Append("Search paths: ").AppendLine(NgxHost.SearchPathSummary());
            if (DlssRuntime.LastEvaluateFailed)
                sb.AppendLine("Last evaluate failed; falling back to a stretch blit.");
            return sb.ToString();
        }
    }
}
