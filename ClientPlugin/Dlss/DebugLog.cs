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
/// File logger compiled into Debug builds only. Call sites are stripped from Release.
/// Writes next to SpaceEngineers.log under the game user-data folder.
/// </summary>
public static class DebugLog
{
    public const string FileName = "SpaceEngineersDLSS.debug.log";
    public const string NativeFileName = "SeDlssNgx.debug.log";

    public static string FilePath { get; private set; }
    public static string NativeFilePath { get; private set; }

    private static readonly object Gate = new object();
    private static readonly Dictionary<string, string> lastFrameByCaller = new Dictionary<string, string>();
    private static StreamWriter writer;

    [Conditional("DEBUG")]
    public static void Open()
    {
        lock (Gate)
        {
            if (writer != null)
                return;

            var dir = ResolveUserDataDir();
            try
            {
                Directory.CreateDirectory(dir);
                FilePath = Path.Combine(dir, FileName);
                NativeFilePath = Path.Combine(dir, NativeFileName);
                writer = new StreamWriter(FilePath, false, new UTF8Encoding(false))
                {
                    AutoFlush = true
                };
                writer.WriteLine("Space Engineers DLSS debug log");
                writer.WriteLine("opened {0:o}", DateTime.Now);
                writer.WriteLine("folder {0}", dir);
                writer.WriteLine("native {0}", NativeFilePath);
                writer.WriteLine();
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
                writer = null;
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
            if (writer == null)
                return;
            try
            {
                writer.WriteLine();
                writer.WriteLine("closed {0:o}", DateTime.Now);
                writer.Dispose();
            }
            catch
            {
                // ignored
            }
            writer = null;
            lastFrameByCaller.Clear();
        }
    }

    private static void WriteCore(string message, bool force, string caller = "")
    {
        if (string.IsNullOrEmpty(message))
            return;
        lock (Gate)
        {
            if (writer == null)
                return;
            if (!force)
            {
                string previous;
                if (lastFrameByCaller.TryGetValue(caller, out previous) && previous == message)
                    return;
                lastFrameByCaller[caller] = message;
            }

            try
            {
                writer.Write(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
                writer.Write(" ");
                writer.WriteLine(message);
            }
            catch
            {
                try
                {
                    writer.Dispose();
                }
                catch
                {
                    // ignored
                }
                writer = null;
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
