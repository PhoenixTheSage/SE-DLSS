using System;
using ClientPlugin.Dlss;
using HarmonyLib;
using VRageRender;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyRender11), "Draw", typeof(bool))]
internal static class DrawPatch
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        try
        {
            GpuSupport.TryProbe();
            DlssRuntime.SnapshotOutputSize();
            DlssRuntime.BeginFrameResources();
            DlssRuntime.EvaluatedThisFrame = false;
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
        catch (Exception e)
        {
            DebugLog.Write("Draw prefix: " + e.GetType().Name + ": " + e.Message);
        }
    }
}
