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

    [HarmonyPostfix]
    private static void Postfix(IRtvBindable destination)
    {
        if (_passthrough)
            return;
        if (!DlssRuntime.IsLive || destination == null)
            return;
        if (!ReferenceEquals(destination, MyRender11.Backbuffer))
            return;
        BillboardOutputPass.TryDrawAfterSceneBlit();
    }
}
