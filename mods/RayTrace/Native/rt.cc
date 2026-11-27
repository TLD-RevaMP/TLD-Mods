// RayTraceNative.dll: the D3D12 side of the RayTrace mod. Unity hands it its D3D12 device through the native plugin
// interface (UnityPluginLoad); the C# side (RayTrace.dll) sends one render event per frame with the scene and the
// camera. In that event:
//  1. a bottom-level acceleration structure for each mesh it hasn't seen (a few per frame), from Unity's own GPU
//     vertex and index buffers;
//  2. the top-level structure of this frame's instances;
//  3. shadow rays toward the sun from every pixel's depth (shaders/raytrace.hlsl), into a mask of its own;
//  4. the mask applied to the frame's colour (shaders/composite.hlsl), into the output texture Unity copies back.
// It records a command list of its own that Unity runs in order with its own (ExecuteCommandList; or, to compare,
// straight into Unity's: CommandRecordingState). The three textures it shares with Unity are random-write textures.
#include <windows.h>
#include <d3d12.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>
#include <vector>
#include <unordered_map>
#include "unity_plugin.h"

// ---------------------------------------------------------------- the data C# sends (layouts mirrored in Native.cs)

#pragma pack(push, 4)
struct RtGeometry
{
    float transform[12];            // object to world, 3x4 row-major
    ID3D12Resource* vertexBuffer;
    ID3D12Resource* indexBuffer;
    uint64_t key;                   // the mesh and submesh: the same key, the same bottom-level structure
    uint32_t vertexOffset;          // bytes to the first vertex's position (the attribute offset and the base vertex)
    uint32_t vertexStride;
    uint32_t vertexCount;
    uint32_t vertexFormat;          // a DXGI_FORMAT for the position
    uint32_t indexStart;            // in indices
    uint32_t indexCount;
    uint32_t index32;               // 1: 32-bit indices, 0: 16-bit
    uint32_t pad;
};

struct RtFrame
{
    RtGeometry* geometries;
    int32_t geometryCount;
    int32_t width, height;
    int32_t mode;                   // 0 off, 1 far shadows (beyond the game's), 2 shadows everywhere, 3 the mask alone
    ID3D12Resource* depth;          // R32 float copy of the camera's depth (Unity's reversed z)
    ID3D12Resource* colorIn;        // the frame's colour after the opaque geometry
    ID3D12Resource* colorOut;       // random-write texture Unity copies back into the frame
    float invViewProj[16];          // row-major: clip (x, y, raw depth) to world
    float cameraPos[4];
    float sunDir[4];                // toward the sun (w: 1 when the sun is up)
    float fadeStart, fadeEnd;       // the far mode's blend: from here (m) to there fully ray traced
    float strength;                 // how much of a pixel's light the sun is (0..1): a shadowed pixel keeps 1 - strength
    float maxDistance;              // shadow rays this long (m)
    int32_t newStructuresPerFrame;
    int32_t frame;
    int32_t stage;                  // testing: 2 the event, 3 + the structures, 4 + the rays, 5 + the composite (all); 50 all but the composite, 51 all but the copy back
    int32_t submit;                 // 0: our own command list, run by Unity (ExecuteCommandList); 1: into Unity's own (CommandRecordingState)
    int32_t effects;                // wanted: 1 sun shadows, 2 ambient occlusion
    int32_t aoRays;
    float aoRadius, aoStrength, aoDistance;
};
#pragma pack(pop)

// ---------------------------------------------------------------- state

static IUnityInterfaces* g_unity;
static IUnityGraphics* g_graphics;
static IUnityGraphicsD3D12v7* g_d3d12;
static ID3D12Device5* g_device;
static char g_status[512] = "not loaded by Unity";
static char g_error[512] = "";
static int g_events, g_dispatches, g_blasBuilt, g_instances, g_blasLive, g_skipped;
static std::vector<char> g_rtLib, g_composite;

static void Status(const char* fmt, ...) { va_list a; va_start(a, fmt); vsnprintf(g_status, sizeof g_status, fmt, a); va_end(a); }
static void Error(const char* fmt, ...) { va_list a; va_start(a, fmt); vsnprintf(g_error, sizeof g_error, fmt, a); va_end(a); }
#define CHECK(x, what) do { HRESULT _hr = (x); if (FAILED(_hr)) { Error("%s failed: 0x%08lx", what, (unsigned long)_hr); return false; } } while (0)

