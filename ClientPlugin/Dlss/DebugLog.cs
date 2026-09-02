using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using VRage.FileSystem;
using VRage.Utils;

namespace ClientPlugin.Dlss;

/// <summary>
/// Writes a debug-only log beside SpaceEngineers.log; call sites are omitted from Release builds.
/// <see cref="Write"/> is for one-shot events. <see cref="WriteFrame"/> is for per-frame
/// sites: first line immediately, then at most once per <see cref="HeartbeatSeconds"/>.
/// </summary>
public static class DebugLog
{
    public const string FileName = "SpaceEngineersDLSS.debug.log";
    public const int HeartbeatSeconds = 5;

    public static string FilePath { get; private set; }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, FrameSite> Sites = new();
    private static readonly long HeartbeatTicks = Stopwatch.Frequency * HeartbeatSeconds;
    private static StreamWriter _writer;

    [Conditional("DEBUG")]
    public static void Open()
    {
        lock (Gate)
        {
            if (_writer != null)
                return;

            var dir = ResolveUserDataDir();
            try
            {
                Directory.CreateDirectory(dir);
                FilePath = Path.Combine(dir, FileName);
                _writer = new StreamWriter(FilePath, false, new UTF8Encoding(false))
                {
                    AutoFlush = true
                };
                _writer.WriteLine("Space Engineers DLSS debug log");
                _writer.WriteLine("opened {0:o}", DateTime.Now);
                _writer.WriteLine("folder {0}", dir);
                _writer.WriteLine("frame sites log first occurrence, then every {0}s", HeartbeatSeconds);
                _writer.WriteLine();
                try
                {
                    MyLog.Default.WriteLine("DLSS debug log: " + FilePath);
                }
                catch
                {
                    // ignored
                }
            }
            catch (Exception e)
            {
                FilePath = null;
                _writer = null;
                try
                {
                    MyLog.Default.WriteLine("DLSS debug log failed to open: " + e.Message);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    [Conditional("DEBUG")]
    public static void Write(string message)
    {
        WriteLine(message);
    }

    [Conditional("DEBUG")]
    public static void WriteFrame(
        string message,
        [CallerMemberName] string caller = null,
        [CallerFilePath] string file = null,
        [CallerLineNumber] int line = 0)
    {
        if (string.IsNullOrEmpty(message))
            return;
        var key = (file ?? "") + ":" + line + ":" + (caller ?? "");
        lock (Gate)
        {
            if (_writer == null)
                return;

            var now = Stopwatch.GetTimestamp();
            if (Sites.TryGetValue(key, out var site))
            {
                site.Repeat++;
                site.Message = message;
                if (now - site.LastWriteTicks < HeartbeatTicks)
                    return;
                WriteLineUnlocked(FormatHeartbeat(site));
                site.Repeat = 0;
                site.LastWriteTicks = now;
                return;
            }

            Sites[key] = new FrameSite
            {
                Message = message,
                LastWriteTicks = now,
                Repeat = 0
            };
            WriteLineUnlocked(message);
        }
    }

    [Conditional("DEBUG")]
    public static void Close()
    {
        lock (Gate)
        {
            if (_writer == null)
                return;
            try
            {
                _writer.WriteLine();
                _writer.WriteLine("closed {0:o}", DateTime.Now);
                _writer.Dispose();
            }
            catch
            {
                // ignored
            }
            _writer = null;
            Sites.Clear();
        }
    }

    private static void WriteLine(string message)
    {
        if (string.IsNullOrEmpty(message))
            return;
        lock (Gate)
        {
            if (_writer == null)
                return;
            WriteLineUnlocked(message);
        }
    }

    private static void WriteLineUnlocked(string message)
    {
        try
        {
            _writer.Write(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
            _writer.Write(" ");
            _writer.WriteLine(message);
        }
        catch
        {
            try
            {
                _writer.Dispose();
            }
            catch
            {
                // ignored
            }
            _writer = null;
        }
    }

    private static string FormatHeartbeat(FrameSite site)
    {
        if (site.Repeat <= 1)
            return site.Message;
        return site.Message + "  x" + site.Repeat;
    }

    private static string ResolveUserDataDir()
    {
        try
        {
            if (!string.IsNullOrEmpty(MyFileSystem.UserDataPath))
                return MyFileSystem.UserDataPath;
        }
        catch
        {
            // ignored
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SpaceEngineers");
    }

    private sealed class FrameSite
    {
        public string Message;
        public long LastWriteTicks;
        public int Repeat;
    }
}
