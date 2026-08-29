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
        DlssRuntime.DisableConsoleDrs();
        DlssRuntime.EvaluatedHdrThisFrame = false;
        if (DlssRuntime.WantsDlss)
        {
            DlssRuntime.TryPrepareFrame();
            if (DlssRuntime.IsLive)
                Jitter.BeginFrame();
        }
    }
}