static const int kFrames = 3;
struct FrameSlot
{
    // Our own list (submit 0): Unity runs it from a worker thread, later, so each slot has its own, recorded again
    // only once the GPU is past it, and the states passed with it live here (Unity reads them there too)
    ID3D12CommandAllocator* allocator = nullptr;
    ID3D12GraphicsCommandList4* list = nullptr;
    UnityGraphicsD3D12ResourceState states[3] = {};
    uint64_t fence = 0;
    ID3D12Resource* instances = nullptr; uint32_t instanceCap = 0; void* instanceMap = nullptr;
    std::vector<ID3D12Resource*> release;   // freed once this slot's fence has passed
};
static FrameSlot g_slots[kFrames];
static int g_slot;
static ID3D12GraphicsCommandList4* g_list;
static ID3D12RootSignature* g_rootSig;
static ID3D12StateObject* g_rtpso;
static ID3D12PipelineState* g_compositePso;
static ID3D12Resource* g_shaderTable;
static ID3D12DescriptorHeap* g_heap;
static UINT g_descSize;
static ID3D12Resource* g_mask; static ID3D12Resource* g_ao; static int g_maskW, g_maskH;
static const int kDesc = 8;   // per slot: t1 depth, t2 colour in, t3 (spare), u0 shadow mask, u1 colour out, u2 ambient occlusion, u3-u4 (spare)
static ID3D12Resource* g_tlas; static ID3D12Resource* g_tlasScratch; static uint64_t g_tlasSize, g_tlasScratchSize;
static ID3D12Resource* g_blasScratch; static uint64_t g_blasScratchSize;
static bool g_ready, g_failed;

// GPU time of our work, per frame: timestamps at its start, after the structures, after the rays and after the composite
// (four per slot), read back once the slot's fence has passed; averaged over the last 60 frames read
static ID3D12QueryHeap* g_queries;
static ID3D12Resource* g_queryReadback;
static double g_tickMs;
static bool g_slotTimed[3];
static double g_gpuBuild, g_gpuRays, g_gpuComposite;
static int g_gpuSamples;

struct Blas { ID3D12Resource* buffer; ID3D12Resource* vb; uint32_t vertexCount; int lastFrame; };
static std::unordered_map<uint64_t, Blas> g_blas;

// ---------------------------------------------------------------- helpers

static ID3D12Resource* Buffer(uint64_t size, D3D12_HEAP_TYPE heap, D3D12_RESOURCE_STATES state, D3D12_RESOURCE_FLAGS flags)
{
    D3D12_HEAP_PROPERTIES hp = {}; hp.Type = heap;
    D3D12_RESOURCE_DESC d = {}; d.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER; d.Width = size < 256 ? 256 : size; d.Height = 1;
    d.DepthOrArraySize = 1; d.MipLevels = 1; d.SampleDesc.Count = 1; d.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR; d.Flags = flags;
    ID3D12Resource* r = nullptr;
    if (FAILED(g_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &d, state, nullptr, __uuidof(ID3D12Resource), (void**)&r))) return nullptr;
    return r;
}

static void Release(ID3D12Resource* r) { if (r) g_slots[g_slot].release.push_back(r); }

static DXGI_FORMAT SrvFormat(DXGI_FORMAT f)
{
    switch (f)
    {
        case DXGI_FORMAT_R32_TYPELESS: case DXGI_FORMAT_D32_FLOAT: return DXGI_FORMAT_R32_FLOAT;
        case DXGI_FORMAT_R16_TYPELESS: case DXGI_FORMAT_D16_UNORM: return DXGI_FORMAT_R16_UNORM;
        case DXGI_FORMAT_R24G8_TYPELESS: case DXGI_FORMAT_D24_UNORM_S8_UINT: return DXGI_FORMAT_R24_UNORM_X8_TYPELESS;
        case DXGI_FORMAT_R32G8X24_TYPELESS: case DXGI_FORMAT_D32_FLOAT_S8X24_UINT: return DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS;
        case DXGI_FORMAT_R8G8B8A8_TYPELESS: case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return DXGI_FORMAT_R8G8B8A8_UNORM;
        case DXGI_FORMAT_B8G8R8A8_TYPELESS: case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: return DXGI_FORMAT_B8G8R8A8_UNORM;
        case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
        case DXGI_FORMAT_R10G10B10A2_TYPELESS: return DXGI_FORMAT_R10G10B10A2_UNORM;
        case DXGI_FORMAT_R11G11B10_FLOAT: return DXGI_FORMAT_R11G11B10_FLOAT;
        case DXGI_FORMAT_R32G32B32A32_TYPELESS: return DXGI_FORMAT_R32G32B32A32_FLOAT;
        default: return f;
    }
}

static D3D12_CPU_DESCRIPTOR_HANDLE Cpu(int slot, int i) { auto h = g_heap->GetCPUDescriptorHandleForHeapStart(); h.ptr += (SIZE_T)(slot * kDesc + i) * g_descSize; return h; }
static D3D12_GPU_DESCRIPTOR_HANDLE Gpu(int slot) { auto h = g_heap->GetGPUDescriptorHandleForHeapStart(); h.ptr += (UINT64)(slot * kDesc) * g_descSize; return h; }

static void Srv(ID3D12Resource* r, D3D12_CPU_DESCRIPTOR_HANDLE at)
{
    D3D12_SHADER_RESOURCE_VIEW_DESC v = {};
    v.Format = SrvFormat(r->GetDesc().Format); v.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
    v.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING; v.Texture2D.MipLevels = 1;
    g_device->CreateShaderResourceView(r, &v, at);
}

