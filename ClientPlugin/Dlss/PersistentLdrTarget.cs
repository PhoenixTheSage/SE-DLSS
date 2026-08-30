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
