#pragma pack_matrix(row_major)
cbuffer Constants : register(b0)
{
    float4x4 InvViewProj;
    float4x4 UnjitteredViewProj;
    float4x4 PrevViewProj;
    float2 RenderSize;
    float2 InvRenderSize;
};
Texture2D DepthTex : register(t0);
SamplerState PointSamp : register(s0);

static const float2 kClosestOff[8] =
{
    float2(1, 0), float2(-1, 0), float2(0, 1), float2(0, -1),
    float2(1, 1), float2(-1, 1), float2(1, -1), float2(-1, -1)
};

float2 CameraVelocity(float2 uv, float depth)
{
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    float4 clip = float4(ndc, depth, 1.0);
    float4 world = mul(clip, InvViewProj);
    world /= max(world.w, 1e-6);
    float4 currClip = mul(world, UnjitteredViewProj);
    currClip /= max(currClip.w, 1e-6);
    float4 prevClip = mul(world, PrevViewProj);
    prevClip /= max(prevClip.w, 1e-6);
    float2 currUv = float2(currClip.x * 0.5 + 0.5, 0.5 - currClip.y * 0.5);
    float2 prevUv = float2(prevClip.x * 0.5 + 0.5, 0.5 - prevClip.y * 0.5);
    return (currUv - prevUv) * RenderSize;
}

float4 PSMain(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float closest = DepthTex.SampleLevel(PointSamp, uv, 0).r;
    float2 bestUv = uv;
    [unroll]
    for (int i = 0; i < 8; i++)
    {
        float2 nuv = uv + kClosestOff[i] * InvRenderSize;
        float nd = DepthTex.SampleLevel(PointSamp, nuv, 0).r;
        if (nd > closest)
        {
            closest = nd;
            bestUv = nuv;
        }
    }
    return float4(CameraVelocity(bestUv, closest), 0, 1);
}
