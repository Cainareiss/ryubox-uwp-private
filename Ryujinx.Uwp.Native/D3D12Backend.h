#pragma once

#include <windows.h>
#include <stdint.h>

// Versioned, instance-owned ABI. A context must not be destroyed concurrently
// with a call. Resource IDs belong to that context and are never COM pointers.
// This is native D3D12: it does not load D3D11On12 or Mesa/Dozen.
#define RYUBOX_D12 extern "C" __declspec(dllexport) HRESULT __stdcall

#include "D3D12Program.h"

struct RyuboxD3D12Info
{
    uint32_t size;
    uint32_t abiVersion;
    uint32_t vendorId;
    uint32_t deviceId;
    uint32_t featureLevel;
    uint32_t resourceBindingTier;
    uint32_t width;
    uint32_t height;
    uint64_t submitted;
    uint64_t completed;
    uint64_t liveResourceBytes;
    uint64_t uploadBytes;
    uint64_t cpuFenceWaits;
    int32_t deviceRemovedReason;
    uint32_t debugErrors;
};

// flags: 1 = require debug layer, 2 = explicitly use WARP for diagnostics.
// Exactly one of window/panel, or neither (offscreen), may be supplied.
RYUBOX_D12 RyuboxD3D12_Create(HWND window, IUnknown* panel, uint32_t width, uint32_t height,
    uint32_t flags, void** context);
RYUBOX_D12 RyuboxD3D12_Destroy(void* context);
RYUBOX_D12 RyuboxD3D12_GetInfo(void* context, RyuboxD3D12Info* info);
// Nine uint64s: reserved default-heap bytes, heaps, placed creations, committed
// creations, heap creations, resource budget, program FXC calls, graphics PSO
// creations, live logical resource bytes. Upload/readback are not included.
RYUBOX_D12 RyuboxD3D12_GetResourceStats(void* context, uint64_t* data, uint32_t count);
// Releases upload pages of completed frames and empty heap chunks (memory pressure).
RYUBOX_D12 RyuboxD3D12_TrimIdleMemory(void* context, uint64_t* releasedBytes);
// Versioned additive diagnostics; does not change GetInfo's ABI.
struct RyuboxD3D12DescriptorStats
{
    uint32_t size, version;
    uint64_t ringEnabled, viewCapacity, samplerCapacity;
    uint64_t viewAllocations, samplerAllocations;
    uint64_t viewPeakPerSubmission, samplerPeakPerSubmission;
    uint64_t viewOccupied, samplerOccupied, viewPeakOccupied, samplerPeakOccupied;
    uint64_t viewPages, samplerPages, pageCreations, heapSwitches, wraps, reclaimed;
    uint64_t pressureEvents, allocationFailures, ringWaits, ringWaitMicroseconds;
    uint64_t cpuFenceWaits, cpuFenceWaitMicroseconds, maxFenceWaitMicroseconds;
    uint64_t estimatedHeapBytes;
};
RYUBOX_D12 RyuboxD3D12_GetDescriptorStats(void* context, RyuboxD3D12DescriptorStats* stats);
// Version 3 timing, render-pass, descriptor-table and swap-chain Present counters. Values are cumulative for the
// context and are collected without flushing or waiting for the GPU queue.
struct RyuboxD3D12PerformanceStats
{
    uint32_t size, version;
    uint64_t drawCalls, renderPasses, shaderCacheHits, shaderCacheMisses;
    uint64_t shaderCompileMicroseconds, psoCacheHits, psoCreations;
    uint64_t psoMicroseconds, descriptorBindMicroseconds, recordMicroseconds;
    uint64_t submitMicroseconds, presentMicroseconds, fenceWaitMicroseconds;
    uint64_t depthAttachmentsDetached, invalidRenderPasses, uavBarriers;
    uint64_t descriptorTableCacheHits, descriptorTableCacheMisses, descriptorCopies;
    // Counts DXGI Present calls for a real swap chain. A successful HRESULT
    // does not prove that photons reached a physical display.
    uint64_t presentAttempts, presentSuccess;
    uint64_t lastPresentFence;
    uint64_t lastPresentQpc;
    int32_t lastPresentResult;
    uint32_t lastPresentWidth, lastPresentHeight;
    uint32_t presentationTarget; // 0=offscreen, 1=HWND, 2=composition panel
    uint32_t reserved;
};
RYUBOX_D12 RyuboxD3D12_GetPerformanceStats(void* context, RyuboxD3D12PerformanceStats* stats);
RYUBOX_D12 RyuboxD3D12_BeginFrame(void* context, float r, float g, float b, float a);
RYUBOX_D12 RyuboxD3D12_EndFrame(void* context, uint32_t syncInterval, uint64_t* fence);
RYUBOX_D12 RyuboxD3D12_Wait(void* context, uint64_t fence);
RYUBOX_D12 RyuboxD3D12_Resize(void* context, uint32_t width, uint32_t height);
RYUBOX_D12 RyuboxD3D12_SetCompositionScale(void* context, float x, float y);

