using ClientPlugin.Dlss;
using HarmonyLib;
using VRageRender;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyRender11), "Draw", new[] { typeof(bool) })]
internal static class DrawPatch
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        DlssRuntime.SnapshotOutputSize();
        DlssRuntime.BeginFrameResources();
        DlssRuntime.EvaluatedHdrThisFrame = false;
        if (DlssRuntime.WantsDlss)
        {
            DlssRuntime.TryPrepareFrame();
            if (DlssRuntime.IsLive)
            {
                Jitter.BeginFrame();
                // Sprites record before DrawScene and must see the swapchain size.
                DlssRuntime.RestoreViewportToOutput();
            }
        }
    }
}
