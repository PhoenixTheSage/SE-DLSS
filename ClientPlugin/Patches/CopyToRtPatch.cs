using System;
using ClientPlugin.Dlss;
using HarmonyLib;
using VRage.Render11.Resources;
using VRageRender;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyCopyToRT), nameof(MyCopyToRT.Run))]
internal static class CopyToRtPatch
{
    [ThreadStatic]
    private static bool _passthrough;

    [HarmonyPrefix]
    private static bool Prefix(IRtvBindable destination, ISrvBindable source)
    {
        if (_passthrough)
            return true;
        // HDR evaluate dest is still a UAV. Drawing PostPP on it is dropped,
        // and treating that as success skipped the swapchain pass. Keen
        // copies the scene; HUD composites after DrawScene jitter restore.
        if (DlssRuntime.ShouldYieldPresentPath)
            return true;
        if (!DlssRuntime.IsLive || destination == null || source == null)
            return true;
        if (!ReferenceEquals(destination, MyRender11.Backbuffer))
            return true;

        var output = DlssRuntime.OutputResolution();
        DebugLog.WriteFrame(
            (DlssRuntime.EvaluatedThisFrame ? "CopyToRT blit after evaluate " : "CopyToRT blit ") +
            source.Size + " -> " + output);

        // The swapchain lacks a UAV, and its reported size follows internal ResolutionI.
        _passthrough = true;
        try
        {
            MyCopyToRT.Run(destination, source, false, new MyViewport(output.X, output.Y), true);
        }
        finally
        {
            _passthrough = false;
            DlssRuntime.RestoreViewportToOutput();
        }
        return false;
    }
}
