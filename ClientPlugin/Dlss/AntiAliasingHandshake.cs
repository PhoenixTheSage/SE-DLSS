namespace ClientPlugin.Dlss;

/// <summary>
/// Well-known AA handshake. FRS resolves this type by name:
/// <c>ClientPlugin.Dlss.AntiAliasingHandshake</c>. No compile-time reference.
/// Choice names: Off, FXAA, DLSS, FRS. Graphics combo key 100 is DLSS; FRS uses 101.
/// </summary>
public static class AntiAliasingHandshake
{
    public const string ChoiceOff = "Off";
    public const string ChoiceFxaa = "FXAA";
    public const string ChoiceDlss = "DLSS";
    public const string ChoiceFrs = "FRS";
    public const long GraphicsComboKey = 100;

    public static bool CanOffer() => GpuSupport.CanOfferDlss;

    public static string GetChoice()
    {
        var config = Config.Current;
        var choice = config == null ? AntiAliasingChoice.Off : config.AntiAliasing;
        return GameAntiAliasing.ChoiceName(GameAntiAliasing.DisplayedChoice(choice));
    }

    public static void ApplyChoice(string choice)
    {
        GameAntiAliasing.ApplyFromPeer(choice);
    }
}
