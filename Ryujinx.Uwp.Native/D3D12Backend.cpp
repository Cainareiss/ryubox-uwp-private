#include "D3D12Backend.h"
#include "D3D12FrontendHost.h"
#include "D3D12DescriptorRing.h"
#include "spirv_to_dxil.h"
#include <d3d12.h>
#include <d3d12sdklayers.h>
#include <d3dcompiler.h>
#include <d3d12shader.h>
#include <dxgi1_4.h>
#include <fileapi.h>
#include <windows.ui.xaml.media.dxinterop.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <map>
#include <new>
#include <cwchar>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace
{
using Microsoft::WRL::ComPtr;
constexpr UINT FrameCount = 3;
constexpr UINT DescriptorsPerFrame = 16384;
constexpr UINT SamplersPerFrame = 512;
constexpr UINT ViewRingCapacity = FrameCount * DescriptorsPerFrame;
constexpr UINT SamplerRingCapacity = FrameCount * SamplersPerFrame;
static_assert(FrameCount * SamplersPerFrame <= D3D12_MAX_SHADER_VISIBLE_SAMPLER_HEAP_SIZE);
constexpr UINT64 MiB = 1024 * 1024;
constexpr UINT64 MinimumResourceBudget = 512 * MiB;
constexpr UINT64 DefaultResourceBudget = 1536 * MiB;
constexpr UINT64 MaximumResourceBudget = 4096 * MiB;
constexpr DWORD FenceTimeoutMs = 5000;

UINT64 ElapsedMicroseconds(const LARGE_INTEGER& start, const LARGE_INTEGER& end)
{
    static const UINT64 frequency = []
    {
        LARGE_INTEGER value{};
        QueryPerformanceFrequency(&value);
        return static_cast<UINT64>(value.QuadPart);
    }();

    return frequency ? static_cast<UINT64>((end.QuadPart - start.QuadPart) * 1000000ull / frequency) : 0;
}

UINT64 ResourceBudgetLimit()
{
    // The old 512 MiB ceiling was deliberately conservative for the early GAL
    // smoke test, but it prevents real Switch texture caches from reaching a
    // title screen. Keep a bounded budget instead of silently allowing
    // unbounded committed resources. A host can lower it for constrained
    // hardware through RYUJINX_UWP_D3D12_RESOURCE_BUDGET_MIB.
    static const UINT64 budget = []
    {
        wchar_t text[32]{};
        DWORD length = GetEnvironmentVariableW(L"RYUJINX_UWP_D3D12_RESOURCE_BUDGET_MIB", text, ARRAYSIZE(text));
        if (!length || length >= ARRAYSIZE(text)) return DefaultResourceBudget;

        wchar_t* end = nullptr;
        UINT64 requested = _wcstoui64(text, &end, 10);
        if (end == text || *end != L'\0') return DefaultResourceBudget;
        if (requested > UINT64_MAX / MiB) return MaximumResourceBudget;
        return std::clamp(requested * MiB, MinimumResourceBudget, MaximumResourceBudget);
    }();

    return budget;
}

UINT64 UploadBudgetLimit()
{
    // Upload pages remain cached until their frame fence completes. A bounded
    // budget proportional to the resource budget avoids both permanent 256 MiB
    // exhaustion and unbounded staging growth during texture-heavy scenes.
    static const UINT64 budget = [] {
        UINT64 limit = std::clamp(ResourceBudgetLimit() / 2, 256 * MiB, 1024 * MiB);
        wchar_t text[32]{};
        DWORD length = GetEnvironmentVariableW(L"RYUJINX_UWP_D3D12_UPLOAD_BUDGET_MIB", text, ARRAYSIZE(text));
        if (!length || length >= ARRAYSIZE(text)) return limit;
        wchar_t* end = nullptr;
        UINT64 requested = _wcstoui64(text, &end, 10);
        if (end == text || *end != L'\0' || requested > limit / MiB) return limit;
        return std::clamp(requested * MiB, 4 * MiB, limit);
    }();
    return budget;
}

// Diagnostic breadcrumb trail for a vendor UMD access violation. The Intel
// driver used by the local test can crash the caller instead of returning a
// device-removed HRESULT, so a bounded on-disk trace is the only way to learn
// which valid-looking D3D12 call immediately preceded the fault. It is fully
// opt-in and intentionally capped to avoid affecting normal rendering.
std::atomic<UINT> g_traceLines{0};
void TraceD3D12(const char* phase)
{
    if (g_traceLines.fetch_add(1, std::memory_order_relaxed) >= 128) return;
    wchar_t path[MAX_PATH]{};
    DWORD count = GetEnvironmentVariableW(L"RYUJINX_UWP_D3D12_TRACE_PATH", path, ARRAYSIZE(path));
    if (!count || count >= ARRAYSIZE(path)) return;
    CREATEFILE2_EXTENDED_PARAMETERS parameters{};
    parameters.dwSize = sizeof(parameters);
    parameters.dwFileAttributes = FILE_ATTRIBUTE_NORMAL;
    HANDLE file = CreateFile2(path, FILE_APPEND_DATA, FILE_SHARE_READ, OPEN_ALWAYS, &parameters);
    if (file == INVALID_HANDLE_VALUE) return;
    char line[192]{};
    int length = _snprintf_s(line, _TRUNCATE, "%llu %s\r\n",
        static_cast<unsigned long long>(GetTickCount64()), phase);
    if (length > 0)
    {
        DWORD written = 0;
        WriteFile(file, line, static_cast<DWORD>(length), &written, nullptr);
    }
    CloseHandle(file);
}

struct Failure
{
    HRESULT result;
    const char* operation;
    const char* source;
    int line;
    std::string detail;
};

inline void CheckWithLocation(const char* source, int line, HRESULT result)
{
    if (FAILED(result)) throw Failure{result, "D3D12 call", source, line, ""};
}

inline void CheckWithLocation(const char* source, int line, HRESULT result, const char* detail)
{
    if (FAILED(result)) throw Failure{result, "D3D12 call", source, line, detail ? detail : ""};
}

inline void CheckWithLocation(const char* source, int line, HRESULT result, const std::string& detail)
{
    if (FAILED(result)) throw Failure{result, "D3D12 call", source, line, detail};
}

inline void RequireWithLocation(const char* source, int line, bool condition)
{
    if (!condition) throw Failure{E_INVALIDARG, "D3D12 validation", source, line, ""};
}

inline void RequireWithLocation(const char* source, int line, bool condition, HRESULT result)
{
    if (!condition) throw Failure{result, "D3D12 validation", source, line, ""};
}

inline void RequireWithLocation(const char* source, int line, bool condition, HRESULT result, const char* detail)
{
    if (!condition) throw Failure{result, "D3D12 validation", source, line, detail ? detail : ""};
}

inline void RequireWithLocation(const char* source, int line, bool condition, HRESULT result, const std::string& detail)
{
    if (!condition) throw Failure{result, "D3D12 validation", source, line, detail};
}

// Preserve the concise call sites while retaining the exact native operation
// that produced an HRESULT. This is diagnostic-only metadata; no validation
// becomes a fallback or a successful no-op.
#define Check(...) CheckWithLocation(__FILE__, __LINE__, __VA_ARGS__)
#define Require(...) RequireWithLocation(__FILE__, __LINE__, __VA_ARGS__)
bool Fits(UINT64 offset, UINT64 count, UINT64 capacity) { return offset <= capacity && count <= capacity - offset; }
UINT64 Align(UINT64 value, UINT64 alignment) { return (value + alignment - 1) & ~(alignment - 1); }

D3D12_HEAP_PROPERTIES Heap(D3D12_HEAP_TYPE type)
{
    D3D12_HEAP_PROPERTIES result{};
    result.Type = type;
    result.CreationNodeMask = result.VisibleNodeMask = 1;
    return result;
}
D3D12_RESOURCE_DESC BufferDesc(UINT64 size)
{
    D3D12_RESOURCE_DESC desc{};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    desc.Width = size;
    desc.Height = desc.SampleDesc.Count = 1;
    desc.DepthOrArraySize = desc.MipLevels = 1;
    desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    return desc;
}
D3D12_RESOURCE_DESC Texture2DDesc(UINT width, UINT height, UINT layers, UINT mips, DXGI_FORMAT format, bool renderTarget, UINT samples = 1)
{
    D3D12_RESOURCE_DESC desc{};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    desc.Width = width;
    desc.Height = height;
    desc.DepthOrArraySize = static_cast<UINT16>(std::max(1u, layers));
    desc.MipLevels = static_cast<UINT16>(std::max(1u, mips));
    desc.SampleDesc.Count = samples;
    desc.SampleDesc.Quality = 0;
    desc.Format = format;
    desc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    desc.Flags = renderTarget ? D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET : D3D12_RESOURCE_FLAG_NONE;
    return desc;
}
D3D12_RESOURCE_DESC TextureDesc(UINT width, UINT height, bool renderTarget, UINT samples = 1)
{
    return Texture2DDesc(width, height, 1, 1, DXGI_FORMAT_R8G8B8A8_UNORM, renderTarget, samples);
}
D3D12_RESOURCE_DESC Texture3DDesc(UINT width, UINT height, UINT depth, UINT mips, DXGI_FORMAT format,
    D3D12_RESOURCE_FLAGS flags)
{
    D3D12_RESOURCE_DESC desc{};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE3D;
    desc.Width = width;
    desc.Height = height;
    desc.DepthOrArraySize = static_cast<UINT16>(depth);
    desc.MipLevels = static_cast<UINT16>(mips);
    desc.Format = format;
    desc.SampleDesc.Count = 1;
    desc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    desc.Flags = flags;
    return desc;
}

bool IsTypelessFormat(DXGI_FORMAT format)
{
    switch (format)
    {
    case DXGI_FORMAT_R32G32B32A32_TYPELESS:
    case DXGI_FORMAT_R32G32B32_TYPELESS:
    case DXGI_FORMAT_R16G16B16A16_TYPELESS:
    case DXGI_FORMAT_R32G32_TYPELESS:
    case DXGI_FORMAT_R32G8X24_TYPELESS:
    case DXGI_FORMAT_R10G10B10A2_TYPELESS:
    case DXGI_FORMAT_R8G8B8A8_TYPELESS:
    case DXGI_FORMAT_R16G16_TYPELESS:
    case DXGI_FORMAT_R32_TYPELESS:
    case DXGI_FORMAT_R24G8_TYPELESS:
    case DXGI_FORMAT_R8G8_TYPELESS:
    case DXGI_FORMAT_R16_TYPELESS:
    case DXGI_FORMAT_R8_TYPELESS:
    case DXGI_FORMAT_BC1_TYPELESS:
    case DXGI_FORMAT_BC2_TYPELESS:
    case DXGI_FORMAT_BC3_TYPELESS:
    case DXGI_FORMAT_BC4_TYPELESS:
    case DXGI_FORMAT_BC5_TYPELESS:
    case DXGI_FORMAT_B8G8R8A8_TYPELESS:
    case DXGI_FORMAT_B8G8R8X8_TYPELESS:
    case DXGI_FORMAT_BC6H_TYPELESS:
    case DXGI_FORMAT_BC7_TYPELESS:
        return true;
    default:
        return false;
    }
}

DXGI_FORMAT GetTypelessFormat(DXGI_FORMAT format)
{
    switch (format)
    {
    case DXGI_FORMAT_R8G8B8A8_UNORM:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
    case DXGI_FORMAT_R8G8B8A8_UINT:
    case DXGI_FORMAT_R8G8B8A8_SNORM:
    case DXGI_FORMAT_R8G8B8A8_SINT:
        return DXGI_FORMAT_R8G8B8A8_TYPELESS;

    case DXGI_FORMAT_B8G8R8A8_UNORM:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        return DXGI_FORMAT_B8G8R8A8_TYPELESS;

    case DXGI_FORMAT_R16G16B16A16_FLOAT:
    case DXGI_FORMAT_R16G16B16A16_UNORM:
    case DXGI_FORMAT_R16G16B16A16_UINT:
    case DXGI_FORMAT_R16G16B16A16_SNORM:
    case DXGI_FORMAT_R16G16B16A16_SINT:
        return DXGI_FORMAT_R16G16B16A16_TYPELESS;

    case DXGI_FORMAT_R32G32B32A32_FLOAT:
    case DXGI_FORMAT_R32G32B32A32_UINT:
    case DXGI_FORMAT_R32G32B32A32_SINT:
        return DXGI_FORMAT_R32G32B32A32_TYPELESS;

    case DXGI_FORMAT_R32G32_FLOAT:
    case DXGI_FORMAT_R32G32_UINT:
    case DXGI_FORMAT_R32G32_SINT:
        return DXGI_FORMAT_R32G32_TYPELESS;

    case DXGI_FORMAT_R16G16_FLOAT:
    case DXGI_FORMAT_R16G16_UNORM:
    case DXGI_FORMAT_R16G16_UINT:
    case DXGI_FORMAT_R16G16_SNORM:
    case DXGI_FORMAT_R16G16_SINT:
        return DXGI_FORMAT_R16G16_TYPELESS;

    case DXGI_FORMAT_R32_FLOAT:
    case DXGI_FORMAT_R32_UINT:
    case DXGI_FORMAT_R32_SINT:
    case DXGI_FORMAT_D32_FLOAT:
        return DXGI_FORMAT_R32_TYPELESS;

    case DXGI_FORMAT_R16_FLOAT:
    case DXGI_FORMAT_R16_UNORM:
    case DXGI_FORMAT_R16_UINT:
    case DXGI_FORMAT_R16_SNORM:
    case DXGI_FORMAT_R16_SINT:
    case DXGI_FORMAT_D16_UNORM:
        return DXGI_FORMAT_R16_TYPELESS;

    case DXGI_FORMAT_D24_UNORM_S8_UINT:
        return DXGI_FORMAT_R24G8_TYPELESS;

    case DXGI_FORMAT_D32_FLOAT_S8X24_UINT:
        return DXGI_FORMAT_R32G8X24_TYPELESS;

    case DXGI_FORMAT_R8G8_UNORM:
    case DXGI_FORMAT_R8G8_UINT:
    case DXGI_FORMAT_R8G8_SNORM:
    case DXGI_FORMAT_R8G8_SINT:
        return DXGI_FORMAT_R8G8_TYPELESS;

    case DXGI_FORMAT_R8_UNORM:
    case DXGI_FORMAT_R8_UINT:
    case DXGI_FORMAT_R8_SNORM:
    case DXGI_FORMAT_R8_SINT:
        return DXGI_FORMAT_R8_TYPELESS;

    case DXGI_FORMAT_R10G10B10A2_UNORM:
    case DXGI_FORMAT_R10G10B10A2_UINT:
        return DXGI_FORMAT_R10G10B10A2_TYPELESS;

    case DXGI_FORMAT_BC1_UNORM:
    case DXGI_FORMAT_BC1_UNORM_SRGB:
        return DXGI_FORMAT_BC1_TYPELESS;

    case DXGI_FORMAT_BC2_UNORM:
    case DXGI_FORMAT_BC2_UNORM_SRGB:
        return DXGI_FORMAT_BC2_TYPELESS;

    case DXGI_FORMAT_BC3_UNORM:
    case DXGI_FORMAT_BC3_UNORM_SRGB:
        return DXGI_FORMAT_BC3_TYPELESS;

    case DXGI_FORMAT_BC4_UNORM:
    case DXGI_FORMAT_BC4_SNORM:
        return DXGI_FORMAT_BC4_TYPELESS;

    case DXGI_FORMAT_BC5_UNORM:
    case DXGI_FORMAT_BC5_SNORM:
        return DXGI_FORMAT_BC5_TYPELESS;

    case DXGI_FORMAT_BC6H_UF16:
    case DXGI_FORMAT_BC6H_SF16:
        return DXGI_FORMAT_BC6H_TYPELESS;

    case DXGI_FORMAT_BC7_UNORM:
    case DXGI_FORMAT_BC7_UNORM_SRGB:
        return DXGI_FORMAT_BC7_TYPELESS;

    default:
        return format;
    }
}

DXGI_FORMAT GetDefaultTypedFormat(DXGI_FORMAT format)
{
    switch (format)
    {
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: return DXGI_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_B8G8R8A8_TYPELESS: return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
    case DXGI_FORMAT_R32G32B32A32_TYPELESS: return DXGI_FORMAT_R32G32B32A32_FLOAT;
    case DXGI_FORMAT_R32G32_TYPELESS: return DXGI_FORMAT_R32G32_FLOAT;
    case DXGI_FORMAT_R16G16_TYPELESS: return DXGI_FORMAT_R16G16_FLOAT;
    case DXGI_FORMAT_R32_TYPELESS: return DXGI_FORMAT_R32_FLOAT;
    case DXGI_FORMAT_R16_TYPELESS: return DXGI_FORMAT_R16_FLOAT;
    case DXGI_FORMAT_R8G8_TYPELESS: return DXGI_FORMAT_R8G8_UNORM;
    case DXGI_FORMAT_R8_TYPELESS: return DXGI_FORMAT_R8_UNORM;
    case DXGI_FORMAT_R10G10B10A2_TYPELESS: return DXGI_FORMAT_R10G10B10A2_UNORM;
    case DXGI_FORMAT_R24G8_TYPELESS: return DXGI_FORMAT_R24_UNORM_X8_TYPELESS;
    case DXGI_FORMAT_R32G8X24_TYPELESS: return DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS;
    case DXGI_FORMAT_BC1_TYPELESS: return DXGI_FORMAT_BC1_UNORM;
    case DXGI_FORMAT_BC2_TYPELESS: return DXGI_FORMAT_BC2_UNORM;
    case DXGI_FORMAT_BC3_TYPELESS: return DXGI_FORMAT_BC3_UNORM;
    case DXGI_FORMAT_BC4_TYPELESS: return DXGI_FORMAT_BC4_UNORM;
    case DXGI_FORMAT_BC5_TYPELESS: return DXGI_FORMAT_BC5_UNORM;
    case DXGI_FORMAT_BC6H_TYPELESS: return DXGI_FORMAT_BC6H_UF16;
    case DXGI_FORMAT_BC7_TYPELESS: return DXGI_FORMAT_BC7_UNORM;
    default: return format;
    }
}

DXGI_FORMAT GetDefaultSrvFormat(DXGI_FORMAT format, DXGI_FORMAT sampleFormat = DXGI_FORMAT_UNKNOWN)
{
    switch (format)
    {
    case DXGI_FORMAT_D24_UNORM_S8_UINT:
    case DXGI_FORMAT_R24G8_TYPELESS:
        return DXGI_FORMAT_R24_UNORM_X8_TYPELESS;

    case DXGI_FORMAT_D32_FLOAT_S8X24_UINT:
    case DXGI_FORMAT_R32G8X24_TYPELESS:
        return DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS;

    case DXGI_FORMAT_D16_UNORM:
    case DXGI_FORMAT_R16_TYPELESS:
        return sampleFormat != DXGI_FORMAT_UNKNOWN ? sampleFormat : DXGI_FORMAT_R16_UNORM;

    case DXGI_FORMAT_D32_FLOAT:
    case DXGI_FORMAT_R32_TYPELESS:
        return sampleFormat != DXGI_FORMAT_UNKNOWN ? sampleFormat : DXGI_FORMAT_R32_FLOAT;

    default:
        if (IsTypelessFormat(format))
            return sampleFormat != DXGI_FORMAT_UNKNOWN ? sampleFormat : GetDefaultTypedFormat(format);
        return format;
    }
}

bool AreFormatsInSameFamily(DXGI_FORMAT a, DXGI_FORMAT b)
{
    if (a == b) return true;
    DXGI_FORMAT ta = GetTypelessFormat(a);
    DXGI_FORMAT tb = GetTypelessFormat(b);
    return ta != DXGI_FORMAT_UNKNOWN && ta == tb;
}

struct Resource
{
    DXGI_FORMAT depthFormat = DXGI_FORMAT_UNKNOWN;
    DXGI_FORMAT sampleFormat = DXGI_FORMAT_UNKNOWN;
    // Keep the backing heap alive until after the placed resource is destroyed.
    ComPtr<ID3D12Heap> backingHeap;
    ComPtr<ID3D12Resource> object;
    bool needsAliasingBarrier = false;
    D3D12_RESOURCE_STATES state = D3D12_RESOURCE_STATE_COMMON;
    std::vector<D3D12_RESOURCE_STATES> subresourceStates;
    D3D12_RESOURCE_DESC desc{};
    UINT64 allocation = 0;
    UINT64 retainedSubmission = 0;
};
struct TextureView
{
    std::shared_ptr<Resource> resource;
    UINT firstMip = 0;
    UINT mipCount = 1;
    UINT firstSlice = 0;
    UINT sliceCount = 1;
    DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
    UINT componentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
};
struct UploadPage
{
    ComPtr<ID3D12Resource> object;
    BYTE* mapped = nullptr;
    UINT64 capacity = 0;
    UINT64 used = 0;
};
struct Frame
{
    struct SamplerGroup
    {
        std::vector<D3D12_SAMPLER_DESC> descriptors;
        ID3D12DescriptorHeap* heap = nullptr;
        UINT offset = 0;
    };
    struct BindingGroup
    {
        UINT64 hash = 0;
        ID3D12RootSignature* root = nullptr;
        std::vector<RyuboxD12Binding> bindings;
        ID3D12DescriptorHeap* viewHeap = nullptr;
        ID3D12DescriptorHeap* samplerHeap = nullptr;
        std::vector<D3D12_GPU_DESCRIPTOR_HANDLE> gpuStarts;
    };
    ComPtr<ID3D12CommandAllocator> allocator;
    UINT64 fence = 0;
    UINT64 descriptorCount = 0;
    UINT64 samplerCount = 0;
    UINT rtvCount = 0;
    UINT dsvCount = 0;
    std::vector<UploadPage> uploads;
    std::vector<std::shared_ptr<Resource>> retained;
    std::vector<UINT64> retainedResourceIds;
    std::vector<ComPtr<ID3D12PipelineState>> pipelines;
    std::vector<ComPtr<ID3D12RootSignature>> roots;
    std::vector<ComPtr<ID3D12DescriptorHeap>> bindingHeaps;
    std::vector<ComPtr<ID3D12CommandSignature>> signatures;
    std::vector<ComPtr<ID3D12QueryHeap>> queries;
    // Groups are scoped to this frame slot. The slot is only reset after its
    // fence completes, so cached shader-visible sampler ranges remain valid
    // without extending descriptor retirement or copying unchanged samplers.
    std::vector<SamplerGroup> samplerGroups;
    std::vector<BindingGroup> bindingGroups;
    std::unordered_multimap<UINT64, size_t> bindingGroupLookup;
    std::unordered_set<ID3D12PipelineState*> retainedPipelines;
    std::unordered_set<ID3D12RootSignature*> retainedRoots;
};

struct ProgramBinding
{
    RyuboxD12BindingInfo info;
    D3D_SHADER_INPUT_TYPE inputType;
    UINT table, tableOffset;
};
struct BindingKey
{
    UINT stage, type, space, binding;
    bool operator==(const BindingKey&) const = default;
};
struct BindingKeyHash
{
    size_t operator()(const BindingKey& key) const noexcept
    {
        size_t hash = key.stage;
        hash = hash * 16777619u ^ key.type;
        hash = hash * 16777619u ^ key.space;
        return hash * 16777619u ^ key.binding;
    }
};
struct Program
{
    UINT auxiliaryRoot = UINT_MAX;
    UINT dispatchRoot = UINT_MAX;
    D3D_PRIMITIVE_TOPOLOGY geometryOutput = D3D_PRIMITIVE_TOPOLOGY_UNDEFINED;
    D3D_TESSELLATOR_OUTPUT_PRIMITIVE tessellationOutput = D3D_TESSELLATOR_OUTPUT_UNDEFINED;
    std::array<ComPtr<ID3DBlob>, 6> shaders;
    std::string hullSource;
    std::map<UINT, ComPtr<ID3DBlob>> hullVariants; // At most 32 IA patch sizes.
    ComPtr<ID3D12RootSignature> root;
    std::vector<BYTE> rootBlob;
    ComPtr<ID3D12PipelineState> compute;
    std::vector<ProgramBinding> bindings;
    std::vector<RyuboxD12VertexInput> inputs;
    std::vector<RyuboxD12SOElement> streamOutputs;
    std::vector<std::pair<UINT, UINT>> tables; // stage, sampler/non-sampler
    std::vector<UINT> tableSizes;
    std::map<std::string, ComPtr<ID3D12PipelineState>> graphics;
};

struct Counter
{
    ComPtr<ID3D12QueryHeap> heap;
    std::shared_ptr<Resource> result;
    UINT type;
    UINT64 fence = 0;
};

class Context
{
public:
    std::mutex mutex;
    HRESULT fault = S_OK;
    std::string programError;
    std::string lastDiagnostic;
    ComPtr<ID3D12Device> device;
    ComPtr<IDXGIAdapter1> adapter;
    ComPtr<IDXGIFactory4> factory;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12GraphicsCommandList> commands;
    ComPtr<ID3D12Fence> fence;
    ComPtr<IDXGISwapChain3> swapchain;
    ComPtr<ISwapChainPanelNative> panel;
    ComPtr<ID3D12DescriptorHeap> rtvHeap;
    ComPtr<ID3D12DescriptorHeap> dsvHeap;
    UINT dsvStride = 0;
    ComPtr<ID3D12DescriptorHeap> srvHeap;
    ComPtr<ID3D12DescriptorHeap> samplerHeap;
    ComPtr<ID3D12PipelineState> byteCopyPipeline;
    struct DescriptorPage
    {
        ComPtr<ID3D12DescriptorHeap> heap;
        D3D12DescriptorRing ring;
        explicit DescriptorPage(UINT capacity) : ring(capacity) {}
    };
    struct DescriptorPool
    {
        std::vector<std::unique_ptr<DescriptorPage>> pages;
        size_t current = 0;
        UINT64 occupied = 0, peak = 0;
    };
    DescriptorPool viewPool, samplerPool;
    RyuboxD3D12DescriptorStats descriptorStats{};
    RyuboxD3D12PerformanceStats performanceStats{sizeof(RyuboxD3D12PerformanceStats), 3};
    bool descriptorRingEnabled = true;
    ID3D12DescriptorHeap* boundViewHeap = nullptr;
    ID3D12DescriptorHeap* boundSamplerHeap = nullptr;
    ComPtr<ID3D12RootSignature> rootSignature;
    ComPtr<ID3D12RootSignature> computeRoot;
    ComPtr<ID3D12PipelineState> blitPipeline;
    ComPtr<ID3D12InfoQueue> debugQueue;
    std::array<Frame, FrameCount> frames;
    std::array<std::shared_ptr<Resource>, FrameCount> targets;
    std::unordered_map<UINT64, std::shared_ptr<Resource>> resources;
    // Views own a reference to the parent allocation and carry descriptor
    // ranges without cloning Resource (which would break placed-heap lifetime).
    std::unordered_map<UINT64, TextureView> textureViews;
    std::unordered_map<UINT64, ComPtr<ID3D12PipelineState>> computePipelines;
    std::unordered_map<UINT64, std::shared_ptr<Program>> programs;
    std::unordered_map<UINT64, Counter> counters;
    UINT activeCounters = 0;
    // Weak accounting includes resources released by the guest but still used
    // by in-flight command lists; removing a handle cannot bypass the budget.
    std::vector<std::weak_ptr<Resource>> allocations;
    HANDLE fenceEvent = nullptr;
    UINT64 submitted = 0;
    UINT64 nextResource = 1;
    UINT64 waits = 0;
    UINT64 uploadBytes = 0;
    UINT width = 0, height = 0, frameIndex = 0, targetIndex = 0, lastTarget = 0;
    UINT rtvStride = 0, srvStride = 0;
    UINT samplerStride = 0;
    UINT debugErrors = 0;
    UINT64 debugRead = 0;
    bool recording = false;
    bool pendingUnfenced = false;
    // Begin() binds the current swap-chain RTV. Keep that fact explicit so a
    // following implicit presentation draw can reuse the binding instead of
    // issuing an identical OMSetRenderTargets call. Besides avoiding needless
    // command traffic this works around a hang in older Intel D3D12 UMDs.
    bool defaultTargetBound = false;
    bool composition = false;
    bool logicOpSupported = false;

    struct MemoryBlock
    {
        UINT64 offset = 0;
        UINT64 size = 0;
        std::weak_ptr<Resource> owner;
    };
    struct MemoryChunk
    {
        ComPtr<ID3D12Heap> heap;
        UINT64 capacity = 64 * MiB;
        std::vector<MemoryBlock> blocks;
    };
    std::vector<MemoryChunk> memoryChunks;
    UINT64 placedAllocations = 0, committedAllocations = 0, heapCreations = 0;
    UINT64 shaderCompilations = 0, pipelineCreations = 0;
    D3D12_RESOURCE_HEAP_TIER resourceHeapTier = D3D12_RESOURCE_HEAP_TIER_1;
    // Some legacy Intel WDDM drivers report heap tier 2 but can hang when a
    // placed resource reuses an aliasing range across frontend/CoreHost
    // sessions. This is still the native D3D12 path: only the allocator mode
    // changes to committed resources for that adapter family. Newer Intel
    // systems may opt back in after validating their driver.
    bool disablePlacedPooling = false;
    ComPtr<ID3D12PipelineLibrary> pipelineLibrary;
    std::wstring pipelineCachePath;
    bool pipelineCacheDirty = false;
    UINT64 pipelineCacheHits = 0;
    UINT64 pipelineCacheStores = 0;

    static UINT64 HashBytes(UINT64 hash, const void* data, size_t bytes)
    {
        const auto* values = static_cast<const BYTE*>(data);
        for (size_t i = 0; i < bytes; ++i)
        {
            hash ^= values[i];
            hash *= 1099511628211ull;
        }
        return hash;
    }

    std::wstring PipelineName(const Program& program, const char* kind, const std::string& state) const
    {
        UINT64 hash = 1469598103934665603ull;
        hash = HashBytes(hash, kind, strlen(kind));
        DXGI_ADAPTER_DESC1 description{};
        if (adapter) adapter->GetDesc1(&description);
        hash = HashBytes(hash, &description.VendorId, sizeof(description.VendorId));
        hash = HashBytes(hash, &description.DeviceId, sizeof(description.DeviceId));
        hash = HashBytes(hash, program.rootBlob.data(), program.rootBlob.size());
        for (const auto& shader : program.shaders)
            if (shader) hash = HashBytes(hash, shader->GetBufferPointer(), shader->GetBufferSize());
        hash = HashBytes(hash, state.data(), state.size());
        wchar_t name[64]{};
        swprintf_s(name, L"RyuboxD12-v2-%hs-%016llX", kind,
            static_cast<unsigned long long>(hash));
        return name;
    }

    void InitializePipelineCache()
    {
        wchar_t path[4096]{};
        DWORD length = GetEnvironmentVariableW(L"RYUBOX_D3D12_PSO_CACHE", path, ARRAYSIZE(path));
        if (!length || length >= ARRAYSIZE(path)) return;
        wchar_t enable[2]{};
        if (GetEnvironmentVariableW(L"RYUBOX_D3D12_ENABLE_PSO_LIBRARY", enable,
            ARRAYSIZE(enable)) != 1 || enable[0] != L'1')
            return;
        wchar_t forceIntel[2]{};
        bool allowIntel = GetEnvironmentVariableW(L"RYUBOX_D3D12_FORCE_PSO_LIBRARY",
            forceIntel, ARRAYSIZE(forceIntel)) == 1 && forceIntel[0] == L'1';
        DXGI_ADAPTER_DESC1 adapterDescription{};
        if (adapter && SUCCEEDED(adapter->GetDesc1(&adapterDescription)) &&
            adapterDescription.VendorId == 0x8086 && !allowIntel)
            return;
        pipelineCachePath.assign(path, length);

        CREATEFILE2_EXTENDED_PARAMETERS readParameters{};
        readParameters.dwSize = sizeof(readParameters);
        readParameters.dwFileAttributes = FILE_ATTRIBUTE_NORMAL;
        HANDLE file = CreateFile2(path, GENERIC_READ, FILE_SHARE_READ, OPEN_EXISTING, &readParameters);
        std::vector<BYTE> bytes;
        LARGE_INTEGER size{};
        bool valid = file != INVALID_HANDLE_VALUE && GetFileSizeEx(file, &size) &&
            size.QuadPart > 0 && size.QuadPart <= 256ll * 1024 * 1024;
        if (valid)
        {
            bytes.resize(static_cast<size_t>(size.QuadPart));
            DWORD read = 0;
            valid = ReadFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr) &&
                read == bytes.size();
        }
        if (file != INVALID_HANDLE_VALUE) CloseHandle(file);

        ComPtr<ID3D12Device1> device1;
        ComPtr<ID3D12PipelineLibrary> library;
        if (valid && SUCCEEDED(device.As(&device1)) &&
            SUCCEEDED(device1->CreatePipelineLibrary(bytes.data(), bytes.size(), IID_PPV_ARGS(&library))))
            pipelineLibrary = std::move(library);
        if (!pipelineLibrary && SUCCEEDED(device.As(&device1)) &&
            SUCCEEDED(device1->CreatePipelineLibrary(nullptr, 0, IID_PPV_ARGS(&library))))
            pipelineLibrary = std::move(library);
    }

    void StorePipelineCache() noexcept
    {
        if (!pipelineLibrary || !pipelineCacheDirty || pipelineCachePath.empty()) return;
        SIZE_T size = pipelineLibrary->GetSerializedSize();
        if (!size || size > 256ull * 1024 * 1024) return;
        std::vector<BYTE> bytes(size);
        if (FAILED(pipelineLibrary->Serialize(bytes.data(), bytes.size()))) return;
        CREATEFILE2_EXTENDED_PARAMETERS writeParameters{};
        writeParameters.dwSize = sizeof(writeParameters);
        writeParameters.dwFileAttributes = FILE_ATTRIBUTE_NORMAL;
        HANDLE file = CreateFile2(pipelineCachePath.c_str(), GENERIC_WRITE, 0, CREATE_ALWAYS, &writeParameters);
        if (file == INVALID_HANDLE_VALUE) return;
        DWORD written = 0;
        bool complete = WriteFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr) &&
            written == bytes.size();
        CloseHandle(file);
        if (complete) { pipelineCacheDirty = false; ++pipelineCacheStores; }
    }

    ~Context()
    {
        // Destruction occurs after callers have relinquished this context.
        // Discard an unsubmitted list; queued references stay owned until wait.
        if (recording && commands) commands->Close();
        recording = false;
        if (device && queue && fence && fenceEvent && SUCCEEDED(device->GetDeviceRemovedReason()))
        {
            try { Wait(submitted); } catch (...) { /* Device fault/timeout. */ }
        }
        if (panel) panel->SetSwapChain(nullptr);
        StorePipelineCache();
        if (fenceEvent) CloseHandle(fenceEvent);
    }

    void Initialize(HWND window, IUnknown* panelObject, UINT w, UINT h, UINT flags)
    {
        Require(!(window && panelObject) && (flags & ~3u) == 0);
        ValidateSize(w, h);
        if (flags & 1)
        {
            ComPtr<ID3D12Debug> debug;
            Check(D3D12GetDebugInterface(IID_PPV_ARGS(&debug)));
            debug->EnableDebugLayer();
        }
        Check(CreateDXGIFactory2((flags & 1) ? DXGI_CREATE_FACTORY_DEBUG : 0, IID_PPV_ARGS(&factory)));
        if (flags & 2)
        {
            Check(factory->EnumWarpAdapter(IID_PPV_ARGS(&adapter)));
        }
        else
        {
            for (UINT index = 0; ; ++index)
            {
                ComPtr<IDXGIAdapter1> candidate;
                HRESULT result = factory->EnumAdapters1(index, &candidate);
                if (result == DXGI_ERROR_NOT_FOUND) break;
                Check(result);
                DXGI_ADAPTER_DESC1 desc{};
                Check(candidate->GetDesc1(&desc));
                if (!(desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) &&
                    SUCCEEDED(D3D12CreateDevice(candidate.Get(), D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device), nullptr)))
                { adapter = candidate; break; }
            }
            Require(adapter != nullptr, DXGI_ERROR_UNSUPPORTED);
        }
        Check(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device)));
        InitializePipelineCache();
        DXGI_ADAPTER_DESC1 adapterDescription{};
        Check(adapter->GetDesc1(&adapterDescription));
        char enableIntelPooling[2]{};
        const bool forceIntelPooling = GetEnvironmentVariableA(
            "RYUBOX_D3D12_ENABLE_INTEL_POOL", enableIntelPooling,
            static_cast<DWORD>(sizeof(enableIntelPooling))) != 0 && enableIntelPooling[0] == '1';
        disablePlacedPooling = adapterDescription.VendorId == 0x8086 && !forceIntelPooling;
        if (flags & 1) Check(device.As(&debugQueue));
        D3D12_FEATURE_DATA_D3D12_OPTIONS options{};
        if (SUCCEEDED(device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS, &options, sizeof(options))))
        {
            logicOpSupported = options.OutputMergerLogicOp != FALSE;
            resourceHeapTier = options.ResourceHeapTier;
        }
        D3D12_COMMAND_QUEUE_DESC queueDesc{};
        queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        Check(device->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&queue)));
        Check(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)));
        fenceEvent = CreateEventExW(nullptr, nullptr, 0, EVENT_MODIFY_STATE | SYNCHRONIZE);
        if (!fenceEvent) Check(HRESULT_FROM_WIN32(GetLastError()));
        for (auto& frame : frames)
            Check(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&frame.allocator)));
        Check(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, frames[0].allocator.Get(),
            nullptr, IID_PPV_ARGS(&commands)));
        Check(commands->Close());
        D3D12_DESCRIPTOR_HEAP_DESC heapDesc{};
        heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        heapDesc.NumDescriptors = FrameCount + FrameCount * DescriptorsPerFrame;
        Check(device->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&rtvHeap)));
        rtvStride = device->GetDescriptorHandleIncrementSize(heapDesc.Type);
        heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_DSV;
        heapDesc.NumDescriptors = FrameCount * DescriptorsPerFrame;
        Check(device->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&dsvHeap)));
        dsvStride = device->GetDescriptorHandleIncrementSize(heapDesc.Type);
        srvStride = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        samplerStride = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_SAMPLER);
        // Diagnostic A/B switch, read once per context. Both modes use D3D12.
        wchar_t fixedDescriptors[2]{};
        descriptorRingEnabled = !(GetEnvironmentVariableW(L"RYUBOX_D3D12_FIXED_DESCRIPTORS",
            fixedDescriptors, ARRAYSIZE(fixedDescriptors)) == 1 && fixedDescriptors[0] == L'1');
        srvHeap = AddDescriptorPage(false).heap;
        samplerHeap = AddDescriptorPage(true).heap;
        width = w; height = h;
        if (window || panelObject)
        {
            DXGI_SWAP_CHAIN_DESC1 desc{};
            desc.Width = w; desc.Height = h;
            desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
            desc.SampleDesc.Count = 1;
            desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
            desc.BufferCount = FrameCount;
            desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
            desc.Scaling = DXGI_SCALING_STRETCH;
            desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
            ComPtr<IDXGISwapChain1> swap;
            if (window)
                Check(factory->CreateSwapChainForHwnd(queue.Get(), window, &desc, nullptr, nullptr, &swap));
            else
            {
                composition = true;
                desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
                Check(factory->CreateSwapChainForComposition(queue.Get(), &desc, nullptr, &swap));
                Check(panelObject->QueryInterface(IID_PPV_ARGS(&panel)));
                Check(panel->SetSwapChain(swap.Get()));
            }
            Check(swap.As(&swapchain));
        }
        CreateTargets();
        CreateBlitPipeline();
        D3D12_ROOT_PARAMETER computeParameters[3]{};
        computeParameters[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
        computeParameters[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_UAV;
        computeParameters[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        computeParameters[2].Constants = {0,0,16};
        D3D12_ROOT_SIGNATURE_DESC computeDesc{3,computeParameters,0,nullptr,D3D12_ROOT_SIGNATURE_FLAG_NONE};
        ComPtr<ID3DBlob> signature, error;
        Check(D3D12SerializeRootSignature(&computeDesc,D3D_ROOT_SIGNATURE_VERSION_1,&signature,&error));
        Check(device->CreateRootSignature(0,signature->GetBufferPointer(),signature->GetBufferSize(),IID_PPV_ARGS(&computeRoot)));
    }

    static void ValidateSize(UINT w, UINT h)
    { Require(w && h && w <= D3D12_REQ_TEXTURE2D_U_OR_V_DIMENSION && h <= D3D12_REQ_TEXTURE2D_U_OR_V_DIMENSION); }

    static void Validate2DSize(UINT w, UINT h, UINT layers, UINT mips)
    {
        Require(w && h && layers && mips &&
            w <= D3D12_REQ_TEXTURE2D_U_OR_V_DIMENSION &&
            h <= D3D12_REQ_TEXTURE2D_U_OR_V_DIMENSION &&
            layers <= D3D12_REQ_TEXTURE2D_ARRAY_AXIS_DIMENSION,
            E_INVALIDARG, "Validate2DSize: texture dimensions or layers out of range");
        UINT maximumMips = 1;
        for (UINT largest = std::max(w, h); largest > 1; largest >>= 1) ++maximumMips;
        Require(mips <= maximumMips && mips <= D3D12_REQ_MIP_LEVELS,
            E_INVALIDARG, "Validate2DSize: mip levels out of range");
    }

    static void Validate3DSize(UINT w, UINT h, UINT d, UINT mips)
    {
        Require(w && h && d && w <= D3D12_REQ_TEXTURE3D_U_V_OR_W_DIMENSION &&
            h <= D3D12_REQ_TEXTURE3D_U_V_OR_W_DIMENSION && d <= D3D12_REQ_TEXTURE3D_U_V_OR_W_DIMENSION);
        UINT maximumMips = 1;
        for (UINT largest = std::max(w, std::max(h, d)); largest > 1; largest >>= 1) ++maximumMips;
        Require(mips && mips <= maximumMips && mips <= D3D12_REQ_MIP_LEVELS);
    }

    void CheckDebug()
    {
        if (!debugQueue) return;
        UINT64 count = debugQueue->GetNumStoredMessagesAllowedByRetrievalFilter();
        for (; debugRead < count; ++debugRead)
        {
            SIZE_T size = 0;
            Check(debugQueue->GetMessage(debugRead, nullptr, &size));
            std::vector<BYTE> bytes(size);
            auto message = reinterpret_cast<D3D12_MESSAGE*>(bytes.data());
            Check(debugQueue->GetMessage(debugRead, message, &size));
            if (message->Severity <= D3D12_MESSAGE_SEVERITY_ERROR)
            {
                ++debugErrors;
                OutputDebugStringA(message->pDescription);
                // Keep the latest validation error in the diagnostic channel
                // consumed by the managed renderer. programError is scoped to
                // program creation and may be queried after a frame boundary.
                lastDiagnostic.assign(message->pDescription ? message->pDescription : "D3D12 validation error");
                if (programError.size() < 8176)
                {
                    programError += "\nD3D12 debug: ";
                    programError.append(message->pDescription,
                        std::min<size_t>(strlen(message->pDescription),8192-programError.size()));
                }
            }
        }
        debugQueue->ClearStoredMessages();
        debugRead = 0;
    }

    void Wait(UINT64 value)
    {
        Require(value <= submitted);
        Check(device->GetDeviceRemovedReason());
        UINT64 completed = fence->GetCompletedValue();
        if (completed == UINT64_MAX) Check(DXGI_ERROR_DEVICE_REMOVED);
        if (completed < value)
        {
            LARGE_INTEGER start{}, end{}, frequency{};
            QueryPerformanceFrequency(&frequency); QueryPerformanceCounter(&start);
            ++waits;
            Check(fence->SetEventOnCompletion(value, fenceEvent));
            DWORD result = WaitForSingleObjectEx(fenceEvent, FenceTimeoutMs, FALSE);
            QueryPerformanceCounter(&end);
            UINT64 microseconds = static_cast<UINT64>((end.QuadPart-start.QuadPart)*1000000/frequency.QuadPart);
            descriptorStats.cpuFenceWaitMicroseconds += microseconds;
            descriptorStats.maxFenceWaitMicroseconds = std::max(descriptorStats.maxFenceWaitMicroseconds, microseconds);
            performanceStats.fenceWaitMicroseconds += microseconds;
            Check(device->GetDeviceRemovedReason());
            if (result != WAIT_OBJECT_0)
                Check(HRESULT_FROM_WIN32(result == WAIT_TIMEOUT ? ERROR_TIMEOUT : GetLastError()));
        }
        CheckDebug();
    }

    DescriptorPage& AddDescriptorPage(bool sampler)
    {
        UINT capacity = sampler ? SamplerRingCapacity : ViewRingCapacity;
        UINT64 bytes = static_cast<UINT64>(capacity) * (sampler ? samplerStride : srvStride);
        // Bounded descriptor address-space estimate, not a GPU residency claim.
        Require(Fits(descriptorStats.estimatedHeapBytes, bytes, 64 * MiB), E_OUTOFMEMORY);
        auto page = std::make_unique<DescriptorPage>(capacity);
        D3D12_DESCRIPTOR_HEAP_DESC desc{};
        desc.Type = sampler ? D3D12_DESCRIPTOR_HEAP_TYPE_SAMPLER : D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        desc.NumDescriptors = capacity; desc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        Check(device->CreateDescriptorHeap(&desc, IID_PPV_ARGS(&page->heap)));
        auto& pool = sampler ? samplerPool : viewPool;
        pool.pages.push_back(std::move(page));
        pool.current = pool.pages.size()-1;
        descriptorStats.estimatedHeapBytes += bytes;
        ++descriptorStats.pageCreations;
        return *pool.pages.back();
    }

    void CollectDescriptors()
    {
        UINT64 completed = fence->GetCompletedValue();
        if (completed == UINT64_MAX) Check(DXGI_ERROR_DEVICE_REMOVED);
        for (auto* pool : {&viewPool, &samplerPool})
        {
            pool->occupied = 0;
            for (auto& page : pool->pages)
            {
                UINT64 before = page->ring.reclaimed;
                page->ring.Collect(completed);
                descriptorStats.reclaimed += page->ring.reclaimed-before;
                pool->occupied += page->ring.Used();
            }
        }
    }

    struct DescriptorSlice { ID3D12DescriptorHeap* heap; UINT offset; };
    DescriptorSlice AllocateDescriptors(bool sampler, UINT count)
    {
        Require(recording, HRESULT_FROM_WIN32(ERROR_INVALID_STATE));
        auto& pool = sampler ? samplerPool : viewPool;
        auto& frame = frames[frameIndex];
        auto& used = sampler ? frame.samplerCount : frame.descriptorCount;
        auto record = [&] {
            used += count;
            auto& total = sampler ? descriptorStats.samplerAllocations : descriptorStats.viewAllocations;
            auto& peak = sampler ? descriptorStats.samplerPeakPerSubmission : descriptorStats.viewPeakPerSubmission;
            total += count; peak = std::max(peak, used);
        };
        if (!descriptorRingEnabled)
        {
            UINT capacity = sampler ? SamplersPerFrame : DescriptorsPerFrame;
            if (!Fits(used, count, capacity)) { ++descriptorStats.allocationFailures; Check(E_OUTOFMEMORY); }
            UINT offset = frameIndex*capacity + static_cast<UINT>(used);
            record(); return {pool.pages[0]->heap.Get(), offset};
        }
        if (count > (sampler ? SamplerRingCapacity : ViewRingCapacity))
        { ++descriptorStats.allocationFailures; Check(E_OUTOFMEMORY); }
        auto attempt = [&](size_t index, DescriptorSlice& result) {
            auto& page = *pool.pages[index];
            UINT offset = 0; UINT64 before = page.ring.Used(), wraps = page.ring.wraps;
            if (!page.ring.Allocate(count, submitted+1, offset)) return false;
            pool.current = index;
            pool.occupied += page.ring.Used()-before;
            pool.peak = std::max(pool.peak, pool.occupied);
            descriptorStats.wraps += page.ring.wraps-wraps;
            record(); result = {page.heap.Get(), offset}; return true;
        };
        DescriptorSlice result{};
        if (attempt(pool.current, result)) return result;
        ++descriptorStats.pressureEvents;
        CollectDescriptors();
        for (size_t i=0; i<pool.pages.size(); ++i)
            if (attempt(i, result)) return result;
        UINT64 bytes = static_cast<UINT64>(sampler ? SamplerRingCapacity : ViewRingCapacity) * (sampler ? samplerStride : srvStride);
        if (!Fits(descriptorStats.estimatedHeapBytes, bytes, 64*MiB))
        {
            // Only wait on submitted work. Never wait for this open list, or
            // submit behind the GAL's back while a guest query is active.
            for (;;)
            {
                UINT64 oldest = UINT64_MAX;
                for (auto& page : pool.pages)
                {
                    UINT64 value = page->ring.OldestFence();
                    if (value && value <= submitted) oldest = std::min(oldest, value);
                }
                if (oldest == UINT64_MAX) break;
                ++descriptorStats.ringWaits;
                UINT64 before = descriptorStats.cpuFenceWaitMicroseconds;
                Wait(oldest);
                descriptorStats.ringWaitMicroseconds += descriptorStats.cpuFenceWaitMicroseconds-before;
                CollectDescriptors();
                for (size_t i=0; i<pool.pages.size(); ++i)
                    if (attempt(i, result)) return result;
            }
            ++descriptorStats.allocationFailures; Check(E_OUTOFMEMORY);
        }
        try { AddDescriptorPage(sampler); }
        catch (...) { ++descriptorStats.allocationFailures; throw; }
        Require(attempt(pool.current, result), E_UNEXPECTED);
        return result;
    }

    void BindDescriptorHeaps(ID3D12DescriptorHeap* views, ID3D12DescriptorHeap* samplers)
    {
        if (boundViewHeap == views && boundSamplerHeap == samplers) return;
        ID3D12DescriptorHeap* heaps[] = {views, samplers};
        commands->SetDescriptorHeaps(samplers ? 2 : 1, heaps);
        boundViewHeap = views; boundSamplerHeap = samplers;
        ++descriptorStats.heapSwitches;
        // All callers set every root table again after a heap switch.
    }

    UINT64 LiveBytes()
    {
        UINT64 bytes = 0;
        auto it = allocations.begin();
        while (it != allocations.end())
        {
            if (auto resource = it->lock()) { bytes += resource->allocation; ++it; }
            else it = allocations.erase(it);
        }
        return bytes;
    }

    // Frame references, not CPU handle deletion, determine when a range is free.
    // Gaps between the remaining sorted allocations implicitly coalesce.
    void CollectChunks()
    {
        for (auto& chunk : memoryChunks)
            std::erase_if(chunk.blocks, [](const MemoryBlock& block) { return block.owner.expired(); });
        std::erase_if(memoryChunks, [](const MemoryChunk& chunk) { return chunk.blocks.empty(); });
    }

    UINT64 ReservedResourceBytes()
    {
        UINT64 bytes = 0;
        for (const auto& chunk : memoryChunks) bytes += chunk.capacity;
        for (const auto& weak : allocations)
            if (auto resource = weak.lock(); resource && !resource->backingHeap) bytes += resource->allocation;
        return bytes;
    }

    std::shared_ptr<Resource> Allocate(const D3D12_RESOURCE_DESC& desc)
    {
        auto info = device->GetResourceAllocationInfo(0, 1, &desc);
        UINT64 live = LiveBytes();
        UINT64 budget = ResourceBudgetLimit();
        if (info.SizeInBytes == UINT64_MAX || !Fits(live, info.SizeInBytes, budget))
        {
            programError = "D3D12 resource budget exceeded: live=" + std::to_string(live) +
                ", requested=" + std::to_string(info.SizeInBytes) +
                ", budget=" + std::to_string(budget) +
                ", resource=" + std::to_string(desc.Width) + "x" + std::to_string(desc.Height) +
                ", format=" + std::to_string(static_cast<UINT>(desc.Format));
            Require(false, E_OUTOFMEMORY);
        }
        auto resource = std::make_shared<Resource>();
        resource->desc = desc;
        resource->allocation = info.SizeInBytes;
        CollectChunks();
        static const bool disablePooling = [] {
            wchar_t value[2]{};
            return GetEnvironmentVariableW(L"RYUBOX_D3D12_COMMITTED_ONLY", value, 2) == 1 && value[0] == L'1';
        }();
        const bool pooled = !disablePooling && !disablePlacedPooling && resourceHeapTier == D3D12_RESOURCE_HEAP_TIER_2 &&
            info.SizeInBytes <= 32 * MiB && info.Alignment <= D3D12_DEFAULT_RESOURCE_PLACEMENT_ALIGNMENT;
        if (pooled)
        {
            MemoryChunk* selected = nullptr;
            UINT64 offset = 0;
            for (auto& chunk : memoryChunks)
            {
                offset = 0;
                for (const auto& block : chunk.blocks)
                {
                    offset = Align(offset, info.Alignment);
                    if (Fits(offset, info.SizeInBytes, block.offset)) break;
                    offset = block.offset + block.size;
                }
                offset = Align(offset, info.Alignment);
                if (Fits(offset, info.SizeInBytes, chunk.capacity)) { selected = &chunk; break; }
            }
            if (!selected)
            {
                constexpr UINT64 capacity = 64 * MiB;
                Require(Fits(ReservedResourceBytes(), capacity, budget), E_OUTOFMEMORY);
                MemoryChunk chunk;
                D3D12_HEAP_DESC heapDesc{};
                heapDesc.SizeInBytes = capacity;
                heapDesc.Properties = Heap(D3D12_HEAP_TYPE_DEFAULT);
                heapDesc.Alignment = D3D12_DEFAULT_RESOURCE_PLACEMENT_ALIGNMENT;
                heapDesc.Flags = D3D12_HEAP_FLAG_ALLOW_ALL_BUFFERS_AND_TEXTURES;
                Check(device->CreateHeap(&heapDesc, IID_PPV_ARGS(&chunk.heap)));
                memoryChunks.push_back(std::move(chunk));
                ++heapCreations;
                selected = &memoryChunks.back();
                offset = 0;
            }
            Check(device->CreatePlacedResource(selected->heap.Get(), offset, &desc,
                resource->state, nullptr, IID_PPV_ARGS(&resource->object)));
            resource->backingHeap = selected->heap;
            resource->needsAliasingBarrier = true;
            selected->blocks.push_back({offset, info.SizeInBytes, resource});
            std::sort(selected->blocks.begin(), selected->blocks.end(),
                [](const MemoryBlock& a, const MemoryBlock& b) { return a.offset < b.offset; });
            ++placedAllocations;
        }
        else
        {
        Require(Fits(ReservedResourceBytes(), info.SizeInBytes, budget), E_OUTOFMEMORY);
        auto heap = Heap(D3D12_HEAP_TYPE_DEFAULT);
        Check(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc,
            resource->state, nullptr, IID_PPV_ARGS(&resource->object)));
        ++committedAllocations;
        }
        allocations.push_back(resource);
        return resource;
    }

    void CreateTargets()
    {
        auto rtv = rtvHeap->GetCPUDescriptorHandleForHeapStart();
        for (UINT i = 0; i < (swapchain ? FrameCount : 1u); ++i)
        {
            auto target = swapchain ? std::make_shared<Resource>() : Allocate(TextureDesc(width, height, true));
            if (swapchain)
            {
                Check(swapchain->GetBuffer(i, IID_PPV_ARGS(&target->object)));
                target->desc = target->object->GetDesc();
            }
            targets[i] = target;
            device->CreateRenderTargetView(target->object.Get(), nullptr, rtv);
            rtv.ptr += rtvStride;
        }
        lastTarget = 0;
    }

    void BeginCommands()
    {
        Require(!recording, HRESULT_FROM_WIN32(ERROR_INVALID_STATE));
        frameIndex = static_cast<UINT>(submitted % FrameCount);
        Frame& frame = frames[frameIndex];
        Wait(frame.fence);
        CollectDescriptors();
        frame.retained.clear();
        for (UINT64 rid : frame.retainedResourceIds) { resources.erase(rid); textureViews.erase(rid); }
        frame.retainedResourceIds.clear();
        frame.pipelines.clear();
        frame.retainedPipelines.clear();
        frame.retainedRoots.clear();
        frame.samplerCount = 0;
        frame.samplerGroups.clear();
        frame.bindingGroups.clear();
        frame.bindingGroupLookup.clear();
        frame.rtvCount = 0;
        frame.dsvCount = 0;
        frame.roots.clear();
        frame.bindingHeaps.clear();
        frame.signatures.clear();
        frame.queries.clear();
        frame.descriptorCount = 0;
        for (auto& page : frame.uploads) page.used = 0;
        Check(frame.allocator->Reset());
        Check(commands->Reset(frame.allocator.Get(), nullptr));
        recording = true;
        boundViewHeap = boundSamplerHeap = nullptr;
        defaultTargetBound = false;
        targetIndex = swapchain ? swapchain->GetCurrentBackBufferIndex() : 0;
        // Establish the implicit presentation target while the command list
        // is otherwise empty. Guest draws commonly leave their color target
        // unset to select this backbuffer. Older Intel D3D12 UMDs can fault
        // if that first OM bind instead occurs after graphics state setup.
        // Explicit guest attachments still replace this binding in DrawProgram.
        Transition(targets[targetIndex], D3D12_RESOURCE_STATE_RENDER_TARGET);
        auto rtv = rtvHeap->GetCPUDescriptorHandleForHeapStart();
        rtv.ptr += targetIndex * rtvStride;
        TraceD3D12("commands:default-om-begin");
        commands->OMSetRenderTargets(1, &rtv, FALSE, nullptr);
        TraceD3D12("commands:default-om-end");
        defaultTargetBound = true;
    }

    void TrimUploadPages(Frame& frame)
    {
        UINT64 released = 0;
        for (const auto& page : frame.uploads) released += page.capacity;
        frame.uploads.clear();
        Require(uploadBytes >= released, E_FAIL);
        uploadBytes -= released;
    }

    // Readback staging is reused across synchronous reads instead of
    // creating a committed READBACK resource per call. Every user waits for
    // its copy before returning and all callers hold context.mutex, so one
    // grow-only buffer plus one side allocator/list is enough.
    static constexpr UINT64 ReadbackPoolMinBytes = 64ull * 1024;
    static constexpr UINT64 ReadbackPoolMaxBytes = 64ull * 1024 * 1024;
    ComPtr<ID3D12Resource> readbackPool;
    UINT64 readbackPoolBytes = 0;
    ComPtr<ID3D12CommandAllocator> sideAllocator;
    ComPtr<ID3D12GraphicsCommandList> sideList;

    ComPtr<ID3D12Resource> AcquireReadback(UINT64 bytes)
    {
        if (bytes > ReadbackPoolMaxBytes)
        {
            ComPtr<ID3D12Resource> oneShot;
            auto heap = Heap(D3D12_HEAP_TYPE_READBACK);
            auto desc = BufferDesc(bytes);
            Check(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc,
                D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&oneShot)));
            return oneShot;
        }
        if (!readbackPool || readbackPoolBytes < bytes)
        {
            UINT64 capacity = ReadbackPoolMinBytes;
            while (capacity < bytes) capacity <<= 1;
            readbackPool.Reset();
            readbackPoolBytes = 0;
            auto heap = Heap(D3D12_HEAP_TYPE_READBACK);
            auto desc = BufferDesc(capacity);
            Check(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc,
                D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&readbackPool)));
            readbackPoolBytes = capacity;
        }
        return readbackPool;
    }

    UINT64 TrimReadbackPool()
    {
        // Safe: synchronous readers finished before releasing context.mutex.
        // Frames may still retain a reference; the memory goes when they drop it.
        UINT64 released = readbackPoolBytes;
        readbackPool.Reset();
        readbackPoolBytes = 0;
        return released;
    }

    UINT64 TrimIdleUploads()
    {
        // Called under context.mutex. Only slots whose submitted work has
        // completed are released; the current slot may be recording.
        if (!fence) return 0;
        UINT64 completed = fence->GetCompletedValue();
        if (completed == UINT64_MAX) return 0;
        UINT64 before = uploadBytes;
        for (UINT i = 0; i < FrameCount; ++i)
            if (i != frameIndex && frames[i].fence <= completed) TrimUploadPages(frames[i]);
        return before - uploadBytes + TrimReadbackPool();
    }

    static bool IsPlanarDepthStencil(DXGI_FORMAT format)
    {
        switch (format)
        {
        case DXGI_FORMAT_R24G8_TYPELESS:
        case DXGI_FORMAT_D24_UNORM_S8_UINT:
        case DXGI_FORMAT_R24_UNORM_X8_TYPELESS:
        case DXGI_FORMAT_X24_TYPELESS_G8_UINT:
        case DXGI_FORMAT_R32G8X24_TYPELESS:
        case DXGI_FORMAT_D32_FLOAT_S8X24_UINT:
        case DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS:
        case DXGI_FORMAT_X32_TYPELESS_G8X24_UINT:
            return true;
        default:
            return false;
        }
    }

    void EnsureSubresourceStates(Resource* resource)
    {
        if (!resource || !resource->subresourceStates.empty()) return;
        UINT planeCount = IsPlanarDepthStencil(resource->desc.Format) ? 2 : 1;
        UINT arraySize = (resource->desc.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE3D) ? 1 : (resource->desc.DepthOrArraySize ? resource->desc.DepthOrArraySize : 1);
        UINT mipLevels = resource->desc.MipLevels ? resource->desc.MipLevels : 1;
        UINT total = (resource->desc.Dimension == D3D12_RESOURCE_DIMENSION_BUFFER) ? 1 : (mipLevels * arraySize * planeCount);
        resource->subresourceStates.assign(total, resource->state);
    }

    void TransitionSubresource(const std::shared_ptr<Resource>& resource, UINT subresource, D3D12_RESOURCE_STATES next)
    {
        Require(recording, HRESULT_FROM_WIN32(ERROR_INVALID_STATE));
        if (resource->retainedSubmission != submitted + 1)
        {
            frames[frameIndex].retained.push_back(resource);
            resource->retainedSubmission = submitted + 1;
        }
        if (resource->needsAliasingBarrier)
        {
            D3D12_RESOURCE_BARRIER alias{};
            alias.Type = D3D12_RESOURCE_BARRIER_TYPE_ALIASING;
            alias.Aliasing.pResourceAfter = resource->object.Get();
            commands->ResourceBarrier(1, &alias);
            resource->needsAliasingBarrier = false;
        }
        EnsureSubresourceStates(resource.get());

        if (subresource == D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES)
        {
            bool allMatch = true;
            for (auto s : resource->subresourceStates) { if (s != next) { allMatch = false; break; } }
            if (allMatch) { resource->state = next; return; }

            bool allSame = true;
            D3D12_RESOURCE_STATES commonState = resource->subresourceStates[0];
            for (size_t i = 1; i < resource->subresourceStates.size(); ++i)
            {
                if (resource->subresourceStates[i] != commonState) { allSame = false; break; }
            }

            if (allSame)
            {
                if (commonState != next)
                {
                    D3D12_RESOURCE_BARRIER barrier{};
                    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
                    barrier.Transition.pResource = resource->object.Get();
                    barrier.Transition.StateBefore = commonState;
                    barrier.Transition.StateAfter = next;
                    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
                    commands->ResourceBarrier(1, &barrier);
                }
            }
            else
            {
                std::vector<D3D12_RESOURCE_BARRIER> barriers;
                for (UINT i = 0; i < static_cast<UINT>(resource->subresourceStates.size()); ++i)
                {
                    if (resource->subresourceStates[i] != next)
                    {
                        D3D12_RESOURCE_BARRIER b{};
                        b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
                        b.Transition.pResource = resource->object.Get();
                        b.Transition.StateBefore = resource->subresourceStates[i];
                        b.Transition.StateAfter = next;
                        b.Transition.Subresource = i;
                        barriers.push_back(b);
                    }
                }
                if (!barriers.empty())
                {
                    commands->ResourceBarrier(static_cast<UINT>(barriers.size()), barriers.data());
                }
            }
            for (auto& s : resource->subresourceStates) s = next;
            resource->state = next;
        }
        else
        {
            if (subresource >= resource->subresourceStates.size()) return;
            if (resource->subresourceStates[subresource] == next) return;

            D3D12_RESOURCE_BARRIER barrier{};
            barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            barrier.Transition.pResource = resource->object.Get();
            barrier.Transition.StateBefore = resource->subresourceStates[subresource];
            barrier.Transition.StateAfter = next;
            barrier.Transition.Subresource = subresource;
            commands->ResourceBarrier(1, &barrier);

            resource->subresourceStates[subresource] = next;
            bool allMatch = true;
            for (auto s : resource->subresourceStates) { if (s != next) { allMatch = false; break; } }
            if (allMatch) resource->state = next;
        }
    }

    void Transition(const std::shared_ptr<Resource>& resource, D3D12_RESOURCE_STATES next)
    {
        TransitionSubresource(resource, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, next);
    }

    void TransitionView(const TextureView& view, D3D12_RESOURCE_STATES next)
    {
        auto resource = view.resource;
        if (!resource) return;
        if (resource->desc.Dimension == D3D12_RESOURCE_DIMENSION_BUFFER)
        {
            Transition(resource, next);
            return;
        }
        EnsureSubresourceStates(resource.get());
        UINT mipLevels = resource->desc.MipLevels ? resource->desc.MipLevels : 1;
        UINT arraySize = (resource->desc.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE3D) ? 1 : (resource->desc.DepthOrArraySize ? resource->desc.DepthOrArraySize : 1);
        UINT planeCount = IsPlanarDepthStencil(resource->desc.Format) ? 2 : 1;

        // A D3D12 3D texture has one subresource per mip (and plane), not
        // one per W slice. FirstWSlice limits an RTV/UAV descriptor only; it
        // must never suppress the resource transition for that mip.
        UINT firstSlice = resource->desc.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE3D ? 0 : view.firstSlice;
        UINT sliceCount = resource->desc.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE3D ? 1 : view.sliceCount;

        if (view.firstMip == 0 && view.mipCount >= mipLevels && firstSlice == 0 && sliceCount >= arraySize)
        {
            Transition(resource, next);
            return;
        }

        for (UINT plane = 0; plane < planeCount; ++plane)
        {
            for (UINT slice = firstSlice; slice < firstSlice + sliceCount && slice < arraySize; ++slice)
            {
                for (UINT mip = view.firstMip; mip < view.firstMip + view.mipCount && mip < mipLevels; ++mip)
                {
                    UINT subresource = mip + slice * mipLevels + plane * mipLevels * arraySize;
                    TransitionSubresource(resource, subresource, next);
                }
            }
        }
    }

    void Begin(float r, float g, float b, float a)
    {
        Require(std::isfinite(r) && std::isfinite(g) && std::isfinite(b) && std::isfinite(a));
        BeginCommands();
        targetIndex = swapchain ? swapchain->GetCurrentBackBufferIndex() : 0;
        Transition(targets[targetIndex], D3D12_RESOURCE_STATE_RENDER_TARGET);
        auto rtv = rtvHeap->GetCPUDescriptorHandleForHeapStart();
        rtv.ptr += targetIndex * rtvStride;
        const float color[] = {r,g,b,a};
        commands->OMSetRenderTargets(1, &rtv, FALSE, nullptr);
        defaultTargetBound = true;
        commands->ClearRenderTargetView(rtv, color, 0, nullptr);
    }

    UINT64 Submit(bool present, UINT interval)
    {
        LARGE_INTEGER submitStart{}, presentStart{}, presentEnd{}, submitEnd{};
        QueryPerformanceCounter(&submitStart);
        Require(recording, HRESULT_FROM_WIN32(ERROR_INVALID_STATE));
        Require(activeCounters==0,HRESULT_FROM_WIN32(ERROR_INVALID_STATE));
        Require(interval <= 1);
        TraceD3D12("submit:close-begin");
        HRESULT hrClose = commands->Close();
        if (FAILED(hrClose))
        {
            CheckDebug();
            HRESULT hrRemoved = device ? device->GetDeviceRemovedReason() : S_OK;
            std::string err = "commands->Close() failed: hr=" + std::to_string(hrClose) +
                              ", deviceRemoved=" + std::to_string(hrRemoved) +
                              ", frameIndex=" + std::to_string(frameIndex) +
                              ", submitted=" + std::to_string(submitted) +
                              ", completed=" + std::to_string(fence ? fence->GetCompletedValue() : 0) +
                              (programError.empty() ? "" : (", " + programError));
            CheckWithLocation(__FILE__, __LINE__, hrClose, err);
        }
        TraceD3D12("submit:execute-begin");
        recording = false;
        ID3D12CommandList* lists[] = { commands.Get() };
        queue->ExecuteCommandLists(1, lists);
        TraceD3D12("submit:execute-end");
        pendingUnfenced = true;
        TraceD3D12("submit:present-begin");
        QueryPerformanceCounter(&presentStart);
        const bool hasPresentTarget = present && swapchain != nullptr;
        HRESULT presentResult = hasPresentTarget ? swapchain->Present(interval, 0) : S_OK;
        QueryPerformanceCounter(&presentEnd);
        if (hasPresentTarget)
        {
            ++performanceStats.presentAttempts;
            performanceStats.lastPresentResult = presentResult;
            performanceStats.lastPresentWidth = width;
            performanceStats.lastPresentHeight = height;
            performanceStats.lastPresentQpc = static_cast<uint64_t>(presentEnd.QuadPart);
            if (SUCCEEDED(presentResult)) ++performanceStats.presentSuccess;
        }
        TraceD3D12("submit:present-end");
        const UINT64 value = submitted + 1;
        TraceD3D12("submit:signal-begin");
        Check(queue->Signal(fence.Get(), value));
        TraceD3D12("submit:signal-end");
        submitted = frames[frameIndex].fence = value;
        if (hasPresentTarget) performanceStats.lastPresentFence = value;
        pendingUnfenced = false;
        // D3D12 buffers decay to COMMON at ExecuteCommandLists boundaries,
        // including buffers explicitly transitioned on a DIRECT queue.
        for (auto& resource : frames[frameIndex].retained)
            if (resource->desc.Dimension == D3D12_RESOURCE_DIMENSION_BUFFER)
                resource->state = D3D12_RESOURCE_STATE_COMMON;
        QueryPerformanceCounter(&submitEnd);
        performanceStats.submitMicroseconds += ElapsedMicroseconds(submitStart, submitEnd);
        performanceStats.presentMicroseconds += ElapsedMicroseconds(presentStart, presentEnd);
        Check(presentResult);
        Check(device->GetDeviceRemovedReason());
        CheckDebug();
        return value;
    }

    UINT64 End(UINT interval)
    {
        Require(recording && interval <= 1);
        Transition(targets[targetIndex], D3D12_RESOURCE_STATE_COMMON); // PRESENT == COMMON (0)
        lastTarget = targetIndex;
        return Submit(true, interval);
    }

    struct UploadSlice { ID3D12Resource* object; BYTE* mapped; UINT64 offset; };
    UploadSlice Upload(UINT64 bytes, UINT64 alignment)
    {
        const UINT64 budget = UploadBudgetLimit();
        Require(recording, HRESULT_FROM_WIN32(ERROR_INVALID_STATE), "Upload requires an open command list.");
        Require(bytes && bytes <= budget, E_INVALIDARG,
            "Upload bytes=" + std::to_string(bytes) + "; budget=" + std::to_string(budget));
        auto& pages = frames[frameIndex].uploads;
        for (auto& page : pages)
        {
            UINT64 offset = Align(page.used, alignment);
            if (Fits(offset, bytes, page.capacity))
            {
                page.used = offset + bytes;
                return {page.object.Get(), page.mapped + offset, offset};
            }
        }
        UINT64 capacity = std::max(4 * MiB, Align(bytes, D3D12_DEFAULT_RESOURCE_PLACEMENT_ALIGNMENT));
        if (!Fits(uploadBytes, capacity, budget))
        {
            // The current slot already contains uploads referenced by commands
            // being recorded. Its previous fence does not cover those commands.
            // Only other slots whose submitted work completed can be released.
            UINT64 completed = fence->GetCompletedValue();
            if (completed == UINT64_MAX) Check(DXGI_ERROR_DEVICE_REMOVED);
            for (UINT i = 0; i < FrameCount; ++i)
            {
                if (i != frameIndex && frames[i].fence <= completed)
                    TrimUploadPages(frames[i]);
            }
        }
        // No copy/state mutation has happened yet. The GAL can submit, wait
        // for its fence and retry on a fresh list. Real allocation failures
        // remain fatal; only this bounded-pool pressure is recoverable.
        Require(Fits(uploadBytes, capacity, budget), HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_QUOTA),
            "Upload pool pressure: reserved=" + std::to_string(uploadBytes) +
            "; requested=" + std::to_string(capacity) + "; budget=" + std::to_string(budget));
        UploadPage page;
        auto heap = Heap(D3D12_HEAP_TYPE_UPLOAD);
        auto desc = BufferDesc(capacity);
        Check(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc,
            D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&page.object)));
        D3D12_RANGE noRead{0,0};
        Check(page.object->Map(0, &noRead, reinterpret_cast<void**>(&page.mapped)));
        page.capacity = capacity; page.used = bytes;
        pages.push_back(page);
        uploadBytes += capacity;
        return {page.object.Get(), page.mapped, 0};
    }

    std::shared_ptr<Resource> GetResource(UINT64 id)
    {
        auto it = resources.find(id);
        if (it != resources.end()) return it->second;
        auto view = textureViews.find(id);
        Require(view != textureViews.end());
        return view->second.resource;
    }

    std::shared_ptr<Resource> Get(UINT64 id, D3D12_RESOURCE_DIMENSION dimension)
    {
        auto resource = GetResource(id);
        Require(resource->desc.Dimension == dimension);
        return resource;
    }

    TextureView GetTextureView(UINT64 id)
    {
        auto view = textureViews.find(id);
        if (view != textureViews.end()) return view->second;
        auto resource = GetResource(id);
        Require(resource->desc.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE2D ||
            resource->desc.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE3D);
        TextureView result{};
        result.resource = resource;
        result.mipCount = resource->desc.MipLevels;
        result.sliceCount = resource->desc.DepthOrArraySize;
        result.format = resource->sampleFormat != DXGI_FORMAT_UNKNOWN ? resource->sampleFormat : resource->desc.Format;
        return result;
    }

    DXGI_FORMAT ResolveRenderTargetFormat(UINT64 id)
    {
        if (id == 0)
        {
            Require(targetIndex < FrameCount);
            return targets[targetIndex]->desc.Format;
        }
        auto view = GetTextureView(id);
        auto resource = view.resource;
        DXGI_FORMAT rtvFormat = view.format;
        if (rtvFormat == DXGI_FORMAT_UNKNOWN || IsTypelessFormat(rtvFormat))
        {
            rtvFormat = resource->sampleFormat != DXGI_FORMAT_UNKNOWN ? resource->sampleFormat : GetDefaultTypedFormat(resource->desc.Format);
        }
        return rtvFormat;
    }

    static UINT MipSize(UINT value, UINT mip) { return std::max(1u, value >> mip); }

    static UINT FormatBytes(DXGI_FORMAT format)
    {
        switch (format)
        {
        case DXGI_FORMAT_R8_TYPELESS:
        case DXGI_FORMAT_R8_UNORM:
        case DXGI_FORMAT_R8_UINT:
        case DXGI_FORMAT_R8_SNORM:
        case DXGI_FORMAT_R8_SINT:
        case DXGI_FORMAT_A8_UNORM:
            return 1;

        case DXGI_FORMAT_R8G8_TYPELESS:
        case DXGI_FORMAT_R8G8_UNORM:
        case DXGI_FORMAT_R8G8_UINT:
        case DXGI_FORMAT_R8G8_SNORM:
        case DXGI_FORMAT_R8G8_SINT:
        case DXGI_FORMAT_R16_TYPELESS:
        case DXGI_FORMAT_R16_FLOAT:
        case DXGI_FORMAT_D16_UNORM:
        case DXGI_FORMAT_R16_UNORM:
        case DXGI_FORMAT_R16_UINT:
        case DXGI_FORMAT_R16_SNORM:
        case DXGI_FORMAT_R16_SINT:
        case DXGI_FORMAT_B5G6R5_UNORM:
        case DXGI_FORMAT_B5G5R5A1_UNORM:
            return 2;

        case DXGI_FORMAT_R8G8B8A8_TYPELESS:
        case DXGI_FORMAT_R8G8B8A8_UNORM:
        case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
        case DXGI_FORMAT_R8G8B8A8_UINT:
        case DXGI_FORMAT_R8G8B8A8_SNORM:
        case DXGI_FORMAT_R8G8B8A8_SINT:
        case DXGI_FORMAT_B8G8R8A8_TYPELESS:
        case DXGI_FORMAT_B8G8R8A8_UNORM:
        case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        case DXGI_FORMAT_B8G8R8X8_TYPELESS:
        case DXGI_FORMAT_B8G8R8X8_UNORM:
        case DXGI_FORMAT_B8G8R8X8_UNORM_SRGB:
        case DXGI_FORMAT_R10G10B10A2_TYPELESS:
        case DXGI_FORMAT_R10G10B10A2_UNORM:
        case DXGI_FORMAT_R10G10B10A2_UINT:
        case DXGI_FORMAT_R11G11B10_FLOAT:
        case DXGI_FORMAT_R16G16_TYPELESS:
        case DXGI_FORMAT_R16G16_FLOAT:
        case DXGI_FORMAT_R16G16_UNORM:
        case DXGI_FORMAT_R16G16_UINT:
        case DXGI_FORMAT_R16G16_SNORM:
        case DXGI_FORMAT_R16G16_SINT:
        case DXGI_FORMAT_R32_TYPELESS:
        case DXGI_FORMAT_D32_FLOAT:
        case DXGI_FORMAT_R32_FLOAT:
        case DXGI_FORMAT_R32_UINT:
        case DXGI_FORMAT_R32_SINT:
        case DXGI_FORMAT_R24G8_TYPELESS:
        case DXGI_FORMAT_D24_UNORM_S8_UINT:
        case DXGI_FORMAT_R24_UNORM_X8_TYPELESS:
        case DXGI_FORMAT_X24_TYPELESS_G8_UINT:
        case DXGI_FORMAT_R9G9B9E5_SHAREDEXP:
            return 4;

        case DXGI_FORMAT_R16G16B16A16_TYPELESS:
        case DXGI_FORMAT_R16G16B16A16_FLOAT:
        case DXGI_FORMAT_R16G16B16A16_UNORM:
        case DXGI_FORMAT_R16G16B16A16_UINT:
        case DXGI_FORMAT_R16G16B16A16_SNORM:
        case DXGI_FORMAT_R16G16B16A16_SINT:
        case DXGI_FORMAT_R32G32_TYPELESS:
        case DXGI_FORMAT_R32G32_FLOAT:
        case DXGI_FORMAT_R32G32_UINT:
        case DXGI_FORMAT_R32G32_SINT:
        case DXGI_FORMAT_R32G8X24_TYPELESS:
        case DXGI_FORMAT_D32_FLOAT_S8X24_UINT:
        case DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS:
        case DXGI_FORMAT_X32_TYPELESS_G8X24_UINT:
            return 8;

        case DXGI_FORMAT_R32G32B32A32_TYPELESS:
        case DXGI_FORMAT_R32G32B32A32_FLOAT:
        case DXGI_FORMAT_R32G32B32A32_UINT:
        case DXGI_FORMAT_R32G32B32A32_SINT:
            return 16;

        default:
            return 0;
        }
    }

    void UploadBuffer(UINT64 id, UINT64 offset, const void* data, UINT64 size)
    {
        auto resource = Get(id, D3D12_RESOURCE_DIMENSION_BUFFER);
        Require(data && size && Fits(offset, size, resource->desc.Width));
        auto slice = Upload(size, 16);
        memcpy(slice.mapped, data, static_cast<size_t>(size));
        Transition(resource, D3D12_RESOURCE_STATE_COPY_DEST);
        commands->CopyBufferRegion(resource->object.Get(), offset, slice.object, slice.offset, size);
    }

    void EnsureByteCopyPipeline()
    {
        if (byteCopyPipeline) return;
        static constexpr char source[] = R"(
ByteAddressBuffer sourceBuffer : register(t0);
RWByteAddressBuffer destinationBuffer : register(u0);
cbuffer CopyConstants : register(b0) { uint sourceOffset; uint destinationOffset; uint byteCount; };
[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint destinationWord = (destinationOffset & ~3u) + id.x * 4u;
    uint destinationEnd = destinationOffset + byteCount;
    if (destinationWord >= destinationEnd) return;
    uint value = destinationBuffer.Load(destinationWord);
    [unroll] for (uint i = 0; i < 4; ++i)
    {
        uint position = destinationWord + i;
        if (position < destinationOffset || position >= destinationEnd) continue;
        uint sourcePosition = sourceOffset + position - destinationOffset;
        uint sourceValue = sourceBuffer.Load(sourcePosition & ~3u);
        uint sourceByte = (sourceValue >> ((sourcePosition & 3u) * 8u)) & 0xffu;
        uint mask = 0xffu << (i * 8u);
        value = (value & ~mask) | (sourceByte << (i * 8u));
    }
    destinationBuffer.Store(destinationWord, value);
}
)";
        ComPtr<ID3DBlob> shader, error;
        HRESULT result = D3DCompile(source, sizeof(source) - 1, "RyuboxD3D12ByteCopy", nullptr, nullptr,
            "main", "cs_5_1", D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &shader, &error);
        if (FAILED(result) && error) OutputDebugStringA(static_cast<const char*>(error->GetBufferPointer()));
        Check(result);
        D3D12_COMPUTE_PIPELINE_STATE_DESC desc{};
        desc.pRootSignature = computeRoot.Get();
        desc.CS = {shader->GetBufferPointer(), shader->GetBufferSize()};
        Check(device->CreateComputePipelineState(&desc, IID_PPV_ARGS(&byteCopyPipeline)));
    }

    void ByteCopy(const std::shared_ptr<Resource>& src, const std::shared_ptr<Resource>& dst,
        UINT64 from, UINT64 to, UINT64 bytes)
    {
        EnsureByteCopyPipeline();
        auto dispatch = [&](const std::shared_ptr<Resource>& input, const std::shared_ptr<Resource>& output,
            UINT64 inputOffset, UINT64 outputOffset, UINT64 count)
        {
            Transition(input, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            Transition(output, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            commands->SetComputeRootSignature(computeRoot.Get());
            commands->SetPipelineState(byteCopyPipeline.Get());
            commands->SetComputeRootShaderResourceView(0, input->object->GetGPUVirtualAddress());
            commands->SetComputeRootUnorderedAccessView(1, output->object->GetGPUVirtualAddress());
            UINT constants[16]{};
            constants[0] = static_cast<UINT>(inputOffset);
            constants[1] = static_cast<UINT>(outputOffset);
            constants[2] = static_cast<UINT>(count);
            commands->SetComputeRoot32BitConstants(2, 16, constants, 0);
            UINT firstWord = static_cast<UINT>(outputOffset / 4);
            UINT lastWord = static_cast<UINT>((outputOffset + count + 3) / 4);
            UINT words = lastWord - firstWord;
            commands->Dispatch((words + 63) / 64, 1, 1);
            D3D12_RESOURCE_BARRIER barrier{};
            barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
            barrier.UAV.pResource = output->object.Get();
            commands->ResourceBarrier(1, &barrier);
        };

        if (src != dst)
        {
            dispatch(src, dst, from, to, bytes);
            return;
        }

        // Overlapping self-copies must observe the original bytes. The
        // temporary is filled by the shader too, so unaligned guest ranges do
        // not reintroduce CopyBufferRegion's alignment restriction.
        auto temporaryDesc = BufferDesc(Align(bytes + 3, 4));
        temporaryDesc.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        auto temporary = Allocate(temporaryDesc);
        frames[frameIndex].retained.push_back(temporary);
        dispatch(src, temporary, from, 0, bytes);
        dispatch(temporary, src, 0, to, bytes);
    }

    void CopyBuffer(UINT64 source, UINT64 destination, UINT64 from, UINT64 to, UINT64 bytes)
    {
        auto src = Get(source, D3D12_RESOURCE_DIMENSION_BUFFER);
        auto dst = Get(destination, D3D12_RESOURCE_DIMENSION_BUFFER);
        Require(bytes && Fits(from, bytes, src->desc.Width) && Fits(to, bytes, dst->desc.Width));
        if ((from | to | bytes) & 3)
        {
            ByteCopy(src, dst, from, to, bytes);
            return;
        }
        if (source == destination)
        {
            // CopyBufferRegion does not permit overlapping ranges of the same
            // resource. The guest DMA engine does, including self-copies used
            // by buffer-cache compaction. Stage through a temporary default
            // heap resource and retain it until the submitting fence.
            auto temporary = Allocate(BufferDesc(bytes));
            frames[frameIndex].retained.push_back(temporary);
            Transition(src, D3D12_RESOURCE_STATE_COPY_SOURCE);
            Transition(temporary, D3D12_RESOURCE_STATE_COPY_DEST);
            commands->CopyBufferRegion(temporary->object.Get(), 0, src->object.Get(), from, bytes);
            Transition(temporary, D3D12_RESOURCE_STATE_COPY_SOURCE);
            Transition(src, D3D12_RESOURCE_STATE_COPY_DEST);
            commands->CopyBufferRegion(src->object.Get(), to, temporary->object.Get(), 0, bytes);
            return;
        }
        Transition(src, D3D12_RESOURCE_STATE_COPY_SOURCE);
        Transition(dst, D3D12_RESOURCE_STATE_COPY_DEST);
        commands->CopyBufferRegion(dst->object.Get(), to, src->object.Get(), from, bytes);
    }

    UINT64 CreateCompute(const char* source, UINT64 bytes)
    {
        Require(source && bytes && bytes <= 4 * MiB && computePipelines.size() < 256);
        ComPtr<ID3DBlob> shader, error;
        HRESULT result = D3DCompile(source, static_cast<SIZE_T>(bytes), "RyuboxD3D12Compute", nullptr, nullptr,
            "main", "cs_5_1", D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &shader, &error);
        if (FAILED(result) && error) OutputDebugStringA(static_cast<const char*>(error->GetBufferPointer()));
        Check(result);
        D3D12_COMPUTE_PIPELINE_STATE_DESC desc{};
        desc.pRootSignature = computeRoot.Get();
        desc.CS = {shader->GetBufferPointer(),shader->GetBufferSize()};
        ComPtr<ID3D12PipelineState> pipeline;
        Check(device->CreateComputePipelineState(&desc,IID_PPV_ARGS(&pipeline)));
        UINT64 id = nextResource++;
        computePipelines.emplace(id,pipeline);
        return id;
    }

    void Dispatch(UINT64 pipeline, UINT64 source, UINT64 destination, const UINT* constants, UINT count,
        UINT x, UINT y, UINT z)
    {
        Require(recording && source != destination && count <= 16 && (!count || constants));
        Require(x && y && z && x <= 65535 && y <= 65535 && z <= 65535);
        auto found = computePipelines.find(pipeline);
        Require(found != computePipelines.end());
        auto src = Get(source,D3D12_RESOURCE_DIMENSION_BUFFER);
        auto dst = Get(destination,D3D12_RESOURCE_DIMENSION_BUFFER);
        Require(src->desc.Width >= 4 && dst->desc.Width >= 4);
        frames[frameIndex].pipelines.push_back(found->second);
        Transition(src,D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        Transition(dst,D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        commands->SetComputeRootSignature(computeRoot.Get());
        commands->SetPipelineState(found->second.Get());
        commands->SetComputeRootShaderResourceView(0,src->object->GetGPUVirtualAddress());
        commands->SetComputeRootUnorderedAccessView(1,dst->object->GetGPUVirtualAddress());
        UINT values[16]{};
        if (count) memcpy(values,constants,count*sizeof(UINT));
        commands->SetComputeRoot32BitConstants(2,16,values,0);
        commands->Dispatch(x,y,z);
        D3D12_RESOURCE_BARRIER barrier{};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        barrier.UAV.pResource = dst->object.Get();
        commands->ResourceBarrier(1,&barrier);
    }

    #include "D3D12Program.inl"
    #include "D3D12Gal.inl"

    static DXGI_FORMAT ResolveCopyFormat(DXGI_FORMAT resourceFormat, DXGI_FORMAT requestedViewFormat = DXGI_FORMAT_UNKNOWN, UINT plane = 0)
    {
        switch (resourceFormat)
        {
        case DXGI_FORMAT_R24G8_TYPELESS:
        case DXGI_FORMAT_D24_UNORM_S8_UINT:
        case DXGI_FORMAT_R24_UNORM_X8_TYPELESS:
        case DXGI_FORMAT_X24_TYPELESS_G8_UINT:
            return (plane == 0) ? DXGI_FORMAT_R32_TYPELESS : DXGI_FORMAT_R8_TYPELESS;

        case DXGI_FORMAT_R32G8X24_TYPELESS:
        case DXGI_FORMAT_D32_FLOAT_S8X24_UINT:
        case DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS:
        case DXGI_FORMAT_X32_TYPELESS_G8X24_UINT:
            return (plane == 0) ? DXGI_FORMAT_R32_TYPELESS : DXGI_FORMAT_R8_TYPELESS;

        case DXGI_FORMAT_R32_TYPELESS:
            if (requestedViewFormat != DXGI_FORMAT_UNKNOWN && requestedViewFormat != DXGI_FORMAT_R32_TYPELESS)
                return requestedViewFormat;
            return DXGI_FORMAT_R32_UINT;

        case DXGI_FORMAT_D32_FLOAT:
            return DXGI_FORMAT_R32_FLOAT;

        case DXGI_FORMAT_R16_TYPELESS:
            if (requestedViewFormat != DXGI_FORMAT_UNKNOWN && requestedViewFormat != DXGI_FORMAT_R16_TYPELESS)
                return requestedViewFormat;
            return DXGI_FORMAT_R16_UNORM;

        case DXGI_FORMAT_D16_UNORM:
            return DXGI_FORMAT_R16_UNORM;

        case DXGI_FORMAT_R8_TYPELESS:
            return (requestedViewFormat != DXGI_FORMAT_UNKNOWN && requestedViewFormat != DXGI_FORMAT_R8_TYPELESS)
                ? requestedViewFormat : DXGI_FORMAT_R8_UNORM;

        case DXGI_FORMAT_R8G8_TYPELESS:
            return (requestedViewFormat != DXGI_FORMAT_UNKNOWN && requestedViewFormat != DXGI_FORMAT_R8G8_TYPELESS)
                ? requestedViewFormat : DXGI_FORMAT_R8G8_UNORM;

        case DXGI_FORMAT_R8G8B8A8_TYPELESS:
            return (requestedViewFormat != DXGI_FORMAT_UNKNOWN && requestedViewFormat != DXGI_FORMAT_R8G8B8A8_TYPELESS)
                ? requestedViewFormat : DXGI_FORMAT_R8G8B8A8_UNORM;

        case DXGI_FORMAT_B8G8R8A8_TYPELESS:
            return (requestedViewFormat != DXGI_FORMAT_UNKNOWN && requestedViewFormat != DXGI_FORMAT_B8G8R8A8_TYPELESS)
                ? requestedViewFormat : DXGI_FORMAT_B8G8R8A8_UNORM;

        case DXGI_FORMAT_B8G8R8X8_TYPELESS:
            return (requestedViewFormat != DXGI_FORMAT_UNKNOWN && requestedViewFormat != DXGI_FORMAT_B8G8R8X8_TYPELESS)
                ? requestedViewFormat : DXGI_FORMAT_B8G8R8X8_UNORM;

        case DXGI_FORMAT_R10G10B10A2_TYPELESS:
            return (requestedViewFormat != DXGI_FORMAT_UNKNOWN && requestedViewFormat != DXGI_FORMAT_R10G10B10A2_TYPELESS)
                ? requestedViewFormat : DXGI_FORMAT_R10G10B10A2_UNORM;

        case DXGI_FORMAT_R16G16_TYPELESS:
            return (requestedViewFormat != DXGI_FORMAT_UNKNOWN && requestedViewFormat != DXGI_FORMAT_R16G16_TYPELESS)
                ? requestedViewFormat : DXGI_FORMAT_R16G16_UNORM;

        case DXGI_FORMAT_R16G16B16A16_TYPELESS:
            return (requestedViewFormat != DXGI_FORMAT_UNKNOWN && requestedViewFormat != DXGI_FORMAT_R16G16B16A16_TYPELESS)
                ? requestedViewFormat : DXGI_FORMAT_R16G16B16A16_FLOAT;

        case DXGI_FORMAT_R32G32_TYPELESS:
            return (requestedViewFormat != DXGI_FORMAT_UNKNOWN && requestedViewFormat != DXGI_FORMAT_R32G32_TYPELESS)
                ? requestedViewFormat : DXGI_FORMAT_R32G32_UINT;

        case DXGI_FORMAT_R32G32B32A32_TYPELESS:
            return (requestedViewFormat != DXGI_FORMAT_UNKNOWN && requestedViewFormat != DXGI_FORMAT_R32G32B32A32_TYPELESS)
                ? requestedViewFormat : DXGI_FORMAT_R32G32B32A32_UINT;

        default:
            return resourceFormat;
        }
    }

    struct D3D12UploadLayout
    {
        UINT subresourceIndex = 0;
        UINT64 offset = 0;
        UINT rowPitch = 0;
        UINT64 rowSizeInBytes = 0;
        UINT numRows = 0;
        UINT depth = 0;
        UINT64 totalBytes = 0;
        DXGI_FORMAT copyFormat = DXGI_FORMAT_UNKNOWN;
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{};
    };

    D3D12UploadLayout GetUploadLayout(const std::shared_ptr<Resource>& resource, UINT mip, UINT arraySlice, UINT planeSlice = 0, DXGI_FORMAT requestedViewFormat = DXGI_FORMAT_UNKNOWN)
    {
        D3D12UploadLayout layout{};
        UINT mipLevels = resource->desc.MipLevels;
        UINT arraySize = (resource->desc.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE3D) ? 1 : resource->desc.DepthOrArraySize;

        layout.subresourceIndex = mip + arraySlice * mipLevels + planeSlice * mipLevels * arraySize;
        layout.copyFormat = ResolveCopyFormat(resource->desc.Format, requestedViewFormat, planeSlice);

        D3D12_RESOURCE_DESC copyDesc = resource->desc;
        copyDesc.Format = layout.copyFormat;

        device->GetCopyableFootprints(
            &copyDesc,
            layout.subresourceIndex,
            1,
            0,
            &layout.footprint,
            &layout.numRows,
            &layout.rowSizeInBytes,
            &layout.totalBytes);

        layout.footprint.Footprint.Format = layout.copyFormat;
        layout.rowPitch = layout.footprint.Footprint.RowPitch;
        layout.depth = layout.footprint.Footprint.Depth;
        return layout;
    }

    void UploadTexture(UINT64 id, UINT mip, UINT x, UINT y, UINT z, UINT width, UINT height, UINT depth,
        const void* data, UINT64 bytes, UINT pitch, UINT depthPitch)
    {
        auto view = GetTextureView(id);
        auto resource = view.resource;
        Require(mip < view.mipCount, E_INVALIDARG, "UploadTexture: mip index out of range");

        UINT physicalMip = view.firstMip + mip;
        UINT mipWidth = MipSize(static_cast<UINT>(resource->desc.Width), physicalMip);
        UINT mipHeight = MipSize(resource->desc.Height, physicalMip);

        UINT arraySlice = 0;
        UINT destinationZ = 0;
        UINT mipDepth = 1;

        if (resource->desc.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE3D)
        {
            mipDepth = MipSize(resource->desc.DepthOrArraySize, physicalMip);
            destinationZ = view.firstSlice + z;
            arraySlice = 0;
            Require(data && width && height && depth && Fits(x, width, mipWidth) && Fits(y, height, mipHeight) &&
                Fits(destinationZ, depth, mipDepth), E_INVALIDARG, "UploadTexture: 3D coordinates or depth out of range");
        }
        else
        {
            arraySlice = view.firstSlice + z;
            destinationZ = 0;
            Require(data && width && height && depth && Fits(x, width, mipWidth) && Fits(y, height, mipHeight) &&
                arraySlice < resource->desc.DepthOrArraySize, E_INVALIDARG, "UploadTexture: 2D/Array coordinates or slice out of range");
        }

        DXGI_FORMAT copyFormat = ResolveCopyFormat(resource->desc.Format, view.format, 0);
        UINT formatBytes = FormatBytes(copyFormat);
        if (formatBytes == 0) formatBytes = FormatBytes(resource->desc.Format);

        UINT rowBytes = 0;
        UINT targetRowPitch = 0;
        UINT64 targetDepthPitch = 0;
        UINT64 total = 0;

        if (formatBytes > 0)
        {
            Require(width <= UINT_MAX / formatBytes, E_INVALIDARG, "UploadTexture: row overflow");
            rowBytes = width * formatBytes;
            targetRowPitch = static_cast<UINT>(Align(rowBytes, D3D12_TEXTURE_DATA_PITCH_ALIGNMENT));
            targetDepthPitch = static_cast<UINT64>(targetRowPitch) * height;
            total = targetDepthPitch * depth;
        }
        else
        {
            auto uploadLayout = GetUploadLayout(resource, physicalMip, arraySlice, 0, view.format);
            rowBytes = (width == mipWidth) ? static_cast<UINT>(uploadLayout.rowSizeInBytes)
                                           : static_cast<UINT>(uploadLayout.rowSizeInBytes * width / mipWidth);
            targetRowPitch = uploadLayout.rowPitch;
            targetDepthPitch = static_cast<UINT64>(targetRowPitch) * height;
            total = targetDepthPitch * depth;
        }

        Require(pitch >= rowBytes && depthPitch >= static_cast<UINT64>(pitch) * height &&
            bytes >= static_cast<UINT64>(depth - 1) * depthPitch + static_cast<UINT64>(height - 1) * pitch + rowBytes,
            E_INVALIDARG, "UploadTexture: source buffer smaller than required pitch/slice");

        D3D12_PLACED_SUBRESOURCE_FOOTPRINT layout{};
        layout.Footprint.Format = copyFormat;
        layout.Footprint.Width = width;
        layout.Footprint.Height = height;
        layout.Footprint.Depth = depth;
        layout.Footprint.RowPitch = targetRowPitch;

        auto slice = Upload(total, D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT);
        for (UINT sliceIndex = 0; sliceIndex < depth; ++sliceIndex)
        {
            for (UINT row = 0; row < height; ++row)
            {
                memcpy(slice.mapped + static_cast<UINT64>(sliceIndex) * targetDepthPitch +
                    static_cast<UINT64>(row) * layout.Footprint.RowPitch,
                    static_cast<const BYTE*>(data) + static_cast<UINT64>(sliceIndex) * depthPitch +
                    static_cast<UINT64>(row) * pitch, rowBytes);
            }
        }
        layout.Offset = slice.offset;

        UINT mipLevels = resource->desc.MipLevels;
        UINT subresourceIndex = physicalMip + arraySlice * mipLevels;

        D3D12_TEXTURE_COPY_LOCATION src{}, dst{};
        src.pResource = slice.object;
        src.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        src.PlacedFootprint = layout;
        dst.pResource = resource->object.Get();
        dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        dst.SubresourceIndex = subresourceIndex;

        TransitionSubresource(resource, subresourceIndex, D3D12_RESOURCE_STATE_COPY_DEST);
        commands->CopyTextureRegion(&dst, x, y, destinationZ, &src, nullptr);
    }

    void CreateBlitPipeline()
    {
        // The same SPIRV-Cross/HLSL translation can be shared with D3D11 later;
        // this presenter compiles its own SM5.1 shaders without a D3D11 device.
        const char* source = R"(
Texture2D<float4> image : register(t0);
SamplerState nearestClamp : register(s0);
cbuffer Crop : register(b0) { float4 crop; };
struct Vertex { float4 position : SV_Position; float2 uv : TEXCOORD0; };
Vertex VS(uint id : SV_VertexID) {
    Vertex v;
    float2 uv = float2((id << 1) & 2, id & 2);
    v.position = float4(uv.x * 2 - 1, 1 - uv.y * 2, 0, 1);
    v.uv = lerp(crop.xy, crop.zw, uv);
    return v;
}
float4 PS(Vertex v) : SV_Target { return image.Sample(nearestClamp, v.uv); }
)";
        ComPtr<ID3DBlob> vs, ps, error;
        Check(D3DCompile(source, strlen(source), "RyuboxD3D12Blit", nullptr, nullptr, "VS", "vs_5_1",
            D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &vs, &error));
        Check(D3DCompile(source, strlen(source), "RyuboxD3D12Blit", nullptr, nullptr, "PS", "ps_5_1",
            D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &ps, &error));
        D3D12_DESCRIPTOR_RANGE range{};
        range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        range.NumDescriptors = 1;
        D3D12_ROOT_PARAMETER parameters[2]{};
        parameters[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        parameters[0].DescriptorTable = {1, &range};
        parameters[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
        parameters[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        parameters[1].Constants = {0, 0, 4};
        parameters[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_VERTEX;
        D3D12_STATIC_SAMPLER_DESC sampler{};
        sampler.Filter = D3D12_FILTER_MIN_MAG_MIP_POINT;
        sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        sampler.MaxAnisotropy = 1;
        sampler.ComparisonFunc = D3D12_COMPARISON_FUNC_ALWAYS;
        sampler.MaxLOD = D3D12_FLOAT32_MAX;
        sampler.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
        D3D12_ROOT_SIGNATURE_DESC rootDesc{2, parameters, 1, &sampler,
            D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT};
        ComPtr<ID3DBlob> signature;
        Check(D3D12SerializeRootSignature(&rootDesc, D3D_ROOT_SIGNATURE_VERSION_1, &signature, &error));
        Check(device->CreateRootSignature(0, signature->GetBufferPointer(), signature->GetBufferSize(),
            IID_PPV_ARGS(&rootSignature)));
        D3D12_GRAPHICS_PIPELINE_STATE_DESC desc{};
        desc.pRootSignature = rootSignature.Get();
        desc.VS = {vs->GetBufferPointer(), vs->GetBufferSize()};
        desc.PS = {ps->GetBufferPointer(), ps->GetBufferSize()};
        auto& blend = desc.BlendState.RenderTarget[0];
        blend.SrcBlend = blend.SrcBlendAlpha = D3D12_BLEND_ONE;
        blend.DestBlend = blend.DestBlendAlpha = D3D12_BLEND_ZERO;
        blend.BlendOp = blend.BlendOpAlpha = D3D12_BLEND_OP_ADD;
        blend.LogicOp = D3D12_LOGIC_OP_NOOP;
        blend.RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
        desc.SampleMask = UINT_MAX;
        desc.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID;
        desc.RasterizerState.CullMode = D3D12_CULL_MODE_NONE;
        desc.RasterizerState.DepthClipEnable = TRUE;
        desc.DepthStencilState.DepthFunc = D3D12_COMPARISON_FUNC_ALWAYS;
        desc.DepthStencilState.FrontFace = desc.DepthStencilState.BackFace = {
            D3D12_STENCIL_OP_KEEP, D3D12_STENCIL_OP_KEEP, D3D12_STENCIL_OP_KEEP, D3D12_COMPARISON_FUNC_ALWAYS};
        desc.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        desc.NumRenderTargets = 1;
        desc.RTVFormats[0] = DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.SampleDesc.Count = 1;
        Check(device->CreateGraphicsPipelineState(&desc, IID_PPV_ARGS(&blitPipeline)));
    }

    void DrawTexture(UINT64 id, float left, float top, float right, float bottom, UINT flip)
    {
        Require(recording && flip <= 1);
        Require(std::isfinite(left) && std::isfinite(top) && std::isfinite(right) && std::isfinite(bottom) &&
            left >= 0 && top >= 0 && right <= 1 && bottom <= 1 && right > left && bottom > top);
        auto resource = Get(id, D3D12_RESOURCE_DIMENSION_TEXTURE2D);
        auto slice = AllocateDescriptors(false, 1);
        UINT offset = slice.offset * srvStride;
        auto cpu = slice.heap->GetCPUDescriptorHandleForHeapStart(); cpu.ptr += offset;
        auto gpu = slice.heap->GetGPUDescriptorHandleForHeapStart(); gpu.ptr += offset;
        D3D12_SHADER_RESOURCE_VIEW_DESC view{};
        view.Format = resource->sampleFormat!=DXGI_FORMAT_UNKNOWN ? resource->sampleFormat : resource->desc.Format;
        view.ViewDimension = resource->desc.SampleDesc.Count > 1 ?
            D3D12_SRV_DIMENSION_TEXTURE2DMS : D3D12_SRV_DIMENSION_TEXTURE2D;
        view.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        if (resource->desc.SampleDesc.Count == 1) view.Texture2D.MipLevels = 1;
        device->CreateShaderResourceView(resource->object.Get(), &view, cpu);
        Transition(resource, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        auto output=TargetView(0);
        commands->OMSetRenderTargets(1,&output,FALSE,nullptr);
        defaultTargetBound = true;
        BindDescriptorHeaps(slice.heap, nullptr);
        commands->SetGraphicsRootSignature(rootSignature.Get());
        commands->SetPipelineState(blitPipeline.Get());
        commands->SetGraphicsRootDescriptorTable(0, gpu);
        const float crop[] = {left, flip ? bottom : top, right, flip ? top : bottom};
        commands->SetGraphicsRoot32BitConstants(1, 4, crop, 0);
        D3D12_VIEWPORT viewport{0, 0, static_cast<float>(width), static_cast<float>(height), 0, 1};
        D3D12_RECT rect{0, 0, static_cast<LONG>(width), static_cast<LONG>(height)};
        commands->RSSetViewports(1, &viewport);
        commands->RSSetScissorRects(1, &rect);
        commands->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        commands->DrawInstanced(3, 1, 0, 0);
    }

    void Resize(UINT w, UINT h)
    {
        ValidateSize(w, h);
        Require(!recording, HRESULT_FROM_WIN32(ERROR_INVALID_STATE));
        if (w == width && h == height) return;
        Wait(submitted);
        for (auto& frame : frames) frame.retained.clear();
        for (auto& target : targets) target.reset();
        // Clear the closed list's stale backbuffer references before ResizeBuffers.
        Check(commands->Reset(frames[frameIndex].allocator.Get(), nullptr));
        Check(commands->Close());
        if (swapchain) Check(swapchain->ResizeBuffers(FrameCount, w, h, DXGI_FORMAT_R8G8B8A8_UNORM, 0));
        width = w; height = h;
        CreateTargets();
    }

    void Read(const std::shared_ptr<Resource>& resource, UINT64 offset, void* destination, UINT64 size, bool texture)
    {
        Require(!recording && destination && size, HRESULT_FROM_WIN32(ERROR_INVALID_STATE));
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT layout{};
        UINT64 total = size;
        UINT rows = 0;
        UINT64 rowBytes = 0;
        if (texture)
        {
            device->GetCopyableFootprints(&resource->desc, 0, 1, 0, &layout, &rows, &rowBytes, &total);
            Require(total != UINT64_MAX && rows && size >= rowBytes * rows);
        }
        else Require(Fits(offset, size, resource->desc.Width));
        // The copy is submitted to the SAME direct queue as preceding writes.
        // Queue ordering plus the copy's fence below already makes the CPU
        // read safe. Waiting for all earlier work here adds a CPU/GPU bubble
        // before even recording the readback command. BeginCommands still
        // waits before reusing a frame's allocator/upload storage.
        ComPtr<ID3D12Resource> readback = AcquireReadback(total);
        BeginCommands();
        auto readbackOwner = std::make_shared<Resource>();
        readbackOwner->object = readback;
        frames[frameIndex].retained.push_back(readbackOwner);
        auto previous = resource->state;
        Transition(resource, D3D12_RESOURCE_STATE_COPY_SOURCE);
        if (texture)
        {
            D3D12_TEXTURE_COPY_LOCATION src{}, dst{};
            src.pResource = resource->object.Get(); src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
            dst.pResource = readback.Get(); dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
            dst.PlacedFootprint = layout;
            commands->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
        }
        else commands->CopyBufferRegion(readback.Get(), 0, resource->object.Get(), offset, size);
        Transition(resource, previous);
        Wait(Submit(false, 0));
        BYTE* mapped = nullptr;
        D3D12_RANGE range{0, static_cast<SIZE_T>(total)};
        Check(readback->Map(0, &range, reinterpret_cast<void**>(&mapped)));
        if (texture)
        {
            UINT64 pitch = rowBytes;
            for (UINT y = 0; y < rows; ++y)
                memcpy(static_cast<BYTE*>(destination) + y * pitch,
                    mapped + layout.Offset + y * static_cast<UINT64>(layout.Footprint.RowPitch), static_cast<size_t>(pitch));
        }
        else memcpy(destination, mapped, static_cast<size_t>(size));
        D3D12_RANGE noWrite{0,0};
        readback->Unmap(0, &noWrite);
    }

    // Guest-memory callbacks may run on a CPU thread while the GPU owner has
    // an unsubmitted command list open. Read only the last submitted contents:
    // buffers decay to COMMON after each DIRECT-list submission, so a separate
    // DIRECT list can implicitly promote the source for this copy. The pending
    // graphics list is neither submitted nor reset here.
    void ReadSubmittedBuffer(const std::shared_ptr<Resource>& resource, UINT64 offset,
        void* destination, UINT64 size)
    {
        Require(destination && size && resource->desc.Dimension == D3D12_RESOURCE_DIMENSION_BUFFER);
        Require(Fits(offset, size, resource->desc.Width));
        if (!recording)
        {
            Read(resource, offset, destination, size, false);
            return;
        }

        ComPtr<ID3D12Resource> readback = AcquireReadback(size);

        // The side allocator is reused: the previous side copy was waited on
        // before its caller returned, so Reset is legal here.
        if (!sideAllocator)
        {
            Check(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&sideAllocator)));
            Check(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, sideAllocator.Get(),
                nullptr, IID_PPV_ARGS(&sideList)));
        }
        else
        {
            Check(sideAllocator->Reset());
            Check(sideList->Reset(sideAllocator.Get(), nullptr));
        }
        auto& list = sideList;
        list->CopyBufferRegion(readback.Get(), 0, resource->object.Get(), offset, size);
        Check(list->Close());
        ID3D12CommandList* lists[] = {list.Get()};
        queue->ExecuteCommandLists(1, lists);
        // Advance the same fence timeline as Submit. The active graphics list
        // will receive the next value when its owner eventually submits it.
        UINT64 value = submitted + 1;
        Check(queue->Signal(fence.Get(), value));
        submitted = value;
        Wait(value);

        BYTE* mapped = nullptr;
        D3D12_RANGE range{0, static_cast<SIZE_T>(size)};
        Check(readback->Map(0, &range, reinterpret_cast<void**>(&mapped)));
        memcpy(destination, mapped, static_cast<size_t>(size));
        D3D12_RANGE noWrite{0, 0};
        readback->Unmap(0, &noWrite);
    }

    void ReadTexture(UINT64 id, UINT mip, void* destination, UINT64 size, UINT rowPitch, UINT depthPitch)
    {
        Require(!recording && destination && size, HRESULT_FROM_WIN32(ERROR_INVALID_STATE));
        auto view = GetTextureView(id);
        auto resource = view.resource;
        Require(mip < view.mipCount);
        UINT physicalMip = view.firstMip + mip;
        UINT width = MipSize(static_cast<UINT>(resource->desc.Width), physicalMip);
        UINT height = MipSize(resource->desc.Height, physicalMip);

        UINT arraySlice = 0;
        UINT firstSlice = 0;
        UINT depth = 1;
        UINT physicalDepth = 1;

        if (resource->desc.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE3D)
        {
            physicalDepth = MipSize(resource->desc.DepthOrArraySize, physicalMip);
            firstSlice = view.firstSlice;
            depth = std::min(MipSize(view.sliceCount, mip), physicalDepth - std::min(firstSlice, physicalDepth));
            arraySlice = 0;
        }
        else
        {
            arraySlice = view.firstSlice;
            firstSlice = 0;
            depth = 1;
        }

        DXGI_FORMAT copyFormat = ResolveCopyFormat(resource->desc.Format, view.format, 0);
        UINT formatBytes = FormatBytes(copyFormat);
        if (formatBytes == 0) formatBytes = FormatBytes(resource->desc.Format);
        Require(formatBytes > 0 && width <= UINT_MAX / formatBytes);
        UINT rowBytes = width * formatBytes;
        Require(depth && rowPitch >= rowBytes && depthPitch >= static_cast<UINT64>(rowPitch) * height &&
            size >= static_cast<UINT64>(depth - 1) * depthPitch + static_cast<UINT64>(height - 1) * rowPitch + rowBytes);

        D3D12_PLACED_SUBRESOURCE_FOOTPRINT layout{};
        layout.Footprint.Format = copyFormat;
        layout.Footprint.Width = width;
        layout.Footprint.Height = height;
        layout.Footprint.Depth = depth;
        layout.Footprint.RowPitch = static_cast<UINT>(Align(rowBytes, D3D12_TEXTURE_DATA_PITCH_ALIGNMENT));
        UINT64 nativeDepthPitch = static_cast<UINT64>(layout.Footprint.RowPitch) * height;
        UINT64 total = nativeDepthPitch * depth;
        ComPtr<ID3D12Resource> readback = AcquireReadback(total);

        if (resource->desc.SampleDesc.Count > 1)
        {
            Require(resource->depthFormat == DXGI_FORMAT_UNKNOWN, E_NOTIMPL,
                "Depth/stencil MSAA readback is not supported.");
            DXGI_FORMAT resolveFormat = ResolveRenderTargetFormat(id);
            if (resolveFormat == DXGI_FORMAT_UNKNOWN || IsTypelessFormat(resolveFormat)) resolveFormat = copyFormat;
            if (resolveFormat == DXGI_FORMAT_UNKNOWN || IsTypelessFormat(resolveFormat)) resolveFormat = resource->desc.Format;
            Require(resolveFormat != DXGI_FORMAT_UNKNOWN && !IsTypelessFormat(resolveFormat),
                E_INVALIDARG, "MSAA readback requires typed resolve format");

            auto tempDesc = TextureDesc(width, height, false);
            tempDesc.Format = resolveFormat;
            tempDesc.MipLevels = 1;
            tempDesc.DepthOrArraySize = 1;
            auto heapDefault = Heap(D3D12_HEAP_TYPE_DEFAULT);
            ComPtr<ID3D12Resource> resolveTemp;
            Check(device->CreateCommittedResource(&heapDefault, D3D12_HEAP_FLAG_NONE, &tempDesc,
                D3D12_RESOURCE_STATE_RESOLVE_DEST, nullptr, IID_PPV_ARGS(&resolveTemp)));

            BeginCommands();
            auto resolveOwner = std::make_shared<Resource>();
            resolveOwner->object = resolveTemp;
            frames[frameIndex].retained.push_back(resolveOwner);

            auto readbackOwner = std::make_shared<Resource>();
            readbackOwner->object = readback;
            frames[frameIndex].retained.push_back(readbackOwner);

            auto previous = resource->state;
            Transition(resource, D3D12_RESOURCE_STATE_RESOLVE_SOURCE);
            UINT subresourceIndex = physicalMip + arraySlice * resource->desc.MipLevels;
            commands->ResolveSubresource(resolveTemp.Get(), 0, resource->object.Get(), subresourceIndex, resolveFormat);
            Transition(resource, previous);

            D3D12_RESOURCE_BARRIER barrier{};
            barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            barrier.Transition.pResource = resolveTemp.Get();
            barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RESOLVE_DEST;
            barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
            barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            commands->ResourceBarrier(1, &barrier);

            D3D12_TEXTURE_COPY_LOCATION src{}, dst{};
            src.pResource = resolveTemp.Get();
            src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
            src.SubresourceIndex = 0;
            dst.pResource = readback.Get();
            dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
            dst.PlacedFootprint = layout;
            D3D12_BOX box{0, 0, 0, width, height, 1};
            commands->CopyTextureRegion(&dst, 0, 0, 0, &src, &box);
            Wait(Submit(false, 0));
        }
        else
        {
            BeginCommands();
            auto readbackOwner = std::make_shared<Resource>();
            readbackOwner->object = readback;
            frames[frameIndex].retained.push_back(readbackOwner);
            auto previous = resource->state;
            Transition(resource, D3D12_RESOURCE_STATE_COPY_SOURCE);

            UINT mipLevels = resource->desc.MipLevels;
            UINT subresourceIndex = physicalMip + arraySlice * mipLevels;

            D3D12_TEXTURE_COPY_LOCATION src{}, dst{};
            src.pResource = resource->object.Get();
            src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
            src.SubresourceIndex = subresourceIndex;
            dst.pResource = readback.Get();
            dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
            dst.PlacedFootprint = layout;
            D3D12_BOX box{0, 0, firstSlice, width, height, firstSlice + depth};
            commands->CopyTextureRegion(&dst, 0, 0, 0, &src, &box);
            Transition(resource, previous);
            Wait(Submit(false, 0));
        }
        BYTE* mapped = nullptr;
        D3D12_RANGE range{0, static_cast<SIZE_T>(total)};
        Check(readback->Map(0, &range, reinterpret_cast<void**>(&mapped)));
        for (UINT slice = 0; slice < depth; ++slice)
            for (UINT row = 0; row < height; ++row)
                memcpy(static_cast<BYTE*>(destination) + static_cast<UINT64>(slice) * depthPitch +
                    static_cast<UINT64>(row) * rowPitch,
                    mapped + static_cast<UINT64>(slice) * nativeDepthPitch +
                    static_cast<UINT64>(row) * layout.Footprint.RowPitch, rowBytes);
        D3D12_RANGE noWrite{0,0};
        readback->Unmap(0, &noWrite);
    }
};

// No C++ exception may cross the P/Invoke boundary. A device error poisons
// the instance; callers recreate it instead of reusing invalid queue state.
template<class F> HRESULT Invoke(void* pointer, F action, bool allowFault = false)
{
    if (!pointer) return E_POINTER;
    auto& context = *static_cast<Context*>(pointer);
    std::lock_guard<std::mutex> guard(context.mutex);
    if (!allowFault && FAILED(context.fault)) return context.fault;
    try { action(context); return S_OK; }
    catch (const Failure& failure)
    {
        char hrBuf[32];
        snprintf(hrBuf, sizeof(hrBuf), "0x%08X", static_cast<unsigned int>(failure.result));
        std::string message = std::string(failure.operation) + " failed at " +
            failure.source + ":" + std::to_string(failure.line) +
            "; HRESULT=" + hrBuf;
        if (context.device && (failure.result == DXGI_ERROR_DEVICE_REMOVED ||
                               failure.result == DXGI_ERROR_DEVICE_HUNG ||
                               failure.result == DXGI_ERROR_DEVICE_RESET))
        {
            HRESULT removedReason = context.device->GetDeviceRemovedReason();
            char reasonBuf[32];
            snprintf(reasonBuf, sizeof(reasonBuf), "0x%08X", static_cast<unsigned int>(removedReason));
            message += "; DeviceRemovedReason=" + std::string(reasonBuf);
        }
        if (!failure.detail.empty())
        {
            message += "; Detail: " + failure.detail;
        }

        context.lastDiagnostic = message;
        if (context.programError.empty())
        {
            context.programError = message;
        }

        char debugLine[512];
        snprintf(debugLine, sizeof(debugLine), "[RyuboxD3D12] %s\n", message.c_str());
        OutputDebugStringA(debugLine);

        // Recoverable errors must NOT poison the entire D3D12 context!
        bool isRecoverable = (failure.result == DXGI_ERROR_UNSUPPORTED ||
                              failure.result == E_INVALIDARG ||
                              failure.result == E_POINTER ||
                              failure.result == HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_QUOTA) ||
                              failure.result == HRESULT_FROM_WIN32(ERROR_INVALID_STATE));

        if (!isRecoverable)
        {
            context.fault = failure.result;
        }
        return failure.result;
    }
    catch (const std::bad_alloc&)
    {
        context.lastDiagnostic = "Out of memory (bad_alloc)";
        context.fault = E_OUTOFMEMORY;
        return E_OUTOFMEMORY;
    }
    catch (const std::exception& ex)
    {
        context.lastDiagnostic = std::string("Standard exception: ") + ex.what();
        context.fault = E_FAIL;
        return E_FAIL;
    }
    catch (...)
    {
        context.lastDiagnostic = "Unknown native exception";
        context.fault = E_FAIL;
        return E_FAIL;
    }
}
}

RYUBOX_D12 RyuboxD3D12_Create(HWND window, IUnknown* panel, uint32_t width, uint32_t height, uint32_t flags, void** result)
{
    if (!result) return E_POINTER;
    *result = nullptr;
    try
    {
        auto context = std::make_unique<Context>();
        context->Initialize(window, panel, width, height, flags);
        *result = context.release();
        return S_OK;
    }
    catch (const Failure& failure) { return failure.result; }
    catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}
RYUBOX_D12 RyuboxD3D12_Destroy(void* context)
{
    if (!context) return S_OK;
    HRESULT result = Invoke(context, [](Context& c)
    {
        if (SUCCEEDED(c.device->GetDeviceRemovedReason()))
        {
            if (c.pendingUnfenced)
            {
                // A submission may have succeeded before Signal failed. It
                // must also be covered by a fence before storage is released.
                const UINT64 value = c.submitted + 1;
                Check(c.queue->Signal(c.fence.Get(), value));
                c.submitted = value;
                c.pendingUnfenced = false;
            }
            c.Wait(c.submitted);
        }
    }, true);
    if (FAILED(result))
    {
        // A timeout does not prove the GPU stopped using these allocations.
        // Keep the instance alive for a native retry instead of freeing active
        // command allocators/resources and provoking a driver crash.
        OutputDebugStringA("Ryubox D3D12 cleanup incomplete: retaining context until GPU completion.\n");
        return result;
    }
    delete static_cast<Context*>(context);
    return S_OK;
}
RYUBOX_D12 RyuboxD3D12_GetInfo(void* context, RyuboxD3D12Info* info)
{
    if (!info || info->size != sizeof(*info)) return E_INVALIDARG;
    return Invoke(context, [&](Context& c)
    {
        c.CheckDebug();
        DXGI_ADAPTER_DESC1 adapter{}; Check(c.adapter->GetDesc1(&adapter));
        D3D12_FEATURE_DATA_D3D12_OPTIONS options{};
        Check(c.device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS, &options, sizeof(options)));
        D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_12_1, D3D_FEATURE_LEVEL_12_0, D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
        D3D12_FEATURE_DATA_FEATURE_LEVELS feature{4, levels, D3D_FEATURE_LEVEL_11_0};
        Check(c.device->CheckFeatureSupport(D3D12_FEATURE_FEATURE_LEVELS, &feature, sizeof(feature)));
        *info = {sizeof(*info), 1, adapter.VendorId, adapter.DeviceId, static_cast<UINT>(feature.MaxSupportedFeatureLevel),
            static_cast<UINT>(options.ResourceBindingTier), c.width, c.height, c.submitted, c.fence->GetCompletedValue(),
            c.LiveBytes(), c.uploadBytes, c.waits, c.device->GetDeviceRemovedReason(), c.debugErrors};
    }, true);
}
RYUBOX_D12 RyuboxD3D12_BeginFrame(void* context, float r, float g, float b, float a)
{ return Invoke(context, [&](Context& c) { c.Begin(r,g,b,a); }); }
HRESULT Ryujinx::Uwp::Native::PrepareD3D12UiDraw(void* context)
{
    return Invoke(context, [](Context& c) {
        Require(c.recording && c.activeCounters==0, HRESULT_FROM_WIN32(ERROR_INVALID_STATE));
        auto& frame=c.frames[c.frameIndex];
        // One UI draw uses projection CBV, font/cover SRV, sampler and RTV.
        if ((!c.descriptorRingEnabled && (frame.descriptorCount+2>DescriptorsPerFrame || frame.samplerCount+1>SamplersPerFrame)) || frame.rtvCount+1>DescriptorsPerFrame)
        {
            c.Submit(false,0);
            c.BeginCommands();
        }
    });
}
RYUBOX_D12 RyuboxD3D12_EndFrame(void* context, uint32_t interval, uint64_t* fence)
{
    if (!fence) return E_POINTER; *fence=0;
    // Do not hold the context mutex while ImGui emits native draw commands.
    HRESULT hr=Ryujinx::Uwp::Native::DrawD3D12FrontendOverlay(context);
    if (FAILED(hr)) return hr;
    bool presented=false;
    hr=Invoke(context, [&](Context& c) { *fence=c.End(interval); presented=c.swapchain != nullptr; });
    if (SUCCEEDED(hr) && presented) Ryujinx::Uwp::Native::NotifyD3D12FrontendPresent(context);
    return hr;
}
RYUBOX_D12 RyuboxD3D12_Wait(void* context, uint64_t fence)
{ return Invoke(context, [&](Context& c) { c.Wait(fence); }); }
RYUBOX_D12 RyuboxD3D12_Resize(void* context, uint32_t width, uint32_t height)
{ return Invoke(context, [&](Context& c) { c.Resize(width,height); }); }
RYUBOX_D12 RyuboxD3D12_SetCompositionScale(void* context, float x, float y)
{
    return Invoke(context, [&](Context& c)
    {
        Require(c.composition && std::isfinite(x) && std::isfinite(y) && x > 0 && y > 0);
        DXGI_MATRIX_3X2_F matrix{1/x,0,0,1/y,0,0};
        Check(c.swapchain->SetMatrixTransform(&matrix));
    });
}
RYUBOX_D12 RyuboxD3D12_CreateBuffer(void* context, uint64_t bytes, uint64_t* result)
{
    if (!result) return E_POINTER; *result = 0;
    return Invoke(context, [&](Context& c) { Require(bytes && bytes <= ResourceBudgetLimit()); auto desc = BufferDesc(bytes);
        desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS; auto resource = c.Allocate(desc);
        UINT64 id = c.nextResource++; c.resources.emplace(id, resource); *result = id; });
}
RYUBOX_D12 RyuboxD3D12_RetainBuffer(void* context, uint64_t source, uint64_t* result)
{
    if (!result) return E_POINTER; *result = 0;
    return Invoke(context, [&](Context& c) {
        auto resource = c.GetResource(source);
        Require(resource->desc.Dimension == D3D12_RESOURCE_DIMENSION_BUFFER);
        UINT64 id = c.nextResource++;
        c.resources.emplace(id, resource);
        *result = id;
    });
}
RYUBOX_D12 RyuboxD3D12_CreateComputePipeline(void* context, const char* hlsl, uint64_t bytes, uint64_t* pipeline)
{
    if (!pipeline) return E_POINTER; *pipeline = 0;
    return Invoke(context,[&](Context& c) { *pipeline = c.CreateCompute(hlsl,bytes); });
}
RYUBOX_D12 RyuboxD3D12_ReleaseComputePipeline(void* context, uint64_t pipeline)
{ return Invoke(context,[&](Context& c) { Require(c.computePipelines.erase(pipeline) == 1); }, true); }
RYUBOX_D12 RyuboxD3D12_Dispatch(void* context, uint64_t pipeline, uint64_t source, uint64_t destination,
    const uint32_t* constants, uint32_t count, uint32_t x, uint32_t y, uint32_t z)
{ return Invoke(context,[&](Context& c) { c.Dispatch(pipeline,source,destination,constants,count,x,y,z); }); }
RYUBOX_D12 RyuboxD3D12_CreateTextureRgba8(void* context, uint32_t width, uint32_t height, uint64_t* result)
{
    if (!result) return E_POINTER; *result = 0;
    return Invoke(context, [&](Context& c) { c.ValidateSize(width,height); auto desc=TextureDesc(width,height,true);
        desc.Flags|=D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS; auto resource = c.Allocate(desc);
        UINT64 id = c.nextResource++; c.resources.emplace(id, resource); *result = id; });
}
RYUBOX_D12 RyuboxD3D12_CreateColorTextureArray(void* context, uint32_t width, uint32_t height,
    uint32_t layers, uint32_t mips, uint32_t format, uint32_t samples, uint64_t* result)
{
    if (!result) return E_POINTER; *result = 0;
    return Invoke(context, [&](Context& c)
    {
        c.Validate2DSize(width, height, layers, mips);
        Require(samples == 1 || samples == 2 || samples == 4 || samples == 8, E_INVALIDARG, "Unsupported sample count");
        if (samples > 1) Require(mips == 1, E_INVALIDARG, "Multisample texture cannot have mipmaps");
        switch (static_cast<DXGI_FORMAT>(format))
        {
        case DXGI_FORMAT_R8_UNORM: case DXGI_FORMAT_R8_SNORM: case DXGI_FORMAT_R8_UINT: case DXGI_FORMAT_R8_SINT:
        case DXGI_FORMAT_R8G8_UNORM: case DXGI_FORMAT_R8G8_SNORM: case DXGI_FORMAT_R8G8_UINT: case DXGI_FORMAT_R8G8_SINT:
        case DXGI_FORMAT_R8G8B8A8_UNORM: case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
        case DXGI_FORMAT_R8G8B8A8_SNORM: case DXGI_FORMAT_R8G8B8A8_UINT: case DXGI_FORMAT_R8G8B8A8_SINT:
        case DXGI_FORMAT_B8G8R8A8_UNORM: case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        case DXGI_FORMAT_R16_UNORM: case DXGI_FORMAT_R16_SNORM: case DXGI_FORMAT_R16_UINT: case DXGI_FORMAT_R16_SINT:
        case DXGI_FORMAT_R16_FLOAT:
        case DXGI_FORMAT_R16G16_UNORM: case DXGI_FORMAT_R16G16_SNORM: case DXGI_FORMAT_R16G16_UINT: case DXGI_FORMAT_R16G16_SINT:
        case DXGI_FORMAT_R16G16_FLOAT:
        case DXGI_FORMAT_R16G16B16A16_UNORM: case DXGI_FORMAT_R16G16B16A16_SNORM: case DXGI_FORMAT_R16G16B16A16_UINT: case DXGI_FORMAT_R16G16B16A16_SINT:
        case DXGI_FORMAT_R16G16B16A16_FLOAT:
        case DXGI_FORMAT_R32_FLOAT: case DXGI_FORMAT_R32_UINT: case DXGI_FORMAT_R32_SINT:
        case DXGI_FORMAT_R32G32_FLOAT: case DXGI_FORMAT_R32G32_UINT: case DXGI_FORMAT_R32G32_SINT:
        case DXGI_FORMAT_R32G32B32A32_FLOAT: case DXGI_FORMAT_R32G32B32A32_UINT: case DXGI_FORMAT_R32G32B32A32_SINT:
        case DXGI_FORMAT_R10G10B10A2_UNORM: case DXGI_FORMAT_R10G10B10A2_UINT:
        case DXGI_FORMAT_R11G11B10_FLOAT:
        case DXGI_FORMAT_R9G9B9E5_SHAREDEXP:
            break;
        default: Require(false, DXGI_ERROR_UNSUPPORTED, "CreateColorTextureArray: unsupported DXGI format");
        }
        D3D12_FEATURE_DATA_FORMAT_SUPPORT support{static_cast<DXGI_FORMAT>(format)};
        Check(c.device->CheckFeatureSupport(D3D12_FEATURE_FORMAT_SUPPORT, &support, sizeof(support)));
        constexpr UINT shaderRead = D3D12_FORMAT_SUPPORT1_SHADER_LOAD | D3D12_FORMAT_SUPPORT1_SHADER_SAMPLE;
        Require((support.Support1 & D3D12_FORMAT_SUPPORT1_TEXTURE2D) != 0 && (support.Support1 & shaderRead) != 0,
            DXGI_ERROR_UNSUPPORTED, "CreateColorTextureArray: missing format support");
        if (samples > 1)
        {
            D3D12_FEATURE_DATA_MULTISAMPLE_QUALITY_LEVELS levels{support.Format, samples, D3D12_MULTISAMPLE_QUALITY_LEVELS_FLAG_NONE, 0};
            Check(c.device->CheckFeatureSupport(D3D12_FEATURE_MULTISAMPLE_QUALITY_LEVELS, &levels, sizeof(levels)));
            Require(levels.NumQualityLevels > 0, DXGI_ERROR_UNSUPPORTED, "CreateColorTextureArray: MSAA quality levels unsupported");
        }
        DXGI_FORMAT storageFormat = GetTypelessFormat(support.Format);
        bool renderTarget = (support.Support1 & D3D12_FORMAT_SUPPORT1_RENDER_TARGET) != 0;
        auto desc = Texture2DDesc(width, height, layers, mips, storageFormat, renderTarget, samples);
        if (samples == 1 && (support.Support1 & D3D12_FORMAT_SUPPORT1_TYPED_UNORDERED_ACCESS_VIEW) &&
            (support.Support2 & D3D12_FORMAT_SUPPORT2_UAV_TYPED_STORE))
            desc.Flags |= D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        auto resource = c.Allocate(desc);
        resource->sampleFormat = support.Format;
        UINT64 id = c.nextResource++; c.resources.emplace(id, resource); *result = id;
    });
}
RYUBOX_D12 RyuboxD3D12_CreateColorTexture(void* context, uint32_t width, uint32_t height, uint32_t format, uint32_t samples, uint64_t* result)
{
    return RyuboxD3D12_CreateColorTextureArray(context, width, height, 1, 1, format, samples, result);
}
RYUBOX_D12 RyuboxD3D12_CreateColorTexture3D(void* context, uint32_t width, uint32_t height, uint32_t depth,
    uint32_t mips, uint32_t format, uint64_t* result)
{
    if (!result) return E_POINTER; *result=0;
    return Invoke(context,[&](Context& c)
    {
        c.Validate3DSize(width,height,depth,mips);
        switch (static_cast<DXGI_FORMAT>(format))
        {
        case DXGI_FORMAT_R8_UNORM: case DXGI_FORMAT_R8_SNORM: case DXGI_FORMAT_R8_UINT: case DXGI_FORMAT_R8_SINT:
        case DXGI_FORMAT_R8G8_UNORM: case DXGI_FORMAT_R8G8_SNORM: case DXGI_FORMAT_R8G8_UINT: case DXGI_FORMAT_R8G8_SINT:
        case DXGI_FORMAT_R8G8B8A8_UNORM: case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
        case DXGI_FORMAT_R8G8B8A8_SNORM: case DXGI_FORMAT_R8G8B8A8_UINT: case DXGI_FORMAT_R8G8B8A8_SINT:
        case DXGI_FORMAT_B8G8R8A8_UNORM: case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        case DXGI_FORMAT_R16_UNORM: case DXGI_FORMAT_R16_SNORM: case DXGI_FORMAT_R16_UINT: case DXGI_FORMAT_R16_SINT:
        case DXGI_FORMAT_R16_FLOAT:
        case DXGI_FORMAT_R16G16_UNORM: case DXGI_FORMAT_R16G16_SNORM: case DXGI_FORMAT_R16G16_UINT: case DXGI_FORMAT_R16G16_SINT:
        case DXGI_FORMAT_R16G16_FLOAT:
        case DXGI_FORMAT_R16G16B16A16_UNORM: case DXGI_FORMAT_R16G16B16A16_SNORM: case DXGI_FORMAT_R16G16B16A16_UINT: case DXGI_FORMAT_R16G16B16A16_SINT:
        case DXGI_FORMAT_R16G16B16A16_FLOAT:
        case DXGI_FORMAT_R32_FLOAT: case DXGI_FORMAT_R32_UINT: case DXGI_FORMAT_R32_SINT:
        case DXGI_FORMAT_R32G32_FLOAT: case DXGI_FORMAT_R32G32_UINT: case DXGI_FORMAT_R32G32_SINT:
        case DXGI_FORMAT_R32G32B32A32_FLOAT: case DXGI_FORMAT_R32G32B32A32_UINT: case DXGI_FORMAT_R32G32B32A32_SINT:
        case DXGI_FORMAT_R10G10B10A2_UNORM: case DXGI_FORMAT_R10G10B10A2_UINT:
        case DXGI_FORMAT_R11G11B10_FLOAT:
        case DXGI_FORMAT_R9G9B9E5_SHAREDEXP:
            break;
        default: Require(false, DXGI_ERROR_UNSUPPORTED);
        }
        D3D12_FEATURE_DATA_FORMAT_SUPPORT support{static_cast<DXGI_FORMAT>(format)};
        Check(c.device->CheckFeatureSupport(D3D12_FEATURE_FORMAT_SUPPORT,&support,sizeof(support)));
        constexpr UINT shaderRead = D3D12_FORMAT_SUPPORT1_SHADER_LOAD | D3D12_FORMAT_SUPPORT1_SHADER_SAMPLE;
        Require((support.Support1 & D3D12_FORMAT_SUPPORT1_TEXTURE3D) != 0 && (support.Support1 & shaderRead) != 0, DXGI_ERROR_UNSUPPORTED);
        D3D12_RESOURCE_FLAGS flags=D3D12_RESOURCE_FLAG_NONE;
        if(support.Support1&D3D12_FORMAT_SUPPORT1_RENDER_TARGET) flags|=D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
        if((support.Support1&D3D12_FORMAT_SUPPORT1_TYPED_UNORDERED_ACCESS_VIEW) &&
           (support.Support2&D3D12_FORMAT_SUPPORT2_UAV_TYPED_STORE)) flags|=D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        DXGI_FORMAT storageFormat = GetTypelessFormat(support.Format);
        auto resource=c.Allocate(Texture3DDesc(width,height,depth,mips,storageFormat,flags));
        resource->sampleFormat = support.Format;
        UINT64 id=c.nextResource++; c.resources.emplace(id,resource); *result=id;
    });
}
RYUBOX_D12 RyuboxD3D12_CreateTextureView(void* context, uint64_t source, uint32_t firstMip,
    uint32_t mipCount, uint32_t firstSlice, uint32_t sliceCount, uint64_t* result)
{
    return RyuboxD3D12_CreateTextureViewWithFormat(context, source, firstMip, mipCount, firstSlice, sliceCount, 0, result);
}
RYUBOX_D12 RyuboxD3D12_CreateTextureViewWithFormat(void* context, uint64_t source, uint32_t firstMip,
    uint32_t mipCount, uint32_t firstSlice, uint32_t sliceCount, uint32_t dxgiFormat, uint64_t* result)
{
    if(!result) return E_POINTER; *result=0;
    return Invoke(context,[&](Context& c)
    {
        auto parent=c.GetTextureView(source);
        Require(mipCount && sliceCount && firstMip+static_cast<UINT64>(mipCount)<=parent.mipCount);
        UINT absoluteMip=parent.firstMip+firstMip;
        auto resource=parent.resource;
        UINT availableSlices=resource->desc.Dimension==D3D12_RESOURCE_DIMENSION_TEXTURE3D
            ? Context::MipSize(resource->desc.DepthOrArraySize,absoluteMip) : resource->desc.DepthOrArraySize;
        UINT absoluteSlice=parent.firstSlice+firstSlice;
        Require(firstSlice+static_cast<UINT64>(sliceCount)<=parent.sliceCount &&
            absoluteSlice+static_cast<UINT64>(sliceCount)<=availableSlices);
        DXGI_FORMAT viewFormat = dxgiFormat != 0 ? static_cast<DXGI_FORMAT>(dxgiFormat) : parent.format;
        Require(AreFormatsInSameFamily(viewFormat, resource->desc.Format) ||
                (resource->sampleFormat != DXGI_FORMAT_UNKNOWN && AreFormatsInSameFamily(viewFormat, resource->sampleFormat)),
                E_INVALIDARG, "CreateTextureViewWithFormat: incompatible view format");
        TextureView view{resource, absoluteMip, mipCount, absoluteSlice, sliceCount, viewFormat, parent.componentMapping};
        UINT64 id=c.nextResource++; c.textureViews.emplace(id,std::move(view)); *result=id;
    });
}
RYUBOX_D12 RyuboxD3D12_ReleaseResource(void* context, uint64_t resource)
{ return Invoke(context, [&](Context& c) { Require(c.resources.erase(resource) + c.textureViews.erase(resource) == 1); }, true); }
RYUBOX_D12 RyuboxD3D12_UploadBuffer(void* context, uint64_t resource, uint64_t offset, const void* data, uint64_t bytes)
{ return Invoke(context, [&](Context& c) { c.UploadBuffer(resource,offset,data,bytes); }); }
RYUBOX_D12 RyuboxD3D12_CopyBuffer(void* context, uint64_t source, uint64_t destination, uint64_t from, uint64_t to, uint64_t bytes)
{ return Invoke(context, [&](Context& c) { c.CopyBuffer(source,destination,from,to,bytes); }); }
RYUBOX_D12 RyuboxD3D12_UploadTextureRgba8(void* context, uint64_t resource, const void* data, uint64_t bytes, uint32_t pitch)
{ return Invoke(context, [&](Context& c) { auto view=c.GetTextureView(resource); auto r=view.resource;
    c.UploadTexture(resource,0,0,0,0,static_cast<UINT>(r->desc.Width),r->desc.Height,1,data,bytes,pitch,pitch*r->desc.Height); }); }
RYUBOX_D12 RyuboxD3D12_UploadTextureRegion(void* context, uint64_t resource, uint32_t mip,
    uint32_t x, uint32_t y, uint32_t z, uint32_t width, uint32_t height, uint32_t depth,
    const void* data, uint64_t bytes, uint32_t rowPitch, uint32_t depthPitch)
{ return Invoke(context,[&](Context& c) { c.UploadTexture(resource,mip,x,y,z,width,height,depth,data,bytes,rowPitch,depthPitch); }); }
RYUBOX_D12 RyuboxD3D12_DrawTexture(void* context, uint64_t resource, float left, float top, float right, float bottom, uint32_t flip)
{ return Invoke(context, [&](Context& c) { c.DrawTexture(resource,left,top,right,bottom,flip); }); }
RYUBOX_D12 RyuboxD3D12_ReadBuffer(void* context, uint64_t resource, uint64_t offset, void* destination, uint64_t bytes)
{ return Invoke(context, [&](Context& c) { c.Read(c.Get(resource,D3D12_RESOURCE_DIMENSION_BUFFER),offset,destination,bytes,false); }); }
RYUBOX_D12 RyuboxD3D12_ReadSubmittedBuffer(void* context, uint64_t resource, uint64_t offset, void* destination, uint64_t bytes)
{ return Invoke(context, [&](Context& c) { c.ReadSubmittedBuffer(c.Get(resource,D3D12_RESOURCE_DIMENSION_BUFFER),offset,destination,bytes); }); }
RYUBOX_D12 RyuboxD3D12_Capture(void* context, void* destination, uint64_t bytes)
{ return Invoke(context, [&](Context& c) { Require(c.submitted > 0); c.Read(c.targets[c.lastTarget],0,destination,bytes,true); }); }
RYUBOX_D12 RyuboxD3D12_GetLastDiagnostic(void* context, char* data, uint32_t capacity)
{
    if (!data || !capacity) return E_INVALIDARG;
    return Invoke(context, [&](Context& ctx) {
        // Validation-layer messages are accumulated in programError by CheckDebug,
        // while failures raised through Invoke use lastDiagnostic. Report either one
        // to managed callers so a non-removal debug error is actionable.
        const std::string& diagnostic = ctx.lastDiagnostic.empty() ? ctx.programError : ctx.lastDiagnostic;
        size_t count = std::min(diagnostic.size(), static_cast<size_t>(capacity - 1));
        memcpy(data, diagnostic.data(), count);
        data[count] = 0;
    }, true);
}


RYUBOX_D12 RyuboxD3D12_CreateProgram(void* c, const RyuboxD12Shader* shaders, uint32_t count, uint64_t* id)
{ if (!id) return E_POINTER; *id = 0; return Invoke(c, [&](Context& ctx) { *id = ctx.CreateProgram(shaders,count); }); }
RYUBOX_D12 RyuboxD3D12_GetProgramError(void* c,char* data,uint32_t capacity)
{ return Invoke(c,[&](Context& ctx) { Require(data && capacity); ctx.CheckDebug(); size_t count=std::min(ctx.programError.size(),static_cast<size_t>(capacity-1)); memcpy(data,ctx.programError.data(),count); data[count]=0; },true); }
RYUBOX_D12 RyuboxD3D12_ReleaseProgram(void* c, uint64_t id)
{ return Invoke(c,[&](Context& ctx) { Require(ctx.programs.erase(id) == 1); },true); }
RYUBOX_D12 RyuboxD3D12_GetProgramBytecode(void* c,uint64_t id,uint32_t stage,void* data,uint64_t* bytes)
{
    if (!bytes) return E_POINTER;
    return Invoke(c,[&](Context& ctx) {
        Require(stage < 6);
        auto blob = ctx.GetProgram(id)->shaders[stage];
        Require(blob != nullptr);
        UINT64 size = blob->GetBufferSize();
        if (data) { Require(*bytes >= size); memcpy(data,blob->GetBufferPointer(),static_cast<size_t>(size)); }
        *bytes = size;
    });
}
RYUBOX_D12 RyuboxD3D12_GetResourceStats(void* c,uint64_t* data,uint32_t count)
{
    if (!data || count != 9) return E_INVALIDARG;
    return Invoke(c,[&](Context& ctx) {
        ctx.CollectChunks();
        data[0]=ctx.ReservedResourceBytes(); data[1]=ctx.memoryChunks.size();
        data[2]=ctx.placedAllocations; data[3]=ctx.committedAllocations;
        data[4]=ctx.heapCreations; data[5]=ResourceBudgetLimit();
        data[6]=ctx.shaderCompilations; data[7]=ctx.pipelineCreations;
        data[8]=ctx.LiveBytes();
    },true);
}
RYUBOX_D12 RyuboxD3D12_TrimIdleMemory(void* c, uint64_t* releasedBytes)
{
    return Invoke(c,[&](Context& ctx) {
        UINT64 released = ctx.TrimIdleUploads();
        ctx.CollectChunks();
        if (releasedBytes) *releasedBytes = released;
    });
}
RYUBOX_D12 RyuboxD3D12_GetDescriptorStats(void* c, RyuboxD3D12DescriptorStats* stats)
{
    if (!stats || stats->size != sizeof(*stats)) return E_INVALIDARG;
    return Invoke(c,[&](Context& ctx) {
        ctx.CollectDescriptors();
        *stats = ctx.descriptorStats;
        stats->size = sizeof(*stats); stats->version = 1;
        stats->ringEnabled = ctx.descriptorRingEnabled;
        stats->viewCapacity = ViewRingCapacity; stats->samplerCapacity = SamplerRingCapacity;
        stats->viewOccupied = ctx.viewPool.occupied; stats->samplerOccupied = ctx.samplerPool.occupied;
        stats->viewPeakOccupied = ctx.viewPool.peak; stats->samplerPeakOccupied = ctx.samplerPool.peak;
        stats->viewPages = ctx.viewPool.pages.size(); stats->samplerPages = ctx.samplerPool.pages.size();
        stats->cpuFenceWaits = ctx.waits;
    },true);
}
RYUBOX_D12 RyuboxD3D12_GetPerformanceStats(void* c, RyuboxD3D12PerformanceStats* stats)
{
    if (!stats || stats->size != sizeof(*stats)) return E_INVALIDARG;
    return Invoke(c,[&](Context& ctx) {
        *stats = ctx.performanceStats;
        stats->presentationTarget = ctx.swapchain ? (ctx.composition ? 2u : 1u) : 0u;
    },true);
}
RYUBOX_D12 RyuboxD3D12_GetProgramBindings(void* c, uint64_t id, RyuboxD12BindingInfo* data, uint32_t* count)
{
    if (!count) return E_POINTER;
    return Invoke(c,[&](Context& ctx) {
        auto p = ctx.GetProgram(id);
        if (data) { Require(*count >= p->bindings.size()); for (size_t i=0;i<p->bindings.size();++i) data[i]=p->bindings[i].info; }
        *count = static_cast<UINT>(p->bindings.size());
    });
}
RYUBOX_D12 RyuboxD3D12_DispatchProgram(void* c, uint64_t id, const RyuboxD12Binding* data, uint32_t count,
    uint32_t x, uint32_t y, uint32_t z)
{ return Invoke(c,[&](Context& ctx) { ctx.DispatchProgram(id,data,count,x,y,z); }); }
RYUBOX_D12 RyuboxD3D12_GetVertexInputs(void* c,uint64_t id,RyuboxD12VertexInput* data,uint32_t* count)
{
    if(!count) return E_POINTER;
    return Invoke(c,[&](Context& ctx) {
        auto p=ctx.GetProgram(id);
        if(data) { Require(*count>=p->inputs.size()); std::copy(p->inputs.begin(),p->inputs.end(),data); }
        *count=static_cast<UINT>(p->inputs.size());
    });
}
RYUBOX_D12 RyuboxD3D12_DrawProgram(void* c, uint64_t id, const RyuboxD12Binding* data, uint32_t count, const RyuboxD12Draw* draw)
{ if (!draw) return E_POINTER; return Invoke(c,[&](Context& ctx) { ctx.DrawProgram(id,data,count,*draw); }); }
RYUBOX_D12 RyuboxD3D12_DrawProgramDepth(void* c, uint64_t id, const RyuboxD12Binding* data, uint32_t count,
    const RyuboxD12Draw* draw, const RyuboxD12DepthState* depth)
{ if(!draw || !depth) return E_POINTER; return Invoke(c,[&](Context& ctx) { ctx.DrawProgram(id,data,count,*draw,depth); }); }
RYUBOX_D12 RyuboxD3D12_CreateDepthTextureArray(void* c, uint32_t width, uint32_t height,
    uint32_t layers, uint32_t mips, uint32_t format, uint32_t samples, uint64_t* result)
{
    if (!result) return E_POINTER; *result = 0;
    return Invoke(c, [&](Context& ctx)
    {
        ctx.Validate2DSize(width, height, layers, mips);
        Require(format == DXGI_FORMAT_D16_UNORM || format == DXGI_FORMAT_D32_FLOAT ||
                format == DXGI_FORMAT_D24_UNORM_S8_UINT || format == DXGI_FORMAT_D32_FLOAT_S8X24_UINT,
            E_INVALIDARG, "CreateDepthTextureArray: unsupported depth format");
        Require(samples == 1 || samples == 2 || samples == 4 || samples == 8, E_INVALIDARG, "Unsupported sample count");
        if (samples > 1) Require(mips == 1, E_INVALIDARG, "Multisample depth cannot have mipmaps");
        D3D12_FEATURE_DATA_FORMAT_SUPPORT support{static_cast<DXGI_FORMAT>(format)};
        Check(ctx.device->CheckFeatureSupport(D3D12_FEATURE_FORMAT_SUPPORT, &support, sizeof(support)));
        Require((support.Support1 & D3D12_FORMAT_SUPPORT1_DEPTH_STENCIL) != 0, DXGI_ERROR_UNSUPPORTED, "CreateDepthTextureArray: missing DSV support");
        if (samples > 1)
        {
            D3D12_FEATURE_DATA_MULTISAMPLE_QUALITY_LEVELS levels{static_cast<DXGI_FORMAT>(format), samples, D3D12_MULTISAMPLE_QUALITY_LEVELS_FLAG_NONE, 0};
            Check(ctx.device->CheckFeatureSupport(D3D12_FEATURE_MULTISAMPLE_QUALITY_LEVELS, &levels, sizeof(levels)));
            Require(levels.NumQualityLevels > 0, DXGI_ERROR_UNSUPPORTED, "CreateDepthTextureArray: MSAA quality levels unsupported");
        }
        auto desc = Texture2DDesc(width, height, layers, mips, DXGI_FORMAT_UNKNOWN, false, samples);
        DXGI_FORMAT sampleFormat = DXGI_FORMAT_UNKNOWN;
        switch (format)
        {
        case DXGI_FORMAT_D16_UNORM:
            desc.Format = DXGI_FORMAT_R16_TYPELESS;
            sampleFormat = DXGI_FORMAT_R16_UNORM;
            break;
        case DXGI_FORMAT_D24_UNORM_S8_UINT:
            desc.Format = DXGI_FORMAT_R24G8_TYPELESS;
            sampleFormat = DXGI_FORMAT_R24_UNORM_X8_TYPELESS;
            break;
        case DXGI_FORMAT_D32_FLOAT_S8X24_UINT:
            desc.Format = DXGI_FORMAT_R32G8X24_TYPELESS;
            sampleFormat = DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS;
            break;
        default:
            desc.Format = DXGI_FORMAT_R32_TYPELESS;
            sampleFormat = DXGI_FORMAT_R32_FLOAT;
            break;
        }
        desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL;
        auto resource = ctx.Allocate(desc);
        resource->depthFormat = static_cast<DXGI_FORMAT>(format);
        resource->sampleFormat = sampleFormat;
        UINT64 id = ctx.nextResource++; ctx.resources.emplace(id, resource); *result = id;
    });
}
RYUBOX_D12 RyuboxD3D12_CreateDepthTexture(void* c, uint32_t width, uint32_t height, uint32_t format, uint32_t samples, uint64_t* result)
{
    return RyuboxD3D12_CreateDepthTextureArray(c, width, height, 1, 1, format, samples, result);
}
RYUBOX_D12 RyuboxD3D12_ClearDepthTexture(void* c, uint64_t id, float value)
{
    return Invoke(c, [&](Context& ctx)
    {
        Require(std::isfinite(value) && value >= 0 && value <= 1);
        auto tView = ctx.GetTextureView(id);
        auto view = ctx.DepthView(tView.resource, &tView);
        ctx.commands->ClearDepthStencilView(view, D3D12_CLEAR_FLAG_DEPTH, value, 0, 0, nullptr);
    });
}
RYUBOX_D12 RyuboxD3D12_ConfigureStreamOutput(void* c,uint64_t id,uint32_t stage,const RyuboxD12SOElement* data,uint32_t count)
{ return Invoke(c,[&](Context& ctx) { ctx.ConfigureStreamOutput(id,stage,data,count); }); }

RYUBOX_D12 RyuboxD3D12_BeginCommands(void* c)
{ return Invoke(c,[](Context& ctx) { if (!ctx.recording) ctx.BeginCommands(); }); }
RYUBOX_D12 RyuboxD3D12_Flush(void* c,uint64_t* fence)
{ if(!fence) return E_POINTER; return Invoke(c,[&](Context& ctx) { *fence=ctx.recording ? ctx.Submit(false,0) : ctx.submitted; }); }
RYUBOX_D12 RyuboxD3D12_MemoryBarrier(void* c)
{ return Invoke(c,[](Context& ctx) {
    // A guest barrier may be the first command after a readback/present closed
    // the native list. Keep the barrier on the direct queue, in order, instead
    // of failing the emulation thread because the list is temporarily closed.
    if (!ctx.recording)
    {
        OutputDebugStringA("[RyuboxD3D12] MemoryBarrier reopened a closed command list.\n");
        ctx.BeginCommands();
    }
    D3D12_RESOURCE_BARRIER b{};
    b.Type=D3D12_RESOURCE_BARRIER_TYPE_UAV;
    ctx.commands->ResourceBarrier(1,&b);
}); }
RYUBOX_D12 RyuboxD3D12_ClearTexture(void* c,uint64_t id,const float* color)
{ if(!color) return E_POINTER; return Invoke(c,[&](Context& ctx) { auto view=ctx.TargetView(id); ctx.commands->ClearRenderTargetView(view,color,0,nullptr); }); }
RYUBOX_D12 RyuboxD3D12_CopyTexture(void* c,uint64_t src,uint64_t dst,uint32_t sx,uint32_t sy,uint32_t dx,uint32_t dy,uint32_t w,uint32_t h)
{ return Invoke(c,[&](Context& ctx) { ctx.CopyTexture(src,dst,0,0,sx,sy,0,dx,dy,0,w,h,1); }); }
RYUBOX_D12 RyuboxD3D12_CopyTextureRegion(void* c,uint64_t src,uint64_t dst,uint32_t srcMip,uint32_t dstMip,
    uint32_t sx,uint32_t sy,uint32_t sz,uint32_t dx,uint32_t dy,uint32_t dz,uint32_t w,uint32_t h,uint32_t d)
{ return Invoke(c,[&](Context& ctx) { ctx.CopyTexture(src,dst,srcMip,dstMip,sx,sy,sz,dx,dy,dz,w,h,d); }); }
RYUBOX_D12 RyuboxD3D12_ReadTexture(void* c,uint64_t id,void* data,uint64_t bytes)
{ return Invoke(c,[&](Context& ctx) { ctx.Read(ctx.Get(id,D3D12_RESOURCE_DIMENSION_TEXTURE2D),0,data,bytes,true); }); }
RYUBOX_D12 RyuboxD3D12_ReadTextureSubresource(void* c,uint64_t id,uint32_t mip,void* data,uint64_t bytes,
    uint32_t rowPitch,uint32_t depthPitch)
{ return Invoke(c,[&](Context& ctx) { ctx.ReadTexture(id,mip,data,bytes,rowPitch,depthPitch); }); }
RYUBOX_D12 RyuboxD3D12_BeginCounter(void* c,uint32_t type,uint64_t* id)
{ if(!id) return E_POINTER; *id=0; return Invoke(c,[&](Context& ctx) { *id=ctx.BeginCounter(type); }); }
RYUBOX_D12 RyuboxD3D12_EndCounter(void* c,uint64_t id)
{ return Invoke(c,[&](Context& ctx) { ctx.EndCounter(id); }); }
RYUBOX_D12 RyuboxD3D12_ReadCounter(void* c,uint64_t id,uint64_t* value)
{ if(!value) return E_POINTER; return Invoke(c,[&](Context& ctx) { *value=ctx.ReadCounter(id); }); }
RYUBOX_D12 RyuboxD3D12_ReleaseCounter(void* c,uint64_t id)
{ return Invoke(c,[&](Context& ctx) { Require(ctx.counters.erase(id)==1); },true); }

typedef bool (*PFN_spirv_to_dxil)(
    const uint32_t *words, size_t word_count,
    struct dxil_spirv_specialization *specializations,
    unsigned int num_specializations, dxil_spirv_shader_stage stage,
    const char *entry_point_name,
    enum dxil_validator_version validator_version_max,
    const struct dxil_spirv_debug_options *debug_options,
    const struct dxil_spirv_runtime_conf *conf,
    const struct dxil_spirv_logger *logger,
    struct dxil_spirv_object *out_dxil);

typedef void (*PFN_spirv_to_dxil_free)(struct dxil_spirv_object *dxil);

static HMODULE GetSpirvToDxilModule()
{
    static HMODULE hMod = []() -> HMODULE
    {
        HMODULE h = LoadLibraryA("spirv_to_dxil.dll");
        if (!h)
        {
            char path[MAX_PATH];
            HMODULE currentMod = nullptr;
            if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCSTR>(&GetSpirvToDxilModule), &currentMod) && currentMod)
            {
                if (GetModuleFileNameA(currentMod, path, MAX_PATH))
                {
                    char* lastSlash = strrchr(path, '\\');
                    if (lastSlash)
                    {
                        strcpy_s(lastSlash + 1, MAX_PATH - (lastSlash + 1 - path), "spirv_to_dxil.dll");
                        h = LoadLibraryExA(path, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
                    }
                }
            }
        }
        return h;
    }();
    return hMod;
}

