using ClientPlugin.Dlss;
using HarmonyLib;
using VRage.Render11.Resources;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyBorrowedRwTextureManager), nameof(MyBorrowedRwTextureManager.BorrowCustom),
    typeof(string), typeof(int), typeof(int))]
internal static class BorrowCustomPatch
{
    [HarmonyPrefix]
    private static bool Prefix(
        MyBorrowedRwTextureManager __instance,
        string debugName,
        int samplesCount,
        int samplesQuality,
        ref IBorrowedCustomTexture __result)
    {
        if (!DlssRuntime.IsLive || !DlssRuntime.EvaluatedThisFrame)
            return true;
        if (debugName != "DrawGameScene.ChromaticAberration" &&
            debugName != "MyRender11.FXAA.Rgb8")
            return true;

        var output = DlssRuntime.OutputResolution();
        if (output.X <= 0 || output.Y <= 0)
            return true;

        // Persist the output-sized dest. DrawGameScene Release()s Chromatic /
        // FXAA every frame; a pooled 5120x1440 fp16 recycle under the GPU
        // hung after the HDR dest was already made persistent.
        __result = DlssRuntime.AcquirePostProcessDest();
        if (__result == null)
            return true;
        DebugLog.WriteFrame("BorrowCustom " + debugName + " at output " + output + " persistent");
        return false;
    }
}