RYUBOX_D12 RyuboxD3D12_CreateBuffer(void* context, uint64_t bytes, uint64_t* resource);
RYUBOX_D12 RyuboxD3D12_RetainBuffer(void* context, uint64_t source, uint64_t* resource);
RYUBOX_D12 RyuboxD3D12_CreateTextureRgba8(void* context, uint32_t width, uint32_t height, uint64_t* resource);
// Typed 2D color textures. Format and sample support are checked on the device;
// unsupported formats/samples fail before resource creation.
RYUBOX_D12 RyuboxD3D12_CreateColorTexture(void* context, uint32_t width, uint32_t height,
    uint32_t dxgiFormat, uint32_t samples, uint64_t* resource);
RYUBOX_D12 RyuboxD3D12_CreateColorTextureArray(void* context, uint32_t width, uint32_t height,
    uint32_t layers, uint32_t mipLevels, uint32_t dxgiFormat, uint32_t samples, uint64_t* resource);
RYUBOX_D12 RyuboxD3D12_CreateColorTexture3D(void* context, uint32_t width, uint32_t height,
    uint32_t depth, uint32_t mipLevels, uint32_t dxgiFormat, uint64_t* resource);
RYUBOX_D12 RyuboxD3D12_CreateTextureView(void* context, uint64_t source, uint32_t firstMip,
    uint32_t mipCount, uint32_t firstSlice, uint32_t sliceCount, uint64_t* resource);
RYUBOX_D12 RyuboxD3D12_CreateTextureViewWithFormat(void* context, uint64_t source, uint32_t firstMip,
    uint32_t mipCount, uint32_t firstSlice, uint32_t sliceCount, uint32_t dxgiFormat, uint64_t* resource);
RYUBOX_D12 RyuboxD3D12_ReleaseResource(void* context, uint64_t resource);
// Initial compute layout: ByteAddressBuffer t0, RWByteAddressBuffer u0,
// and up to 16 uint root constants b0. HLSL entry point is main (SM5.1).
RYUBOX_D12 RyuboxD3D12_CreateComputePipeline(void* context, const char* hlsl, uint64_t bytes, uint64_t* pipeline);
RYUBOX_D12 RyuboxD3D12_ReleaseComputePipeline(void* context, uint64_t pipeline);
RYUBOX_D12 RyuboxD3D12_Dispatch(void* context, uint64_t pipeline, uint64_t source, uint64_t destination,
    const uint32_t* constants, uint32_t count, uint32_t groupsX, uint32_t groupsY, uint32_t groupsZ);
// Upload/copy/draw operations require an open frame. CPU data is copied before
// return. Resources/descriptors/upload pages remain alive until the fence.
RYUBOX_D12 RyuboxD3D12_UploadBuffer(void* context, uint64_t resource, uint64_t offset,
    const void* data, uint64_t bytes);
RYUBOX_D12 RyuboxD3D12_CopyBuffer(void* context, uint64_t source, uint64_t destination,
    uint64_t sourceOffset, uint64_t destinationOffset, uint64_t bytes);
RYUBOX_D12 RyuboxD3D12_UploadTextureRgba8(void* context, uint64_t resource,
    const void* data, uint64_t bytes, uint32_t rowPitch);
RYUBOX_D12 RyuboxD3D12_UploadTextureRegion(void* context, uint64_t resource, uint32_t mip,
    uint32_t x, uint32_t y, uint32_t z, uint32_t width, uint32_t height, uint32_t depth,
    const void* data, uint64_t bytes, uint32_t rowPitch, uint32_t depthPitch);
// Normalized crop; output fills the current surface. Flip applies exactly once.
RYUBOX_D12 RyuboxD3D12_DrawTexture(void* context, uint64_t resource,
    float left, float top, float right, float bottom, uint32_t flipY);

// Explicit synchronous diagnostics, never used by the normal present path.
// Require a closed frame. Output is tightly packed, top-left first, RGBA8.
RYUBOX_D12 RyuboxD3D12_ReadBuffer(void* context, uint64_t resource, uint64_t offset,
    void* destination, uint64_t bytes);
// Reads the last submitted contents without disturbing an open graphics list.
RYUBOX_D12 RyuboxD3D12_ReadSubmittedBuffer(void* context, uint64_t resource, uint64_t offset,
    void* destination, uint64_t bytes);
RYUBOX_D12 RyuboxD3D12_Capture(void* context, void* destination, uint64_t bytes);
RYUBOX_D12 RyuboxD3D12_GetLastDiagnostic(void* context, char* data, uint32_t capacity);
