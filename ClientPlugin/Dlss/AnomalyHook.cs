using System;
using System.Reflection;
using System.Text;
using VRage.Utils;

namespace ClientPlugin.Dlss;

/// <summary>
/// Optional runtime binding to Anomaly Shader Framework. Resolves well-known
/// types by name — do not add a compile-time project reference.
/// <see href="https://github.com/PhoenixTheSage/Anomaly/wiki"/>
/// </summary>
internal static class AnomalyHook
{
    public const string RegistryTypeName = "ClientPlugin.Velocity.VelocityRegistry";
    public const string CatalogTypeName = "ClientPlugin.Buffers.BufferCatalog";
    public const string OwnedPassTypeName = "ClientPlugin.Shaders.OwnedPassRegistry";
    public const string FrameTemporalTypeName = "ClientPlugin.Shaders.FrameTemporal";
    public const string ReactiveMaskName = "reactiveMask";
    public const string VelocityName = "velocity";

    // Matches Anomaly's VelocityConvention flags.
    public const int ConventionUnjittered = 1;
    public const int ConventionPixelSpace = 2;
    public const int ConventionMatchesRenderResolution = 4;
    public const int ExpectedConvention =
        ConventionUnjittered | ConventionPixelSpace | ConventionMatchesRenderResolution;

    private static readonly object Gate = new();
    private static readonly object[] ReactiveNameArgs = { ReactiveMaskName };
    private static readonly object[] VelocityNameArgs = { VelocityName };

    private static PropertyInfo _activeProperty;
    private static MethodInfo _catalogActive;
    private static MethodInfo _notifyUpscale;
    private static MethodInfo _invalidateHistory;
    private static FieldInfo _anomalyConfigCurrent;
    private static PropertyInfo _velocitySource;
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
    private static bool _loggedReactiveSize;
    private static bool _loggedNotify;
    private static bool _notifiedThisFrame;
    private static bool _notifiedLastEvaluate;
    private static bool _usedReactiveThisFrame;

    public static bool RegistryFound
    {
        get
        {
            lock (Gate)
                return _activeProperty != null;
        }
    }

    public static bool CatalogFound
    {
        get
        {
            lock (Gate)
                return _catalogActive != null;
        }
    }

    public static bool CanNotifyUpscale
    {
        get
        {
            lock (Gate)
                return _notifyUpscale != null;
        }
    }

    public static void Probe()
    {
        lock (Gate)
        {
            EnsureLoadHook();
            if (_activeProperty == null || _catalogActive == null || _notifyUpscale == null ||
                _invalidateHistory == null || _velocitySource == null)
                ScanAssembliesUnlocked();
        }
    }

    public static void BeginFrame()
    {
        lock (Gate)
        {
            _notifiedThisFrame = false;
            _usedReactiveThisFrame = false;
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
            _catalogActive = null;
            _notifyUpscale = null;
            _invalidateHistory = null;
            _anomalyConfigCurrent = null;
            _velocitySource = null;
            _registryAssembly = null;
            _loggedFound = _loggedMissing = _loggedUnavailable = false;
            _loggedConvention = _loggedSize = _loggedReactiveSize = false;
            _loggedNotify = _notifiedThisFrame = _notifiedLastEvaluate = _usedReactiveThisFrame = false;
        }
    }

