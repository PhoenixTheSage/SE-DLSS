using System;
using System.Runtime.InteropServices;

namespace ClientPlugin.Dlss;

/// <summary>
/// Invokes NGX driver-core initialization from a mapped native module.
/// </summary>
/// <remarks>
/// The driver resolves its caller from the native return address. A normal
/// .NET Framework P/Invoke return address belongs to JIT code and cannot be
/// resolved to an HMODULE. DispCallFunc performs the call from OleAut32.dll,
/// preserving a real PE-backed caller without a custom native bridge.
/// </remarks>
internal static class NgxOleInvoker
{
    private const int CcCdecl = 1;
    private const ushort VtI4 = 3;
    private const ushort VtUi4 = 19;
    private const ushort VtUi8 = 21;
    private const int VariantSize = 24;
    private const int VariantValueOffset = 8;

    internal static int InitProject(
        IntPtr function,
        IntPtr projectId,
        int engineType,
        IntPtr engineVersion,
        IntPtr logPath,
        IntPtr device,
        int sdkVersion,
        IntPtr featureInfo)
    {
        return Invoke(
            function,
            [VtUi8, VtI4, VtUi8, VtUi8, VtUi8, VtUi4, VtUi8],
            [
                projectId.ToInt64(),
                engineType,
                engineVersion.ToInt64(),
                logPath.ToInt64(),
                device.ToInt64(),
                unchecked((uint)sdkVersion),
                featureInfo.ToInt64()
            ]);
    }

    internal static int InitExt(
        IntPtr function,
        ulong applicationId,
        IntPtr logPath,
        IntPtr device,
        int sdkVersion,
        IntPtr featureInfo)
    {
        return Invoke(
            function,
            [VtUi8, VtUi8, VtUi8, VtUi4, VtUi8],
            [
                unchecked((long)applicationId),
                logPath.ToInt64(),
                device.ToInt64(),
                unchecked((uint)sdkVersion),
                featureInfo.ToInt64()
            ]);
    }

    internal static int Init(
        IntPtr function,
        ulong applicationId,
        IntPtr logPath,
        IntPtr device,
        int sdkVersion)
    {
        return Invoke(
            function,
            [VtUi8, VtUi8, VtUi8, VtUi4],
            [
                unchecked((long)applicationId),
                logPath.ToInt64(),
                device.ToInt64(),
                unchecked((uint)sdkVersion)
            ]);
    }

    private static int Invoke(IntPtr function, ushort[] types, long[] arguments)
    {
        if (function == IntPtr.Zero)
            throw new ArgumentNullException(nameof(function));
        if (IntPtr.Size != 8)
            throw new PlatformNotSupportedException("NGX requires a 64-bit process");
        if (types.Length != arguments.Length)
            throw new ArgumentException("NGX argument type/value count mismatch");

        var storage = Marshal.AllocHGlobal(VariantSize * arguments.Length);
        var result = Marshal.AllocHGlobal(VariantSize);
        var argumentPointers = new IntPtr[arguments.Length];
        try
        {
            Zero(storage, VariantSize * arguments.Length);
            Zero(result, VariantSize);
            for (var i = 0; i < arguments.Length; i++)
            {
                var argument = IntPtr.Add(storage, i * VariantSize);
                argumentPointers[i] = argument;
                Marshal.WriteInt64(argument, VariantValueOffset, arguments[i]);
            }

            var address = new UIntPtr(unchecked((ulong)function.ToInt64()));
            var hr = DispCallFunc(
                IntPtr.Zero,
                address,
                CcCdecl,
                VtI4,
                (uint)arguments.Length,
                types,
                argumentPointers,
                result);
            if (hr != 0)
                throw new ExternalException("OleAut32 DispCallFunc failed", hr);

            return Marshal.ReadInt32(result, VariantValueOffset);
        }
        finally
        {
            Marshal.FreeHGlobal(result);
            Marshal.FreeHGlobal(storage);
        }
    }

    private static void Zero(IntPtr pointer, int length)
    {
        for (var i = 0; i < length; i++)
            Marshal.WriteByte(pointer, i, 0);
    }

    [DllImport("oleaut32.dll", ExactSpelling = true)]
    private static extern int DispCallFunc(
        IntPtr pvInstance,
        UIntPtr oVft,
        int cc,
        ushort vtReturn,
        uint cActuals,
        [In] ushort[] prgvt,
        [In] IntPtr[] prgpvarg,
        IntPtr pvargResult);
}
