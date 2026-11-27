// profile: lib_6_3
// Per pixel, from the point the depth buffer shows: a shadow ray toward the sun, short ambient occlusion rays over the
// surface's hemisphere, and a reflection: a primary ray finds the surface (or glass in front of it), its material and
// its normal, and a mirror ray what it reflects.
#include "common.hlsli"
RaytracingAccelerationStructure Scene : register(t0);
struct InstanceInfo { uint triOffset, flags; float smoothness, metallic; float3 albedo; float pad; };
StructuredBuffer<InstanceInfo> Instances : register(t4);
ByteAddressBuffer TriNormals : register(t5);
struct Payload { float t; uint inst; float3 n; };   // t < 0: nothing hit
static const uint MaskSolid = 1, MaskSeeThrough = 2, NoNormals = 0xFFFFFFFF;

// a fixed number per pixel (no change from frame to frame: noise that holds still, which the composite's blur hides)
float Hash(uint2 p, uint k)
{
    uint h = p.x * 1973u + p.y * 9277u + k * 26699u;
    h = (h ^ (h >> 13)) * 1274126177u; h ^= h >> 16;
    return (h & 0xFFFFFF) / 16777216.0;
}

bool Blocked(float3 origin, float3 dir, float tMax)
{
    RayDesc r; r.Origin = origin; r.Direction = dir; r.TMin = 0; r.TMax = tMax;
    Payload pl; pl.t = 0; pl.inst = 0; pl.n = 0;
    TraceRay(Scene, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER | RAY_FLAG_FORCE_OPAQUE, MaskSolid, 0, 1, 0, r, pl);
    return pl.t >= 0;
}

Payload Closest(float3 origin, float3 dir, float tMax, uint mask)
{
    RayDesc r; r.Origin = origin; r.Direction = dir; r.TMin = 0; r.TMax = tMax;
    Payload pl; pl.t = -1; pl.inst = 0; pl.n = 0;
    TraceRay(Scene, RAY_FLAG_FORCE_OPAQUE, mask, 0, 1, 0, r, pl);
    return pl;
}

float3 Capped(float3 c, float most) { float l = dot(c, float3(0.2126, 0.7152, 0.0722)); return l > most ? c * (most / l) : c; }

// The sky in a direction: the frame's own sky where that direction is on screen and nothing stands in front of it there,
// else an estimate from the ambient's colours (no brighter than the frame's sky usually is)
float3 Sky(float3 d)
{
    float4 clip = mul(viewProj, float4(cameraPos.xyz + d * 10000, 1));
    if (clip.w > 0)
    {
        float2 ndc = clip.xy / clip.w, uv = float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
        if (all(uv >= 0) && all(uv < 1))
        {
            int2 q = int2(uv * size.xy);
            if (Depth.Load(int3(q, 0)) <= 0) return ColorIn.Load(int3(q, 0)).rgb;
        }
    }
    float3 s = d.y >= 0 ? lerp(skyHorizon.rgb, skyTop.rgb, sqrt(saturate(d.y))) : lerp(skyHorizon.rgb, skyGround.rgb, saturate(-d.y * 4));
    return Capped(s, 0.8);
}

// What a reflection ray sees: the frame itself where its hit is on screen (and not hidden there), else the hit's
// material colour lit by the sun (with a shadow ray) and the sky, else the sky
float3 Reflected(float3 origin, float3 dir, float pixelDist)
{
    Payload h = Closest(origin, dir, 1000, MaskSolid);
    if (h.t < 0) return Sky(dir);
    float3 hp = origin + dir * h.t;
    float4 clip = mul(viewProj, float4(hp, 1));
    if (clip.w > 0)
    {
        float2 ndc = clip.xy / clip.w, uv = float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
        if (all(uv >= 0) && all(uv < 1))
        {
            int2 q = int2(uv * size.xy);
            float3 seen = WorldPos(q, Depth.Load(int3(q, 0)));
            if (distance(seen, hp) < 0.3 + 0.03 * distance(hp, cameraPos.xyz)) return ColorIn.Load(int3(q, 0)).rgb;
        }
    }
    InstanceInfo m = Instances[h.inst];
    float3 n = any(h.n != 0) ? h.n : -dir;
    if (dot(n, dir) > 0) n = -n;
    float lambert = saturate(dot(n, sunDir.xyz));
    float sun = sunDir.w > 0 && lambert > 0 && !Blocked(hp + n * 0.05, sunDir.xyz, maxDistance) ? lambert : 0;
    // the material's tint without its texture (a textured surface averages well under its tint), lit by sun and sky
    // (the hidden ground under a car is shaded and textured: kept dark, it was a bright grey before 2026-11-27)
    return Capped(m.albedo * 0.15 * (lerp(skyGround.rgb, skyTop.rgb, n.y * 0.5 + 0.5) * 0.6 + sunColor.rgb * sun), 0.25);
}

