using System;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using Resource = SharpDX.Direct3D11.Resource;

namespace ClientPlugin.Dlss;

/// <summary>
/// Plugin-owned D3D11 helpers: camera motion vectors, depth upsample, UAV evaluate target.
/// Disposes only objects this class created.
/// </summary>
internal static class DlssD3d
{
    private const int ConstantBufferSize = 208;

    private static Texture2D _mvTex;
    private static RenderTargetView _mvRtv;
    private static ShaderResourceView _mvSrv;
    private static VertexShader _mvVs;
    private static PixelShader _mvPs;
    private static Buffer _mvCb;
    private static SamplerState _mvSampler;
    private static uint _mvW;
    private static uint _mvH;

    private static VertexShader _depthVs;
    private static PixelShader _depthPs;
    private static SamplerState _depthSampler;
    private static DepthStencilState _depthWriteAlways;

    private static IntPtr _cachedDepthRes;
    private static ShaderResourceView _cachedDepthSrv;
    private static IntPtr _cachedUpSrcRes;
    private static ShaderResourceView _cachedUpSrcSrv;
    private static IntPtr _cachedUpDestRes;
    private static DepthStencilView _cachedUpDestDsv;

    private static Texture2D _evalOutTex;
    private static uint _evalOutW;
    private static uint _evalOutH;
    private static Format _evalOutFmt = Format.Unknown;
    private static IntPtr _cachedEvalDest;
    private static Texture2DDescription _cachedEvalDestDesc;
    private static bool _cachedEvalDestHasUav;

    private static readonly ShaderResourceView[] NullSrvs = new ShaderResourceView[8];
    private static readonly UnorderedAccessView[] NullUavs = new UnorderedAccessView[8];
    private static readonly byte[] MvCbScratch = new byte[ConstantBufferSize];

    internal static void Release()
    {
        ReleaseMvPipeline();
        ReleaseEvalOutput();
        ReleaseMvShaders();
        ReleaseDepthUpsample();
        ReleaseCachedViews();
    }

