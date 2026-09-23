#include "ImGuiD3D12Renderer.h"
#include "D3D12FrontendHost.h"
#include "third_party/imgui/imgui.h"
#include <algorithm>
#include <cstring>
#include <vector>
#include <limits>

namespace Ryujinx::Uwp::Native
{
namespace
{
// Releasing IDs does not release queued objects: the context retains each
// referenced allocation until its submission fence has completed.
struct Buffers
{
    void* context;
    UINT64 vertex = 0, index = 0, projection = 0;
    ~Buffers()
    {
        for (UINT64 id : { vertex, index, projection })
            if (id) RyuboxD3D12_ReleaseResource(context, id);
    }
};
}
ImGuiD3D12Renderer::~ImGuiD3D12Renderer()
{
    for (auto id : m_textures) RyuboxD3D12_ReleaseResource(m_context, id);
    if (m_program) RyuboxD3D12_ReleaseProgram(m_context, m_program);
}

HRESULT ImGuiD3D12Renderer::Initialize(const char* vs, const char* ps)
{
    if (!m_context || !vs || !ps || m_program) return E_INVALIDARG;
    RyuboxD12Shader shaders[] = { {1, vs, std::strlen(vs)}, {5, ps, std::strlen(ps)} };
    return RyuboxD3D12_CreateProgram(m_context, shaders, 2, &m_program);
}

HRESULT ImGuiD3D12Renderer::CreateTexture(const void* pixels, UINT width, UINT height, UINT pitch, UINT64* id)
{
    if (!pixels || !id || !width || !height || width > UINT_MAX / 4 || pitch < width * 4) return E_INVALIDARG;
    *id = 0;
    UINT64 texture = 0;
    HRESULT hr = RyuboxD3D12_CreateTextureRgba8(m_context, width, height, &texture);
    if (FAILED(hr)) return hr;
    hr = RyuboxD3D12_BeginCommands(m_context);
    if (SUCCEEDED(hr)) hr = RyuboxD3D12_UploadTextureRgba8(m_context, texture, pixels, UINT64(pitch) * height, pitch);
    UINT64 fence = 0;
    if (SUCCEEDED(hr)) hr = RyuboxD3D12_Flush(m_context, &fence);
    if (FAILED(hr)) { RyuboxD3D12_ReleaseResource(m_context, texture); return hr; }
    try { m_textures.insert(texture); }
    catch (...) { RyuboxD3D12_ReleaseResource(m_context, texture); throw; }
    *id = texture;
    return S_OK;
}

void ImGuiD3D12Renderer::ReleaseTexture(UINT64 id) noexcept
{
    if (m_textures.erase(id)) RyuboxD3D12_ReleaseResource(m_context, id);
}

HRESULT ImGuiD3D12Renderer::Render(const ImDrawData* data, UINT width, UINT height)
{
    if (!m_program || !data || !width || !height) return E_INVALIDARG;
    if (data->TotalVtxCount == 0 || data->TotalIdxCount == 0) return S_OK;
    if (data->TotalVtxCount < 0 || data->TotalIdxCount < 0 || data->DisplaySize.x <= 0 || data->DisplaySize.y <= 0) return E_INVALIDARG;
    UINT64 vertexBytes = UINT64(data->TotalVtxCount) * sizeof(ImDrawVert);
    UINT64 indexBytes = UINT64(data->TotalIdxCount) * sizeof(ImDrawIdx);
    if (vertexBytes > UINT_MAX || indexBytes > UINT_MAX) return E_INVALIDARG;
    std::vector<ImDrawVert> vertices;
    std::vector<ImDrawIdx> indices;
    vertices.reserve(data->TotalVtxCount); indices.reserve(data->TotalIdxCount);
    // Validate all texture IDs and counts before recording any draw.
    for (int i = 0; i < data->CmdListsCount; ++i)
    {
        const auto* list = data->CmdLists[i];
        for (const auto& cmd : list->CmdBuffer)
        {
            if (cmd.UserCallback && cmd.UserCallback != ImDrawCallback_ResetRenderState) return E_NOTIMPL;
            if (!cmd.UserCallback && (!m_textures.count(reinterpret_cast<UINT64>(cmd.GetTexID())) ||
                UINT64(cmd.IdxOffset) + cmd.ElemCount > UINT64(list->IdxBuffer.Size) ||
                cmd.VtxOffset > UINT(list->VtxBuffer.Size))) return E_INVALIDARG;
        }
        vertices.insert(vertices.end(), list->VtxBuffer.begin(), list->VtxBuffer.end());
        indices.insert(indices.end(), list->IdxBuffer.begin(), list->IdxBuffer.end());
    }
    if (vertices.size() != size_t(data->TotalVtxCount) || indices.size() != size_t(data->TotalIdxCount)) return E_INVALIDARG;
    Buffers buffers{m_context};
    HRESULT hr = RyuboxD3D12_CreateBuffer(m_context, vertexBytes, &buffers.vertex);
    if (FAILED(hr)) return hr;
    hr = RyuboxD3D12_CreateBuffer(m_context, indexBytes, &buffers.index);
    if (FAILED(hr)) return hr;
    hr = RyuboxD3D12_CreateBuffer(m_context, 256, &buffers.projection);
    if (FAILED(hr)) return hr;
    hr = RyuboxD3D12_UploadBuffer(m_context, buffers.vertex, 0, vertices.data(), vertexBytes);
    if (FAILED(hr)) return hr;
    hr = RyuboxD3D12_UploadBuffer(m_context, buffers.index, 0, indices.data(), indexBytes);
    if (FAILED(hr)) return hr;
    float l = data->DisplayPos.x, r = l + data->DisplaySize.x;
    float t = data->DisplayPos.y, b = t + data->DisplaySize.y;
    float matrix[4][4] = {{2/(r-l),0,0,0},{0,2/(t-b),0,0},{0,0,0.5f,0},{(r+l)/(l-r),(t+b)/(b-t),0.5f,1}};
    hr = RyuboxD3D12_UploadBuffer(m_context, buffers.projection, 0, matrix, sizeof(matrix));
    if (FAILED(hr)) return hr;
    D3D12_INPUT_ELEMENT_DESC attributes[] = {
        {"POSITION",0,DXGI_FORMAT_R32G32_FLOAT,0,offsetof(ImDrawVert,pos),D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA,0},
        {"COLOR",0,DXGI_FORMAT_R8G8B8A8_UNORM,0,offsetof(ImDrawVert,col),D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA,0},
        {"TEXCOORD",0,DXGI_FORMAT_R32G32_FLOAT,0,offsetof(ImDrawVert,uv),D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA,0}};
    RyuboxD12VertexBuffer vertex{buffers.vertex,0,UINT(vertexBytes),sizeof(ImDrawVert)};
    RyuboxD12Draw draw{};
    draw.colorTargetCount = 1;
    draw.attributes = attributes; draw.attributeCount = 3;
    draw.vertices = &vertex; draw.vertexCount = 1;
    draw.indexResource = buffers.index; draw.indexBytes = UINT(indexBytes);
    draw.indexFormat = sizeof(ImDrawIdx)==2 ? DXGI_FORMAT_R16_UINT : DXGI_FORMAT_R32_UINT;
    draw.topology = D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST; draw.instances = 1;
    draw.viewport = {0,0,float(width),float(height),0,1};
    draw.rasterizer.FillMode = D3D12_FILL_MODE_SOLID; draw.rasterizer.CullMode = D3D12_CULL_MODE_NONE;
    draw.rasterizer.DepthClipEnable = TRUE;
    auto& blend = draw.blend.RenderTarget[0];
    blend.BlendEnable = TRUE; blend.SrcBlend = D3D12_BLEND_SRC_ALPHA; blend.DestBlend = D3D12_BLEND_INV_SRC_ALPHA;
    blend.BlendOp = blend.BlendOpAlpha = D3D12_BLEND_OP_ADD;
    blend.SrcBlendAlpha = D3D12_BLEND_ONE; blend.DestBlendAlpha = D3D12_BLEND_INV_SRC_ALPHA;
    blend.LogicOp = D3D12_LOGIC_OP_NOOP; blend.RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
    RyuboxD12Binding bindings[3]{};
    bindings[0].stage=1; bindings[0].type=D3D12_DESCRIPTOR_RANGE_TYPE_CBV;
    bindings[0].resource=buffers.projection; bindings[0].bytes=256;
    bindings[1].stage=5; bindings[1].type=D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
    bindings[2].stage=5; bindings[2].type=D3D12_DESCRIPTOR_RANGE_TYPE_SAMPLER;
    auto& sampler=bindings[2].sampler;
    sampler.Filter=D3D12_FILTER_MIN_MAG_MIP_LINEAR;
    sampler.AddressU=sampler.AddressV=sampler.AddressW=D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
    sampler.MaxAnisotropy=1; sampler.ComparisonFunc=D3D12_COMPARISON_FUNC_ALWAYS; sampler.MaxLOD=D3D12_FLOAT32_MAX;
    UINT indexOffset=0, vertexOffset=0;
    for (int i=0;i<data->CmdListsCount;++i)
    {
        const auto* list=data->CmdLists[i];
        for (const auto& cmd:list->CmdBuffer)
        {
            // Each draw below rebinds all state; reset markers need no command.
            if (cmd.UserCallback==ImDrawCallback_ResetRenderState) continue;
            draw.scissor={LONG(std::max(0.f,(cmd.ClipRect.x-l)*data->FramebufferScale.x)),
                LONG(std::max(0.f,(cmd.ClipRect.y-t)*data->FramebufferScale.y)),
                LONG(std::min(float(width),(cmd.ClipRect.z-l)*data->FramebufferScale.x)),
                LONG(std::min(float(height),(cmd.ClipRect.w-t)*data->FramebufferScale.y))};
            if (draw.scissor.right<=draw.scissor.left || draw.scissor.bottom<=draw.scissor.top || !cmd.ElemCount) continue;
            // Also accounts for descriptors consumed by guest draws before
            // the FPS overlay. Recycle only through the normal fence path.
            hr=PrepareD3D12UiDraw(m_context); if (FAILED(hr)) return hr;
            bindings[1].resource=reinterpret_cast<UINT64>(cmd.GetTexID());
            draw.count=cmd.ElemCount; draw.first=indexOffset+cmd.IdxOffset;
            draw.baseVertex=INT(vertexOffset+cmd.VtxOffset);
            hr=RyuboxD3D12_DrawProgram(m_context,m_program,bindings,3,&draw);
            if (FAILED(hr)) return hr;
        }
        indexOffset+=list->IdxBuffer.Size; vertexOffset+=list->VtxBuffer.Size;
    }
    return S_OK;
}
}
