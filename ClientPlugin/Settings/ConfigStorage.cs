using System;
using System.IO;
using System.Xml.Serialization;
using VRage.FileSystem;
using VRage.Utils;

namespace ClientPlugin.Settings;

public static class ConfigStorage
{
    private static readonly string ConfigFileName = Plugin.Name + ".cfg";
    private static string ConfigFilePath => Path.Combine(MyFileSystem.UserDataPath, "Storage", ConfigFileName);

    public static void Save(Config config)
    {
        var path = ConfigFilePath;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            using var text = File.CreateText(path);
            new XmlSerializer(typeof(Config)).Serialize(text, config);
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

        var xmlSerializer = new XmlSerializer(typeof(Config));
        Config.SuppressApply = true;
        try
        {
            var xml = File.ReadAllText(path);
            xml = xml.Replace("LatestRecommended", "LatestModel");
            xml = xml.Replace("<Evaluate>Ldr</Evaluate>", "");
            xml = xml.Replace("<Evaluate>Hdr</Evaluate>", "");
            xml = xml.Replace("<Model>CnnA</Model>", "<Model>CnnF</Model>");
            xml = xml.Replace("<Model>CnnB</Model>", "<Model>CnnF</Model>");
            xml = xml.Replace("<Model>CnnC</Model>", "<Model>CnnF</Model>");
            xml = xml.Replace("<Model>CnnD</Model>", "<Model>CnnF</Model>");
            xml = xml.Replace("<Model>CnnE</Model>", "<Model>CnnF</Model>");
            using var reader = new StringReader(xml);
            return (Config)xmlSerializer.Deserialize(reader) ?? Config.Default;
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