    internal static IntPtr GenerateCameraMotionVectors(
        Device device,
        DeviceContext context,
        Resource depth,
        uint width,
        uint height,
        float[] invViewProj,
        float[] unjitteredViewProj,
        float[] prevViewProj)
    {
        if (device == null || context == null || depth == null || width == 0 || height == 0)
        {
            NgxApi.SetError("motion-vector args are incomplete");
            return IntPtr.Zero;
        }

        if (!EnsureMvShaders(device) || !EnsureMvTarget(device, width, height))
            return IntPtr.Zero;

        FillMvConstantBuffer(width, height, invViewProj, unjitteredViewProj, prevViewProj);
        try
        {
            var mapped = context.MapSubresource(_mvCb, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
            System.Runtime.InteropServices.Marshal.Copy(MvCbScratch, 0, mapped.DataPointer, ConstantBufferSize);
            context.UnmapSubresource(_mvCb, 0);
        }
        catch (Exception e)
        {
            NgxApi.SetError("failed to map motion-vector constant buffer: " + e.GetType().Name);
            return IntPtr.Zero;
        }

        context.OutputMerger.SetTargets((DepthStencilView)null, (RenderTargetView)null);

        if (!TryGetTexture2D(depth, out var depthTex))
        {
            NgxApi.SetError("motion-vector depth is not a Texture2D");
            return IntPtr.Zero;
        }

        if (!EnsureDepthSrv(device, depth, depthTex.Description, ref _cachedDepthRes, ref _cachedDepthSrv))
        {
            NgxApi.SetError("failed to create depth SRV for motion vectors");
            DebugLog.Write("depth SRV failed fmt=" + (uint)depthTex.Description.Format);
            return IntPtr.Zero;
        }

        context.Rasterizer.SetViewport(0, 0, width, height, 0f, 1f);
        context.InputAssembler.InputLayout = null;
        context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        context.VertexShader.Set(_mvVs);
        context.VertexShader.SetConstantBuffer(0, _mvCb);
        context.PixelShader.SetConstantBuffer(0, _mvCb);
        context.PixelShader.SetSampler(0, _mvSampler);
        context.OutputMerger.SetTargets(_mvRtv);
        context.PixelShader.Set(_mvPs);
        context.PixelShader.SetShaderResource(0, _cachedDepthSrv);
        context.Draw(3, 0);
        context.PixelShader.SetShaderResource(0, null);
        context.OutputMerger.SetTargets((RenderTargetView)null);
        return _mvTex.NativePointer;
    }

    internal static bool TryUpsampleDepth(Device device, DeviceContext context, Resource srcDepth, Resource destDepth)
    {
        if (device == null || context == null || srcDepth == null || destDepth == null)
        {
            NgxApi.SetError("depth upsample args are incomplete");
            return false;
        }

        if (!EnsureDepthUpsample(device))
            return false;

        if (!TryGetTexture2D(srcDepth, out var srcTex) || !TryGetTexture2D(destDepth, out var destTex))
        {
            NgxApi.SetError("depth upsample textures are not Texture2D");
            return false;
        }

        context.OutputMerger.SetTargets((DepthStencilView)null, (RenderTargetView)null);

        if (!EnsureDepthSrv(device, srcDepth, srcTex.Description, ref _cachedUpSrcRes, ref _cachedUpSrcSrv))
        {
            NgxApi.SetError("failed to create depth upsample source SRV");
            return false;
        }

        if (_cachedUpDestDsv == null || _cachedUpDestRes != destDepth.NativePointer)
        {
            DisposeView(ref _cachedUpDestDsv);
            _cachedUpDestRes = destDepth.NativePointer;
            try
            {
                _cachedUpDestDsv = new DepthStencilView(device, destDepth, new DepthStencilViewDescription
                {
                    Format = DepthDsvFormat(destTex.Description.Format),
                    Dimension = DepthStencilViewDimension.Texture2D
                });
            }
            catch
            {
                _cachedUpDestDsv = null;
                NgxApi.SetError("failed to create depth upsample dest DSV");
                return false;
            }
        }

        context.OutputMerger.SetTargets(_cachedUpDestDsv, (RenderTargetView)null);
        context.OutputMerger.SetDepthStencilState(_depthWriteAlways);
        context.Rasterizer.SetViewport(0, 0, destTex.Description.Width, destTex.Description.Height, 0f, 1f);
        context.InputAssembler.InputLayout = null;
        context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        context.VertexShader.Set(_depthVs);
        context.PixelShader.Set(_depthPs);
        context.PixelShader.SetShaderResource(0, _cachedUpSrcSrv);
        context.PixelShader.SetSampler(0, _depthSampler);
        context.Draw(3, 0);
        context.PixelShader.SetShaderResource(0, null);
        context.OutputMerger.SetTargets((DepthStencilView)null, (RenderTargetView)null);
        context.OutputMerger.SetDepthStencilState(null);
        NgxApi.SetError("ok");
        return true;
    }

    internal static IntPtr EnsureMotionOrZero(Device device, DeviceContext context, IntPtr motion, uint renderWidth, uint renderHeight)
    {
        if (motion != IntPtr.Zero)
            return motion;
        if (device == null || !EnsureMvTarget(device, renderWidth, renderHeight))
            return IntPtr.Zero;
        context.ClearRenderTargetView(_mvRtv, new RawColor4(0, 0, 0, 0));
        return _mvTex.NativePointer;
    }

    internal static bool PrepareEvalOutput(
        Device device,
        Resource output,
        out IntPtr evalOutput,
        out bool copyBack,
        out string destDesc)
    {
        evalOutput = IntPtr.Zero;
        copyBack = false;
        destDesc = "null";
        if (!DescribeEvalDest(output, out var desc, out var hasUav))
        {
            NgxApi.SetError("output is not a 2D texture");
            return false;
        }

        destDesc = desc.Width + "x" + desc.Height + " fmt=" + (uint)desc.Format + " bind=0x" +
                   ((uint)desc.BindFlags).ToString("x");
        if (hasUav)
        {
            evalOutput = output.NativePointer;
            return true;
        }

        if (device == null || !EnsureEvalOutput(device, desc))
        {
            DebugLog.Write("EnsureEvalOutput failed dest=" + desc.Width + "x" + desc.Height +
                           " fmt=" + (uint)desc.Format + " bind=0x" + ((uint)desc.BindFlags).ToString("x"));
            return false;
        }

        evalOutput = _evalOutTex.NativePointer;
        copyBack = true;
        return true;
    }

    internal static void CopyEvalOutput(DeviceContext context, Resource output)
    {
        if (_evalOutTex == null || output == null)
            return;
        context.CopyResource(_evalOutTex, output);
    }

    internal static void UnbindPipeline(DeviceContext context)
    {
        context.OutputMerger.ResetTargets();
        context.PixelShader.SetShaderResources(0, NullSrvs);
        context.VertexShader.SetShaderResources(0, NullSrvs);
        try
        {
            context.ComputeShader.SetShaderResources(0, NullSrvs);
            context.ComputeShader.SetUnorderedAccessViews(0, NullUavs);
            context.OutputMerger.SetUnorderedAccessViews(0, NullUavs);
        }
        catch
        {
            // Feature level or SharpDX build without CS UAV helpers.
        }
    }

    internal static string Describe(Resource resource)
    {
        return Describe(resource == null ? IntPtr.Zero : resource.NativePointer, resource);
    }

    internal static string Describe(IntPtr pointer, Resource known = null)
    {
        if (pointer == IntPtr.Zero)
            return "null";
        Texture2D tex = known as Texture2D;
        if (tex == null || tex.IsDisposed)
        {
            if (_mvTex != null && !_mvTex.IsDisposed && _mvTex.NativePointer == pointer)
                tex = _mvTex;
            else if (_evalOutTex != null && !_evalOutTex.IsDisposed && _evalOutTex.NativePointer == pointer)
                tex = _evalOutTex;
            else
                return "0x" + pointer.ToInt64().ToString("X") + " external";
        }

        try
        {
            var desc = tex.Description;
            return "0x" + pointer.ToInt64().ToString("X") + " " +
                   desc.Width + "x" + desc.Height +
                   " fmt=" + desc.Format + "(" + (uint)desc.Format + ")" +
                   " bind=0x" + ((uint)desc.BindFlags).ToString("x") +
                   " usage=" + desc.Usage +
                   " msaa=" + desc.SampleDescription.Count +
                   " mips=" + desc.MipLevels;
        }
        catch
        {
            return "0x" + pointer.ToInt64().ToString("X") + " not-tex2d";
        }
    }

    internal static string DescribeContext(Device device, DeviceContext context)
    {
        var devicePtr = device == null ? IntPtr.Zero : device.NativePointer;
        var contextPtr = context == null ? IntPtr.Zero : context.NativePointer;
        return "device=0x" + devicePtr.ToInt64().ToString("X") +
               " ctx=0x" + contextPtr.ToInt64().ToString("X");
    }

    private static bool DescribeEvalDest(Resource output, out Texture2DDescription desc, out bool hasUav)
    {
        desc = default;
        hasUav = false;
        if (output == null)
            return false;
        if (_cachedEvalDest == output.NativePointer)
        {
            desc = _cachedEvalDestDesc;
            hasUav = _cachedEvalDestHasUav;
            return true;
        }

        if (!TryGetTexture2D(output, out var tex))
            return false;
        desc = tex.Description;
        _cachedEvalDest = output.NativePointer;
        _cachedEvalDestDesc = desc;
        _cachedEvalDestHasUav = (desc.BindFlags & BindFlags.UnorderedAccess) != 0;
        hasUav = _cachedEvalDestHasUav;
        return true;
    }

    private static bool EnsureEvalOutput(Device device, Texture2DDescription destDesc)
    {
        if (_evalOutTex != null && _evalOutW == (uint)destDesc.Width && _evalOutH == (uint)destDesc.Height &&
            _evalOutFmt == destDesc.Format)
            return true;
        ReleaseEvalOutput();
        try
        {
            _evalOutTex = new Texture2D(device, new Texture2DDescription
            {
                Width = destDesc.Width,
                Height = destDesc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = destDesc.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource | BindFlags.RenderTarget
            });
        }
        catch (Exception e)
        {
            NgxApi.SetError("failed to create UAV evaluate target " + destDesc.Width + "x" + destDesc.Height +
                            " fmt=" + (uint)destDesc.Format + " (" + e.GetType().Name + ")");
            return false;
        }

        _evalOutW = (uint)destDesc.Width;
        _evalOutH = (uint)destDesc.Height;
        _evalOutFmt = destDesc.Format;
        return true;
    }

    private static bool EnsureMvShaders(Device device)
    {
        if (_mvVs != null && _mvPs != null && _mvCb != null && _mvSampler != null)
            return true;
        try
        {
            _mvVs = new VertexShader(device, ShaderBytecode.FullscreenVs);
            _mvPs = new PixelShader(device, ShaderBytecode.MvPs);
            _mvCb = new Buffer(device, new BufferDescription
            {
                SizeInBytes = ConstantBufferSize,
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ConstantBuffer,
                CpuAccessFlags = CpuAccessFlags.Write
            });
            _mvSampler = new SamplerState(device, new SamplerStateDescription
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp
            });
            return true;
        }
        catch (Exception e)
        {
            ReleaseMvShaders();
            NgxApi.SetError("failed to create motion-vector pipeline: " + e.GetType().Name);
            return false;
        }
    }

