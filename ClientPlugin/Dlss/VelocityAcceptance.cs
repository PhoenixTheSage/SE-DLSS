using System.Collections.Generic;

namespace ClientPlugin.Dlss;

internal static class VelocityAcceptance
{
    internal static string ResetReason(bool resetHistory, bool configChanged, bool historyValid,
        bool motionFailed, bool sourceChanged, bool cameraCut)
    {
        var reasons = new List<string>();
        if (resetHistory) reasons.Add("history-reset/resize/retry");
        if (configChanged) reasons.Add("configuration");
        if (!historyValid) reasons.Add("invalid-history");
        if (motionFailed) reasons.Add("motion-generation-failed");
        if (sourceChanged) reasons.Add("source-change");
        if (cameraCut) reasons.Add("camera-cut");
        return reasons.Count == 0 ? "none" : string.Join(",", reasons);
    }
}