float3 Unpack(uint v)
{
    float2 e = float2(int2(v << 16, v) >> 16) / 32767.0;
    float3 n = float3(e, 1 - abs(e.x) - abs(e.y));
    if (n.z < 0) n.xy = (1 - abs(n.yx)) * float2(n.x >= 0 ? 1 : -1, n.y >= 0 ? 1 : -1);
    return normalize(n);
}

[shader("raygeneration")]
void Gen()
{
    uint2 px = DispatchRaysIndex().xy;
    float depth = Depth.Load(int3(px, 0));
    if (depth <= 0) { Mask[px] = 0; Ao[px] = 1; Refl[px] = 0; return; }
    float3 p = WorldPos(px, depth);
    float3 toCam = cameraPos.xyz - p;
    float dist = length(toCam);
    float3 view = toCam / max(dist, 1e-4);
    // off the surface it was read from: toward the camera, more the further away (depth is coarser there)
    float bias = 0.03 + dist * 0.004;

    if (effects & EffectShadows)
        Mask[px] = Blocked(p + view * bias + sunDir.xyz * bias, sunDir.xyz, maxDistance) ? 1 : 0;

    if (effects & EffectAo)
    {
        float open = 1;
        if (dist < aoDistance)
        {
            float3 n = NormalAt(px, p);
            float3 t = normalize(abs(n.y) < 0.99 ? cross(n, float3(0, 1, 0)) : cross(n, float3(1, 0, 0)));
            float3 b = cross(n, t);
            float3 o = p + n * (0.02 + dist * 0.002) + view * bias * 0.5;
            float rot = Hash(px, 7) * 6.2831853;
            int rays = max(1, aoRays), hit = 0;
            for (int k = 0; k < rays; k++)
            {
                // cosine-weighted over the hemisphere, the angles spread evenly and turned per pixel
                float u1 = (k + Hash(px, k)) / rays, a = rot + k * 2.3999632;
                float rr = sqrt(u1);
                float3 dir = normalize(t * (rr * cos(a)) + b * (rr * sin(a)) + n * sqrt(max(0, 1 - u1)));
                if (Blocked(o, dir, aoRadius)) hit++;
            }
            open = 1 - (float)hit / rays;
        }
        Ao[px] = open;
    }

    if (effects & EffectRefl)
    {
        float4 result = 0;
        // the primary ray: the surface under this pixel, or glass and water in front of it
        Payload s = Closest(cameraPos.xyz, -view, dist + max(0.3, dist * 0.02), MaskSolid | MaskSeeThrough);
        if (mode == 6) { InstanceInfo d = Instances[s.inst]; Refl[px] = s.t >= 0 ? float4(d.smoothness, d.flags & 1, 1, 1) : 0; return; }
        if (s.t >= 0)
        {
            InstanceInfo m = Instances[s.inst];
            if (m.smoothness > 0.7)
            {
                float3 sp = cameraPos.xyz - view * s.t;
                float3 n = any(s.n != 0) ? s.n : NormalAt(px, p);
                if (dot(n, view) < 0) n = -n;
                float3 r = reflect(-view, n);
                float3 col = Reflected(sp + n * (0.02 + s.t * 0.002), r, s.t);
                // Fresnel (Schlick): a metal reflects its own colour, anything else 4 % head-on, more at a grazing angle
                float3 f0 = lerp(0.04, m.albedo, m.metallic);
                float3 fr = f0 + (1 - f0) * pow(1 - saturate(dot(n, view)), 5);
                float amount = max(fr.r, max(fr.g, fr.b));
                // Unity already gives a glossy material its own reflection (probes, the sky): one sharp mirror ray is added
                // only where the surface is near a mirror (glass, water, polish), from smoothness 0.7 to fully at 1 (the
                // bus's paint, 0.75, gets a little)
                result = float4(col * fr / max(amount, 1e-3), amount * saturate((m.smoothness - 0.7) / 0.3));
            }
        }
        Refl[px] = result;
    }
}

[shader("miss")]
void Miss(inout Payload pl) { pl.t = -1; }

// the hit's distance, its instance and its normal in world space (its triangle's vertex normals interpolated)
[shader("closesthit")]
void Hit(inout Payload pl, in BuiltInTriangleIntersectionAttributes a)
{
    pl.t = RayTCurrent(); pl.inst = InstanceID(); pl.n = 0;
    uint tri = Instances[InstanceID()].triOffset;
    if (tri != NoNormals)
    {
        uint3 w = TriNormals.Load3((tri + PrimitiveIndex()) * 12);
        float3 b = float3(1 - a.barycentrics.x - a.barycentrics.y, a.barycentrics.x, a.barycentrics.y);
        float3 n = Unpack(w.x) * b.x + Unpack(w.y) * b.y + Unpack(w.z) * b.z;
        pl.n = normalize(mul(n, (float3x3)WorldToObject3x4()));
    }
}
