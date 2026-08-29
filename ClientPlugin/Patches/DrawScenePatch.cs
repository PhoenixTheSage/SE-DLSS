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
        DlssRuntime.DisableConsoleDrs();
        if (!DlssRuntime.WantsDlss || !DlssRuntime.TryPrepareFrame())
        {
            DlssRuntime.RestoreOutputResolution();
            return;
        }

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
            DlssRuntime.RestoreViewportToOutput();
    }
}
