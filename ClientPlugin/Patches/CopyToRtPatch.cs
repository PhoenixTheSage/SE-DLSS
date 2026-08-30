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
    private static bool passthrough;

    [HarmonyPrefix]
    private static bool Prefix(IRtvBindable destination, ISrvBindable source)
    {
        if (passthrough)
            return true;
        if (!DlssRuntime.IsLive || destination == null || source == null)
            return true;
        if (!ReferenceEquals(destination, MyRender11.Backbuffer))
            return true;

        var output = DlssRuntime.OutputResolution();
        DebugLog.WriteFrame(
            (DlssRuntime.EvaluatedHdrThisFrame ? "CopyToRT blit after HDR " : "CopyToRT blit ") +
            source.Size + " -> " + output);

        // Never evaluate into the DXGI swapchain. It is RT+SRV only (NGX copyBack),
        // and Backbuffer.Size aliases internal ResolutionI after SetDRS.
        passthrough = true;
        try
        {
            MyCopyToRT.Run(destination, source, false, new MyViewport(output.X, output.Y), true);
        }
        finally
        {
            passthrough = false;
            DlssRuntime.RestoreViewportToOutput();
        }
        return false;
    }

    [HarmonyPostfix]
    private static void Postfix(IRtvBindable destination)
    {
        if (passthrough)
            return;
        if (!DlssRuntime.IsLive || destination == null)
            return;
        if (!ReferenceEquals(destination, MyRender11.Backbuffer))
            return;
        BillboardOutputPass.TryDrawAfterSceneBlit();
    }
}
