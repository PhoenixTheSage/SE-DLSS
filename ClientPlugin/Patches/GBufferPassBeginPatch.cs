using ClientPlugin.Dlss;
using HarmonyLib;
using VRageRender;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyRenderingPass), "Begin")]
internal static class GBufferPassBeginPatch
{
    [HarmonyPrefix]
    private static void Prefix(MyRenderingPass __instance)
    {
        if (!DlssRuntime.IsLive || !(__instance is MyGBufferPass))
            return;
        var env = MyRender11.Environment;
        if (env == null)
            return;
        __instance.ViewProjection = env.Matrices.ViewProjectionAt0;
        __instance.Projection = env.Matrices.Projection;
    }
}
