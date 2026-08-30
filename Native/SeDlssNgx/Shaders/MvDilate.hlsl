cbuffer Constants : register(b0)
{
    float4x4 InvViewProj;
    float4x4 UnjitteredViewProj;
    float4x4 PrevViewProj;
    float2 RenderSize;
    float2 InvRenderSize;
};
Texture2D VelocityTex : register(t0);
Texture2D DepthTex : register(t1);
SamplerState PointSamp : register(s0);

static const float2 kMvDilateOff[8] =
{
    float2(1, 0), float2(-1, 0), float2(0, 1), float2(0, -1),
    float2(1, 1), float2(-1, 1), float2(1, -1), float2(-1, -1)
};

float4 PSMain(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float closest = DepthTex.SampleLevel(PointSamp, uv, 0).r;
    float2 bestUv = uv;
    [unroll]
    for (int i = 0; i < 8; i++)
    {
        float2 nuv = uv + kMvDilateOff[i] * InvRenderSize;
        float nd = DepthTex.SampleLevel(PointSamp, nuv, 0).r;
        if (nd > closest)
        {
            closest = nd;
            bestUv = nuv;
        }
    }
    return float4(VelocityTex.SampleLevel(PointSamp, bestUv, 0).xy, 0, 1);
}
