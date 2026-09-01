using System;
using System.Reflection;
using ClientPlugin.Dlss;
using HarmonyLib;
using SharpDX;
using SharpDX.Direct3D11;
using VRage.Utils;

namespace ClientPlugin.Patches;

/// <summary>
/// NGX must be shut down while the D3D11 device is still alive. Keen disposes
/// the device on the render thread; <see cref="Plugin.Dispose"/> is too late.
/// Patched with a separate Harmony id so <c>UnpatchAll</c> on plugin dispose
/// does not remove this hook before the device is released.
/// </summary>
internal static class DeviceDisposePatch
{
    internal const string HarmonyId = Plugin.Name + ".DeviceLifetime";

    internal static void Apply(Harmony harmony)
    {
        try
        {
            var dispose = FindParameterlessDispose();
            if (dispose == null)
            {
                MyLog.Default.Warning("DLSS: could not find SharpDX DisposeBase.Dispose()");
                return;
            }
            harmony.Patch(dispose, prefix: new HarmonyMethod(typeof(DeviceDisposePatch), nameof(Prefix))
            {
                priority = Priority.First
            });
        }
        catch (Exception e)
        {
            MyLog.Default.Error("DLSS failed to hook D3D device dispose: " + e);
        }
    }

    private static MethodInfo FindParameterlessDispose()
    {
        var dispose = AccessTools.DeclaredMethod(typeof(DisposeBase), nameof(DisposeBase.Dispose), Type.EmptyTypes)
                      ?? AccessTools.Method(typeof(DisposeBase), nameof(DisposeBase.Dispose), Type.EmptyTypes);
        if (dispose != null)
            return dispose;

        foreach (var method in typeof(DisposeBase).GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (method.Name == nameof(DisposeBase.Dispose) && method.GetParameters().Length == 0)
                return method;
        }
        return null;
    }

    private static void Prefix(DisposeBase __instance)
    {
        if (__instance is not Device device || device.IsDisposed)
            return;
        NgxHost.OnDeviceDisposing(device);
    }
}
