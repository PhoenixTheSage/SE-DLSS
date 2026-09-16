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
/// Fallback when <c>CreateTexture</c> is still 8-bit UNORM (HdrRender not
/// live). Hold the borrowed fp16 UAV until resize/shutdown. Keen's
/// <c>DrawGameScene</c> always <c>Release</c>s the dest — forwarding that
/// to the pool recycled the UAV under NGX and hung the GPU.
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
    }

    public void DisposeInner()
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
