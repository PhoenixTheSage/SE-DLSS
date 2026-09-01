using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ClientPlugin.Dlss;

/// <summary>
/// MSVC vtable for NVIDIA's <c>NVSDK_NGX_Parameter</c>.
/// </summary>
internal sealed class NgxParameter
{
    // MSVC emits each overloaded virtual group in reverse declaration order:
    // Set: void*, D3D12, D3D11, int, uint, double, float, ull
    // Get: void**, D3D12**, D3D11**, int*, uint*, double*, float*, ull*
    private const int SlotSetVoid = 0;
    private const int SlotSetD3D11 = 2;
    private const int SlotSetInt = 3;
    private const int SlotSetUInt = 4;
    private const int SlotSetFloat = 6;
    private const int SlotSetUInt64 = 7;
    private const int SlotGetVoid = 8;
    private const int SlotGetD3D11 = 10;
    private const int SlotGetInt = 11;
    private const int SlotGetUInt = 12;
    private const int SlotGetFloat = 14;
    private const int SlotReset = 16;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetUInt64Fn(IntPtr self, IntPtr name, ulong value);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetFloatFn(IntPtr self, IntPtr name, float value);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetUIntFn(IntPtr self, IntPtr name, uint value);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetIntFn(IntPtr self, IntPtr name, int value);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetPtrFn(IntPtr self, IntPtr name, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int GetFloatFn(IntPtr self, IntPtr name, out float value);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int GetUIntFn(IntPtr self, IntPtr name, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int GetIntFn(IntPtr self, IntPtr name, out int value);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int GetPtrFn(IntPtr self, IntPtr name, out IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void ResetFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int OptimalSettingsFn(IntPtr parameters);

    private static readonly Dictionary<IntPtr, VTable> Tables = new();

    private readonly IntPtr _self;
    private readonly VTable _vt;

    private NgxParameter(IntPtr self, VTable vt)
    {
        _self = self;
        _vt = vt;
    }

    internal IntPtr Pointer => _self;

    internal static NgxParameter FromNative(IntPtr self)
    {
        if (self == IntPtr.Zero)
            return null;
        var vptr = Marshal.ReadIntPtr(self);
        if (vptr == IntPtr.Zero)
            return null;
        if (!Tables.TryGetValue(vptr, out var vt))
        {
            vt = VTable.Read(vptr);
            Tables[vptr] = vt;
        }

        return new NgxParameter(self, vt);
    }

    internal void Set(IntPtr name, ulong value) => _vt.SetUInt64(_self, name, value);

    internal void Set(IntPtr name, float value) => _vt.SetFloat(_self, name, value);

    internal void Set(IntPtr name, uint value) => _vt.SetUInt(_self, name, value);

    internal void Set(IntPtr name, int value) => _vt.SetInt(_self, name, value);

    internal unsafe void SetD3D11(IntPtr name, IntPtr resource)
    {
        // A raw calli exactly matches the MSVC x64 virtual member call:
        // RCX=this, RDX=name, R8=ID3D11Resource*.
        var set = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)_vt.SetD3D11;
        set(_self, name, resource);
    }

    internal void SetVoid(IntPtr name, IntPtr value) => _vt.SetVoid(_self, name, value);

    internal int Get(IntPtr name, out float value) => _vt.GetFloat(_self, name, out value);

    internal int Get(IntPtr name, out uint value) => _vt.GetUInt(_self, name, out value);

    internal int Get(IntPtr name, out int value) => _vt.GetInt(_self, name, out value);

    internal int Get(IntPtr name, out IntPtr value) => _vt.GetVoid(_self, name, out value);

    internal unsafe int GetD3D11(IntPtr name, out IntPtr value)
    {
        var nativeValue = IntPtr.Zero;
        var get = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr*, int>)_vt.GetD3D11;
        var result = get(_self, name, &nativeValue);
        value = nativeValue;
        return result;
    }

    internal void Reset() => _vt.Reset(_self);

    internal bool TryInvokeOptimalSettings()
    {
        if (NgxResult.Failed(_vt.GetVoid(_self, NgxNames.OptimalSettingsCallback, out var pfn)) ||
            pfn == IntPtr.Zero)
            return false;

        var invoke = Marshal.GetDelegateForFunctionPointer<OptimalSettingsFn>(pfn);
        return !NgxResult.Failed(invoke(_self));
    }

    internal static void ClearCache()
    {
        Tables.Clear();
    }

    private sealed class VTable
    {
        internal SetUInt64Fn SetUInt64;
        internal SetFloatFn SetFloat;
        internal SetUIntFn SetUInt;
        internal SetIntFn SetInt;
        internal IntPtr SetD3D11;
        internal SetPtrFn SetVoid;
        internal GetFloatFn GetFloat;
        internal GetUIntFn GetUInt;
        internal GetIntFn GetInt;
        internal IntPtr GetD3D11;
        internal GetPtrFn GetVoid;
        internal ResetFn Reset;

        internal static VTable Read(IntPtr vptr)
        {
            return new VTable
            {
                SetUInt64 = Slot<SetUInt64Fn>(vptr, SlotSetUInt64),
                SetFloat = Slot<SetFloatFn>(vptr, SlotSetFloat),
                SetUInt = Slot<SetUIntFn>(vptr, SlotSetUInt),
                SetInt = Slot<SetIntFn>(vptr, SlotSetInt),
                SetD3D11 = SlotPointer(vptr, SlotSetD3D11),
                SetVoid = Slot<SetPtrFn>(vptr, SlotSetVoid),
                GetFloat = Slot<GetFloatFn>(vptr, SlotGetFloat),
                GetUInt = Slot<GetUIntFn>(vptr, SlotGetUInt),
                GetInt = Slot<GetIntFn>(vptr, SlotGetInt),
                GetD3D11 = SlotPointer(vptr, SlotGetD3D11),
                GetVoid = Slot<GetPtrFn>(vptr, SlotGetVoid),
                Reset = Slot<ResetFn>(vptr, SlotReset)
            };
        }

        private static T Slot<T>(IntPtr vptr, int index) where T : class
        {
            var fn = SlotPointer(vptr, index);
            return Marshal.GetDelegateForFunctionPointer<T>(fn);
        }

        private static IntPtr SlotPointer(IntPtr vptr, int index)
        {
            return Marshal.ReadIntPtr(vptr, index * IntPtr.Size);
        }
    }
}

internal static class NgxResult
{
    internal const int Success = 1;
    internal const int Fail = unchecked((int)0xBAD00000);
    internal const int FailAccessViolation = unchecked((int)0xBAD00099);
    internal const int FailRwFlagMissing = unchecked((int)(0xBAD00000 | 9));

    internal static bool Failed(int result)
    {
        var u = (uint)result;
        return u != Success && ((u & 0x80000000u) != 0 || (u & 0xFFF00000u) == unchecked((uint)Fail));
    }

    internal static string Name(int result)
    {
        switch ((uint)result & 0xFFFFu)
        {
            case 1: return "FeatureNotSupported";
            case 2: return "PlatformError";
            case 3: return "FeatureAlreadyExists";
            case 4: return "FeatureNotFound";
            case 5: return "InvalidParameter";
            case 6: return "ScratchBufferTooSmall";
            case 7: return "NotInitialized";
            case 8: return "UnsupportedInputFormat";
            case 9: return "RWFlagMissing";
            case 10: return "MissingInput";
            case 11: return "UnableToInitializeFeature";
            case 12: return "OutOfDate";
            case 13: return "OutOfGPUMemory";
            case 14: return "UnsupportedFormat";
            default: return "Fail";
        }
    }
}
