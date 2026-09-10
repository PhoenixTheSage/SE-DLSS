using System;
using System.Reflection;
using ClientPlugin.Dlss;
using VRage.Utils;

namespace ClientPlugin.RichHud;

/// <summary>
/// Optional bind to Anomaly's Rich HUD extension.
/// Well-known type: <c>ClientPlugin.RichHud.TerminalConfigRegistry</c>.
/// <see href="https://github.com/PhoenixTheSage/Anomaly/wiki/Terminal-config"/>
/// </summary>
internal static class AnomalyTerminalHook
{
    public const string RegistryTypeName = "ClientPlugin.RichHud.TerminalConfigRegistry";
    public const string FolderTitle = "DLSS";
    public const string SettingsPage = "Settings";
    public const string PageTitle = FolderTitle;

    static readonly object Gate = new();
    static bool _installed;

    public static bool Installed
    {
        get
        {
            lock (Gate)
                return _installed;
        }
    }

    public static bool TryInstall()
    {
        lock (Gate)
        {
            if (_installed)
                return true;

            var page = RequestPageUnlocked();
            if (page == null)
                return false;

            Populate(page);
            _installed = true;
            MyLog.Default.WriteLine("DLSS: Rich HUD page under Anomaly Shaders / " + FolderTitle + " / " + SettingsPage);
            DebugLog.Write("Anomaly TerminalConfigRegistry page " + PageTitle);
            return true;
        }
    }

    public static void Reset()
    {
        lock (Gate)
            _installed = false;
    }

    static object RequestPageUnlocked()
    {
        Assembly[] assemblies;
        try
        {
            assemblies = AppDomain.CurrentDomain.GetAssemblies();
        }
        catch
        {
            return null;
        }

        foreach (var assembly in assemblies)
        {
            if (assembly == null || assembly == typeof(AnomalyTerminalHook).Assembly)
                continue;

            Type registry;
            try
            {
                registry = assembly.GetType(RegistryTypeName, throwOnError: false, ignoreCase: false);
            }
            catch
            {
                continue;
            }

            var requestFolder = FindStatic(registry, "RequestFolderPage", typeof(string), typeof(string));
            var request = FindStatic(registry, "RequestPage", typeof(string));
            if (requestFolder == null && request == null)
                continue;

            try
            {
                if (requestFolder != null)
                    return requestFolder.Invoke(null, new object[] { FolderTitle, SettingsPage });
                return request.Invoke(null, new object[] { PageTitle });
            }
            catch (Exception e)
            {
                DebugLog.Write("Anomaly RequestPage: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        return null;
    }

    static void Populate(object page)
    {
        var type = page.GetType();
        Invoke(type, page, "Category", "DLSS");
        Invoke(type, page, "Dropdown", "Anti-aliasing", typeof(AntiAliasingChoice),
            (Func<object>)(() => RichHudOptions.GetAntiAliasing()),
            (Action<object>)(v => RichHudOptions.SetAntiAliasing((AntiAliasingChoice)v)),
            "DLSS replaces FXAA; choose Off or FXAA to use the game's anti-aliasing.");
        Invoke(type, page, "Dropdown", "Mode", typeof(DlssMode),
            (Func<object>)(() => RichHudOptions.GetMode()),
            (Action<object>)(v => RichHudOptions.SetMode((DlssMode)v)),
            "Quality trades internal resolution against image quality. DLAA stays at native resolution.");
        Invoke(type, page, "Dropdown", "Model", typeof(DlssModel),
            (Func<object>)(() => RichHudOptions.GetModel()),
            (Action<object>)(v => RichHudOptions.SetModel((DlssModel)v)),
            "DLSS model. Latest Model uses transformer K; CNN F is the legacy option. " +
            "NVIDIA App overrides do not apply to this unofficial title.");
        Invoke(type, page, "Slider", "Sharpness", 0f, 1f,
            (Func<float>)RichHudOptions.GetSharpness,
            (Action<float>)RichHudOptions.SetSharpness,
            "Optional sharpening; transformer models may ignore it.",
            0.05f);
        Invoke(type, page, "Button", "Show Status",
            (Action)RichHudOptions.ShowStatus,
            "GPU, NGX support, resolutions, and Anomaly buffer status");
    }

    static void Invoke(Type type, object instance, string name, params object[] args)
    {
        var method = FindInstance(type, name, args);
        if (method == null)
        {
            DebugLog.Write("Anomaly terminal missing " + name);
            return;
        }

        method.Invoke(instance, PadDefaults(method, args));
    }

    static MethodInfo FindStatic(Type type, string name, params Type[] parameters)
    {
        if (type == null)
            return null;
        return type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, parameters, null);
    }

    static MethodInfo FindInstance(Type type, string name, object[] args)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        foreach (var method in type.GetMethods(flags))
        {
            if (method.Name != name || method.IsGenericMethodDefinition)
                continue;
            var parameters = method.GetParameters();
            if (!ArgumentsMatch(parameters, args))
                continue;
            return method;
        }

        return null;
    }

    static bool ArgumentsMatch(ParameterInfo[] parameters, object[] args)
    {
        if (args.Length > parameters.Length)
            return false;
        for (var i = 0; i < parameters.Length; i++)
        {
            if (i >= args.Length)
                return parameters[i].IsOptional || parameters[i].HasDefaultValue;
            var value = args[i];
            var expected = parameters[i].ParameterType;
            if (value == null)
            {
                if (expected.IsValueType && Nullable.GetUnderlyingType(expected) == null)
                    return false;
                continue;
            }

            if (!expected.IsInstanceOfType(value))
                return false;
        }

        return true;
    }

    static object[] PadDefaults(MethodInfo method, object[] args)
    {
        var parameters = method.GetParameters();
        if (args.Length == parameters.Length)
            return args;
        var padded = new object[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
            padded[i] = i < args.Length ? args[i] : parameters[i].DefaultValue;
        return padded;
    }
}
