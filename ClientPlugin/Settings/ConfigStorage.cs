using System;
using System.IO;
using System.Threading;
using System.Xml.Serialization;
using VRage.FileSystem;
using VRage.Utils;

namespace ClientPlugin.Settings;

public static class ConfigStorage
{
    const int DebounceMs = 400;

    static readonly string ConfigFileName = Plugin.Name + ".cfg";
    static readonly XmlSerializer Serializer = new XmlSerializer(typeof(Config));
    static readonly object Gate = new();
    static readonly object WriteGate = new();
    static Config pending;
    static bool dirty;
    static int lastRequestTick;
    static int writesInFlight;

    static string ConfigFilePath => Path.Combine(MyFileSystem.UserDataPath, "Storage", ConfigFileName);

    /// <summary>
    /// Marks the config dirty. Rich HUD sliders fire this while dragging.
    /// <see cref="FlushPending"/> waits ~400 ms of quiet, then writes on a
    /// worker so Plugin.Update never hits File.CreateText.
    /// </summary>
    public static void Save(Config config)
    {
        if (config == null)
            return;
        lock (Gate)
        {
            pending = config;
            dirty = true;
            lastRequestTick = Environment.TickCount;
        }
    }

    public static void FlushPending(bool force = false)
    {
        Config toWrite = null;
        lock (Gate)
        {
            if (dirty && pending != null)
            {
                if (!force && unchecked(Environment.TickCount - lastRequestTick) < DebounceMs)
                    return;
                toWrite = pending;
                dirty = false;
            }
            else if (!force)
            {
                return;
            }
        }

        if (!force)
        {
            Interlocked.Increment(ref writesInFlight);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Write(toWrite); }
                finally { Interlocked.Decrement(ref writesInFlight); }
            });
            return;
        }

        var spins = 0;
        while (Volatile.Read(ref writesInFlight) > 0 && spins++ < 2000)
            Thread.Sleep(1);
        if (toWrite != null)
            Write(toWrite);
    }

    static void Write(Config config)
    {
        var path = ConfigFilePath;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            lock (WriteGate)
            {
                using var text = File.CreateText(path);
                Serializer.Serialize(text, config);
            }
        }
        catch (Exception e)
        {
            MyLog.Default.Warning(
                $"{ConfigFileName}: Failed to save config file: {e.GetType().Name}: {e.Message} ({path})");
        }
    }

    public static Config Load()
    {
        var path = ConfigFilePath;
        if (!File.Exists(path))
            return Config.Default;

        Config.SuppressApply = true;
        try
        {
            var xml = File.ReadAllText(path);
            xml = xml.Replace("LatestRecommended", "LatestModel");
            xml = xml.Replace("<Evaluate>Ldr</Evaluate>", "");
            xml = xml.Replace("<Evaluate>Hdr</Evaluate>", "");
            xml = xml.Replace("<UseAnomalyMotionVectors>true</UseAnomalyMotionVectors>", "");
            xml = xml.Replace("<UseAnomalyMotionVectors>false</UseAnomalyMotionVectors>", "");
            xml = xml.Replace("<Model>CnnA</Model>", "<Model>CnnF</Model>");
            xml = xml.Replace("<Model>CnnB</Model>", "<Model>CnnF</Model>");
            xml = xml.Replace("<Model>CnnC</Model>", "<Model>CnnF</Model>");
            xml = xml.Replace("<Model>CnnD</Model>", "<Model>CnnF</Model>");
            xml = xml.Replace("<Model>CnnE</Model>", "<Model>CnnF</Model>");
            using var reader = new StringReader(xml);
            return (Config)Serializer.Deserialize(reader) ?? Config.Default;
        }
        catch (Exception e)
        {
            MyLog.Default.Warning(
                $"{ConfigFileName}: Failed to read config file: {e.GetType().Name}: {e.Message} ({ConfigFilePath})");
        }
        finally
        {
            Config.SuppressApply = false;
        }

        return Config.Default;
    }
}
