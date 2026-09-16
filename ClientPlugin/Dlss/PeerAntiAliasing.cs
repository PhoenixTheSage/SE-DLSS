using System;
using System.Reflection;

namespace ClientPlugin.Dlss;

/// <summary>
/// Discovers FRS <c>AntiAliasingHandshake</c> by type name.
/// </summary>
internal static class PeerAntiAliasing
{
    public const string TypeName = "ClientPlugin.Frs.AntiAliasingHandshake";
    public const long FallbackGraphicsKey = 101;

    static Type _type;
    static MethodInfo _canOffer;
    static MethodInfo _getChoice;
    static MethodInfo _applyChoice;
    static long? _graphicsKey;

    public static bool Present
    {
        get
        {
            Ensure();
            return _type != null;
        }
    }

    public static bool CanOffer
    {
        get
        {
            Ensure();
            if (_canOffer == null)
                return false;
            try
            {
                return (bool)_canOffer.Invoke(null, null);
            }
            catch
            {
                return false;
            }
        }
    }

    public static long GraphicsComboKey
    {
        get
        {
            Ensure();
            return _graphicsKey ?? FallbackGraphicsKey;
        }
    }

    public static string GetChoice()
    {
        Ensure();
        if (_getChoice == null)
            return null;
        try
        {
            return _getChoice.Invoke(null, null) as string;
        }
        catch
        {
            return null;
        }
    }

    public static void ApplyChoice(string choice)
    {
        Ensure();
        if (_applyChoice == null || string.IsNullOrEmpty(choice))
            return;
        try
        {
            _applyChoice.Invoke(null, new object[] { choice });
        }
        catch (Exception e)
        {
            DebugLog.Write("peer ApplyChoice: " + e.GetType().Name + ": " + e.Message);
        }
    }

    static void Ensure()
    {
        if (_type != null)
            return;

        Type found = null;
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == null)
                    continue;
                Type type;
                try
                {
                    type = assembly.GetType(TypeName, throwOnError: false, ignoreCase: false);
                }
                catch
                {
                    continue;
                }

                if (type == null)
                    continue;
                found = type;
                break;
            }
        }
        catch
        {
            return;
        }

        if (found == null)
            return;

        _type = found;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        _canOffer = found.GetMethod("CanOffer", flags, null, Type.EmptyTypes, null);
        _getChoice = found.GetMethod("GetChoice", flags, null, Type.EmptyTypes, null);
        _applyChoice = found.GetMethod("ApplyChoice", flags, null, new[] { typeof(string) }, null);
        var key = found.GetField("GraphicsComboKey", flags);
        if (key != null && key.FieldType == typeof(long))
            _graphicsKey = (long)key.GetValue(null);
        DebugLog.Write("handshake peer " + TypeName + " key=" + GraphicsComboKey);
    }
}
