using ClientPlugin.Dlss;
using HarmonyLib;
using VRage.Render11.Resources;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyChromaticAberration), nameof(MyChromaticAberration.Run))]
internal static class ChromaticAberrationPatch
{
    [HarmonyPrefix]
    private static void Prefix(IUavBindable dst)
    {
        if (!DlssRuntime.IsLive || dst == null)
            return;

        var size = dst.Size;
        if (size.X <= 0 || size.Y <= 0)
            return;
        if ((int)MyCommon.FrameConstantsData.Screen.Resolution.X == size.X &&
            (int)MyCommon.FrameConstantsData.Screen.Resolution.Y == size.Y)
            return;

        // ChromaticAberration.hlsl builds UVs from frame_.Screen.resolution.
        var data = MyCommon.FrameConstantsData;
        data.Screen.Resolution = new Vector2(size.X, size.Y);
        MyCommon.FrameConstantsData = data;
        var mapping = MyMapping.MapDiscard(MyCommon.FrameConstants);
        try
        {
            mapping.WriteAndPosition(ref MyCommon.FrameConstantsData);
        }
        finally
        {
            mapping.Unmap();
        }
    }
}
