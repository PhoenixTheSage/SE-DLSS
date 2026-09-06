using System;
using System.Reflection;
using ClientPlugin.Dlss;

public static class Program
{
    public static object Active { get; set; }
    public static void Main()
    {
        // Inject a reflection boundary, exercising the production consumer without a GPU.
        Set("_activeProperty", typeof(Program).GetProperty("Active"));
        Set("_anomalyConfigCurrent", typeof(ProbeConfig).GetField("Current"));
        Set("_velocityProbe", typeof(ProbeConfig).GetProperty("VelocityProbe"));
        Set("_velocitySource", typeof(ProbeConfig).GetProperty("VelocitySource"));
        var buffer = new Buffer(); Active = buffer;
        Assert(Read(out var ptr) && ptr == new IntPtr(123), "canonical accepted");
        foreach (var flags in new[] { 0, 1, 3, 7, 11, 13, 14, 31 })
        {
            buffer.Convention = flags;
            Assert(!Read(out ptr) && ptr == IntPtr.Zero, "reject convention " + flags);
        }
        buffer.Convention = 15;
        for (int probe = 1; probe <= 4; probe++)
        {
            ProbeConfig.Current.VelocityProbe = probe;
            Assert(!Read(out ptr) && ptr == IntPtr.Zero, "reject probe " + probe);
        }
        ProbeConfig.Current.VelocityProbe = 0;
        Set("_velocityProbe", null);
        Assert(!Read(out ptr), "unknown probe rejected");
        Set("_velocityProbe", typeof(ProbeConfig).GetProperty("VelocityProbe"));
        buffer.Width = 960;
        Assert(!Read(out ptr), "old dimensions rejected on resize");
        buffer.Width = 1280; buffer.NativeResource = new IntPtr(456);
        Assert(Read(out ptr) && ptr == new IntPtr(456), "fresh resource read after resize");
        buffer.HistoryValid = false;
        Assert(AnomalyHook.TryGetLive(1280, 720, out ptr, out var history) && !history, "invalid history propagated");
        buffer.IsAvailable = false; Assert(!Read(out ptr), "unavailable rejected");
        buffer.IsAvailable = true; buffer.NativeResource = IntPtr.Zero; Assert(!Read(out ptr), "null rejected");
        Assert(VelocityAcceptance.ResetReason(false,false,true,false,false,false) == "none", "steady history");
        Assert(VelocityAcceptance.ResetReason(false,false,true,false,false,true) == "camera-cut", "cut resets");
        Assert(VelocityAcceptance.ResetReason(true,false,false,false,false,false).Contains("invalid-history"), "resize resets");
        Assert(VelocityAcceptance.ResetReason(false,false,true,false,true,false) == "source-change", "source switch resets");
        Console.WriteLine("All consumer contract and reset tests passed. GPU/visual acceptance remains manual.");
    }
    static bool Read(out IntPtr p) => AnomalyHook.TryGetLive(1280,720,out p,out _);
    static void Set(string name, object value) => typeof(AnomalyHook).GetField(name, BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,value);
    static void Assert(bool ok,string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
}
public class Buffer
{
    public bool IsAvailable { get; set; } = true;
    public IntPtr NativeResource { get; set; } = new IntPtr(123);
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Convention { get; set; } = 15;
    public bool HistoryValid { get; set; } = true;
}
public class ProbeConfig
{
    public static ProbeConfig Current = new();
    public int VelocityProbe { get; set; }
    public string VelocitySource => "GBuffer";
}
namespace ClientPlugin.Dlss
{
    public static class DebugLog { public static void Write(string s) {} }
    public static class DlssRuntime { public static string LastBindingEvidence; public static int InternalWidth=1280, InternalHeight=720; }
}
namespace VRage.Utils
{
    public class MyLog { public static MyLog Default=new(); public void WriteLine(string s) {} public void Warning(string s) {} }
}
