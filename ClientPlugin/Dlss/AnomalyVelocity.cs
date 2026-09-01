using System;
using System.Reflection;
using System.Text;
using VRage.Utils;

namespace ClientPlugin.Dlss;

/// <summary>
/// Resolves Anomaly's <c>ClientPlugin.Velocity.VelocityRegistry.Active</c> by well-known
/// type name. Do not add a compile-time project reference to Anomaly.
/// </summary>
internal static class AnomalyVelocity
{
    public const string RegistryTypeName = "ClientPlugin.Velocity.VelocityRegistry";

    // Matches Anomaly's VelocityConvention flags.
    public const int ConventionUnjittered = 1;
    public const int ConventionPixelSpace = 2;
    public const int ConventionMatchesRenderResolution = 4;
    public const int ExpectedConvention =
        ConventionUnjittered | ConventionPixelSpace | ConventionMatchesRenderResolution;

    private static readonly object Gate = new();
    private static PropertyInfo _activeProperty;
    private static string _registryAssembly;
    private static Type _bufferType;
    private static PropertyInfo _isAvailable;
    private static PropertyInfo _nativeResource;
    private static PropertyInfo _width;
    private static PropertyInfo _height;
    private static PropertyInfo _convention;
    private static PropertyInfo _historyValid;
    private static bool _loadHooked;
    private static bool _loggedFound;
    private static bool _loggedMissing;
    private static bool _loggedUnavailable;
    private static bool _loggedConvention;
    private static bool _loggedSize;

    public static bool RegistryFound
    {
        get
        {
            lock (Gate)
                return _activeProperty != null;
        }
    }

    public static void Probe()
    {
        lock (Gate)
        {
            EnsureLoadHook();
            if (_activeProperty == null)
                ScanAssembliesUnlocked();
        }
    }

    public static void Reset()
    {
        lock (Gate)
        {
            if (_loadHooked)
            {
                try
                {
                    AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                }
                catch
                {
                    // ignored
                }

                _loadHooked = false;
            }

            ClearAccessors();
            _activeProperty = null;
            _registryAssembly = null;
            _loggedFound = _loggedMissing = _loggedUnavailable = false;
            _loggedConvention = _loggedSize = false;
        }
    }

    public static bool TryGetLive(int expectedWidth, int expectedHeight, out IntPtr native, out bool historyValid)
    {
        native = IntPtr.Zero;
        historyValid = false;
        if (!TryReadActive(out var available, out var resource, out var width, out var height,
                out var convention, out historyValid))
            return false;
        if (!available || resource == IntPtr.Zero)
            return false;

        if ((convention & ExpectedConvention) != ExpectedConvention)
            LogConventionOnce(convention);

        if (expectedWidth > 0 && expectedHeight > 0 &&
            (width != expectedWidth || height != expectedHeight))
        {
            LogSizeOnce(width, height, expectedWidth, expectedHeight);
            return false;
        }

        native = resource;
        return true;
    }

    public static void NoteCameraFallback()
    {
        if (RegistryFound)
            LogUnavailableOnce();
        else
            LogMissingOnce();
    }

    public static void AppendStatus(StringBuilder sb)
    {
        if (sb == null)
            return;
        if (Config.Current != null && !Config.Current.UseAnomalyMotionVectors)
        {
            sb.AppendLine("Motion vectors: camera (Anomaly integration disabled)");
            return;
        }

        Probe();
        if (!TryReadActive(out var available, out var resource, out var width, out var height,
                out var convention, out var historyValid))
        {
            sb.AppendLine(RegistryFound
                ? "Motion vectors: camera (Anomaly unreadable)"
                : "Motion vectors: camera (Anomaly not loaded)");
            return;
        }

        if (!available || resource == IntPtr.Zero)
        {
            sb.AppendLine("Motion vectors: camera (Anomaly buffer not ready)");
            return;
        }

        var sizeOk = DlssRuntime.InternalWidth <= 0 ||
                     (width == DlssRuntime.InternalWidth && height == DlssRuntime.InternalHeight);
        sb.Append("Motion vectors: ");
        sb.Append(sizeOk ? "Anomaly " : "camera (Anomaly size mismatch) ");
        sb.Append(width).Append('x').Append(height);
        sb.Append(" history=").Append(historyValid ? "yes" : "no");
        sb.Append(" convention=0x").Append(convention.ToString("x"));
        sb.AppendLine();
    }

    private static bool TryReadActive(
        out bool available,
        out IntPtr native,
        out int width,
        out int height,
        out int convention,
        out bool historyValid)
    {
        available = false;
        native = IntPtr.Zero;
        width = 0;
        height = 0;
        convention = 0;
        historyValid = false;

        Probe();
        PropertyInfo active;
        lock (Gate)
            active = _activeProperty;
        if (active == null)
            return false;

        try
        {
            var buffer = active.GetValue(null);
            if (buffer == null)
                return false;

            lock (Gate)
            {
                if (!EnsureAccessorsUnlocked(buffer.GetType()))
                    return false;
                available = ReadBool(_isAvailable, buffer);
                native = ReadIntPtr(_nativeResource, buffer);
                width = ReadInt(_width, buffer);
                height = ReadInt(_height, buffer);
                convention = ReadInt(_convention, buffer);
                historyValid = ReadBool(_historyValid, buffer);
            }

            return true;
        }
        catch (Exception e)
        {
            lock (Gate)
            {
                ClearAccessors();
                _activeProperty = null;
            }

            DebugLog.Write("Anomaly Active read failed: " + e.GetType().Name + ": " + e.Message);
            return false;
        }
    }

