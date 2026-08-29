using System;
using ClientPlugin.Dlss;
using HarmonyLib;
using VRage.Render11.Resources;
using VRageRender;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyCopyToRT), nameof(MyCopyToRT.Run))]
internal static class CopyToRtPatch
{
    [ThreadStatic]
    private static bool passthrough;

    [HarmonyPrefix]
    private static bool Prefix(IRtvBindable destination, ISrvBindable source)
    {
        if (passthrough)
            return true;
        if (!DlssRuntime.IsLive || destination == null || source == null)
            return true;
        if (!ReferenceEquals(destination, MyRender11.Backbuffer))
            return true;

        var output = DlssRuntime.OutputResolution();
        if (DlssRuntime.EvaluatedHdrThisFrame &&
            source.Size.X == output.X &&
            source.Size.Y == output.Y)
        {
            passthrough = true;
            try
            {
                MyCopyToRT.Run(destination, source, false, new MyViewport(output.X, output.Y), true);
            }
            finally
            {
                passthrough = false;
                DlssRuntime.RestoreViewportToOutput();
            }
            return false;
        }

        if (DlssRuntime.InternalWidth <= 0 ||
            source.Size.X != DlssRuntime.InternalWidth ||
            source.Size.Y != DlssRuntime.InternalHeight)
            return true;

        if (DlssRuntime.TryEvaluate(destination, source))
        {
            DlssRuntime.RestoreViewportToOutput();
            return false;
        }

        // Keen's CopyToRT viewport follows ViewportResolution (internal after SetDRS),
        // so the original blit would only fill a corner of the DXGI backbuffer.
        passthrough = true;
        try
        {
            MyCopyToRT.Run(destination, source, false, new MyViewport(output.X, output.Y), true);
        }
        finally
        {
            passthrough = false;
            DlssRuntime.RestoreViewportToOutput();
        }
        return false;
    }
}
