using ClientPlugin.Dlss;
using HarmonyLib;
using VRage.Render11.Resources;
using VRageRender;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyBorrowedRwTextureManager), nameof(MyBorrowedRwTextureManager.BorrowCustom),
    new[] { typeof(string), typeof(int), typeof(int) })]
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

        // Name-only BorrowCustom uses ResolutionI (internal after SetDRS) and
        // would downsample the 1080 HDR result before CopyToRT.
        DebugLog.WriteFrame("BorrowCustom " + debugName + " at output " + output);
        __result = __instance.BorrowCustom(debugName, output.X, output.Y, samplesCount, samplesQuality);
        return false;
    }
}