RYUBOX_D12 RyuboxD3D12_TranslateSpirvToDxil(
    const uint32_t* words, size_t wordCount, uint32_t stage,
    const char* entryPoint, void** outData, uint64_t* outBytes, void** outHandle)
{
    if (!words || !wordCount || !outData || !outBytes || !outHandle) return E_POINTER;
    *outData = nullptr;
    *outBytes = 0;
    *outHandle = nullptr;

    // Fail closed before invoking the unverified third-party ABI. In particular,
    // padding an older runtime_conf to 128 bytes does not establish ABI safety,
    // and NO_DXIL_VALIDATION must not be presented as a validated shader path.
    HRESULT status = RyuboxD3D12_GetDxilStatus();
    if (FAILED(status)) return status;

    HMODULE hMod = GetSpirvToDxilModule();
    if (!hMod) return HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND);

    static auto pfn_translate = reinterpret_cast<PFN_spirv_to_dxil>(GetProcAddress(hMod, "spirv_to_dxil"));
    static auto pfn_free = reinterpret_cast<PFN_spirv_to_dxil_free>(GetProcAddress(hMod, "spirv_to_dxil_free"));

    if (!pfn_translate || !pfn_free) return E_NOINTERFACE;

    dxil_spirv_shader_stage dxilStage = DXIL_SPIRV_SHADER_NONE;
    switch (stage)
    {
    case 0: dxilStage = DXIL_SPIRV_SHADER_VERTEX; break;
    case 1: dxilStage = DXIL_SPIRV_SHADER_TESS_CTRL; break;
    case 2: dxilStage = DXIL_SPIRV_SHADER_TESS_EVAL; break;
    case 3: dxilStage = DXIL_SPIRV_SHADER_GEOMETRY; break;
    case 4: dxilStage = DXIL_SPIRV_SHADER_FRAGMENT; break;
    case 5: dxilStage = DXIL_SPIRV_SHADER_COMPUTE; break;
    default: return E_INVALIDARG;
    }

    struct dxil_spirv_debug_options debug_options = { false };
    uint8_t conf_buf[128] = {};
    struct dxil_spirv_runtime_conf* conf = reinterpret_cast<struct dxil_spirv_runtime_conf*>(conf_buf);
    conf->shader_model_max = SHADER_MODEL_6_0;

    auto* obj = new (std::nothrow) dxil_spirv_object();
    if (!obj) return E_OUTOFMEMORY;
    memset(obj, 0, sizeof(*obj));

    struct dxil_spirv_logger logger = { nullptr, [](void*, const char* msg) { if (msg) OutputDebugStringA(msg); } };

    bool ok = false;
    __try
    {
        ok = pfn_translate(
            words, wordCount,
            nullptr, 0,
            dxilStage,
            (entryPoint && *entryPoint) ? entryPoint : "main",
            NO_DXIL_VALIDATION,
            &debug_options,
            conf,
            &logger,
            obj);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        ok = false;
    }

    if (!ok || !obj->binary.buffer || obj->binary.size == 0)
    {
        if (obj->binary.buffer) pfn_free(obj);
        delete obj;
        return E_FAIL;
    }

    *outData = obj->binary.buffer;
    *outBytes = static_cast<uint64_t>(obj->binary.size);
    *outHandle = obj;
    return S_OK;
}

RYUBOX_D12 RyuboxD3D12_GetDxilStatus()
{
    return E_NOTIMPL; // Needs matching compiler headers, linked stages and DXIL reflection/validation.
}

RYUBOX_D12 RyuboxD3D12_FreeDxil(void* handle)
{
    if (!handle) return S_OK;
    HMODULE hMod = GetSpirvToDxilModule();
    if (!hMod) return HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND);

    static auto pfn_free = reinterpret_cast<PFN_spirv_to_dxil_free>(GetProcAddress(hMod, "spirv_to_dxil_free"));
    if (!pfn_free) return E_NOINTERFACE;

    auto* obj = static_cast<dxil_spirv_object*>(handle);
    pfn_free(obj);
    delete obj;
    return S_OK;
}