static void Uav(ID3D12Resource* r, D3D12_CPU_DESCRIPTOR_HANDLE at)
{
    D3D12_UNORDERED_ACCESS_VIEW_DESC v = {};
    v.Format = SrvFormat(r->GetDesc().Format); v.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
    g_device->CreateUnorderedAccessView(r, nullptr, &v, at);
}

static void UavBarrier(ID3D12Resource* r)
{
    D3D12_RESOURCE_BARRIER b = {}; b.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV; b.UAV.pResource = r;
    g_list->ResourceBarrier(1, &b);
}

// ---------------------------------------------------------------- set-up

static bool CreatePipelines()
{
    // one root signature for both passes: t0 the scene (root SRV), a table (kDesc: three SRVs from t1, the rest UAVs
    // from u0), and 44 root constants (b0)
    D3D12_DESCRIPTOR_RANGE ranges[2] = {};
    ranges[0].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV; ranges[0].NumDescriptors = 3; ranges[0].BaseShaderRegister = 1; ranges[0].OffsetInDescriptorsFromTableStart = 0;
    ranges[1].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV; ranges[1].NumDescriptors = kDesc - 3; ranges[1].BaseShaderRegister = 0; ranges[1].OffsetInDescriptorsFromTableStart = 3;
    D3D12_ROOT_PARAMETER p[3] = {};
    p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV; p[0].Descriptor.ShaderRegister = 0;
    p[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE; p[1].DescriptorTable.NumDescriptorRanges = 2; p[1].DescriptorTable.pDescriptorRanges = ranges;
    p[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS; p[2].Constants.ShaderRegister = 0; p[2].Constants.Num32BitValues = 44;
    D3D12_ROOT_SIGNATURE_DESC rs = {}; rs.NumParameters = 3; rs.pParameters = p;
    typedef HRESULT (WINAPI* Serialize)(const D3D12_ROOT_SIGNATURE_DESC*, D3D_ROOT_SIGNATURE_VERSION, ID3DBlob**, ID3DBlob**);
    auto serialize = (Serialize)GetProcAddress(GetModuleHandleA("d3d12.dll"), "D3D12SerializeRootSignature");
    if (!serialize) { Error("no D3D12SerializeRootSignature"); return false; }
    ID3DBlob* blob = nullptr; ID3DBlob* err = nullptr;
    if (FAILED(serialize(&rs, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &err))) { Error("root signature: %s", err ? (const char*)err->GetBufferPointer() : "?"); return false; }
    CHECK(g_device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), __uuidof(ID3D12RootSignature), (void**)&g_rootSig), "CreateRootSignature");
    blob->Release();

    // the ray tracing pipeline: Gen (ray generation), Miss, and a hit group around an empty closest hit (the rays skip it)
    D3D12_EXPORT_DESC exports[3] = { { L"Gen", nullptr, D3D12_EXPORT_FLAG_NONE }, { L"Miss", nullptr, D3D12_EXPORT_FLAG_NONE }, { L"Hit", nullptr, D3D12_EXPORT_FLAG_NONE } };
    D3D12_DXIL_LIBRARY_DESC lib = {}; lib.DXILLibrary.pShaderBytecode = g_rtLib.data(); lib.DXILLibrary.BytecodeLength = g_rtLib.size(); lib.NumExports = 3; lib.pExports = exports;
    D3D12_HIT_GROUP_DESC hg = {}; hg.HitGroupExport = L"HitGroup"; hg.Type = D3D12_HIT_GROUP_TYPE_TRIANGLES; hg.ClosestHitShaderImport = L"Hit";
    D3D12_RAYTRACING_SHADER_CONFIG sc = {}; sc.MaxPayloadSizeInBytes = 4; sc.MaxAttributeSizeInBytes = 8;
    D3D12_RAYTRACING_PIPELINE_CONFIG pc = {}; pc.MaxTraceRecursionDepth = 1;
    D3D12_GLOBAL_ROOT_SIGNATURE grs = { g_rootSig };
    D3D12_STATE_SUBOBJECT sub[5] = {
        { D3D12_STATE_SUBOBJECT_TYPE_DXIL_LIBRARY, &lib }, { D3D12_STATE_SUBOBJECT_TYPE_HIT_GROUP, &hg },
        { D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_SHADER_CONFIG, &sc }, { D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_PIPELINE_CONFIG, &pc },
        { D3D12_STATE_SUBOBJECT_TYPE_GLOBAL_ROOT_SIGNATURE, &grs } };
    D3D12_STATE_OBJECT_DESC so = {}; so.Type = D3D12_STATE_OBJECT_TYPE_RAYTRACING_PIPELINE; so.NumSubobjects = 5; so.pSubobjects = sub;
    CHECK(g_device->CreateStateObject(&so, __uuidof(ID3D12StateObject), (void**)&g_rtpso), "CreateStateObject");

    ID3D12StateObjectProperties* props = nullptr;
    CHECK(g_rtpso->QueryInterface(__uuidof(ID3D12StateObjectProperties), (void**)&props), "StateObjectProperties");
    g_shaderTable = Buffer(3 * D3D12_RAYTRACING_SHADER_TABLE_BYTE_ALIGNMENT, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_STATE_GENERIC_READ, D3D12_RESOURCE_FLAG_NONE);
    if (!g_shaderTable) { Error("shader table"); return false; }
    char* map = nullptr; g_shaderTable->Map(0, nullptr, (void**)&map);
    const wchar_t* names[3] = { L"Gen", L"Miss", L"HitGroup" };
    for (int i = 0; i < 3; i++)
    {
        void* id = props->GetShaderIdentifier(names[i]);
        if (!id) { Error("no shader identifier %d", i); return false; }
        memcpy(map + i * D3D12_RAYTRACING_SHADER_TABLE_BYTE_ALIGNMENT, id, D3D12_SHADER_IDENTIFIER_SIZE_IN_BYTES);
    }
    g_shaderTable->Unmap(0, nullptr); props->Release();

    D3D12_COMPUTE_PIPELINE_STATE_DESC cp = {}; cp.pRootSignature = g_rootSig; cp.CS.pShaderBytecode = g_composite.data(); cp.CS.BytecodeLength = g_composite.size();
    CHECK(g_device->CreateComputePipelineState(&cp, __uuidof(ID3D12PipelineState), (void**)&g_compositePso), "CreateComputePipelineState");

    D3D12_DESCRIPTOR_HEAP_DESC hd = {}; hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV; hd.NumDescriptors = kDesc * kFrames; hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
    CHECK(g_device->CreateDescriptorHeap(&hd, __uuidof(ID3D12DescriptorHeap), (void**)&g_heap), "CreateDescriptorHeap");
    D3D12_QUERY_HEAP_DESC qd = {}; qd.Type = D3D12_QUERY_HEAP_TYPE_TIMESTAMP; qd.Count = 4 * kFrames;
    UINT64 freq = 0;
    ID3D12CommandQueue* queue = g_d3d12->GetCommandQueue();
    if (queue && SUCCEEDED(queue->GetTimestampFrequency(&freq)) && freq && SUCCEEDED(g_device->CreateQueryHeap(&qd, __uuidof(ID3D12QueryHeap), (void**)&g_queries)))
    {
        g_tickMs = 1000.0 / (double)freq;
        g_queryReadback = Buffer(8 * 4 * kFrames, D3D12_HEAP_TYPE_READBACK, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_FLAG_NONE);
    }
    g_descSize = g_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);

    for (auto& s : g_slots)
    {
        CHECK(g_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, __uuidof(ID3D12CommandAllocator), (void**)&s.allocator), "CreateCommandAllocator");
        CHECK(g_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, s.allocator, nullptr, __uuidof(ID3D12GraphicsCommandList4), (void**)&s.list), "CreateCommandList");
        s.list->Close();
    }
    return true;
}

