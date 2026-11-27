// profile: lib_6_3
// One shadow ray per pixel: from the point the depth buffer shows, toward the sun, against every mesh near the camera.
#include "common.hlsli"
RaytracingAccelerationStructure Scene : register(t0);
struct Payload { float lit; };

[shader("raygeneration")]
void Gen()
{
    uint2 px = DispatchRaysIndex().xy;
    float depth = Depth.Load(int3(px, 0));
    if (depth <= 0) { Mask[px] = 0; return; }
    float3 p = WorldPos(px, depth);
    float3 toCam = c.cameraPos.xyz - p;
    float dist = length(toCam);
    // off the surface it was read from: toward the camera and the sun, more the further away (depth is coarser there)
    float bias = 0.03 + dist * 0.004;
    RayDesc r;
    r.Origin = p + toCam / max(dist, 1e-4) * bias + c.sunDir.xyz * bias;
    r.Direction = c.sunDir.xyz;
    r.TMin = 0;
    r.TMax = c.maxDistance;
    Payload pl; pl.lit = 0;
    TraceRay(Scene, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER | RAY_FLAG_FORCE_OPAQUE, 0xFF, 0, 1, 0, r, pl);
    Mask[px] = 1 - pl.lit;
}

[shader("miss")]
void Miss(inout Payload pl) { pl.lit = 1; }

[shader("closesthit")]
void Hit(inout Payload pl, in BuiltInTriangleIntersectionAttributes a) { }
