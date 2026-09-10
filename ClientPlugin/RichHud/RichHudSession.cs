using VRage.Game;
using VRage.Game.Components;

namespace ClientPlugin.RichHud;

/// <summary>
/// Queues the Anomaly Shaders / DLSS page. Safe when Anomaly or Master is absent.
/// Pulsar MyGui remains the settings UI. Do not vendor a Rich HUD client.
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public sealed class RichHudSession : MySessionComponentBase
{
    public override void Init(MyObjectBuilder_SessionComponent sessionComponent)
    {
        AnomalyTerminalHook.TryInstall();
    }

    // Lobbies: pause does not tick BeforeSimulation in SP; Draw still runs.
    public override void Draw()
    {
        AnomalyTerminalHook.TryInstall();
        base.Draw();
    }

    protected override void UnloadData()
    {
        // Keep the page registered for the next world. Anomaly unmounts itself.
    }
}
