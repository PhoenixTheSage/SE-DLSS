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
/// </summary>
public static class DebugLog
{
    public const string FileName = "SpaceEngineersDLSS.debug.log";
    public const string NativeFileName = "SeDlssNgx.debug.log";

    public static string FilePath { get; private set; }
    public static string NativeFilePath { get; private set; }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> LastFrameByCaller = new();
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
                NativeFilePath = Path.Combine(dir, NativeFileName);
                _writer = new StreamWriter(FilePath, false, new UTF8Encoding(false))
                {
                    AutoFlush = true
                };
                _writer.WriteLine("Space Engineers DLSS debug log");
                _writer.WriteLine("opened {0:o}", DateTime.Now);
                _writer.WriteLine("folder {0}", dir);
                _writer.WriteLine("native {0}", NativeFilePath);
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
                NativeFilePath = null;
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
        WriteCore(message, force: true);
    }

    [Conditional("DEBUG")]
    public static void WriteFrame(string message, [CallerMemberName] string caller = null)
    {
        WriteCore(message, force: false, caller ?? "");
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
            LastFrameByCaller.Clear();
        }
    }

    private static void WriteCore(string message, bool force, string caller = "")
    {
        if (string.IsNullOrEmpty(message))
            return;
        lock (Gate)
        {
            if (_writer == null)
                return;
            if (!force)
            {
                if (LastFrameByCaller.TryGetValue(caller, out var previous) && previous == message)
                    return;
                LastFrameByCaller[caller] = message;
            }

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
}
