using ClientPlugin.Dlss;
using HarmonyLib;
using VRageRender;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyRender11), "get_FxaaEnabled")]
internal static class FxaaEnabledPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ref bool __result)
    {
        if (!DlssRuntime.IsLive)
            return true;
        __result = false;
        return false;
    }
}
