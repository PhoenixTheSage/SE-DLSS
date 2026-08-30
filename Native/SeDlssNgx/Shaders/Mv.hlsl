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
    float depth = DepthTex.SampleLevel(PointSamp, uv, 0).r;
    return float4(CameraVelocity(uv, depth), 0, 1);
}
