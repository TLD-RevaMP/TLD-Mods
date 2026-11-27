// profile: cs_6_0
// The traced results applied to the frame. Shadows by mode: 1 only beyond the game's own (blended in from fadeStart to
// fadeEnd), 2 everywhere, 3 the shadow mask itself; ambient occlusion within aoDistance, blurred over its depth
// neighbours; mode 4 shows the ambient occlusion alone.
#include "common.hlsli"
Texture2D<float4> ColorIn : register(t2);
RWTexture2D<float4> ColorOut : register(u1);

float AoBlurred(int2 px, float depth)
{
    float sum = 0, weight = 0;
    for (int y = -2; y <= 2; y++)
        for (int x = -2; x <= 2; x++)
        {
            int2 q = clamp(px + int2(x, y), int2(0, 0), int2(c.size.xy) - 1);
            float d = Depth.Load(int3(q, 0));
            float w = exp(-abs(d - depth) / (depth * 0.02 + 1e-7)) * (1.0 / (1 + x * x + y * y));
            sum += Ao[q] * w; weight += w;
        }
    return weight > 0 ? sum / weight : 1;
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= (uint2)c.size.xy)) return;
    float4 col = ColorIn.Load(int3(id.xy, 0));
    float depth = Depth.Load(int3(id.xy, 0));
    if (depth <= 0) { ColorOut[id.xy] = c.mode == 3 || c.mode == 4 ? float4(0.4, 0.6, 1, 1) : col; return; }
    float dist = distance(WorldPos(id.xy, depth), c.cameraPos.xyz);
    float ao = (c.effects & EffectAo) ? AoBlurred(id.xy, depth) : 1;
    float shadow = (c.effects & EffectShadows) ? Mask[id.xy] : 0;
    if (c.mode == 3) { ColorOut[id.xy] = float4((1 - shadow).xxx, 1); return; }
    if (c.mode == 4) { ColorOut[id.xy] = float4(ao.xxx, 1); return; }
    if (c.mode == 1 || c.mode == 2)
    {
        float weight = c.mode == 1 ? saturate((dist - c.fadeStart) / max(c.fadeEnd - c.fadeStart, 1e-3)) : 1;
        col.rgb *= 1 - c.strength * shadow * weight;
    }
    float aoFade = 1 - saturate((dist - c.aoDistance * 0.75) / max(c.aoDistance * 0.25, 1e-3));
    col.rgb *= lerp(1, ao, c.aoStrength * aoFade);
    ColorOut[id.xy] = col;
}