    public static bool TryGetLive(int expectedWidth, int expectedHeight, out IntPtr native, out bool historyValid)
    {
        native = IntPtr.Zero;
        historyValid = false;
        if (!TryReadVelocity(out var available, out var resource, out var width, out var height,
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

    public static bool TryGetReactiveMask(int expectedWidth, int expectedHeight, out IntPtr native)
    {
        native = IntPtr.Zero;
        if (!TryReadCatalog(ReactiveNameArgs, out var available, out var resource, out var width, out var height))
            return false;
        if (!available || resource == IntPtr.Zero)
            return false;

        if (expectedWidth > 0 && expectedHeight > 0 &&
            (width != expectedWidth || height != expectedHeight))
        {
            LogReactiveSizeOnce(width, height, expectedWidth, expectedHeight);
            return false;
        }

        native = resource;
        lock (Gate)
            _usedReactiveThisFrame = true;
        return true;
    }

    public static void NotifyUpscaleComplete()
    {
        MethodInfo notify;
        lock (Gate)
        {
            if (_notifiedThisFrame)
                return;
            notify = _notifyUpscale;
        }

        if (notify == null)
        {
            Probe();
            lock (Gate)
                notify = _notifyUpscale;
            if (notify == null)
                return;
        }

        try
        {
            notify.Invoke(null, notify.GetParameters().Length == 0 ? null : new object[] { null });
            lock (Gate)
            {
                _notifiedThisFrame = true;
                _notifiedLastEvaluate = true;
                if (_loggedNotify)
                    return;
                _loggedNotify = true;
            }

            DebugLog.Write("Anomaly NotifyUpscaleComplete");
        }
        catch (Exception e)
        {
            DebugLog.Write("Anomaly NotifyUpscaleComplete: " + e.GetType().Name + ": " + e.Message);
        }
    }

    public static void InvalidateHistory()
    {
        MethodInfo invalidate;
        lock (Gate)
            invalidate = _invalidateHistory;
        if (invalidate == null)
        {
            Probe();
            lock (Gate)
                invalidate = _invalidateHistory;
            if (invalidate == null)
                return;
        }

        try
        {
            invalidate.Invoke(null, null);
        }
        catch (Exception e)
        {
            DebugLog.Write("Anomaly InvalidateHistory: " + e.GetType().Name + ": " + e.Message);
        }
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
        }
        else
        {
            Probe();
            if (!TryReadVelocity(out var available, out var resource, out var width, out var height,
                    out var convention, out var historyValid))
            {
                sb.AppendLine(RegistryFound
                    ? "Motion vectors: camera (Anomaly unreadable)"
                    : "Motion vectors: camera (Anomaly not loaded)");
            }
            else if (!available || resource == IntPtr.Zero)
            {
                sb.AppendLine("Motion vectors: camera (Anomaly buffer not ready)");
            }
            else
            {
                var sizeOk = DlssRuntime.InternalWidth <= 0 ||
                             (width == DlssRuntime.InternalWidth && height == DlssRuntime.InternalHeight);
                sb.Append("Motion vectors: ");
                if (!sizeOk)
                    sb.Append("camera (Anomaly size mismatch) ");
                else if (!DlssRuntime.UsedExternalVelocity)
                    sb.Append("camera (Anomaly live, not bound last evaluate) ");
                else
                {
                    sb.Append("Anomaly ");
                    var source = ReadVelocitySource();
                    if (!string.IsNullOrEmpty(source))
                        sb.Append(source).Append(' ');
                }

                sb.Append(width).Append('x').Append(height);
                sb.Append(" history=").Append(historyValid ? "yes" : "no");
                sb.Append(" convention=0x").Append(convention.ToString("x"));
                sb.AppendLine();
            }
        }

        AppendReactiveStatus(sb);
        AppendUpscaleStatus(sb);
    }

    private static void AppendReactiveStatus(StringBuilder sb)
    {
        if (!CatalogFound && !RegistryFound)
        {
            sb.AppendLine("Reactive mask: Anomaly not loaded");
            return;
        }

        if (!CatalogFound)
        {
            sb.AppendLine("Reactive mask: catalog not present");
            return;
        }

        if (!TryReadCatalog(ReactiveNameArgs, out var available, out var resource, out var width, out var height) ||
            !available || resource == IntPtr.Zero)
        {
            sb.AppendLine("Reactive mask: not published (no Reactive pack)");
            return;
        }

        var sizeOk = DlssRuntime.InternalWidth <= 0 ||
                     (width == DlssRuntime.InternalWidth && height == DlssRuntime.InternalHeight);
        sb.Append("Reactive mask: ");
        sb.Append(sizeOk ? "Anomaly " : "skipped (size mismatch) ");
        sb.Append(width).Append('x').Append(height);
        lock (Gate)
            sb.Append(_usedReactiveThisFrame ? " bound" : "");
        sb.AppendLine();
    }

    private static void AppendUpscaleStatus(StringBuilder sb)
    {
        if (!CanNotifyUpscale && !RegistryFound)
        {
            sb.AppendLine("AfterUpscale: Anomaly not loaded");
            return;
        }

        if (!CanNotifyUpscale)
        {
            sb.AppendLine("AfterUpscale: notify API not present");
            return;
        }

        lock (Gate)
            sb.AppendLine(_notifiedLastEvaluate
                ? "AfterUpscale: notified"
                : "AfterUpscale: waiting for evaluate");
    }

    private static bool TryReadVelocity(
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
        if (active != null)
        {
            try
            {
                var buffer = active.GetValue(null);
                if (buffer != null &&
                    TryReadBuffer(buffer, requireVelocityFields: true, out available, out native, out width,
                        out height, out convention, out historyValid))
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
            }
        }

        // Catalog Active("velocity") aliases the same producer; no convention flags.
        if (!TryReadCatalog(VelocityNameArgs, out available, out native, out width, out height))
            return false;
        convention = ExpectedConvention;
        historyValid = Jitter.HasPrevious;
        return true;
    }

    private static bool TryReadCatalog(object[] nameArgs, out bool available, out IntPtr native, out int width,
        out int height)
    {
        available = false;
        native = IntPtr.Zero;
        width = 0;
        height = 0;

        Probe();
        MethodInfo active;
        lock (Gate)
            active = _catalogActive;
        if (active == null)
            return false;

        try
        {
            var buffer = active.Invoke(null, nameArgs);
            if (buffer == null)
                return false;
            return TryReadBuffer(buffer, requireVelocityFields: false, out available, out native, out width,
                out height, out _, out _);
        }
        catch (Exception e)
        {
            lock (Gate)
            {
                ClearAccessors();
                _catalogActive = null;
            }

            DebugLog.Write("Anomaly catalog read failed: " + e.GetType().Name + ": " + e.Message);
            return false;
        }
    }

    private static bool TryReadBuffer(
        object buffer,
        bool requireVelocityFields,
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

        lock (Gate)
        {
            if (!EnsureAccessorsUnlocked(buffer.GetType(), requireVelocityFields))
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
            TryBindUnlocked(args.LoadedAssembly);
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
            TryBindUnlocked(assembly);
            if (_activeProperty != null && _catalogActive != null && _notifyUpscale != null &&
                _invalidateHistory != null && _velocitySource != null)
                return;
        }
    }

    private static void TryBindUnlocked(Assembly assembly)
    {
        if (assembly == null || assembly == typeof(AnomalyHook).Assembly)
            return;
        try
        {
            var bound = false;
            if (_activeProperty == null)
            {
                var type = assembly.GetType(RegistryTypeName, throwOnError: false, ignoreCase: false);
                var prop = type?.GetProperty("Active", BindingFlags.Public | BindingFlags.Static);
                if (prop != null && prop.CanRead)
                {
                    _activeProperty = prop;
                    _registryAssembly = assembly.GetName().Name;
                    bound = true;
                }
            }

            if (_catalogActive == null)
            {
                var catalog = assembly.GetType(CatalogTypeName, throwOnError: false, ignoreCase: false);
                _catalogActive = FindStatic(catalog, "Active", typeof(string));
                bound |= _catalogActive != null;
            }

            if (_notifyUpscale == null)
            {
                var owned = assembly.GetType(OwnedPassTypeName, throwOnError: false, ignoreCase: false);
                _notifyUpscale = FindStatic(owned, "NotifyUpscaleComplete") ??
                                 FindStatic(owned, "NotifyUpscaleComplete", typeof(object));
                bound |= _notifyUpscale != null;
            }

            if (_invalidateHistory == null)
            {
                var temporal = assembly.GetType(FrameTemporalTypeName, throwOnError: false, ignoreCase: false);
                _invalidateHistory = FindStatic(temporal, "InvalidateHistory");
                bound |= _invalidateHistory != null;
            }

            if (_velocitySource == null)
                bound |= BindAnomalyConfigUnlocked(assembly);

            if (bound)
                LogFoundOnce(assembly);
        }
        catch (Exception e)
        {
            DebugLog.Write("Anomaly scan skipped: " + e.GetType().Name);
        }
    }

    private static bool BindAnomalyConfigUnlocked(Assembly assembly)
    {
        var configType = assembly.GetType("ClientPlugin.Config", throwOnError: false, ignoreCase: false);
        if (configType == null)
            return false;
        var current = configType.GetField("Current", BindingFlags.Public | BindingFlags.Static);
        var source = configType.GetProperty("VelocitySource", BindingFlags.Public | BindingFlags.Instance);
        if (current == null || source == null || !source.CanRead)
            return false;
        _anomalyConfigCurrent = current;
        _velocitySource = source;
        return true;
    }

    private static string ReadVelocitySource()
    {
        FieldInfo current;
        PropertyInfo source;
        lock (Gate)
        {
            current = _anomalyConfigCurrent;
            source = _velocitySource;
        }

        if (current == null || source == null)
            return null;
        try
        {
            var cfg = current.GetValue(null);
            var value = source.GetValue(cfg);
            return value?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static MethodInfo FindStatic(Type type, string name, params Type[] parameters)
    {
        if (type == null)
            return null;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        if (parameters == null || parameters.Length == 0)
            return type.GetMethod(name, flags, null, Type.EmptyTypes, null);
        return type.GetMethod(name, flags, null, parameters, null);
    }

    private static void LogFoundOnce(Assembly assembly)
    {
        if (_loggedFound)
            return;
        _loggedFound = true;
        var name = _registryAssembly ?? assembly.GetName().Name;
        MyLog.Default.WriteLine("DLSS: bound Anomaly types from " + name);
        DebugLog.Write("Anomaly types from " + assembly.FullName +
                       " velocity=" + (_activeProperty != null) +
                       " catalog=" + (_catalogActive != null) +
                       " afterUpscale=" + (_notifyUpscale != null) +
                       " temporal=" + (_invalidateHistory != null) +
                       " source=" + (_velocitySource != null));
    }

    private static bool EnsureAccessorsUnlocked(Type bufferType, bool requireVelocityFields)
    {
        if (bufferType == null)
            return false;
        if (_bufferType == bufferType && _isAvailable != null)
        {
            if (!requireVelocityFields)
                return true;
            return _convention != null && _historyValid != null;
        }

        ClearAccessors();
        _bufferType = bufferType;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        _isAvailable = bufferType.GetProperty("IsAvailable", flags);
        _nativeResource = bufferType.GetProperty("NativeResource", flags);
        _width = bufferType.GetProperty("Width", flags);
        _height = bufferType.GetProperty("Height", flags);
        _convention = bufferType.GetProperty("Convention", flags);
        _historyValid = bufferType.GetProperty("HistoryValid", flags);
        if (_isAvailable == null || _nativeResource == null || _width == null || _height == null)
            return false;
        return !requireVelocityFields || (_convention != null && _historyValid != null);
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

    private static void LogReactiveSizeOnce(int width, int height, int expectedWidth, int expectedHeight)
    {
        if (_loggedReactiveSize)
            return;
        _loggedReactiveSize = true;
        MyLog.Default.Warning(
            "DLSS: Anomaly reactiveMask size " + width + "x" + height +
            " does not match internal " + expectedWidth + "x" + expectedHeight +
            "; skipping bias mask.");
        DebugLog.Write("Anomaly reactive size mismatch " + width + "x" + height +
                       " vs " + expectedWidth + "x" + expectedHeight);
    }
}
