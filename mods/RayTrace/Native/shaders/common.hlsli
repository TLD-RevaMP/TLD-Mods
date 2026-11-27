// What both passes share: the root constants (b0, rt.cc's Constants), the textures and the depth to world position.
struct Constants
{
    row_major float4x4 invViewProj;   // clip (x, y, raw depth) to world
    float4 cameraPos;
    float4 sunDir;                    // toward the sun
    float4 size;                      // width, height, 1/width, 1/height
    float fadeStart, fadeEnd, strength, maxDistance;
    int mode, frame, effects, aoRays; // effects: 1 shadows traced this frame, 2 ambient occlusion traced this frame
    float aoRadius, aoStrength, aoDistance, pad0;
    float4 pad1;
};
ConstantBuffer<Constants> c : register(b0);
Texture2D<float> Depth : register(t1);
RWTexture2D<float> Mask : register(u0);   // 1: in shadow
RWTexture2D<float> Ao : register(u2);     // 1: open, 0: closed in

static const int EffectShadows = 1, EffectAo = 2;

// Unity's reversed depth on D3D: 1 at the near plane, 0 at the far one (the sky)
float3 WorldPos(uint2 px, float depth)
{
    float2 uv = (px + 0.5) * c.size.zw;
    float4 w = mul(c.invViewProj, float4(uv.x * 2 - 1, 1 - uv.y * 2, depth, 1));
    return w.xyz / w.w;
}

float3 PosAt(int2 q)
{
    q = clamp(q, int2(0, 0), int2(c.size.xy) - 1);
    return WorldPos(q, Depth.Load(int3(q, 0)));
}

// The surface's normal from its neighbours' depth (the nearer neighbour on each axis: no smearing over edges), facing
// the camera
float3 NormalAt(int2 px, float3 p)
{
    float3 r = PosAt(px + int2(1, 0)) - p, l = p - PosAt(px - int2(1, 0));
    float3 d = PosAt(px + int2(0, 1)) - p, u = p - PosAt(px - int2(0, 1));
    float3 dx = dot(r, r) < dot(l, l) ? r : l, dy = dot(d, d) < dot(u, u) ? d : u;
    float3 n = normalize(cross(dy, dx));
    return dot(n, c.cameraPos.xyz - p) < 0 ? -n : n;
}
