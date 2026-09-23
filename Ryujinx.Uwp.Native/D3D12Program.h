#pragma once
#include <d3d12.h>
#include <stdint.h>

#ifndef RYUBOX_D12
#define RYUBOX_D12 extern "C" __declspec(dllexport) HRESULT __stdcall
#endif

// Stages follow GAL: compute, vertex, hull, domain, geometry, pixel.
// Sources are UTF-8 HLSL, entry point main. Caller owns memory during the call.
struct RyuboxD12Shader { uint32_t stage; const char* source; uint64_t bytes; };
struct RyuboxD12BindingInfo
{
    uint32_t stage, type, space, binding, count, dimension;
};
struct RyuboxD12VertexInput { char semantic[256]; uint32_t index, componentType, mask; };
// type uses D3D12_DESCRIPTOR_RANGE_TYPE (SRV=0, UAV=1, CBV=2, sampler=3).
struct RyuboxD12Binding
{
    uint32_t stage, type, space, binding;
    uint64_t resource, offset, bytes;
    uint32_t format, stride, componentMapping;
    D3D12_SAMPLER_DESC sampler;
};
struct RyuboxD12VertexBuffer { uint64_t resource, offset; uint32_t bytes, stride; };
struct RyuboxD12SOElement
{
    char semantic[256];
    uint32_t index, component, components, buffer, stream, offset, stride;
};
struct RyuboxD12SOBuffer { uint64_t resource, offset, bytes, counter; };
struct RyuboxD12DepthState
{
    uint64_t target;
    uint32_t enable, write, comparison, noColorTarget;
    uint32_t stencilEnable;
    uint32_t frontFail, frontPass, frontDepthFail, frontFunc, frontRef, frontReadMask, frontWriteMask;
    uint32_t backFail, backPass, backDepthFail, backFunc, backRef, backReadMask, backWriteMask;
};
struct RyuboxD12Draw
{
    uint64_t colorTarget; // Zero selects the current presentation surface.
    uint32_t colorTargetCount; // Zero only for a depth-only draw.
    // Bit N identifies a real attachment at colorTargets[N]. A zero mask with
    // one target means the implicit presentation surface.
    uint32_t colorTargetMask;
    uint64_t colorTargets[8];
    const D3D12_INPUT_ELEMENT_DESC* attributes;
    const RyuboxD12VertexBuffer* vertices;
    uint32_t attributeCount, vertexCount;
    uint64_t indexResource, indexOffset;
    uint32_t indexBytes, indexFormat;
    uint32_t topology, count, instances, first, firstInstance;
    int32_t baseVertex;
    D3D12_VIEWPORT viewport;
    D3D12_RECT scissor;
    D3D12_RASTERIZER_DESC rasterizer;
    D3D12_BLEND_DESC blend;
    // Optional ExecuteIndirect argument/count buffers. The command is draw or
    // draw-indexed according to indexResource. Zero countResource: fixed count.
    uint64_t indirectResource, indirectOffset, countResource, countOffset;
    uint32_t maxCommands, indirectStride;
    float blendConstant[4]; // Dynamic OM state; deliberately excluded from the PSO key.
    uint32_t rasterizerDiscard; // bit 0: discard all; bit 1: cull final polygon primitives.
    uint32_t streamOutput;
    RyuboxD12SOBuffer streamBuffers[4];
};

RYUBOX_D12 RyuboxD3D12_CreateProgram(void*, const RyuboxD12Shader*, uint32_t, uint64_t*);
RYUBOX_D12 RyuboxD3D12_GetProgramError(void*, char*, uint32_t);
RYUBOX_D12 RyuboxD3D12_ReleaseProgram(void*, uint64_t);
RYUBOX_D12 RyuboxD3D12_GetProgramBindings(void*, uint64_t, RyuboxD12BindingInfo*, uint32_t*);
RYUBOX_D12 RyuboxD3D12_GetVertexInputs(void*, uint64_t, RyuboxD12VertexInput*, uint32_t*);
RYUBOX_D12 RyuboxD3D12_ConfigureStreamOutput(void*, uint64_t, uint32_t, const RyuboxD12SOElement*, uint32_t);
RYUBOX_D12 RyuboxD3D12_DispatchProgram(void*, uint64_t, const RyuboxD12Binding*, uint32_t,
    uint32_t, uint32_t, uint32_t);
RYUBOX_D12 RyuboxD3D12_DrawProgram(void*, uint64_t, const RyuboxD12Binding*, uint32_t, const RyuboxD12Draw*);
RYUBOX_D12 RyuboxD3D12_DrawProgramDepth(void*, uint64_t, const RyuboxD12Binding*, uint32_t, const RyuboxD12Draw*, const RyuboxD12DepthState*);
RYUBOX_D12 RyuboxD3D12_CreateDepthTexture(void*, uint32_t, uint32_t, uint32_t, uint32_t, uint64_t*);
RYUBOX_D12 RyuboxD3D12_CreateDepthTextureArray(void*, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint64_t*);
RYUBOX_D12 RyuboxD3D12_ClearDepthTexture(void*, uint64_t, float);
RYUBOX_D12 RyuboxD3D12_BeginCommands(void*);
RYUBOX_D12 RyuboxD3D12_Flush(void*, uint64_t*);
RYUBOX_D12 RyuboxD3D12_MemoryBarrier(void*);
RYUBOX_D12 RyuboxD3D12_ClearTexture(void*, uint64_t, const float*);
RYUBOX_D12 RyuboxD3D12_CopyTexture(void*, uint64_t, uint64_t, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t);
RYUBOX_D12 RyuboxD3D12_ReadTexture(void*, uint64_t, void*, uint64_t);
RYUBOX_D12 RyuboxD3D12_CopyTextureRegion(void*, uint64_t, uint64_t, uint32_t, uint32_t,
    uint32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t);
RYUBOX_D12 RyuboxD3D12_ReadTextureSubresource(void*, uint64_t, uint32_t, void*, uint64_t, uint32_t, uint32_t);
RYUBOX_D12 RyuboxD3D12_BeginCounter(void*, uint32_t, uint64_t*);
RYUBOX_D12 RyuboxD3D12_EndCounter(void*, uint64_t);
RYUBOX_D12 RyuboxD3D12_ReadCounter(void*, uint64_t, uint64_t*);
RYUBOX_D12 RyuboxD3D12_ReleaseCounter(void*, uint64_t);

RYUBOX_D12 RyuboxD3D12_TranslateSpirvToDxil(const uint32_t* words, size_t wordCount, uint32_t stage,
    const char* entryPoint, void** outData, uint64_t* outBytes, void** outHandle);
RYUBOX_D12 RyuboxD3D12_FreeDxil(void* handle);
RYUBOX_D12 RyuboxD3D12_GetProgramBytecode(void* context, uint64_t id, uint32_t stage, void* data, uint64_t* bytes);
// Experimental Mesa ABI has not been verified against the supplied Eden DLL.
RYUBOX_D12 RyuboxD3D12_GetDxilStatus();
