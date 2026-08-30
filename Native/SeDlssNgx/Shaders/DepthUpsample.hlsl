Texture2D DepthTex : register(t0);
SamplerState PointSamp : register(s0);

float PSMain(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Depth
{
    return DepthTex.SampleLevel(PointSamp, uv, 0).r;
}