    private static void EnsureLoadHook()
    {
        if (_loadHooked)
            return;
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        _loadHooked = true;
    }

    private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
    {
        if (args?.LoadedAssembly == null)
            return;
        lock (Gate)
        {
            if (_activeProperty == null)
                TryBindRegistryUnlocked(args.LoadedAssembly);
        }
    }

    private static void ScanAssembliesUnlocked()
    {
        Assembly[] assemblies;
        try
        {
            assemblies = AppDomain.CurrentDomain.GetAssemblies();
        }
        catch
        {
            return;
        }

        foreach (var assembly in assemblies)
        {
            TryBindRegistryUnlocked(assembly);
            if (_activeProperty != null)
                return;
        }
    }

    private static void TryBindRegistryUnlocked(Assembly assembly)
    {
        if (_activeProperty != null || assembly == null)
            return;
        if (assembly == typeof(AnomalyVelocity).Assembly)
            return;
        try
        {
            var type = assembly.GetType(RegistryTypeName, throwOnError: false, ignoreCase: false);
            var prop = type?.GetProperty("Active", BindingFlags.Public | BindingFlags.Static);
            if (prop == null || !prop.CanRead)
                return;

            _activeProperty = prop;
            _registryAssembly = assembly.GetName().Name;
            if (_loggedFound)
                return;
            _loggedFound = true;
            MyLog.Default.WriteLine("DLSS: bound Anomaly VelocityRegistry from " + _registryAssembly);
            DebugLog.Write("Anomaly VelocityRegistry from " + assembly.FullName);
        }
        catch (Exception e)
        {
            DebugLog.Write("Anomaly scan skipped: " + e.GetType().Name);
        }
    }

    private static bool EnsureAccessorsUnlocked(Type bufferType)
    {
        if (bufferType == null)
            return false;
        if (_bufferType == bufferType && _isAvailable != null)
            return true;

        ClearAccessors();
        _bufferType = bufferType;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        _isAvailable = bufferType.GetProperty("IsAvailable", flags);
        _nativeResource = bufferType.GetProperty("NativeResource", flags);
        _width = bufferType.GetProperty("Width", flags);
        _height = bufferType.GetProperty("Height", flags);
        _convention = bufferType.GetProperty("Convention", flags);
        _historyValid = bufferType.GetProperty("HistoryValid", flags);
        return _isAvailable != null && _nativeResource != null && _width != null &&
               _height != null && _convention != null && _historyValid != null;
    }

    private static void ClearAccessors()
    {
        _bufferType = null;
        _isAvailable = _nativeResource = _width = _height = _convention = _historyValid = null;
    }

    private static bool ReadBool(PropertyInfo prop, object instance)
    {
        var value = prop?.GetValue(instance);
        return value is bool b && b;
    }

    private static int ReadInt(PropertyInfo prop, object instance)
    {
        var value = prop?.GetValue(instance);
        if (value == null)
            return 0;
        if (value is int i)
            return i;
        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private static IntPtr ReadIntPtr(PropertyInfo prop, object instance)
    {
        var value = prop?.GetValue(instance);
        return value is IntPtr p ? p : IntPtr.Zero;
    }

    private static void LogMissingOnce()
    {
        if (_loggedMissing)
            return;
        _loggedMissing = true;
        DebugLog.Write("Anomaly not loaded; camera motion vectors");
    }

    private static void LogUnavailableOnce()
    {
        if (_loggedUnavailable)
            return;
        _loggedUnavailable = true;
        DebugLog.Write("Anomaly buffer not ready; camera motion vectors");
    }

    private static void LogConventionOnce(int convention)
    {
        if (_loggedConvention)
            return;
        _loggedConvention = true;
        MyLog.Default.Warning(
            "DLSS: Anomaly velocity convention 0x" + convention.ToString("x") +
            " is missing expected flags 0x" + ExpectedConvention.ToString("x"));
        DebugLog.Write("Anomaly convention 0x" + convention.ToString("x"));
    }

    private static void LogSizeOnce(int width, int height, int expectedWidth, int expectedHeight)
    {
        if (_loggedSize)
            return;
        _loggedSize = true;
        MyLog.Default.Warning(
            "DLSS: Anomaly velocity size " + width + "x" + height +
            " does not match internal " + expectedWidth + "x" + expectedHeight +
            "; using camera motion vectors.");
        DebugLog.Write("Anomaly size mismatch " + width + "x" + height +
                       " vs " + expectedWidth + "x" + expectedHeight);
    }
}
