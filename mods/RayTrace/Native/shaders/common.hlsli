// What both passes share: the root constants (b0, rt.cc's Constants) and the depth to world position.
struct Constants
{
    row_major float4x4 invViewProj;   // clip (x, y, raw depth) to world
    float4 cameraPos;
    float4 sunDir;                    // toward the sun
    float4 size;                      // width, height, 1/width, 1/height
    float fadeStart, fadeEnd, strength, maxDistance;
    int mode, frame, pad0, pad1;
    float4 pad2[2];
};
ConstantBuffer<Constants> c : register(b0);
Texture2D<float> Depth : register(t1);
RWTexture2D<float> Mask : register(u0);   // 1: in shadow

// Unity's reversed depth on D3D: 1 at the near plane, 0 at the far one (the sky)
float3 WorldPos(uint2 px, float depth)
{
    float2 uv = (px + 0.5) * c.size.zw;
    float4 w = mul(c.invViewProj, float4(uv.x * 2 - 1, 1 - uv.y * 2, depth, 1));
    return w.xyz / w.w;
}
