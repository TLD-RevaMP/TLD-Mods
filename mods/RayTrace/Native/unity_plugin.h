// The few pieces of Unity's native plugin interface this plugin uses, declared by hand (the layouts and GUIDs Unity
// 2022.3 hands a native plugin; Unity's own headers are not part of this repository).
#pragma once
#include <stdint.h>
#include <d3d12.h>

struct UnityInterfaceGUID { uint64_t high, low; };
struct IUnityInterface {};

struct IUnityInterfaces
{
    IUnityInterface* (__stdcall* GetInterface)(UnityInterfaceGUID guid);
    void (__stdcall* RegisterInterface)(UnityInterfaceGUID guid, IUnityInterface* ptr);
    IUnityInterface* (__stdcall* GetInterfaceSplit)(uint64_t high, uint64_t low);
    void (__stdcall* RegisterInterfaceSplit)(uint64_t high, uint64_t low, IUnityInterface* ptr);
};

enum UnityGfxRenderer { kUnityGfxRendererD3D11 = 2, kUnityGfxRendererD3D12 = 18 };
enum UnityGfxDeviceEventType { kUnityGfxDeviceEventInitialize = 0, kUnityGfxDeviceEventShutdown = 1 };
typedef void (__stdcall* IUnityGraphicsDeviceEventCallback)(UnityGfxDeviceEventType eventType);

struct IUnityGraphics : IUnityInterface
{
    UnityGfxRenderer (__stdcall* GetRenderer)();
    void (__stdcall* RegisterDeviceEventCallback)(IUnityGraphicsDeviceEventCallback callback);
    void (__stdcall* UnregisterDeviceEventCallback)(IUnityGraphicsDeviceEventCallback callback);
    int (__stdcall* ReserveEventIDRange)(int count);
};
static const UnityInterfaceGUID kGuidUnityGraphics = { 0x7CBA0A9CA4DDB544ULL, 0x8C5AD4926EB17B11ULL };

struct UnityGraphicsD3D12ResourceState { ID3D12Resource* resource; D3D12_RESOURCE_STATES expected, current; };
struct UnityGraphicsD3D12RecordingState { ID3D12GraphicsCommandList* commandList; };
struct UnityD3D12PluginEventConfig { int graphicsQueueAccess; uint32_t flags; bool ensureActiveRenderTextureIsBound; };
struct UnityGraphicsD3D12PhysicalVideoMemoryControlValues { uint64_t reservation, systemMemoryThreshold, residencyHysteresisThreshold; float nonEvictableRelativeThreshold; };

// IUnityGraphicsD3D12v7 (render/submission thread only)
struct IUnityGraphicsD3D12v7 : IUnityInterface
{
    ID3D12Device* (__stdcall* GetDevice)();
    void* (__stdcall* GetSwapChain)();
    uint32_t (__stdcall* GetSyncInterval)();
    UINT (__stdcall* GetPresentFlags)();
    ID3D12Fence* (__stdcall* GetFrameFence)();
    uint64_t (__stdcall* GetNextFrameFenceValue)();
    uint64_t (__stdcall* ExecuteCommandList)(ID3D12GraphicsCommandList* list, int stateCount, UnityGraphicsD3D12ResourceState* states);
    void (__stdcall* SetPhysicalVideoMemoryControlValues)(const UnityGraphicsD3D12PhysicalVideoMemoryControlValues* values);
    ID3D12CommandQueue* (__stdcall* GetCommandQueue)();
    ID3D12Resource* (__stdcall* TextureFromRenderBuffer)(void* renderBuffer);
    ID3D12Resource* (__stdcall* TextureFromNativeTexture)(void* texture);
    void (__stdcall* ConfigureEvent)(int eventId, const UnityD3D12PluginEventConfig* config);
    bool (__stdcall* CommandRecordingState)(UnityGraphicsD3D12RecordingState* out);
};
static const UnityInterfaceGUID kGuidUnityGraphicsD3D12v7 = { 0x4624B0DA41B64AACULL, 0x915AABCB9BC3F0D3ULL };

typedef void (__stdcall* UnityRenderingEventAndData)(int eventId, void* data);