    private static bool EnsureMvTarget(Device device, uint width, uint height)
    {
        if (_mvTex != null && _mvW == width && _mvH == height)
            return true;
        ReleaseMvPipeline();
        try
        {
            _mvTex = new Texture2D(device, new Texture2DDescription
            {
                Width = (int)width,
                Height = (int)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R16G16_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
            });
            _mvRtv = new RenderTargetView(device, _mvTex);
            _mvSrv = new ShaderResourceView(device, _mvTex);
            _mvW = width;
            _mvH = height;
            return true;
        }
        catch
        {
            ReleaseMvPipeline();
            NgxApi.SetError("failed to create motion-vector targets");
            return false;
        }
    }

    private static bool EnsureDepthUpsample(Device device)
    {
        if (_depthVs != null && _depthPs != null && _depthSampler != null && _depthWriteAlways != null)
            return true;
        try
        {
            _depthVs = new VertexShader(device, ShaderBytecode.FullscreenVs);
            _depthPs = new PixelShader(device, ShaderBytecode.DepthUpsamplePs);
            _depthSampler = new SamplerState(device, new SamplerStateDescription
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp
            });
            _depthWriteAlways = new DepthStencilState(device, new DepthStencilStateDescription
            {
                IsDepthEnabled = true,
                DepthWriteMask = DepthWriteMask.All,
                DepthComparison = Comparison.Always
            });
            return true;
        }
        catch (Exception e)
        {
            ReleaseDepthUpsample();
            NgxApi.SetError("failed to create depth-upsample pipeline: " + e.GetType().Name);
            return false;
        }
    }

    private static bool EnsureDepthSrv(
        Device device,
        Resource resource,
        Texture2DDescription desc,
        ref IntPtr cachedRes,
        ref ShaderResourceView cachedSrv)
    {
        if (cachedSrv != null && cachedRes == resource.NativePointer)
            return true;
        DisposeView(ref cachedSrv);
        cachedRes = resource.NativePointer;

        var formats = new[]
        {
            DepthSrvFormat(desc.Format),
            Format.R32_Float_X8X24_Typeless,
            Format.R32_Float,
            Format.R24_UNorm_X8_Typeless
        };
        foreach (var fmt in formats)
        {
            if (fmt == Format.Unknown)
                continue;
            try
            {
                cachedSrv = new ShaderResourceView(device, resource, new ShaderResourceViewDescription
                {
                    Format = fmt,
                    Dimension = ShaderResourceViewDimension.Texture2D,
                    Texture2D = { MipLevels = 1 }
                });
                return true;
            }
            catch
            {
                cachedSrv = null;
            }
        }

        return false;
    }

    private static void FillMvConstantBuffer(
        uint width,
        uint height,
        float[] invViewProj,
        float[] unjitteredViewProj,
        float[] prevViewProj)
    {
        System.Buffer.BlockCopy(invViewProj, 0, MvCbScratch, 0, 64);
        System.Buffer.BlockCopy(unjitteredViewProj, 0, MvCbScratch, 64, 64);
        System.Buffer.BlockCopy(prevViewProj, 0, MvCbScratch, 128, 64);
        var renderW = BitConverter.GetBytes((float)width);
        var renderH = BitConverter.GetBytes((float)height);
        var invW = BitConverter.GetBytes(1f / width);
        var invH = BitConverter.GetBytes(1f / height);
        System.Buffer.BlockCopy(renderW, 0, MvCbScratch, 192, 4);
        System.Buffer.BlockCopy(renderH, 0, MvCbScratch, 196, 4);
        System.Buffer.BlockCopy(invW, 0, MvCbScratch, 200, 4);
        System.Buffer.BlockCopy(invH, 0, MvCbScratch, 204, 4);
    }

    private static bool TryGetTexture2D(Resource resource, out Texture2D tex)
    {
        tex = resource as Texture2D;
        return tex != null && !tex.IsDisposed;
    }

    private static Format DepthSrvFormat(Format resourceFormat)
    {
        switch (resourceFormat)
        {
            case Format.R32G8X24_Typeless:
            case Format.D32_Float_S8X24_UInt:
                return Format.R32_Float_X8X24_Typeless;
            case Format.R24G8_Typeless:
            case Format.D24_UNorm_S8_UInt:
                return Format.R24_UNorm_X8_Typeless;
            case Format.R32_Typeless:
            case Format.D32_Float:
                return Format.R32_Float;
            case Format.R16_Typeless:
            case Format.D16_UNorm:
                return Format.R16_UNorm;
            default:
                return resourceFormat;
        }
    }

    private static Format DepthDsvFormat(Format resourceFormat)
    {
        switch (resourceFormat)
        {
            case Format.R32G8X24_Typeless:
            case Format.D32_Float_S8X24_UInt:
                return Format.D32_Float_S8X24_UInt;
            case Format.R24G8_Typeless:
            case Format.D24_UNorm_S8_UInt:
                return Format.D24_UNorm_S8_UInt;
            case Format.R32_Typeless:
            case Format.D32_Float:
                return Format.D32_Float;
            case Format.R16_Typeless:
            case Format.D16_UNorm:
                return Format.D16_UNorm;
            default:
                return resourceFormat;
        }
    }

    private static void ReleaseMvPipeline()
    {
        DisposeView(ref _mvSrv);
        DisposeView(ref _mvRtv);
        DisposeView(ref _mvTex);
        _mvW = _mvH = 0;
    }

    private static void ReleaseEvalOutput()
    {
        DisposeView(ref _evalOutTex);
        _evalOutW = _evalOutH = 0;
        _evalOutFmt = Format.Unknown;
        _cachedEvalDest = IntPtr.Zero;
        _cachedEvalDestHasUav = false;
    }

    private static void ReleaseMvShaders()
    {
        DisposeView(ref _mvVs);
        DisposeView(ref _mvPs);
        DisposeView(ref _mvCb);
        DisposeView(ref _mvSampler);
    }

    private static void ReleaseDepthUpsample()
    {
        DisposeView(ref _depthVs);
        DisposeView(ref _depthPs);
        DisposeView(ref _depthSampler);
        DisposeView(ref _depthWriteAlways);
    }

    private static void ReleaseCachedViews()
    {
        DisposeView(ref _cachedDepthSrv);
        _cachedDepthRes = IntPtr.Zero;
        DisposeView(ref _cachedUpSrcSrv);
        _cachedUpSrcRes = IntPtr.Zero;
        DisposeView(ref _cachedUpDestDsv);
        _cachedUpDestRes = IntPtr.Zero;
    }

    private static void DisposeView<T>(ref T view) where T : class, IDisposable
    {
        if (view == null)
            return;
        try
        {
            view.Dispose();
        }
        catch
        {
            // Device may already be tearing down.
        }

        view = null;
    }
}
