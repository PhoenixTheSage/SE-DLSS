using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ClientPlugin.Dlss;

/// <summary>
/// NVIDIA <c>NVSDK_NGX_AppLogCallback</c> sink. NGX may invoke this from any thread.
/// </summary>
internal static class NgxLog
{
    internal const int LevelOff = 0;
    internal const int LevelOn = 1;
    internal const int LevelVerbose = 2;

    private const int Capacity = 24;
    private const int MaxLineChars = 400;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void AppLogCallback(IntPtr message, int loggingLevel, int sourceComponent);

    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new();
    // Rooted so GetFunctionPointerForDelegate stays valid for the process lifetime.
    private static readonly AppLogCallback Callback = OnLog;
    private static string _lastWritten;

    internal static IntPtr FunctionPointer { get; } = Marshal.GetFunctionPointerForDelegate(Callback);

    internal static bool HasMessages
    {
        get
        {
            lock (Gate)
                return Recent.Count > 0;
        }
    }

    internal static string LastLine
    {
        get
        {
            lock (Gate)
                return Recent.Count == 0 ? "" : PeekLast();
        }
    }

    internal static string LastLines(int max)
    {
        lock (Gate)
        {
            if (Recent.Count == 0)
                return "(none)";
            var items = Recent.ToArray();
            var start = items.Length > max ? items.Length - max : 0;
            return string.Join(" | ", items, start, items.Length - start);
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            Recent.Clear();
            _lastWritten = null;
        }
    }

    private static string PeekLast()
    {
        string last = null;
        foreach (var line in Recent)
            last = line;
        return last ?? "";
    }

    private static void OnLog(IntPtr message, int loggingLevel, int sourceComponent)
    {
        try
        {
            if (message == IntPtr.Zero)
                return;
            var text = Marshal.PtrToStringAnsi(message);
            if (string.IsNullOrEmpty(text))
                return;
            text = text.Trim();
            if (text.Length > MaxLineChars)
                text = text.Substring(0, MaxLineChars);
            var line = "NGX[" + loggingLevel + "/" + sourceComponent + "] " + text;
            lock (Gate)
            {
                if (Recent.Count >= Capacity)
                    Recent.Dequeue();
                Recent.Enqueue(line);
                if (string.Equals(_lastWritten, line, StringComparison.Ordinal))
                    return;
                _lastWritten = line;
            }

            DebugLog.Write(line);
        }
        catch
        {
            // NGX holds native locks; never throw back into the driver.
        }
    }
}
