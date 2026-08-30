using System;
using ClientPlugin.Dlss;
using HarmonyLib;
using VRage.Render11.Resources;
using VRage.Utils;
using VRageRender;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyToneMapping), nameof(MyToneMapping.Run))]
internal static class ToneMappingPatch
{
    private static bool _exceptionLogged;

    [HarmonyPostfix]
    private static void Postfix(ref IBorrowedCustomTexture __result)
    {
        if (!DlssRuntime.IsLive || DlssRuntime.EvaluatedThisFrame || __result == null)
            return;
        if (__result.Size.X != DlssRuntime.InternalWidth || __result.Size.Y != DlssRuntime.InternalHeight)
            return;

        try
        {
            var dest = DlssRuntime.AcquireLdrOutput();
            if (dest == null)
                return;

            if (!DlssRuntime.TryEvaluate(dest, __result))
            {
                DebugLog.Write("ToneMapping LDR evaluate failed src=" + __result.Size +
                               " dest=" + dest.Size);
                return;
            }

            __result.Release();
            __result = dest;
            DlssRuntime.EvaluatedThisFrame = true;
            DlssRuntime.ApplyOutputSpace();
            DebugLog.WriteFrame("ToneMapping LDR evaluate src=" + DlssRuntime.InternalWidth + "x" +
                                DlssRuntime.InternalHeight + " dest=" + dest.Size);
        }
        catch (Exception e)
        {
            var message = e.GetType().Name + ": " + e.Message;
            if (!_exceptionLogged)
            {
                _exceptionLogged = true;
                MyLog.Default.Warning("DLSS tone-mapping patch failed: " + message);
            }
            DebugLog.Write("ToneMapping LDR threw " + e);
        }
    }
}
