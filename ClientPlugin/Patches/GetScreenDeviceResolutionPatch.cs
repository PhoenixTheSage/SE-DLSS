using ClientPlugin.Dlss;
using HarmonyLib;
using VRageRender;

namespace ClientPlugin.Patches;

// Bypass Keen's monitor probe, which P/Invokes the unavailable PSNative.dll.
[HarmonyPatch(typeof(MyRender11), nameof(MyRender11.GetDeviceVSyncMode))]
internal static class GetDeviceVSyncModePatch
{
    [HarmonyPrefix]
    private static bool Prefix(ref int __result)
    {
        __result = MyRender11.DeviceSettings.VSync;
        return false;
    }
}

[HarmonyPatch(typeof(MyRender11), nameof(MyRender11.GetScreenDeviceResolution))]
internal static class GetScreenDeviceResolutionPatch
{
    [HarmonyPrefix]
    private static bool Prefix(out int width, out int height, ref bool __result)
    {
        if (!DlssRuntime.WantsDlss)
        {
            width = 0;
            height = 0;
            return true;
        }

        var size = DlssRuntime.OutputResolution();
        if (size.X <= 0 || size.Y <= 0)
        {
            width = 0;
            height = 0;
            return true;
        }

        width = size.X;
        height = size.Y;
        __result = true;
        return false;
    }
}
