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

        IBorrowedRtvTexture hdr = null;
        try
        {
            hdr = MyManagers.RwTexturesPool.BorrowRtv(
                "DLSS.HdrUpscale",
                output.X,
                output.Y,
                MyGBuffer.LBufferFormat);
            if (!DlssRuntime.TryEvaluate(hdr, src))
                return true;

            __result = TonemapAtOutput(hdr, avgLum, bloom, enableTonemapping, dirtTexture, needsAlphaLuminance, output);
            DlssRuntime.EvaluatedHdrThisFrame = __result != null;
            return __result == null;
        }
        catch
        {
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
        var dest = MyManagers.RwTexturesPool.BorrowCustom("DLSS.Tonemapped", output.X, output.Y);
        var savedResolution = MyCommon.FrameConstantsData.Screen.Resolution;
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
        finally
        {
            var data = MyCommon.FrameConstantsData;
            data.Screen.Resolution = savedResolution;
            MyCommon.FrameConstantsData = data;
            var mapping = MyMapping.MapDiscard(MyCommon.FrameConstants);
            mapping.WriteAndPosition(ref MyCommon.FrameConstantsData);
            mapping.Unmap();
        }
    }
}
