using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ClientPlugin.Dlss;

/// <summary>
/// Pinned ANSI NGX parameter names. Allocated once so Evaluate does not marshal strings.
/// </summary>
internal static class NgxNames
{
    internal static readonly IntPtr Width = Alloc("Width");
    internal static readonly IntPtr Height = Alloc("Height");
    internal static readonly IntPtr OutWidth = Alloc("OutWidth");
    internal static readonly IntPtr OutHeight = Alloc("OutHeight");
    internal static readonly IntPtr Sharpness = Alloc("Sharpness");
    internal static readonly IntPtr Reset = Alloc("Reset");
    internal static readonly IntPtr Color = Alloc("Color");
    internal static readonly IntPtr Output = Alloc("Output");
    internal static readonly IntPtr Depth = Alloc("Depth");
    internal static readonly IntPtr MotionVectors = Alloc("MotionVectors");
    internal static readonly IntPtr BiasCurrentColorMask = Alloc("DLSS.Input.Bias.Current.Color.Mask");
    internal static readonly IntPtr JitterOffsetX = Alloc("Jitter.Offset.X");
    internal static readonly IntPtr JitterOffsetY = Alloc("Jitter.Offset.Y");
    internal static readonly IntPtr MvScaleX = Alloc("MV.Scale.X");
    internal static readonly IntPtr MvScaleY = Alloc("MV.Scale.Y");
    internal static readonly IntPtr PerfQualityValue = Alloc("PerfQualityValue");
    internal static readonly IntPtr RtxValue = Alloc("RTXValue");
    internal static readonly IntPtr OptimalSettingsCallback = Alloc("DLSSOptimalSettingsCallback");
    internal static readonly IntPtr CreateFlags = Alloc("DLSS.Feature.Create.Flags");
    internal static readonly IntPtr EnableOutputSubrects = Alloc("DLSS.Enable.Output.Subrects");
    internal static readonly IntPtr RenderSubrectWidth = Alloc("DLSS.Render.Subrect.Dimensions.Width");
    internal static readonly IntPtr RenderSubrectHeight = Alloc("DLSS.Render.Subrect.Dimensions.Height");
    internal static readonly IntPtr SuperSamplingAvailable = Alloc("SuperSampling.Available");
    internal static readonly IntPtr PresetDlaa = Alloc("DLSS.Hint.Render.Preset.DLAA");
    internal static readonly IntPtr PresetQuality = Alloc("DLSS.Hint.Render.Preset.Quality");
    internal static readonly IntPtr PresetBalanced = Alloc("DLSS.Hint.Render.Preset.Balanced");
    internal static readonly IntPtr PresetPerformance = Alloc("DLSS.Hint.Render.Preset.Performance");
    internal static readonly IntPtr PresetUltraPerformance = Alloc("DLSS.Hint.Render.Preset.UltraPerformance");
    internal static readonly IntPtr PresetUltraQuality = Alloc("DLSS.Hint.Render.Preset.UltraQuality");

    private static IntPtr Alloc(string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }
}