static void Connect()
{
    if (g_device || !g_unity) return;
    g_d3d12 = (IUnityGraphicsD3D12v7*)g_unity->GetInterface(kGuidUnityGraphicsD3D12v7);
    if (!g_d3d12) { Status("Unity gave no D3D12 interface (renderer %d): start the game with -force-d3d12", g_graphics ? (int)g_graphics->GetRenderer() : -1); return; }
    ID3D12Device* dev = g_d3d12->GetDevice();
    if (!dev) { Status("D3D12 interface but no device yet"); return; }
    D3D12_FEATURE_DATA_D3D12_OPTIONS5 o5 = {};
    dev->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS5, &o5, sizeof o5);
    if (o5.RaytracingTier < D3D12_RAYTRACING_TIER_1_0) { Status("the GPU or driver has no ray tracing (DXR)"); return; }
    if (FAILED(dev->QueryInterface(__uuidof(ID3D12Device5), (void**)&g_device))) { Status("the device has no ID3D12Device5 (no DXR)"); return; }
    // Event 1 (our own list): Unity submits what it recorded before the event (the copies) first. Event 2 (into Unity's
    // list): it binds its own state again after ours (ModifiesCommandBuffersState).
    UnityD3D12PluginEventConfig cfg = {}; cfg.graphicsQueueAccess = 0 /* DontCare */;
    cfg.flags = 1u << 1 /* FlushCommandBuffers */; cfg.ensureActiveRenderTextureIsBound = false;
    g_d3d12->ConfigureEvent(1, &cfg);
    cfg.flags = 1u << 3 /* ModifiesCommandBuffersState */;
    g_d3d12->ConfigureEvent(2, &cfg);
    Status("D3D12 device ready, raytracing tier %d", (int)o5.RaytracingTier);
}

// ---------------------------------------------------------------- per frame

static void ReleaseSlot(FrameSlot& s)
{
    for (auto* r : s.release) r->Release();
    s.release.clear();
}

