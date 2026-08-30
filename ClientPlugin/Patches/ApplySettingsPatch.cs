using ClientPlugin.Dlss;
using HarmonyLib;
using VRage;
using VRageRender;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyRender11), nameof(MyRender11.ApplySettings))]
internal static class ApplySettingsPatch
{
    [HarmonyPrefix]
    private static bool Prefix(MyRenderDeviceSettings settings)
    {
        if (!DlssRuntime.IsLive)
            return true;
        if (!DlssRuntime.SettingsMatchOutput(settings.BackBufferWidth, settings.BackBufferHeight))
            return true;
        if (!DlssRuntime.SwapchainMatchesOutput())
            return true;

        // Backbuffer.Size follows internal ResolutionI; the swapchain is already output-sized.
        DebugLog.WriteFrame("ApplySettings skip swapchain resize; buffers already " +
                            settings.BackBufferWidth + "x" + settings.BackBufferHeight);
        if (MyRender11.m_settings.UseStereoRendering)
            settings.UseStereoRendering = true;
        MyVRage.Platform.Render.ApplyRenderSettings(settings);
        MyRender11.m_settings = settings;
        return false;
    }
}
