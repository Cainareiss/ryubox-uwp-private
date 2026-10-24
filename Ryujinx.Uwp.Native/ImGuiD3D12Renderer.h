#pragma once
#include "D3D12Backend.h"
#include <unordered_set>
struct ImDrawData;

namespace Ryujinx::Uwp::Native
{
// Borrows a D3D12 context. All calls belong to its command-recording owner.
// Texture IDs are native resource IDs, never D3D11 SRV pointers.
class ImGuiD3D12Renderer final
{
public:
    explicit ImGuiD3D12Renderer(void* context) : m_context(context) {}
    ~ImGuiD3D12Renderer();
    HRESULT Initialize(const char* vertexShader, const char* pixelShader);
    HRESULT CreateTexture(const void* pixels, UINT width, UINT height, UINT pitch, UINT64* id);
    void ReleaseTexture(UINT64 id) noexcept;
    HRESULT Render(const ImDrawData* data, UINT width, UINT height);
private:
    void* m_context;
    UINT64 m_program = 0;
    std::unordered_set<UINT64> m_textures;
};
}