static ID3D12Resource* Texture(int w, int h, DXGI_FORMAT format)
{
    D3D12_HEAP_PROPERTIES hp = {}; hp.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC d = {}; d.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D; d.Width = w; d.Height = h; d.DepthOrArraySize = 1; d.MipLevels = 1;
    d.Format = format; d.SampleDesc.Count = 1; d.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
    ID3D12Resource* r = nullptr;
    if (FAILED(g_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &d, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, nullptr, __uuidof(ID3D12Resource), (void**)&r))) return nullptr;
    return r;
}

/// Our own textures at the frame's size: the shadow mask and the ambient occlusion.
static bool EnsureMask(int w, int h)
{
    if (g_mask && g_ao && g_maskW == w && g_maskH == h) return true;
    Release(g_mask); Release(g_ao);
    g_mask = Texture(w, h, DXGI_FORMAT_R8_UNORM); g_ao = Texture(w, h, DXGI_FORMAT_R8_UNORM);
    if (!g_mask || !g_ao) { Error("mask textures"); return false; }
    g_maskW = w; g_maskH = h;
    return true;
}

static bool EnsureBuffer(ID3D12Resource*& r, uint64_t& size, uint64_t need, D3D12_RESOURCE_STATES state)
{
    if (r && size >= need) return true;
    Release(r);
    size = need + need / 4;
    r = Buffer(size, D3D12_HEAP_TYPE_DEFAULT, state, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
    if (!r) { Error("buffer of %llu bytes", (unsigned long long)size); return false; }
    return true;
}

static D3D12_RAYTRACING_GEOMETRY_DESC GeometryDesc(const RtGeometry& g)
{
    D3D12_RAYTRACING_GEOMETRY_DESC d = {};
    d.Type = D3D12_RAYTRACING_GEOMETRY_TYPE_TRIANGLES; d.Flags = D3D12_RAYTRACING_GEOMETRY_FLAG_OPAQUE;
    d.Triangles.VertexBuffer.StartAddress = g.vertexBuffer->GetGPUVirtualAddress() + g.vertexOffset;
    d.Triangles.VertexBuffer.StrideInBytes = g.vertexStride;
    d.Triangles.VertexCount = g.vertexCount;
    d.Triangles.VertexFormat = (DXGI_FORMAT)g.vertexFormat;
    d.Triangles.IndexBuffer = g.indexBuffer->GetGPUVirtualAddress() + (uint64_t)g.indexStart * (g.index32 ? 4 : 2);
    d.Triangles.IndexCount = g.indexCount;
    d.Triangles.IndexFormat = g.index32 ? DXGI_FORMAT_R32_UINT : DXGI_FORMAT_R16_UINT;
    return d;
}

// The bottom-level structures this frame still needs, at most `budget` new ones (they share one scratch buffer, so
// they are built one after another with a barrier between).
static bool BuildBottomLevel(const RtFrame& f)
{
    int built = 0;
    for (int i = 0; i < f.geometryCount; i++)
    {
        const RtGeometry& g = f.geometries[i];
        auto it = g_blas.find(g.key);
        if (it != g_blas.end() && it->second.vb == g.vertexBuffer && it->second.vertexCount == g.vertexCount) { it->second.lastFrame = f.frame; continue; }
        if (built >= f.newStructuresPerFrame) continue;
        if (!g.vertexBuffer || !g.indexBuffer || g.indexCount < 3) { g_skipped++; continue; }
        // The build reads every vertex and index it is told of: a buffer smaller than that (a mesh the game is
        // rebuilding, its count already new and its buffer not) is a GPU page fault and the device is gone (the game
        // crashed while its terrain loaded, 2026-11-27)
        {
            uint64_t vbNeed = (uint64_t)g.vertexOffset + (uint64_t)g.vertexStride * (g.vertexCount ? g.vertexCount - 1 : 0) + (g.vertexFormat == 10 ? 8 : 12);
            uint64_t ibNeed = ((uint64_t)g.indexStart + g.indexCount) * (g.index32 ? 4 : 2);
            if (g.vertexBuffer->GetDesc().Width < vbNeed || g.indexBuffer->GetDesc().Width < ibNeed) { g_skipped++; continue; }
        }
        D3D12_RAYTRACING_GEOMETRY_DESC gd = GeometryDesc(g);
        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS in = {};
        in.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL; in.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
        in.NumDescs = 1; in.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY; in.pGeometryDescs = &gd;
        D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO info = {};
        g_device->GetRaytracingAccelerationStructurePrebuildInfo(&in, &info);
        if (!info.ResultDataMaxSizeInBytes) { g_skipped++; continue; }
        if (!EnsureBuffer(g_blasScratch, g_blasScratchSize, info.ScratchDataSizeInBytes, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)) return false;
        ID3D12Resource* result = Buffer(info.ResultDataMaxSizeInBytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
        if (!result) { Error("bottom-level buffer"); return false; }
        if (built) UavBarrier(g_blasScratch);
        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC b = {};
        b.Inputs = in; b.DestAccelerationStructureData = result->GetGPUVirtualAddress(); b.ScratchAccelerationStructureData = g_blasScratch->GetGPUVirtualAddress();
        g_list->BuildRaytracingAccelerationStructure(&b, 0, nullptr);
        if (it != g_blas.end()) Release(it->second.buffer);
        g_blas[g.key] = Blas{ result, g.vertexBuffer, g.vertexCount, f.frame };
        built++; g_blasBuilt++;
    }
    if (built) UavBarrier(nullptr);
    // structures no instance used for 10 s of frames go, once the GPU is past this frame
    if ((f.frame & 63) == 0)
        for (auto it = g_blas.begin(); it != g_blas.end();)
        {
            if (f.frame - it->second.lastFrame > 600) { Release(it->second.buffer); it = g_blas.erase(it); }
            else ++it;
        }
    g_blasLive = (int)g_blas.size();
    return true;
}

static bool BuildTopLevel(const RtFrame& f, FrameSlot& s, uint32_t& count)
{
    count = 0;
    uint32_t need = (uint32_t)f.geometryCount;
    if (!s.instances || s.instanceCap < need)
    {
        Release(s.instances);
        s.instanceCap = need + need / 2 + 64;
        s.instances = Buffer((uint64_t)s.instanceCap * sizeof(D3D12_RAYTRACING_INSTANCE_DESC), D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_STATE_GENERIC_READ, D3D12_RESOURCE_FLAG_NONE);
        if (!s.instances) { Error("instance buffer"); return false; }
        s.instances->Map(0, nullptr, &s.instanceMap);
    }
    auto* out = (D3D12_RAYTRACING_INSTANCE_DESC*)s.instanceMap;
    for (int i = 0; i < f.geometryCount; i++)
    {
        const RtGeometry& g = f.geometries[i];
        auto it = g_blas.find(g.key);
        if (it == g_blas.end() || it->second.lastFrame != f.frame) continue;
        D3D12_RAYTRACING_INSTANCE_DESC d = {};
        memcpy(d.Transform, g.transform, sizeof d.Transform);
        d.InstanceMask = 0xFF; d.Flags = D3D12_RAYTRACING_INSTANCE_FLAG_TRIANGLE_CULL_DISABLE | D3D12_RAYTRACING_INSTANCE_FLAG_FORCE_OPAQUE;
        d.AccelerationStructure = it->second.buffer->GetGPUVirtualAddress();
        out[count++] = d;
    }
    g_instances = (int)count;
    D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS in = {};
    in.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL; in.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_BUILD;
    in.NumDescs = count; in.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY; in.InstanceDescs = s.instances->GetGPUVirtualAddress();
    D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO info = {};
    g_device->GetRaytracingAccelerationStructurePrebuildInfo(&in, &info);
    if (!EnsureBuffer(g_tlas, g_tlasSize, info.ResultDataMaxSizeInBytes, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE)) return false;
    if (!EnsureBuffer(g_tlasScratch, g_tlasScratchSize, info.ScratchDataSizeInBytes, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)) return false;
    D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC b = {};
    b.Inputs = in; b.DestAccelerationStructureData = g_tlas->GetGPUVirtualAddress(); b.ScratchAccelerationStructureData = g_tlasScratch->GetGPUVirtualAddress();
    g_list->BuildRaytracingAccelerationStructure(&b, 0, nullptr);
    UavBarrier(g_tlas);
    return true;
}

struct Constants   // b0, 44 values (shaders/common.hlsli)
{
    float invViewProj[16];
    float cameraPos[4];
    float sunDir[4];
    float size[4];         // width, height, 1/width, 1/height
    float fadeStart, fadeEnd, strength, maxDistance;
    int32_t mode, frame, effects, aoRays;
    float aoRadius, aoStrength, aoDistance, pad0;
    float pad1[4];
};
static_assert(sizeof(Constants) == 44 * 4, "root constants");

static bool Frame(const RtFrame& f)
{
    if (!g_ready)
    {
        if (g_failed || !g_device) return false;
        if (g_rtLib.empty() || g_composite.empty()) { Error("no shaders"); return false; }
        if (!CreatePipelines()) { g_failed = true; return false; }
        g_ready = true;
    }
    if (!f.depth || !f.colorIn || !f.colorOut || f.width <= 0 || f.height <= 0) { Error("missing textures"); return false; }
    FrameSlot& s = g_slots[g_slot];
    ID3D12Fence* fence = g_d3d12->GetFrameFence();
    if (s.fence && fence && fence->GetCompletedValue() < s.fence) { g_skipped++; return true; }   // the GPU is still on this slot's frame: skip
    ID3D12GraphicsCommandList4* unityList = nullptr;
    if (f.submit == 1)
    {
        // into Unity's own command list, where our event sits in its frame (Unity's copy back after it crashed the
        // GPU while the world loaded, 2026-11-27: its own bindings are not all restored after ours)
        UnityGraphicsD3D12RecordingState rec = {};
        if (!g_d3d12->CommandRecordingState(&rec) || !rec.commandList) { Error("Unity is not recording a command list"); return false; }
        if (FAILED(rec.commandList->QueryInterface(__uuidof(ID3D12GraphicsCommandList4), (void**)&unityList))) { Error("no ID3D12GraphicsCommandList4"); return false; }
    }
    struct Done { ID3D12GraphicsCommandList4* l; ~Done() { if (l) l->Release(); } } done{ unityList };
    ReleaseSlot(s);
    if (g_queryReadback && g_slotTimed[g_slot])   // this slot's last frame is done: its timestamps are in
    {
        uint64_t* t = nullptr; D3D12_RANGE r = { (SIZE_T)g_slot * 32, (SIZE_T)g_slot * 32 + 32 };
        if (SUCCEEDED(g_queryReadback->Map(0, &r, (void**)&t)) && t)
        {
            uint64_t* q = t + g_slot * 4;
            if (q[1] >= q[0] && q[2] >= q[1] && q[3] >= q[2])
            {
                double k = g_gpuSamples < 60 ? 1.0 / (g_gpuSamples + 1) : 1.0 / 60;
                g_gpuBuild += ((q[1] - q[0]) * g_tickMs - g_gpuBuild) * k; g_gpuRays += ((q[2] - q[1]) * g_tickMs - g_gpuRays) * k;
                g_gpuComposite += ((q[3] - q[2]) * g_tickMs - g_gpuComposite) * k; g_gpuSamples++;
            }
            D3D12_RANGE none = { 0, 0 }; g_queryReadback->Unmap(0, &none);
        }
        g_slotTimed[g_slot] = false;
    }
    if (unityList) g_list = unityList;
    else
    {
        g_list = s.list;
        if (FAILED(s.allocator->Reset()) || FAILED(g_list->Reset(s.allocator, nullptr))) { Error("command list reset"); return false; }
    }
    if (!EnsureMask(f.width, f.height)) { if (!unityList) g_list->Close(); return false; }

    // descriptors: t1 depth, t2 colour in, t3 (colour in again), u0 the mask, u1 colour out
    Srv(f.depth, Cpu(g_slot, 0)); Srv(f.colorIn, Cpu(g_slot, 1)); Srv(f.colorIn, Cpu(g_slot, 2));
    Uav(g_mask, Cpu(g_slot, 3)); Uav(f.colorOut, Cpu(g_slot, 4)); Uav(g_ao, Cpu(g_slot, 5)); Uav(g_ao, Cpu(g_slot, 6)); Uav(g_ao, Cpu(g_slot, 7));

    if (g_queries) g_list->EndQuery(g_queries, D3D12_QUERY_TYPE_TIMESTAMP, g_slot * 4 + 0);
    uint32_t instances = 0;
    bool ok = f.stage < 3 || (BuildBottomLevel(f) && BuildTopLevel(f, s, instances));
    if (g_queries) g_list->EndQuery(g_queries, D3D12_QUERY_TYPE_TIMESTAMP, g_slot * 4 + 1);
    if (ok && f.stage >= 4)
    {
        Constants c = {};
        memcpy(c.invViewProj, f.invViewProj, sizeof c.invViewProj); memcpy(c.cameraPos, f.cameraPos, 16); memcpy(c.sunDir, f.sunDir, 16);
        c.size[0] = (float)f.width; c.size[1] = (float)f.height; c.size[2] = 1.0f / f.width; c.size[3] = 1.0f / f.height;
        c.fadeStart = f.fadeStart; c.fadeEnd = f.fadeEnd; c.strength = f.strength; c.maxDistance = f.maxDistance; c.mode = f.mode; c.frame = f.frame;
        c.aoRays = f.aoRays; c.aoRadius = f.aoRadius; c.aoStrength = f.aoStrength; c.aoDistance = f.aoDistance;
        // what this frame traces: shadows while the sun is up and a shadow mode is on, ambient occlusion when wanted
        bool shadows = (f.effects & 1) && f.sunDir[3] > 0 && f.mode >= 1 && f.mode <= 3;
        bool ao = (f.effects & 2) != 0;
        c.effects = instances ? (shadows ? 1 : 0) | (ao ? 2 : 0) : 0;
        ID3D12DescriptorHeap* heaps[1] = { g_heap };
        g_list->SetDescriptorHeaps(1, heaps);
        g_list->SetComputeRootSignature(g_rootSig);
        g_list->SetComputeRootShaderResourceView(0, g_tlas->GetGPUVirtualAddress());
        g_list->SetComputeRootDescriptorTable(1, Gpu(g_slot));
        g_list->SetComputeRoot32BitConstants(2, 44, &c, 0);
        if (c.effects)
        {
            g_list->SetPipelineState1(g_rtpso);
            D3D12_DISPATCH_RAYS_DESC d = {};
            UINT64 base = g_shaderTable->GetGPUVirtualAddress();
            d.RayGenerationShaderRecord = { base, D3D12_RAYTRACING_SHADER_TABLE_BYTE_ALIGNMENT };
            d.MissShaderTable = { base + D3D12_RAYTRACING_SHADER_TABLE_BYTE_ALIGNMENT, D3D12_RAYTRACING_SHADER_TABLE_BYTE_ALIGNMENT, D3D12_RAYTRACING_SHADER_TABLE_BYTE_ALIGNMENT };
            d.HitGroupTable = { base + 2 * D3D12_RAYTRACING_SHADER_TABLE_BYTE_ALIGNMENT, D3D12_RAYTRACING_SHADER_TABLE_BYTE_ALIGNMENT, D3D12_RAYTRACING_SHADER_TABLE_BYTE_ALIGNMENT };
            d.Width = f.stage == 41 ? 1 : f.width; d.Height = f.stage == 41 ? 1 : f.height; d.Depth = 1;
            if (f.stage != 40) { g_list->DispatchRays(&d); g_dispatches++; }
        }

        UavBarrier(g_mask);
        if (g_queries) g_list->EndQuery(g_queries, D3D12_QUERY_TYPE_TIMESTAMP, g_slot * 4 + 2);
        if (f.stage == 5 || f.stage == 51)
        {
            g_list->SetComputeRoot32BitConstants(2, 44, &c, 0);
            g_list->SetPipelineState(g_compositePso);
            g_list->Dispatch((f.width + 7) / 8, (f.height + 7) / 8, 1);
        }
    }
    UavBarrier(nullptr);   // our writes (the output texture) before Unity copies it back
    if (g_queries && ok && f.stage >= 4)
    {
        g_list->EndQuery(g_queries, D3D12_QUERY_TYPE_TIMESTAMP, g_slot * 4 + 3);
        g_list->ResolveQueryData(g_queries, D3D12_QUERY_TYPE_TIMESTAMP, g_slot * 4, 4, g_queryReadback, (UINT64)g_slot * 32);
        g_slotTimed[g_slot] = true;
    }
    if (unityList)
        s.fence = g_d3d12->GetNextFrameFenceValue();   // this slot's resources are free once the GPU has finished this frame
    else
    {
        if (FAILED(g_list->Close())) { Error("command list close"); g_list = nullptr; return false; }
        s.states[0] = { f.depth, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE };
        s.states[1] = { f.colorIn, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE };
        s.states[2] = { f.colorOut, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS };
        s.fence = g_d3d12->ExecuteCommandList(g_list, ok ? 3 : 0, s.states);
    }
    g_list = nullptr;
    g_slot = (g_slot + 1) % kFrames;
    return ok;
}

// ---------------------------------------------------------------- Unity's calls

static void __stdcall OnDeviceEvent(UnityGfxDeviceEventType type)
{
    if (type == kUnityGfxDeviceEventInitialize) Connect();
    if (type == kUnityGfxDeviceEventShutdown && g_device) { g_device->Release(); g_device = nullptr; g_ready = false; }
}

static void __stdcall OnRenderEvent(int eventId, void* data)
{
    g_events++;
    Connect();
    if ((eventId == 1 || eventId == 2) && data && g_device && ((const RtFrame*)data)->stage >= 2) Frame(*(const RtFrame*)data);
}

extern "C" {
__declspec(dllexport) void __stdcall UnityPluginLoad(IUnityInterfaces* unity)
{
    g_unity = unity;
    g_graphics = (IUnityGraphics*)unity->GetInterface(kGuidUnityGraphics);
    Status("loaded by Unity, renderer %d", g_graphics ? (int)g_graphics->GetRenderer() : -1);
    if (g_graphics) g_graphics->RegisterDeviceEventCallback(OnDeviceEvent);
    OnDeviceEvent(kUnityGfxDeviceEventInitialize);   // the device can exist already when the plugin loads late
}

__declspec(dllexport) void __stdcall UnityPluginUnload()
{
    if (g_graphics) g_graphics->UnregisterDeviceEventCallback(OnDeviceEvent);
}

__declspec(dllexport) UnityRenderingEventAndData __stdcall RT_RenderEventFunc() { return OnRenderEvent; }

/// The compiled shaders (DXIL): the ray tracing library and the composite compute shader. Before the first frame.
__declspec(dllexport) void __stdcall RT_SetShaders(const void* rtLib, int rtLength, const void* composite, int compositeLength)
{
    g_rtLib.assign((const char*)rtLib, (const char*)rtLib + rtLength);
    g_composite.assign((const char*)composite, (const char*)composite + compositeLength);
}

__declspec(dllexport) int __stdcall RT_Status(char* buffer, int size)
{
    snprintf(buffer, size, "%s; events %d, frames traced %d, structures %d (built %d), instances %d, skipped %d; GPU ms: structures %.2f, rays %.2f, composite %.2f%s%s",
             g_status, g_events, g_dispatches, g_blasLive, g_blasBuilt, g_instances, g_skipped, g_gpuBuild, g_gpuRays, g_gpuComposite, g_error[0] ? "; error: " : "", g_error);
    return g_ready ? 2 : g_device ? 1 : 0;
}
}
