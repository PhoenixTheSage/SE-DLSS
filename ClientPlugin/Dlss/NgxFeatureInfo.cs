using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ClientPlugin.Dlss;

/// <summary>
/// MSVC x64 <c>NVSDK_NGX_FeatureCommonInfo</c> matching ngx_min.h / the C++ wrapper.
/// </summary>
internal sealed class NgxFeatureInfo : IDisposable
{
    private const int BlobSize = 256;

    [StructLayout(LayoutKind.Sequential)]
    private struct PathListInfo
    {
        public IntPtr Path;
        public uint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LoggingInfo
    {
        public IntPtr LoggingCallback;
        public int MinimumLoggingLevel;
        public byte DisableOtherLoggingSinks;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FeatureCommonInfo
    {
        public PathListInfo PathListInfo;
        public IntPtr InternalData;
        public LoggingInfo LoggingInfo;
    }

    private IntPtr _blob;
    private IntPtr _pathArray;
    private readonly List<IntPtr> _strings = [];

    internal IntPtr Pointer => _blob;

    internal static int NativeSize => Marshal.SizeOf<FeatureCommonInfo>();

    internal static NgxFeatureInfo Create(IEnumerable<string> paths)
    {
        var info = new NgxFeatureInfo();
        info.Build(paths);
        return info;
    }

    private void Build(IEnumerable<string> paths)
    {
        var unique = new List<string>();
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path))
                continue;
            var exists = false;
            foreach (var existing in unique)
            {
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
                unique.Add(path);
        }

        _blob = Marshal.AllocHGlobal(BlobSize);
        for (var i = 0; i < BlobSize; i++)
            Marshal.WriteByte(_blob, i, 0);

        if (unique.Count > 0)
        {
            _pathArray = Marshal.AllocHGlobal(unique.Count * IntPtr.Size);
            for (var i = 0; i < unique.Count; i++)
            {
                var str = Marshal.StringToHGlobalUni(unique[i]);
                _strings.Add(str);
                Marshal.WriteIntPtr(_pathArray, i * IntPtr.Size, str);
            }
        }

        var native = new FeatureCommonInfo
        {
            PathListInfo = new PathListInfo
            {
                Path = _pathArray,
                Length = (uint)unique.Count
            },
            LoggingInfo = new LoggingInfo
            {
                LoggingCallback = NgxLog.FunctionPointer,
                MinimumLoggingLevel = NgxLog.LevelVerbose,
                DisableOtherLoggingSinks = 0
            }
        };
        Marshal.StructureToPtr(native, _blob, false);
    }

    internal string Describe()
    {
        if (_blob == IntPtr.Zero)
            return "FeatureCommonInfo null";
        var bytes = new byte[40];
        Marshal.Copy(_blob, bytes, 0, bytes.Length);
        var callback = Marshal.ReadIntPtr(_blob, 24);
        return "FeatureCommonInfo size=" + NativeSize +
               " paths=" + _strings.Count +
               " pathArr=0x" + _pathArray.ToInt64().ToString("X") +
               " logCb=0x" + callback.ToInt64().ToString("X") +
               " hex=" + BitConverter.ToString(bytes);
    }

    public void Dispose()
    {
        foreach (var str in _strings)
            Marshal.FreeHGlobal(str);
        _strings.Clear();
        if (_pathArray != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_pathArray);
            _pathArray = IntPtr.Zero;
        }

        if (_blob != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_blob);
            _blob = IntPtr.Zero;
        }
    }
}
