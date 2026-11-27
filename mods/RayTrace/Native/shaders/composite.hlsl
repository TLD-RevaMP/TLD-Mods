// profile: cs_6_0
// The shadow mask applied to the frame. Mode 1: only beyond the game's own shadows (blended in from fadeStart to
// fadeEnd), 2: everywhere, 3: the mask itself (white lit, black shadow). Zero or less: the colour as it was.
#include "common.hlsli"
Texture2D<float4> ColorIn : register(t2);
RWTexture2D<float4> ColorOut : register(u1);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= (uint2)c.size.xy)) return;
    float4 col = ColorIn.Load(int3(id.xy, 0));
    float depth = Depth.Load(int3(id.xy, 0));
    if (c.mode <= 0 || depth <= 0) { ColorOut[id.xy] = c.mode == 3 && depth <= 0 ? float4(0.4, 0.6, 1, 1) : col; return; }
    float shadow = Mask[id.xy];
    if (c.mode == 3) { ColorOut[id.xy] = float4((1 - shadow).xxx, 1); return; }
    float weight = 1;
    if (c.mode == 1)
    {
        float dist = distance(WorldPos(id.xy, depth), c.cameraPos.xyz);
        weight = saturate((dist - c.fadeStart) / max(c.fadeEnd - c.fadeStart, 1e-3));
    }
    col.rgb *= 1 - c.strength * shadow * weight;
    ColorOut[id.xy] = col;
}
