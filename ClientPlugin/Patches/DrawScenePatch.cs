using ClientPlugin.Dlss;
using HarmonyLib;
using VRageRender;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyRender11), nameof(MyRender11.DrawScene))]
internal static class DrawScenePatch
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        if (!DlssRuntime.WantsDlss || !DlssRuntime.TryPrepareFrame())
        {
            DlssRuntime.RestoreOutputResolution();
            return;
        }

        BillboardOutputPass.BeginDraw();
        DebugLog.WriteFrame("DrawScene internal " + DlssRuntime.InternalWidth + "x" + DlssRuntime.InternalHeight);
        DlssRuntime.ApplyInternalResolution();
        DlssRuntime.PinViewportToInternal();
    }

    [HarmonyPostfix]
    private static void Postfix()
    {
        var env = MyRender11.Environment;
        if (env != null)
            Jitter.Restore(env.Matrices);
        if (DlssRuntime.IsLive)
            DlssRuntime.ApplyOutputSpace();
    }
}
