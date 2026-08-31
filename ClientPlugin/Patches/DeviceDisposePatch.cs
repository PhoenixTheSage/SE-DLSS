using ClientPlugin.Dlss;
using HarmonyLib;
using SharpDX;
using SharpDX.Direct3D11;

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
        var dispose = AccessTools.DeclaredMethod(typeof(DisposeBase), nameof(DisposeBase.Dispose))
                      ?? AccessTools.Method(typeof(DisposeBase), nameof(DisposeBase.Dispose));
        if (dispose == null)
            return;
        harmony.Patch(dispose, prefix: new HarmonyMethod(typeof(DeviceDisposePatch), nameof(Prefix))
        {
            priority = Priority.First
        });
    }

    private static void Prefix(DisposeBase __instance)
    {
        if (__instance is not Device device || device.IsDisposed)
            return;
        NgxHost.OnDeviceDisposing(device.NativePointer);
    }
}
