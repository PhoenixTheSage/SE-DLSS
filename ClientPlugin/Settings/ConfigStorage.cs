using System;
using System.IO;
using System.Xml.Serialization;
using VRage.FileSystem;
using VRage.Utils;

namespace ClientPlugin.Settings;

public static class ConfigStorage
{
    private static readonly string ConfigFileName = string.Concat(Plugin.Name, ".cfg");
    private static string ConfigFilePath => Path.Combine(MyFileSystem.UserDataPath, "Storage", ConfigFileName);

    public static void Save(Config config)
    {
        var path = ConfigFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var text = File.CreateText(path))
            new XmlSerializer(typeof(Config)).Serialize(text, config);
    }

    public static Config Load()
    {
        var path = ConfigFilePath;
        if (!File.Exists(path))
        {
            return Config.Default;
        }

        var xmlSerializer = new XmlSerializer(typeof(Config));
        Config.SuppressApply = true;
        try
        {
            var xml = File.ReadAllText(path);
            xml = xml.Replace("LatestRecommended", "LatestModel");
            xml = xml.Replace("<Evaluate>Ldr</Evaluate>", "");
            xml = xml.Replace("<Evaluate>Hdr</Evaluate>", "");
            using (var reader = new StringReader(xml))
                return (Config)xmlSerializer.Deserialize(reader) ?? Config.Default;
        }
        catch (Exception)
        {
            MyLog.Default.Warning($"{ConfigFileName}: Failed to read config file: {ConfigFilePath}");
        }
        finally
        {
            Config.SuppressApply = false;
        }

        return Config.Default;
    }
        
}