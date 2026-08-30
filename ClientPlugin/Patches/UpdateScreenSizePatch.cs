using System;
using System.Reflection;
using ClientPlugin.Dlss;
using HarmonyLib;
using Sandbox;
using VRage.Game.Utils;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MySandboxGame), nameof(MySandboxGame.UpdateScreenSize))]
internal static class UpdateScreenSizePatch
{
    [HarmonyPrefix]
    private static void Prefix(ref int width, ref int height, ref MyViewport viewport)
    {
        var originalWidth = width;
        var originalHeight = height;
        if (!TryForceOutput(ref width, ref height, ref viewport))
            return;
        DebugLog.Write("UpdateScreenSize remapped " + originalWidth + "x" + originalHeight + " -> " +
                       width + "x" + height);
    }

    internal static bool TryForceOutput(ref int width, ref int height, ref MyViewport viewport)
    {
        if (!DlssRuntime.WantsDlss)
            return false;
        var output = DlssRuntime.OutputResolution();
        if (output.X <= 0 || output.Y <= 0)
            return false;
        if (width == output.X && height == output.Y)
            return false;
        width = output.X;
        height = output.Y;
        viewport = new MyViewport(output.X, output.Y);
        return true;
    }
}

[HarmonyPatch(typeof(MyCamera), nameof(MyCamera.UpdateScreenSize))]
internal static class CameraUpdateScreenSizePatch
{
    [HarmonyPrefix]
    private static void Prefix(ref MyViewport currentScreenViewport)
    {
        if (!DlssRuntime.WantsDlss)
            return;

        var output = DlssRuntime.OutputResolution();
        if (output.X <= 0 || output.Y <= 0)
            return;

        currentScreenViewport = new MyViewport(output.X, output.Y);
    }
}

[HarmonyPatch(typeof(MyCamera), nameof(MyCamera.Update))]
internal static class CameraUpdateViewportPatch
{
    [HarmonyPrefix]
    private static void Prefix(MyCamera __instance)
    {
        ForceViewport(__instance);
    }

    internal static void ForceViewport(MyCamera camera)
    {
        if (camera == null || !DlssRuntime.WantsDlss)
            return;
        var output = DlssRuntime.OutputResolution();
        if (output.X <= 0 || output.Y <= 0)
            return;
        if ((int)camera.Viewport.Width == output.X && (int)camera.Viewport.Height == output.Y)
            return;
        camera.Viewport = new MyViewport(output.X, output.Y);
    }
}

[HarmonyPatch]
internal static class CameraViewportSizePatch
{
    private static MethodBase TargetMethod()
    {
        foreach (var method in typeof(MyCamera).GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (method.ReturnType == typeof(Vector2) &&
                method.Name.IndexOf("ViewportSize", StringComparison.Ordinal) >= 0)
                return method;
        return AccessTools.DeclaredMethod(typeof(MyCamera), "VRage.ModAPI.IMyCamera.get_ViewportSize");
    }

    [HarmonyPostfix]
    private static void Postfix(ref Vector2 __result)
    {
        if (!DlssRuntime.WantsDlss)
            return;
        var output = DlssRuntime.OutputResolution();
        if (output.X <= 0 || output.Y <= 0)
            return;
        if ((int)__result.X == output.X && (int)__result.Y == output.Y)
            return;
        __result = new Vector2(output.X, output.Y);
    }
}
