using System;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using VRage.Render11.Resources;
using VRageMath;

namespace ClientPlugin.Dlss;

internal sealed class PersistentLdrTarget : IBorrowedCustomTexture
{
    private readonly ICustomTexture inner;

    public PersistentLdrTarget(ICustomTexture inner)
    {
        this.inner = inner;
    }

    public void AddRef()
    {
    }

    public void Release()
    {
    }

    public string Name => inner.Name;
    public SharpDX.Direct3D11.Resource Resource => inner.Resource;
    public Vector3I Size3 => inner.Size3;
    public Vector2I Size => inner.Size;
    public Format Format => inner.Linear.Format;
    public int MipLevels => inner.Linear.MipLevels;
    public ShaderResourceView Srv => inner.Linear.Srv;
    public UnorderedAccessView Uav => inner.Uav;
    public IRtvTexture Linear => inner.Linear;
    public IRtvTexture SRgb => inner.SRgb;

    public event Action<ITexture> OnFormatChanged
    {
        add { }
        remove { }
    }
}

/// <summary>
/// Keen <c>CreateTexture</c> is always 8-bit UNORM. Display evaluate needs
/// fp16 so DLSS / BT.2390 values above 1 are not clipped. <c>Release</c>
/// returns the UAV to the pool — DrawGameScene always releases the dest.
/// </summary>
internal sealed class HdrUavTarget : IBorrowedCustomTexture
{
    IBorrowedUavTexture inner;
    readonly Action<HdrUavTarget> onReleased;

    public HdrUavTarget(IBorrowedUavTexture inner, Action<HdrUavTarget> onReleased)
    {
        this.inner = inner;
        this.onReleased = onReleased;
    }

    public void AddRef() => inner?.AddRef();

    public void Release()
    {
        var tex = inner;
        if (tex == null)
            return;
        inner = null;
        onReleased?.Invoke(this);
        tex.Release();
    }

    public string Name => inner != null ? inner.Name : "DLSS.HdrUpscale";
    public SharpDX.Direct3D11.Resource Resource => inner?.Resource;
    public Vector3I Size3 => inner != null ? inner.Size3 : default;
    public Vector2I Size => inner != null ? inner.Size : default;
    public Format Format => inner != null ? inner.Format : Format.Unknown;
    public int MipLevels => inner != null ? inner.MipLevels : 0;
    public ShaderResourceView Srv => inner?.Srv;
    public UnorderedAccessView Uav => inner?.Uav;
    public IRtvTexture Linear => inner;
    public IRtvTexture SRgb => inner;

    public event Action<ITexture> OnFormatChanged
    {
        add { }
        remove { }
    }
}
