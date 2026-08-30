using System;
using ClientPlugin.Dlss;
using HarmonyLib;
using VRage.Render11.Common;
using VRage.Render11.Resources;
using VRage.Render11.Resources.Textures;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyToneMapping), nameof(MyToneMapping.Run))]
internal static class ToneMappingPatch
{
    [HarmonyPrefix]
    private static bool Prefix(
        ISrvBindable src,
        ISrvBindable avgLum,
        ISrvBindable bloom,
        bool enableTonemapping,
        string dirtTexture,
        bool needsAlphaLuminance,
        ref IBorrowedCustomTexture __result)
    {
        if (!DlssRuntime.IsLive || src == null)
            return true;
        if (src.Size.X != DlssRuntime.InternalWidth || src.Size.Y != DlssRuntime.InternalHeight)
            return true;

        var output = DlssRuntime.OutputResolution();
        if (output.X <= 0 || output.Y <= 0)
            return true;

        IBorrowedUavTexture hdr = null;
        try
        {
            // NGX EvaluateFeature requires D3D11_BIND_UNORDERED_ACCESS on the output.
            // BorrowRtv is RT+SRV only and fails with 0x8A000009 (RWFlagMissing).
            hdr = MyManagers.RwTexturesPool.BorrowUav(
                "DLSS.HdrUpscale",
                output.X,
                output.Y,
                MyGBuffer.LBufferFormat);
            if (!DlssRuntime.TryEvaluate(hdr, src, avgLum))
            {
                DebugLog.Write("ToneMapping evaluate failed src=" + src.Size + " out=" + output);
                return true;
            }

            __result = TonemapAtOutput(hdr, avgLum, bloom, enableTonemapping, dirtTexture, needsAlphaLuminance, output);
            DlssRuntime.EvaluatedHdrThisFrame = __result != null;
            if (__result != null)
                DlssRuntime.ApplyOutputSpace();
            DebugLog.WriteFrame("ToneMapping HDR evaluate src=" + src.Size + " out=" + output +
                                " tonemap=" + (__result != null));
            return __result == null;
        }
        catch (Exception e)
        {
            DebugLog.Write("ToneMapping threw " + e);
            DlssRuntime.EvaluatedHdrThisFrame = false;
            return true;
        }
        finally
        {
            hdr?.Release();
        }
    }

    private static IBorrowedCustomTexture TonemapAtOutput(
        ISrvBindable hdr,
        ISrvBindable avgLum,
        ISrvBindable bloom,
        bool enableTonemapping,
        string dirtTexture,
        bool needsAlphaLuminance,
        Vector2I output)
    {
        // (name, width, height) binds to (name, samplesCount, samplesQuality) and
        // CreateTexture2D fails with E_INVALIDARG (1920 samples).
        var dest = MyManagers.RwTexturesPool.BorrowCustom("DLSS.Tonemapped", output.X, output.Y, 1, 0);
        try
        {
            var data = MyCommon.FrameConstantsData;
            data.Screen.Resolution = new Vector2(output.X, output.Y);
            MyCommon.FrameConstantsData = data;
            var mapping = MyMapping.MapDiscard(MyCommon.FrameConstants);
            mapping.WriteAndPosition(ref MyCommon.FrameConstantsData);
            mapping.Unmap();

            var rc = MyImmediateRC.RC;
            rc.ComputeShader.SetConstantBuffer(0, MyCommon.FrameConstants);
            rc.ComputeShader.SetUav(0, dest);
            ITexture tempTexture = MyManagers.Textures.GetTempTexture(dirtTexture, new MyTextureStreamingManager.QueryArgs
            {
                TextureType = MyFileTextureEnum.ALPHAMASK,
                WaitUntilLoaded = true,
                SkipQualityReduction = true
            }, 100);
            rc.ComputeShader.SetSrvs(0, hdr, avgLum, bloom, tempTexture);
            rc.ComputeShader.SetSampler(0, MySamplerStateManager.Default);
            rc.ComputeShader.SetSampler(1, MySamplerStateManager.Point);
            rc.ComputeShader.SetSampler(2, MySamplerStateManager.Default);
            rc.ComputeShader.SetSampler(3, MySamplerStateManager.Default);
            rc.ComputeShader.Set((!enableTonemapping) ? MyToneMapping.m_csSkip : (needsAlphaLuminance ? MyToneMapping.m_csAlphaLuminance : MyToneMapping.m_cs));
            rc.Dispatch((output.X + 8 - 1) / 8, (output.Y + 8 - 1) / 8, 1);
            rc.ComputeShader.SetUav(0, null);
            rc.ComputeShader.Set(null);
            return dest;
        }
        catch
        {
            dest.Release();
            return null;
        }
    }
}
