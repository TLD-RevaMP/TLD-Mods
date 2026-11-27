// profile: cs_6_0
// The traced results applied to the frame. Shadows by mode: 1 only beyond the game's own (blended in from fadeStart to
// fadeEnd), 2 everywhere, 3 the shadow mask itself; ambient occlusion within aoDistance, blurred over its depth
// neighbours; mode 4 shows the ambient occlusion alone; reflections blended in by their amount (mode 5: alone).
#include "common.hlsli"

RWTexture2D<float4> ColorOut : register(u1);

float AoBlurred(int2 px, float depth)
{
    float sum = 0, weight = 0;
    for (int y = -2; y <= 2; y++)
        for (int x = -2; x <= 2; x++)
        {
            int2 q = clamp(px + int2(x, y), int2(0, 0), int2(size.xy) - 1);
            float d = Depth.Load(int3(q, 0));
            float w = exp(-abs(d - depth) / (depth * 0.02 + 1e-7)) * (1.0 / (1 + x * x + y * y));
            sum += Ao[q] * w; weight += w;
        }
    return weight > 0 ? sum / weight : 1;
}

float3 GiBlurred(int2 px, float depth)
{
    float3 sum = 0; float weight = 0;
    for (int y = -2; y <= 2; y++)
        for (int x = -2; x <= 2; x++)
        {
            int2 q = clamp(px + int2(x, y) * 2, int2(0, 0), int2(size.xy) - 1);
            float d = Depth.Load(int3(q, 0));
            float w = exp(-abs(d - depth) / (depth * 0.02 + 1e-7)) * (1.0 / (1 + x * x + y * y));
            sum += Gi[q].rgb * w; weight += w;
        }
    return weight > 0 ? sum / weight : 0;
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= (uint2)size.xy)) return;
    float4 col = ColorIn.Load(int3(id.xy, 0));
    float depth = Depth.Load(int3(id.xy, 0));
    if (depth <= 0) { ColorOut[id.xy] = mode == 3 || mode == 4 ? float4(0.4, 0.6, 1, 1) : col; return; }
    float dist = distance(WorldPos(id.xy, depth), cameraPos.xyz);
    float ao = (effects & EffectAo) ? AoBlurred(id.xy, depth) : 1;
    float shadow = (effects & EffectShadows) ? Mask[id.xy] : 0;
    if (mode == 3) { ColorOut[id.xy] = float4((1 - shadow).xxx, 1); return; }
    if (mode == 4) { ColorOut[id.xy] = float4(ao.xxx, 1); return; }
    if (mode == 1 || mode == 2)
    {
        float weight = mode == 1 ? saturate((dist - fadeStart) / max(fadeEnd - fadeStart, 1e-3)) : 1;
        col.rgb *= 1 - strength * shadow * weight;
    }
    float aoFade = 1 - saturate((dist - aoDistance * 0.75) / max(aoDistance * 0.25, 1e-3));
    col.rgb *= lerp(1, ao, aoStrength * aoFade);
    if (effects & EffectGi)
    {
        // the bounced light on this surface times its albedo. The frame has no albedo: it shows the albedo times the
        // light that arrives, the game's ambient (for this facing) plus the sun where the shadow rays reach it
        float3 gi = GiBlurred(id.xy, depth);
        float giFade = 1 - saturate((dist - giDistance * 0.75) / max(giDistance * 0.25, 1e-3));
        if (mode == 7) { ColorOut[id.xy] = float4(gi * 2, 1); return; }
        float3 n = NormalAt(id.xy, WorldPos(id.xy, depth));
        float3 light = lerp(skyGround.rgb, skyTop.rgb, saturate(n.y * 0.5 + 0.5));
        if (sunDir.w > 0) light += sunColor.rgb * saturate(dot(n, sunDir.xyz)) * (1 - shadow);
        float3 albedo = min(col.rgb / max(light, 0.02), 0.8);
        col.rgb += albedo * gi * giStrength * giFade;
    }
    if (effects & EffectRefl)
    {
        float4 r = Refl[id.xy];
        col.rgb = lerp(col.rgb, r.rgb, saturate(r.a * reflStrength));
        if (mode == 6) { ColorOut[id.xy] = float4(r.rgb, 1); return; }
        if (mode == 5) { ColorOut[id.xy] = float4(r.rgb * saturate(r.a * reflStrength * 4), 1); return; }
    }
    ColorOut[id.xy] = col;
}
