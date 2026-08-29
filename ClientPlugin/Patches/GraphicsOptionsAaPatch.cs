using ClientPlugin.Dlss;
using HarmonyLib;
using Sandbox.Engine.Platform.VideoMode;
using Sandbox.Graphics.GUI;
using SpaceEngineers.Game.GUI;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyGuiScreenOptionsGraphics))]
internal static class GraphicsOptionsAaPatch
{
    private static readonly AccessTools.FieldRef<MyGuiScreenOptionsGraphics, MyGuiControlCombobox> Combo =
        AccessTools.FieldRefAccess<MyGuiScreenOptionsGraphics, MyGuiControlCombobox>("m_comboAntialiasing");

    [HarmonyPostfix]
    [HarmonyPatch(nameof(MyGuiScreenOptionsGraphics.RecreateControls))]
    private static void RecreateControlsPostfix(MyGuiScreenOptionsGraphics __instance)
    {
        GameAntiAliasing.BindGraphicsCombo(Combo(__instance));
    }

    [HarmonyPostfix]
    [HarmonyPatch("WriteSettingsToControls")]
    private static void WriteSettingsPostfix(MyGuiScreenOptionsGraphics __instance)
    {
        GameAntiAliasing.AfterGraphicsWrite(Combo(__instance));
    }

    [HarmonyPostfix]
    [HarmonyPatch("ReadSettingsFromControls")]
    private static void ReadSettingsPostfix(MyGuiScreenOptionsGraphics __instance, ref MyGraphicsSettings graphicsSettings)
    {
        GameAntiAliasing.RemapDlssKey(Combo(__instance), ref graphicsSettings);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyGuiScreenOptionsGraphics.OnOkClick))]
    private static void OkPrefix()
    {
        GameAntiAliasing.OnGraphicsOk();
    }
}

[HarmonyPatch(typeof(MyGuiScreenBase), nameof(MyGuiScreenBase.CloseScreen), new[] { typeof(bool) })]
internal static class GraphicsOptionsClosePatch
{
    [HarmonyPostfix]
    private static void Postfix(MyGuiScreenBase __instance)
    {
        if (__instance is MyGuiScreenOptionsGraphics)
            GameAntiAliasing.OnGraphicsScreenClosed();
    }
}
