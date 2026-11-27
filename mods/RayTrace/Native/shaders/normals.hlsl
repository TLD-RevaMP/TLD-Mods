// profile: cs_6_0
// A mesh's triangle normals for the rays' hits, once, when its structure is built: per triangle its three vertex
// normals (or its face normal three times), packed (octahedral, 16 bits a component), twelve bytes a triangle.
ByteAddressBuffer Vertices : register(t0);
ByteAddressBuffer Normals : register(t1);
ByteAddressBuffer Indices : register(t2);
RWByteAddressBuffer Out : register(u0);
struct Params { uint vOffset, vStride, vFormat, nOffset, nStride, nFormat, iOffset, index32, tris, outTri, pad0, pad1; };
ConstantBuffer<Params> p : register(b0);

uint Index(uint i)
{
    if (p.index32) return Indices.Load(p.iOffset + i * 4);
    uint a = p.iOffset + i * 2, w = Indices.Load(a & ~3u);
    return (a & 2) ? (w >> 16) : (w & 0xFFFF);
}

float3 Position(uint v)
{
    uint a = p.vOffset + v * p.vStride;
    if (p.vFormat == 10) { uint2 h = uint2(Vertices.Load(a), Vertices.Load(a + 4)); return float3(f16tof32(h.x), f16tof32(h.x >> 16), f16tof32(h.y)); }
    return asfloat(uint3(Vertices.Load(a), Vertices.Load(a + 4), Vertices.Load(a + 8)));
}

float3 Normal(uint v, float3 face)
{
    uint a = p.nOffset + v * p.nStride;
    float3 n = face;
    if (p.nFormat == 1) n = asfloat(uint3(Normals.Load(a), Normals.Load(a + 4), Normals.Load(a + 8)));
    else if (p.nFormat == 2) { uint2 h = uint2(Normals.Load(a), Normals.Load(a + 4)); n = float3(f16tof32(h.x), f16tof32(h.x >> 16), f16tof32(h.y)); }
    else if (p.nFormat == 3) { uint w = Normals.Load(a); int3 q = int3(w << 24, w << 16, w << 8) >> 24; n = max(float3(q) / 127.0, -1); }
    return dot(n, n) > 1e-8 ? normalize(n) : face;
}

uint Pack(float3 n)
{
    n /= abs(n.x) + abs(n.y) + abs(n.z);
    float2 sgn = float2(n.x >= 0 ? 1 : -1, n.y >= 0 ? 1 : -1);
    float2 e = n.z >= 0 ? n.xy : (1 - abs(n.yx)) * sgn;
    int2 q = int2(round(clamp(e, -1, 1) * 32767));
    return (uint(q.x) & 0xFFFF) | (uint(q.y) << 16);
}

[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint t = id.x;
    if (t >= p.tris) return;
    uint i0 = Index(t * 3), i1 = Index(t * 3 + 1), i2 = Index(t * 3 + 2);
    float3 p0 = Position(i0), p1 = Position(i1), p2 = Position(i2);
    float3 c = cross(p1 - p0, p2 - p0);
    float3 face = dot(c, c) > 1e-20 ? normalize(c) : float3(0, 1, 0);
    Out.Store3((p.outTri + t) * 12, uint3(Pack(Normal(i0, face)), Pack(Normal(i1, face)), Pack(Normal(i2, face))));
}
