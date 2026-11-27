// profile: lib_6_3
// Per pixel, from the point the depth buffer shows: one shadow ray toward the sun, and a few short ambient occlusion rays
// over the surface's hemisphere; against every mesh near the camera.
#include "common.hlsli"
RaytracingAccelerationStructure Scene : register(t0);
struct Payload { float lit; };

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
    Payload pl; pl.lit = 0;
    TraceRay(Scene, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER | RAY_FLAG_FORCE_OPAQUE, 0xFF, 0, 1, 0, r, pl);
    return pl.lit < 0.5;
}

[shader("raygeneration")]
void Gen()
{
    uint2 px = DispatchRaysIndex().xy;
    float depth = Depth.Load(int3(px, 0));
    if (depth <= 0) { Mask[px] = 0; Ao[px] = 1; return; }
    float3 p = WorldPos(px, depth);
    float3 toCam = c.cameraPos.xyz - p;
    float dist = length(toCam);
    float3 view = toCam / max(dist, 1e-4);
    // off the surface it was read from: toward the camera, more the further away (depth is coarser there)
    float bias = 0.03 + dist * 0.004;

    if (c.effects & EffectShadows)
        Mask[px] = Blocked(p + view * bias + c.sunDir.xyz * bias, c.sunDir.xyz, c.maxDistance) ? 1 : 0;

    if (c.effects & EffectAo)
    {
        float open = 1;
        if (dist < c.aoDistance)
        {
            float3 n = NormalAt(px, p);
            float3 t = normalize(abs(n.y) < 0.99 ? cross(n, float3(0, 1, 0)) : cross(n, float3(1, 0, 0)));
            float3 b = cross(n, t);
            float3 o = p + n * (0.02 + dist * 0.002) + view * bias * 0.5;
            float rot = Hash(px, 7) * 6.2831853;
            int rays = max(1, c.aoRays), hit = 0;
            for (int k = 0; k < rays; k++)
            {
                // cosine-weighted over the hemisphere, the angles spread evenly and turned per pixel
                float u1 = (k + Hash(px, k)) / rays, a = rot + k * 2.3999632;
                float rr = sqrt(u1);
                float3 dir = normalize(t * (rr * cos(a)) + b * (rr * sin(a)) + n * sqrt(max(0, 1 - u1)));
                if (Blocked(o, dir, c.aoRadius)) hit++;
            }
            open = 1 - (float)hit / rays;
        }
        Ao[px] = open;
    }
}

[shader("miss")]
void Miss(inout Payload pl) { pl.lit = 1; }

[shader("closesthit")]
void Hit(inout Payload pl, in BuiltInTriangleIntersectionAttributes a) { }
