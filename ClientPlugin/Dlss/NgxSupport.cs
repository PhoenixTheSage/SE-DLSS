namespace ClientPlugin.Dlss;

public enum NgxSupportKind
{
    Available,
    Pending,
    MissingDll,
    CapabilityReadFailed,
    NeedsUpdatedDriver,
    FeatureDenied,
    SuperSamplingUnavailable,
    InitFailed,
    NotNvidia
}

public readonly struct NgxSupportVerdict
{
    public NgxSupportKind Kind { get; }
    public bool IsSupported { get; }
    public bool SupportKnown { get; }
    public bool Recoverable { get; }
    public string Message { get; }

    NgxSupportVerdict(NgxSupportKind kind, bool isSupported, bool supportKnown, bool recoverable, string message)
    {
        Kind = kind;
        IsSupported = isSupported;
        SupportKnown = supportKnown;
        Recoverable = recoverable;
        Message = message ?? "";
    }

    public static NgxSupportVerdict Supported(string message = null) =>
        new(NgxSupportKind.Available, true, true, false,
            string.IsNullOrEmpty(message) ? "Super Sampling is available" : message);

    public static NgxSupportVerdict Pending(string message = null) =>
        new(NgxSupportKind.Pending, false, false, true,
            string.IsNullOrEmpty(message) ? "NGX Super Sampling has not been probed yet" : message);

    public static NgxSupportVerdict MissingDll() =>
        new(NgxSupportKind.MissingDll, false, false, true,
            "nvngx_dlss.dll is not on the plugin search paths. Pulsar downloads it from the GitHub release on first launch.");

    public static NgxSupportVerdict CapabilityReadFailed() =>
        new(NgxSupportKind.CapabilityReadFailed, false, false, true,
            "NGX SuperSampling.Available could not be read; will retry");

    public static NgxSupportVerdict NeedsUpdatedDriver(int major, int minor, bool hasVersion) =>
        new(NgxSupportKind.NeedsUpdatedDriver, false, true, false,
            hasVersion
                ? "update NVIDIA Game Ready driver to " + major + "." + minor + " (DLSS 310.7.0)"
                : "update NVIDIA Game Ready driver (DLSS Super Sampling requires a newer driver)");

    public static NgxSupportVerdict FeatureDenied(int featureInitResult) =>
        new(NgxSupportKind.FeatureDenied, false, true, false,
            "NGX denied Super Sampling (FeatureInitResult=0x" + ((uint)featureInitResult).ToString("X8") + ")");

    public static NgxSupportVerdict SuperSamplingUnavailable() =>
        new(NgxSupportKind.SuperSamplingUnavailable, false, true, false,
            "NGX initialized but Super Sampling is not available");

    public static NgxSupportVerdict InitFailed(string message) =>
        new(NgxSupportKind.InitFailed, false, true, false,
            string.IsNullOrEmpty(message) ? "NGX init failed" : message);

    public static NgxSupportVerdict NotNvidia(string message) =>
        new(NgxSupportKind.NotNvidia, false, true, false, message ?? "DLSS requires an NVIDIA GPU");
}

/// <summary>
/// Pure NGX Super Sampling classifier. No D3D; safe for unit tests.
/// </summary>
public static class NgxSupport
{
    public const int Success = 1;

    public static bool IsNgxFail(int result)
    {
        var u = (uint)result;
        return u != Success && ((u & 0x80000000u) != 0 || (u & 0xFFF00000u) == 0xBAD00000u);
    }

    public static NgxSupportVerdict ClassifyCaps(
        bool availableGetOk,
        int available,
        bool needsDriverGetOk,
        int needsUpdatedDriver,
        bool minMajorGetOk,
        int minMajor,
        bool minMinorGetOk,
        int minMinor,
        bool featureInitGetOk,
        int featureInitResult)
    {
        if (needsDriverGetOk && needsUpdatedDriver != 0)
            return NgxSupportVerdict.NeedsUpdatedDriver(minMajor, minMinor, minMajorGetOk && minMinorGetOk);
        if (!availableGetOk)
            return NgxSupportVerdict.CapabilityReadFailed();
        if (featureInitGetOk && IsNgxFail(featureInitResult))
            return NgxSupportVerdict.FeatureDenied(featureInitResult);
        if (available == 0)
            return NgxSupportVerdict.SuperSamplingUnavailable();
        return NgxSupportVerdict.Supported();
    }
}
