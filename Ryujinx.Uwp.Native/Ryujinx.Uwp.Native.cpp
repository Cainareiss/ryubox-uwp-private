#include <windows.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <d3dcompiler.h>
#include <dxgi1_6.h>
#include <xaudio2.h>
#include <windows.ui.xaml.media.dxinterop.h>
#include <wrl.h>

#include "ImGuiD3D11Shell.h"
#include "D3D12FrontendHost.h"
#include "RyuboxFrontendHost.h"
#include "../UWP-ImGuiFrontend/include/UwpImGuiFrontend/Frontend.h"
#include "../UWP-ImGuiFrontend/include/UwpImGuiFrontend/ShellRenderer.h"
#include "../UWP-ImGuiFrontend/include/UwpImGuiFrontend/ScreenScraper.h"
#include "../UWP-ImGuiFrontend/include/UwpImGuiFrontend/RyuboxSettings.h"
#include "../UWP-ImGuiFrontend/include/UwpImGuiFrontend/Settings.h"

#include <cstdint>
#include <cstring>
#include <mutex>
#include <atomic>
#include <new>
#include <cstdio>
#include <cstdarg>
#include <algorithm>
#include <cmath>

using Microsoft::WRL::ComPtr;

// The ImGui frontend is created before the first D3D11 frame.  Previously it
// was only initialized from PresentRendererFrame(), which is a game-owned
// path and is not reached while the launcher is idle.  That left the launcher
// clearing and presenting an empty backbuffer (a reproducible black screen).
namespace Ryujinx::Uwp::Native
{
    void EnsureFrontendInitialized();
    extern std::atomic<bool> g_showFpsCounter;
}

// The UWP SDK headers hide this declaration even though the CoreHost already
// resolves the same KernelBase API at runtime. Declare the ABI explicitly so
// the handler itself can remain native and avoid a NativeAOT reverse callback.
extern "C" WINBASEAPI PVOID WINAPI AddVectoredExceptionHandler(
    ULONG first,
    PVECTORED_EXCEPTION_HANDLER handler);

namespace
{
    // Xbox composition is substantially more stable with a bounded
    // triple-buffered flip chain, matching the proven native emulator hosts.
    constexpr UINT RenderTargetCount = 3;
    constexpr LONG ProbeDriverLoaded = 1;
    constexpr LONG ProbeEntryPointAvailable = 2;
    constexpr LONG ProbeDxilPackaged = 4;
    constexpr LONG ProbeXboxCompilerPackaged = 8;
    constexpr LONG ProbeScarlettCompilerPackaged = 16;

    volatile LONG g_probeFlags = 0;
    volatile LONG g_probeError = ERROR_SUCCESS;

    inline UINT AtLeastOne(UINT value)
    {
        return value == 0 ? 1u : value;
    }

    struct InputState final
    {
        uint32_t buttons = 0;
        float leftX = 0.0f;
        float leftY = 0.0f;
        float rightX = 0.0f;
        float rightY = 0.0f;
        float leftTrigger = 0.0f;
        float rightTrigger = 0.0f;
    };

    struct D3D11PresentationTelemetry final
    {
        uint64_t beginCount = 0;
        uint64_t bindCount = 0;
        uint64_t copyAttempts = 0;
        uint64_t copySuccess = 0;
        uint64_t presentAttempts = 0;
        uint64_t presentSuccess = 0;
        uint64_t uploadAttempts = 0;
        uint64_t uploadSuccess = 0;
        int32_t lastCopyResult = S_OK;
        int32_t lastPresentResult = S_OK;
        int32_t lastUploadResult = S_OK;
        uint32_t graphicsThreadId = 0;
        uint32_t width = 0;
        uint32_t height = 0;
        uint32_t format = 0;
        uint32_t bufferCount = 0;
        uint64_t sessionId = 0;
        uint64_t deviceGeneration = 0;
        uint64_t visibleGuestFrames = 0;
        uint64_t guestFramesAcceptedByPresenter = 0;
        uint64_t guestFramesSubmittedToSwapChain = 0;
        uint64_t frontendFramesSubmitted = 0;
        uint64_t deviceLostAt = 0;
        int32_t deviceRemovedReason = S_OK;
        uint32_t presentationState = 0;
        uint32_t presentationOwner = 0;
        uint32_t reserved = 0;
    };

    enum class SessionPresentationState : uint32_t
    {
        FrontendVisible = 0,
        Launching = 1,
        WaitingFirstGuestFrame = 2,
        TransitionToGame = 3,
        GameVisible = 4,
        ReturningToFrontend = 5,
        Failed = 6,
    };

    enum class PresentationOwner : uint32_t
    {
        Frontend = 0,
        Game = 1,
    };

    // NativeAOT cannot safely enter a managed delegate from a Windows vectored
    // exception handler. Keep sparse guest-memory faults entirely native: the
    // handler takes no locks and only performs MEM_COMMIT plus atomic telemetry.
    constexpr uintptr_t LazyCommitGranularity = 64u * 1024u;

    struct LazyCommitTelemetry final
    {
        uint64_t attempts = 0;
        uint64_t successes = 0;
        uint64_t failures = 0;
        uint64_t committedBytes = 0;
        uint64_t concurrentEntries = 0;
        uint64_t activeHandlers = 0;
        uint64_t maxActiveHandlers = 0;
        uint64_t lastFaultAddress = 0;
        uint64_t lastSuccessAddress = 0;
        uint32_t lastError = ERROR_SUCCESS;
        uint32_t lastThreadId = 0;
        uint32_t registered = 0;
        uint32_t reserved = 0;
    };

    volatile PVOID g_lazyCommitBase = nullptr;
    volatile LONG64 g_lazyCommitSize = 0;
    PVOID g_lazyCommitVeh = nullptr;
    volatile LONG64 g_lazyCommitAttempts = 0;
    volatile LONG64 g_lazyCommitSuccesses = 0;
    volatile LONG64 g_lazyCommitFailures = 0;
    volatile LONG64 g_lazyCommittedBytes = 0;
    volatile LONG64 g_lazyCommitConcurrentEntries = 0;
    volatile LONG64 g_lazyCommitActiveHandlers = 0;
    volatile LONG64 g_lazyCommitMaxActiveHandlers = 0;
    volatile LONG64 g_lazyCommitLastFaultAddress = 0;
    volatile LONG64 g_lazyCommitLastSuccessAddress = 0;
    volatile LONG g_lazyCommitLastError = ERROR_SUCCESS;
    volatile LONG g_lazyCommitLastThreadId = 0;

    void UpdateLazyCommitMaximum(volatile LONG64* target, LONG64 value)
    {
        LONG64 observed = InterlockedCompareExchange64(target, 0, 0);
        while (observed < value)
        {
            const LONG64 previous = InterlockedCompareExchange64(target, value, observed);
            if (previous == observed)
            {
                break;
            }

            observed = previous;
        }
    }

    LONG CALLBACK LazyGuestCommitExceptionHandler(EXCEPTION_POINTERS* exceptionPointers)
    {
        if (exceptionPointers == nullptr
            || exceptionPointers->ExceptionRecord == nullptr
            || exceptionPointers->ExceptionRecord->ExceptionCode != EXCEPTION_ACCESS_VIOLATION
            || exceptionPointers->ExceptionRecord->NumberParameters < 2)
        {
            return EXCEPTION_CONTINUE_SEARCH;
        }

        const uintptr_t base = reinterpret_cast<uintptr_t>(
            InterlockedCompareExchangePointer(&g_lazyCommitBase, nullptr, nullptr));
        const uint64_t size = static_cast<uint64_t>(
            InterlockedCompareExchange64(&g_lazyCommitSize, 0, 0));
        const uintptr_t faultAddress = static_cast<uintptr_t>(
            exceptionPointers->ExceptionRecord->ExceptionInformation[1]);

        if (base == 0 || size == 0 || faultAddress < base
            || static_cast<uint64_t>(faultAddress - base) >= size)
        {
            return EXCEPTION_CONTINUE_SEARCH;
        }

        const LONG64 active = InterlockedIncrement64(&g_lazyCommitActiveHandlers);
        if (active > 1)
        {
            InterlockedIncrement64(&g_lazyCommitConcurrentEntries);
        }
        UpdateLazyCommitMaximum(&g_lazyCommitMaxActiveHandlers, active);

        InterlockedIncrement64(&g_lazyCommitAttempts);
        InterlockedExchange64(&g_lazyCommitLastFaultAddress, static_cast<LONG64>(faultAddress));
        InterlockedExchange(&g_lazyCommitLastThreadId, static_cast<LONG>(GetCurrentThreadId()));

        const uintptr_t offset = faultAddress - base;
        const uintptr_t commitOffset = offset & ~(LazyCommitGranularity - 1u);
        const uint64_t remaining = size - static_cast<uint64_t>(commitOffset);
        const SIZE_T commitSize = static_cast<SIZE_T>(
            remaining < LazyCommitGranularity ? remaining : LazyCommitGranularity);
        void* const commitAddress = reinterpret_cast<void*>(base + commitOffset);

        // MEM_COMMIT is idempotent for already committed pages. This matters
        // when two guest CPU threads fault within the same 64 KiB block.
        void* const committed = VirtualAlloc(
            commitAddress,
            commitSize,
            MEM_COMMIT,
            PAGE_READWRITE);

        if (committed != nullptr)
        {
            InterlockedIncrement64(&g_lazyCommitSuccesses);
            InterlockedAdd64(&g_lazyCommittedBytes, static_cast<LONG64>(commitSize));
            InterlockedExchange64(
                &g_lazyCommitLastSuccessAddress,
                static_cast<LONG64>(reinterpret_cast<uintptr_t>(commitAddress)));
            InterlockedExchange(&g_lazyCommitLastError, ERROR_SUCCESS);
            InterlockedDecrement64(&g_lazyCommitActiveHandlers);
            return EXCEPTION_CONTINUE_EXECUTION;
        }

        InterlockedIncrement64(&g_lazyCommitFailures);
        InterlockedExchange(&g_lazyCommitLastError, static_cast<LONG>(GetLastError()));
        InterlockedDecrement64(&g_lazyCommitActiveHandlers);
        return EXCEPTION_CONTINUE_SEARCH;
    }

    void ResetLazyCommitTelemetry()
    {
        InterlockedExchange64(&g_lazyCommitAttempts, 0);
        InterlockedExchange64(&g_lazyCommitSuccesses, 0);
        InterlockedExchange64(&g_lazyCommitFailures, 0);
        InterlockedExchange64(&g_lazyCommittedBytes, 0);
        InterlockedExchange64(&g_lazyCommitConcurrentEntries, 0);
        InterlockedExchange64(&g_lazyCommitActiveHandlers, 0);
        InterlockedExchange64(&g_lazyCommitMaxActiveHandlers, 0);
        InterlockedExchange64(&g_lazyCommitLastFaultAddress, 0);
        InterlockedExchange64(&g_lazyCommitLastSuccessAddress, 0);
        InterlockedExchange(&g_lazyCommitLastError, ERROR_SUCCESS);
        InterlockedExchange(&g_lazyCommitLastThreadId, 0);
    }

    class CoreBridge final
    {
    public:
        HRESULT Start(
            const wchar_t* gamePath,
            const wchar_t* keysPath,
            const wchar_t* firmwarePath,
            const wchar_t* backend)
        {
            if (gamePath == nullptr || gamePath[0] == L'\0'
                || keysPath == nullptr || keysPath[0] == L'\0'
                || backend == nullptr || backend[0] == L'\0')
            {
                return E_INVALIDARG;
            }

            // The native shim only owns the ABI and input state. The managed
            // NativeAOT CoreHost owns all paths and emulation state. Avoiding
            // STL objects here keeps this DLL independent of MSVCP140.dll,
            // which is not guaranteed to be present in an Xbox Dev Mode
            // package even when the UWP CRT is available.
            (void)gamePath;
            (void)keysPath;
            (void)firmwarePath;
            (void)backend;
            m_input = {};
            m_started = true;

            // The managed Ryujinx core is not loadable by the classic UWP
            // profile yet. Keep this explicit so the app cannot report a
            // running emulator when only the host ABI is active.
            m_coreBound = false;
            return S_OK;
        }

        void Stop()
        {
            m_started = false;
            m_coreBound = false;
            m_input = {};
        }

        void SetInput(const InputState& input)
        {
            if (m_started)
            {
                m_input = input;
            }
        }

        bool IsStarted() const
        {
            return m_started;
        }

        bool IsCoreBound() const
        {
            return m_coreBound;
        }

    private:
        bool m_started = false;
        bool m_coreBound = false;
        InputState m_input;
    };

    class GraphicsHost final
    {
    public:
        HRESULT Initialize(IUnknown* panel, uint32_t width, uint32_t height)
        {
            if (panel == nullptr)
            {
                return E_INVALIDARG;
            }

            Shutdown();

            HRESULT result = D3D12CreateDevice(
                nullptr,
                D3D_FEATURE_LEVEL_11_0,
                IID_PPV_ARGS(&m_device));
            if (FAILED(result))
            {
                return result;
            }

            D3D12_COMMAND_QUEUE_DESC queueDescription{};
            queueDescription.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;

            result = m_device->CreateCommandQueue(
                &queueDescription,
                IID_PPV_ARGS(&m_queue));
            if (FAILED(result))
            {
                return result;
            }

            result = CreateSwapChain(width, height);
            if (FAILED(result))
            {
                return result;
            }

            ComPtr<ISwapChainPanelNative> panelNative;
            result = panel->QueryInterface(IID_PPV_ARGS(&panelNative));
            if (FAILED(result))
            {
                return result;
            }

            result = panelNative->SetSwapChain(m_swapChain.Get());
            if (FAILED(result))
            {
                return result;
            }

            result = CreateRenderResources();
            if (FAILED(result))
            {
                return result;
            }

            result = m_device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&m_fence));
            if (FAILED(result))
            {
                return result;
            }

            m_fenceEvent = CreateEventExW(nullptr, nullptr, 0, EVENT_MODIFY_STATE | SYNCHRONIZE);
            if (m_fenceEvent == nullptr)
            {
                return HRESULT_FROM_WIN32(GetLastError());
            }

            m_initialized = true;
            return Render();
        }

        HRESULT Resize(uint32_t width, uint32_t height)
        {
            if (!m_initialized || !m_swapChain)
            {
                return S_FALSE;
            }

            width = AtLeastOne(width);
            height = AtLeastOne(height);

            HRESULT result = WaitForGpu();
            if (FAILED(result))
            {
                return result;
            }

            for (ComPtr<ID3D12Resource>& renderTarget : m_renderTargets)
            {
                renderTarget.Reset();
            }

            result = m_swapChain->ResizeBuffers(
                RenderTargetCount,
                width,
                height,
                DXGI_FORMAT_R8G8B8A8_UNORM,
                0);
            if (FAILED(result))
            {
                return result;
            }

            m_frameIndex = m_swapChain->GetCurrentBackBufferIndex();
            return CreateRenderResources();
        }

        HRESULT Render()
        {
            if (!m_initialized)
            {
                return S_FALSE;
            }

            HRESULT result = m_commandAllocator->Reset();
            if (FAILED(result))
            {
                return result;
            }

            result = m_commandList->Reset(m_commandAllocator.Get(), nullptr);
            if (FAILED(result))
            {
                return result;
            }

            D3D12_RESOURCE_BARRIER toRenderTarget{};
            toRenderTarget.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            toRenderTarget.Transition.pResource = m_renderTargets[m_frameIndex].Get();
            toRenderTarget.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
            toRenderTarget.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
            toRenderTarget.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            m_commandList->ResourceBarrier(1, &toRenderTarget);

            constexpr FLOAT clearColor[] = { 0.035f, 0.055f, 0.090f, 1.0f };
            D3D12_CPU_DESCRIPTOR_HANDLE rtv = m_rtvHeap->GetCPUDescriptorHandleForHeapStart();
            rtv.ptr += static_cast<SIZE_T>(m_frameIndex) * m_rtvDescriptorSize;
            m_commandList->OMSetRenderTargets(1, &rtv, FALSE, nullptr);
            m_commandList->ClearRenderTargetView(rtv, clearColor, 0, nullptr);

            D3D12_RESOURCE_BARRIER toPresent = toRenderTarget;
            toPresent.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
            toPresent.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
            m_commandList->ResourceBarrier(1, &toPresent);

            result = m_commandList->Close();
            if (FAILED(result))
            {
                return result;
            }

            ID3D12CommandList* commandLists[] = { m_commandList.Get() };
            m_queue->ExecuteCommandLists(1, commandLists);

            result = m_swapChain->Present(1, 0);
            if (FAILED(result))
            {
                return result;
            }

            result = WaitForGpu();
            m_frameIndex = m_swapChain->GetCurrentBackBufferIndex();
            return result;
        }

        void Shutdown()
        {
            if (m_queue && m_fence)
            {
                WaitForGpu();
            }

            if (m_fenceEvent != nullptr)
            {
                CloseHandle(m_fenceEvent);
                m_fenceEvent = nullptr;
            }

            for (ComPtr<ID3D12Resource>& renderTarget : m_renderTargets)
            {
                renderTarget.Reset();
            }

            m_commandList.Reset();
            m_commandAllocator.Reset();
            m_rtvHeap.Reset();
            m_fence.Reset();
            m_swapChain.Reset();
            m_queue.Reset();
            m_device.Reset();
            m_initialized = false;
            m_fenceValue = 0;
            m_frameIndex = 0;
        }

    private:
        HRESULT CreateSwapChain(uint32_t width, uint32_t height)
        {
            ComPtr<IDXGIFactory4> factory;
            HRESULT result = CreateDXGIFactory2(0, IID_PPV_ARGS(&factory));
            if (FAILED(result))
            {
                return result;
            }

            DXGI_SWAP_CHAIN_DESC1 description{};
            description.Width = AtLeastOne(width);
            description.Height = AtLeastOne(height);
            description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
            description.SampleDesc.Count = 1;
            description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
            description.BufferCount = RenderTargetCount;
            description.Scaling = DXGI_SCALING_STRETCH;
            description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
            description.AlphaMode = DXGI_ALPHA_MODE_IGNORE;

            ComPtr<IDXGISwapChain1> swapChain1;
            result = factory->CreateSwapChainForComposition(
                m_queue.Get(),
                &description,
                nullptr,
                &swapChain1);
            if (FAILED(result))
            {
                return result;
            }

            return swapChain1.As(&m_swapChain);
        }

        HRESULT CreateRenderResources()
        {
            D3D12_DESCRIPTOR_HEAP_DESC heapDescription{};
            heapDescription.NumDescriptors = RenderTargetCount;
            heapDescription.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;

            HRESULT result = m_device->CreateDescriptorHeap(
                &heapDescription,
                IID_PPV_ARGS(&m_rtvHeap));
            if (FAILED(result))
            {
                return result;
            }

            m_rtvDescriptorSize = m_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
            D3D12_CPU_DESCRIPTOR_HANDLE rtv = m_rtvHeap->GetCPUDescriptorHandleForHeapStart();

            for (UINT index = 0; index < RenderTargetCount; index++)
            {
                result = m_swapChain->GetBuffer(index, IID_PPV_ARGS(&m_renderTargets[index]));
                if (FAILED(result))
                {
                    return result;
                }

                m_device->CreateRenderTargetView(m_renderTargets[index].Get(), nullptr, rtv);
                rtv.ptr += m_rtvDescriptorSize;
            }

            if (!m_commandAllocator)
            {
                result = m_device->CreateCommandAllocator(
                    D3D12_COMMAND_LIST_TYPE_DIRECT,
                    IID_PPV_ARGS(&m_commandAllocator));
                if (FAILED(result))
                {
                    return result;
                }
            }

            if (!m_commandList)
            {
                result = m_device->CreateCommandList(
                    0,
                    D3D12_COMMAND_LIST_TYPE_DIRECT,
                    m_commandAllocator.Get(),
                    nullptr,
                    IID_PPV_ARGS(&m_commandList));
                if (FAILED(result))
                {
                    return result;
                }

                result = m_commandList->Close();
            }

            return result;
        }

        HRESULT WaitForGpu()
        {
            if (!m_queue || !m_fence || m_fenceEvent == nullptr)
            {
                return S_FALSE;
            }

            const UINT64 value = ++m_fenceValue;
            HRESULT result = m_queue->Signal(m_fence.Get(), value);
            if (FAILED(result))
            {
                return result;
            }

            if (m_fence->GetCompletedValue() < value)
            {
                result = m_fence->SetEventOnCompletion(value, m_fenceEvent);
                if (FAILED(result))
                {
                    return result;
                }

                WaitForSingleObject(m_fenceEvent, INFINITE);
            }

            return S_OK;
        }

        bool m_initialized = false;
        UINT m_frameIndex = 0;
        UINT m_rtvDescriptorSize = 0;
        UINT64 m_fenceValue = 0;
        HANDLE m_fenceEvent = nullptr;
        ComPtr<ID3D12Device> m_device;
        ComPtr<ID3D12CommandQueue> m_queue;
        ComPtr<IDXGISwapChain3> m_swapChain;
        ComPtr<ID3D12DescriptorHeap> m_rtvHeap;
        ComPtr<ID3D12CommandAllocator> m_commandAllocator;
        ComPtr<ID3D12GraphicsCommandList> m_commandList;
        ComPtr<ID3D12Fence> m_fence;
        ComPtr<ID3D12Resource> m_renderTargets[RenderTargetCount];
    };

    // The Cemu UWP host keeps the device and composition swap chain in the
    // host process and passes the surface into the emulator core.  This is
    // the same first boundary for Ryujinx: D3D11 owns the XAML surface here,
    // while the real Switch GAL renderer can be attached in a later slice.
    // It intentionally lives beside the existing D3D12 host so changing the
    // backend cannot invalidate the Dozen path.
    class D3D11GraphicsHost final
    {
    public:
        HRESULT GetNativeObjects(void** device, void** deviceContext, void** swapChain) const
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);

            if (!m_initialized || device == nullptr || deviceContext == nullptr || swapChain == nullptr)
            {
                return E_INVALIDARG;
            }

            // These are borrowed pointers. The XAML host remains the owner and
            // keeps the ComPtr instances alive for the lifetime of the GAL.
            *device = m_device.Get();
            *deviceContext = m_context.Get();
            *swapChain = m_swapChain.Get();
            return S_OK;
        }

        HRESULT GetSurfaceSize(uint32_t* width, uint32_t* height) const
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);

            if (!m_initialized || width == nullptr || height == nullptr)
            {
                return E_INVALIDARG;
            }

            *width = m_width;
            *height = m_height;
            return S_OK;
        }

        // SwapChainPanel sizes are expressed in XAML DIPs, while the
        // composition swap chain is allocated in physical pixels. Applying
        // the inverse matrix here prevents XAML from scaling that backbuffer
        // a second time. This is independent from guest viewport/crop data.
        HRESULT SetCompositionScale(float scaleX, float scaleY)
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);

            if (!m_initialized || !m_swapChain)
            {
                return E_UNEXPECTED;
            }

            // This rejects zero, negative, NaN and impractically large values
            // before calculating the inverse matrix.
            if (!(scaleX > 0.0f && scaleX <= 16.0f)
                || !(scaleY > 0.0f && scaleY <= 16.0f))
            {
                return E_INVALIDARG;
            }

            m_compositionScaleX = scaleX;
            m_compositionScaleY = scaleY;
            return ApplyCompositionScaleLocked();
        }

        HRESULT UploadFrame(const void* pixels, uint32_t width, uint32_t height, uint32_t rowPitch)
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);
            InterlockedIncrement64(&m_uploadAttempts);

            if (!m_sourceStatsCaptured)
            {
                wchar_t sourceStatsEnabled[4]{};
                if (GetEnvironmentVariableW(L"RYUJINX_UWP_D3D11_SOURCE_STATS", sourceStatsEnabled,
                    ARRAYSIZE(sourceStatsEnabled)) != 0 && sourceStatsEnabled[0] == L'1')
                {
                    WriteSourceStats("uploadFrame entry size=%ux%u rowPitch=%u\\n", width, height, rowPitch);
                }
            }

            if (!m_initialized || pixels == nullptr || !m_swapChain || !m_context || !m_uploadTexture)
            {
                InterlockedExchange(&m_lastUploadResult, E_INVALIDARG);
                return E_INVALIDARG;
            }

            if (width == 0 || height == 0 || width > (UINT32_MAX / 4u)
                || width != m_width || height != m_height || rowPitch < width * 4u)
            {
                InterlockedExchange(&m_lastUploadResult, E_INVALIDARG);
                return E_INVALIDARG;
            }

            uint64_t uploadStatsSample = ++m_uploadStatsSamples;
            if (uploadStatsSample == 1 || uploadStatsSample == 30 || uploadStatsSample == 60 ||
                uploadStatsSample == 120 || uploadStatsSample % 300 == 0)
            {
                wchar_t sourceStatsEnabled[4]{};
                if (GetEnvironmentVariableW(L"RYUJINX_UWP_D3D11_SOURCE_STATS", sourceStatsEnabled,
                    ARRAYSIZE(sourceStatsEnabled)) != 0 && sourceStatsEnabled[0] == L'1')
                {
                    const auto* bytes = static_cast<const uint8_t*>(pixels);
                    const uint32_t sampleWidth = (std::min)(width, 64u);
                    const uint32_t sampleHeight = (std::min)(height, 64u);
                    uint64_t sampled = 0;
                    uint64_t nonZero = 0;
                    uint64_t colored = 0;
                    uint64_t energy = 0;
                    for (uint32_t sy = 0; sy < sampleHeight; sy++)
                    {
                        const uint32_t y = (static_cast<uint64_t>(sy) * height) / sampleHeight;
                        const uint8_t* row = bytes + static_cast<size_t>(y) * rowPitch;
                        for (uint32_t sx = 0; sx < sampleWidth; sx++)
                        {
                            const uint32_t x = (static_cast<uint64_t>(sx) * width) / sampleWidth;
                            const uint8_t* pixel = row + static_cast<size_t>(x) * 4u;
                            const uint32_t r = pixel[0];
                            const uint32_t g = pixel[1];
                            const uint32_t b = pixel[2];
                            const uint32_t a = pixel[3];
                            sampled++;
                            energy += r + g + b;
                            if (r != 0 || g != 0 || b != 0 || a != 0) nonZero++;
                            if (r != g || g != b) colored++;
                        }
                    }
                    WriteSourceStats("uploadStats size=%ux%u rowPitch=%u samples=%llu nonZero=%llu colored=%llu energy=%llu\\n",
                        width, height, rowPitch, static_cast<unsigned long long>(sampled),
                        static_cast<unsigned long long>(nonZero), static_cast<unsigned long long>(colored),
                        static_cast<unsigned long long>(energy));
                }
            }

            ComPtr<ID3D11Texture2D> backBuffer;
            HRESULT result = m_swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
            if (FAILED(result))
            {
                InterlockedExchange(&m_lastUploadResult, result);
                return result;
            }

            m_context->UpdateSubresource(m_uploadTexture.Get(), 0, nullptr, pixels, rowPitch, 0);
            m_context->CopyResource(backBuffer.Get(), m_uploadTexture.Get());

            MarkGuestFrameAcceptedLocked();
            DrawGameFpsLocked();
            // Upload only copies into the current backbuffer. The managed
            // D3D11 window invokes the common swapBuffersCallback exactly
            // once after this method returns. Calling Present here as well
            // advances a flip-discard swap chain twice and exposes an empty
            // backbuffer on the second present.
            InterlockedExchange(&m_lastUploadResult, S_OK);
            InterlockedIncrement64(&m_uploadSuccess);
            return S_OK;
        }

        HRESULT CopyRendererTexture(IUnknown* resource)
        {
            return CopyRendererTexture(resource, false, 0.0f, 0.0f, 0.0f, 0.0f, true);
        }

        HRESULT CopyRendererTexture(
            IUnknown* resource,
            bool flipY,
            float u0,
            float v0,
            float u1,
            float v1,
            bool preserveExplicitCrop)
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);
            InterlockedIncrement64(&m_copyAttempts);

            // The managed D3D11 presentation boundary already converts the
            // GAL row-order flag to the top-left fullscreen-blit convention.
            // Consuming its complement here performed the conversion twice,
            // leaving both direct and compatibility paths in the GAL order.
            // The ABI parameter is the final native sampling decision.
            // CPU-composed UploadFrame frames are already top-down and do not
            // pass through this function.
            const bool effectiveFlipY = flipY;

            wchar_t sourceStatsEnabled[4]{};
            const bool collectSourceStats =
                GetEnvironmentVariableW(
                    L"RYUJINX_UWP_D3D11_SOURCE_STATS",
                    sourceStatsEnabled,
                    ARRAYSIZE(sourceStatsEnabled)) != 0 &&
                sourceStatsEnabled[0] == L'1';
            if (collectSourceStats && !m_sourceStatsCaptured)
            {
                WriteSourceStats("copyRendererTexture entry resource=0x%p crop=%.1f,%.1f-%.1f,%.1f requestedFlip=%u effectiveFlip=%u\\n",
                    resource, u0, v0, u1, v1, flipY ? 1u : 0u, effectiveFlipY ? 1u : 0u);
            }

            if (!m_initialized || resource == nullptr || !m_swapChain || !m_context)
            {
                InterlockedExchange(&m_lastCopyResult, E_INVALIDARG);
                return E_INVALIDARG;
            }

            if (!std::isfinite(u0) || !std::isfinite(v0) || !std::isfinite(u1) || !std::isfinite(v1))
            {
                InterlockedExchange(&m_lastCopyResult, E_INVALIDARG);
                return E_INVALIDARG;
            }

            HRESULT ownership = ValidateGraphicsThreadLocked();
            if (FAILED(ownership))
            {
                InterlockedExchange(&m_lastCopyResult, ownership);
                return ownership;
            }

            ComPtr<ID3D11Texture2D> source;
            HRESULT result = resource->QueryInterface(IID_PPV_ARGS(&source));
            if (FAILED(result))
            {
                InterlockedExchange(&m_lastCopyResult, result);
                return result;
            }

            ComPtr<ID3D11Texture2D> backBuffer;
            result = m_swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
            if (FAILED(result))
            {
                InterlockedExchange(&m_lastCopyResult, result);
                return result;
            }

            D3D11_TEXTURE2D_DESC sourceDescription{};
            D3D11_TEXTURE2D_DESC backBufferDescription{};
            source->GetDesc(&sourceDescription);
            backBuffer->GetDesc(&backBufferDescription);

            // Capture one small, read-only diagnostic sample from the actual
            // guest presentation resource.  This is opt-in because Map(READ)
            // synchronizes the GPU.  It tells us whether a black host window
            // is caused by an empty renderer target or by the presentation
            // copy/blit after the renderer has produced pixels.
            if (!m_sourceStatsCaptured)
            {
                if (collectSourceStats)
                {
                    CaptureSourceStatsLocked(source.Get(), sourceDescription);
                    m_sourceStatsCaptured = true;
                }
            }

            // Diagnostic path for titles whose source texture is already a
            // full-size render target.  This deliberately bypasses the
            // fullscreen presentation shader so we can distinguish a bad
            // source frame from a bad SRV/sampler/blit pass.  It is opt-in
            // and is only valid when the resource descriptions match exactly
            // and no crop/vertical transform is requested.
            wchar_t forceCopyResource[4]{};
            const bool useDirectResourceCopy =
                GetEnvironmentVariableW(
                    L"RYUJINX_UWP_D3D11_PRESENT_COPY_RESOURCE",
                    forceCopyResource,
                    ARRAYSIZE(forceCopyResource)) != 0 &&
                forceCopyResource[0] == L'1';
            const bool fullSourceRegion =
                u0 <= 0.0f && v0 <= 0.0f &&
                (u1 <= 0.0f || u1 >= static_cast<float>(sourceDescription.Width)) &&
                (v1 <= 0.0f || v1 >= static_cast<float>(sourceDescription.Height));
            if (useDirectResourceCopy && !effectiveFlipY && fullSourceRegion &&
                sourceDescription.Width == backBufferDescription.Width &&
                sourceDescription.Height == backBufferDescription.Height &&
                sourceDescription.Format == backBufferDescription.Format &&
                sourceDescription.SampleDesc.Count == backBufferDescription.SampleDesc.Count &&
                sourceDescription.ArraySize == backBufferDescription.ArraySize &&
                sourceDescription.MipLevels == backBufferDescription.MipLevels)
            {
                m_context->CopyResource(backBuffer.Get(), source.Get());
                InterlockedExchange(&m_lastCopyResult, S_OK);
                InterlockedIncrement64(&m_copySuccess);
                MarkGuestFrameAcceptedLocked();
                return S_OK;
            }

            if (sourceDescription.SampleDesc.Count == 1 &&
                sourceDescription.ArraySize == 1 &&
                sourceDescription.MipLevels == 1 &&
                m_blitVertexShader &&
                m_blitPixelShader &&
                m_blitSampler &&
                m_blitConstants)
            {
                ComPtr<ID3D11ShaderResourceView> sourceView;
                ComPtr<ID3D11ShaderResourceView> sourceViewChroma;
                
                if (sourceDescription.Format == DXGI_FORMAT_NV12)
                {
                    D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc{};
                    srvDesc.Format = DXGI_FORMAT_R8_UNORM;
                    srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
                    srvDesc.Texture2D.MipLevels = 1;
                    result = m_device->CreateShaderResourceView(source.Get(), &srvDesc, &sourceView);
                    
                    if (SUCCEEDED(result))
                    {
                        srvDesc.Format = DXGI_FORMAT_R8G8_UNORM;
                        result = m_device->CreateShaderResourceView(source.Get(), &srvDesc, &sourceViewChroma);
                    }
                }
                else
                {
                    result = m_device->CreateShaderResourceView(source.Get(), nullptr, &sourceView);
                }

                ComPtr<ID3D11RenderTargetView> targetView;
                if (SUCCEEDED(result))
                {
                    result = m_device->CreateRenderTargetView(backBuffer.Get(), nullptr, &targetView);
                }

                if (SUCCEEDED(result))
                {
                    ID3D11DeviceContext* presentationContext = m_presentContext
                        ? m_presentContext.Get()
                        : m_context.Get();
                    D3D11_MAPPED_SUBRESOURCE mapped{};
                    result = presentationContext->Map(m_blitConstants.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
                    if (SUCCEEDED(result))
                    {
                        float* constants = static_cast<float*>(mapped.pData);
                        constants[0] = effectiveFlipY ? 1.0f : 0.0f;
                        constants[1] = 0.0f; // Reserved
                        constants[2] = 0.0f; // Reserved
                        constants[3] = 0.0f; // Reserved

                        float cropU0 = u0;
                        float cropV0 = v0;
                        float cropU1 = (u1 <= 0.0f || u1 > static_cast<float>(sourceDescription.Width)) ? static_cast<float>(sourceDescription.Width) : u1;
                        float cropV1 = (v1 <= 0.0f || v1 > static_cast<float>(sourceDescription.Height)) ? static_cast<float>(sourceDescription.Height) : v1;

                        // Legacy callers may still opt into proportional crop
                        // expansion. Current GAL callers resolve ScaleFactor before
                        // reaching this presenter and pass preserveExplicitCrop=true,
                        // so their populated sub-rectangle must remain authoritative.
                        const float requestedWidth = cropU1 - cropU0;
                        const float requestedHeight = cropV1 - cropV0;
                        if (!preserveExplicitCrop &&
                            cropU0 == 0.0f && cropV0 == 0.0f &&
                            requestedWidth > 0.0f && requestedHeight > 0.0f &&
                            cropU1 < static_cast<float>(sourceDescription.Width) &&
                            cropV1 < static_cast<float>(sourceDescription.Height))
                        {
                            const float scaleX = static_cast<float>(sourceDescription.Width) / requestedWidth;
                            const float scaleY = static_cast<float>(sourceDescription.Height) / requestedHeight;
                            const float scaleDelta = scaleX > scaleY ? scaleX - scaleY : scaleY - scaleX;
                            if (scaleX > 1.0f && scaleX <= 4.0f && scaleDelta <= 0.01f)
                            {
                                cropU1 = static_cast<float>(sourceDescription.Width);
                                cropV1 = static_cast<float>(sourceDescription.Height);
                            }
                        }

                        if (cropU1 <= cropU0)
                        {
                            cropU0 = 0.0f;
                            cropU1 = static_cast<float>(sourceDescription.Width);
                        }
                        if (cropV1 <= cropV0)
                        {
                            cropV0 = 0.0f;
                            cropV1 = static_cast<float>(sourceDescription.Height);
                        }

                        constants[4] = cropU0 / static_cast<float>(sourceDescription.Width);
                        constants[5] = cropV0 / static_cast<float>(sourceDescription.Height);
                        constants[6] = cropU1 / static_cast<float>(sourceDescription.Width);
                        constants[7] = cropV1 / static_cast<float>(sourceDescription.Height);
                        presentationContext->Unmap(m_blitConstants.Get(), 0);

                        D3D11_VIEWPORT viewport{};
                        viewport.Width = static_cast<float>(m_width);
                        viewport.Height = static_cast<float>(m_height);
                        viewport.MinDepth = 0.0f;
                        viewport.MaxDepth = 1.0f;

                        ID3D11RenderTargetView* renderTargets[] = { targetView.Get() };
                        ID3D11ShaderResourceView* shaderResources[] = { sourceView.Get(), sourceViewChroma.Get() };
                        ID3D11SamplerState* samplers[] = { m_blitSampler.Get() };
                        ID3D11Buffer* constantBuffers[] = { m_blitConstants.Get() };
                        presentationContext->OMSetRenderTargets(1, renderTargets, nullptr);
                        presentationContext->RSSetViewports(1, &viewport);
                        presentationContext->IASetInputLayout(nullptr);
                        presentationContext->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                        presentationContext->VSSetShader(m_blitVertexShader.Get(), nullptr, 0);
                        
                        if (sourceDescription.Format == DXGI_FORMAT_NV12 && m_blitPixelShaderNV12)
                        {
                            presentationContext->PSSetShader(m_blitPixelShaderNV12.Get(), nullptr, 0);
                            presentationContext->PSSetShaderResources(0, 2, shaderResources);
                        }
                        else
                        {
                            presentationContext->PSSetShader(m_blitPixelShader.Get(), nullptr, 0);
                            presentationContext->PSSetShaderResources(0, 1, shaderResources);
                        }
                        
                        presentationContext->VSSetConstantBuffers(0, 1, constantBuffers);
                        presentationContext->PSSetConstantBuffers(0, 1, constantBuffers);
                        presentationContext->PSSetSamplers(0, 1, samplers);
                        presentationContext->Draw(3, 0);

                        ID3D11ShaderResourceView* nullResources[] = { nullptr, nullptr };
                        presentationContext->PSSetShaderResources(0, 2, nullResources);

                        // The GAL and presenter share the immediate context.
                        // Record presentation on a deferred context, then ask
                        // D3D11 to restore every guest pipeline binding after
                        // executing it. Otherwise the fullscreen blit shaders,
                        // viewport and OM target leak into the next guest frame.
                        if (m_presentContext)
                        {
                            ComPtr<ID3D11CommandList> commandList;
                            result = m_presentContext->FinishCommandList(FALSE, &commandList);
                            if (SUCCEEDED(result))
                            {
                                m_context->ExecuteCommandList(commandList.Get(), TRUE);
                            }
                        }
                    }
                }

                if (SUCCEEDED(result))
                {
                    InterlockedExchange(&m_lastCopyResult, S_OK);
                    InterlockedIncrement64(&m_copySuccess);
                    MarkGuestFrameAcceptedLocked();
                    return S_OK;
                }
            }

            if (sourceDescription.SampleDesc.Count > 1 && backBufferDescription.SampleDesc.Count == 1)
            {
                m_context->ResolveSubresource(
                    backBuffer.Get(),
                    0,
                    source.Get(),
                    0,
                    backBufferDescription.Format);
            }
            else
            {
                // Copy only mip 0/layer 0 so a renderer texture with additional
                // mip levels remains compatible with the one-level backbuffer.
                m_context->CopySubresourceRegion(backBuffer.Get(), 0, 0, 0, 0, source.Get(), 0, nullptr);
            }

            InterlockedExchange(&m_lastCopyResult, S_OK);
            InterlockedIncrement64(&m_copySuccess);
            MarkGuestFrameAcceptedLocked();
            return S_OK;
        }

        HRESULT PresentRendererFrame(UINT syncInterval = 1)
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);
            if (syncInterval > 1) return E_INVALIDARG;
            InterlockedIncrement64(&m_presentAttempts);
            if (!m_initialized || !m_swapChain)
            {
                InterlockedExchange(&m_lastPresentResult, E_FAIL);
                return E_FAIL;
            }

            HRESULT ownership = ValidateGraphicsThreadLocked();
            if (FAILED(ownership))
            {
                return ownership;
            }

            // Once the CoreHost owns presentation, the backbuffer already
            // contains the guest frame copied by CopyRendererTexture. Drawing
            // the shell here would overwrite it with the frontend immediately
            // before Present (or blend both UIs together).
            if (m_imguiShell.IsInitialized() && !m_coreSessionActive)
            {
                m_imguiShell.Render();
            }
            DrawGameFpsLocked();

            InterlockedIncrement64(&m_guestFramesSubmittedToSwapChain);
            HRESULT result = m_swapChain->Present(syncInterval, 0);
            InterlockedExchange(&m_lastPresentResult, result);
            if (SUCCEEDED(result))
            {
                InterlockedIncrement64(&m_presentSuccess);
                InterlockedIncrement64(&m_visibleGuestFrames);
                m_presentationState = SessionPresentationState::GameVisible;
                CountGameFrameLocked();
            }
            else if (m_device)
            {
                const HRESULT removedReason = m_device->GetDeviceRemovedReason();
                if (FAILED(removedReason))
                {
                    InterlockedExchange(&m_deviceRemovedReason, removedReason);
                    InterlockedExchange64(&m_deviceLostAt, static_cast<LONG64>(GetTickCount64()));
                    m_presentationState = SessionPresentationState::Failed;
                }
            }
            return result;
        }

        void CaptureSourceStatsLocked(ID3D11Texture2D* source, const D3D11_TEXTURE2D_DESC& sourceDescription)
        {
            if (source == nullptr || !m_context || sourceDescription.SampleDesc.Count != 1 ||
                sourceDescription.ArraySize != 1 || sourceDescription.MipLevels != 1 ||
                sourceDescription.Width == 0 || sourceDescription.Height == 0)
            {
                return;
            }

            D3D11_TEXTURE2D_DESC stagingDescription = sourceDescription;
            stagingDescription.Usage = D3D11_USAGE_STAGING;
            stagingDescription.BindFlags = 0;
            stagingDescription.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            stagingDescription.MiscFlags = 0;

            ComPtr<ID3D11Texture2D> staging;
            HRESULT result = m_device->CreateTexture2D(&stagingDescription, nullptr, &staging);
            if (FAILED(result))
            {
                WriteSourceStats("sourceStats createStaging failed hr=0x%08X format=%u size=%ux%u\\n",
                    static_cast<unsigned>(result), static_cast<unsigned>(sourceDescription.Format),
                    sourceDescription.Width, sourceDescription.Height);
                return;
            }

            m_context->CopyResource(staging.Get(), source);
            m_context->Flush();

            D3D11_MAPPED_SUBRESOURCE mapped{};
            result = m_context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped);
            if (FAILED(result))
            {
                WriteSourceStats("sourceStats map failed hr=0x%08X format=%u size=%ux%u\\n",
                    static_cast<unsigned>(result), static_cast<unsigned>(sourceDescription.Format),
                    sourceDescription.Width, sourceDescription.Height);
                return;
            }

            uint64_t sampled = 0;
            uint64_t nonZero = 0;
            uint64_t colored = 0;
            uint64_t energy = 0;
            uint32_t minByte = 255;
            uint32_t maxByte = 0;
            const uint32_t sampleWidth = std::min<uint32_t>(sourceDescription.Width, 64u);
            const uint32_t sampleHeight = std::min<uint32_t>(sourceDescription.Height, 64u);
            for (uint32_t sy = 0; sy < sampleHeight; sy++)
            {
                const uint32_t y = (static_cast<uint64_t>(sy) * sourceDescription.Height) / sampleHeight;
                const auto* row = static_cast<const uint8_t*>(mapped.pData) +
                    static_cast<size_t>(y) * mapped.RowPitch;
                for (uint32_t sx = 0; sx < sampleWidth; sx++)
                {
                    const uint32_t x = (static_cast<uint64_t>(sx) * sourceDescription.Width) / sampleWidth;
                    const uint8_t* pixel = row + static_cast<size_t>(x) * 4u;
                    const uint32_t r = pixel[0];
                    const uint32_t g = pixel[1];
                    const uint32_t b = pixel[2];
                    const uint32_t a = pixel[3];
                    const uint32_t rgb = r + g + b;
                    sampled++;
                    energy += rgb;
                    minByte = (std::min)(minByte, (std::min)((std::min)(r, g), (std::min)(b, a)));
                    maxByte = (std::max)(maxByte, (std::max)((std::max)(r, g), (std::max)(b, a)));
                    if (rgb != 0 || a != 0) nonZero++;
                    if (r != g || g != b) colored++;
                }
            }

            m_context->Unmap(staging.Get(), 0);
            WriteSourceStats(
                "sourceStats ok format=%u size=%ux%u rowPitch=%u samples=%llu nonZero=%llu colored=%llu energy=%llu min=%u max=%u\\n",
                static_cast<unsigned>(sourceDescription.Format), sourceDescription.Width, sourceDescription.Height,
                mapped.RowPitch, static_cast<unsigned long long>(sampled),
                static_cast<unsigned long long>(nonZero), static_cast<unsigned long long>(colored),
                static_cast<unsigned long long>(energy), minByte, maxByte);
        }

        static void WriteSourceStats(const char* format, ...)
        {
            char message[1024]{};
            va_list arguments;
            va_start(arguments, format);
            vsnprintf_s(message, sizeof(message), _TRUNCATE, format, arguments);
            va_end(arguments);

            wchar_t path[1024]{};
            DWORD configuredPath = GetEnvironmentVariableW(
                L"RYUJINX_UWP_D3D11_SOURCE_STATS_PATH", path, ARRAYSIZE(path));
            if (configuredPath == 0 || configuredPath >= ARRAYSIZE(path))
            {
                if (GetTempPathW(ARRAYSIZE(path), path) == 0)
                {
                    return;
                }
                wcscat_s(path, L"RyuboxD3D11SourceStats.log");
            }
            CREATEFILE2_EXTENDED_PARAMETERS createParameters{};
            createParameters.dwSize = sizeof(createParameters);
            createParameters.dwFileAttributes = FILE_ATTRIBUTE_NORMAL;
            HANDLE file = CreateFile2(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                OPEN_ALWAYS, &createParameters);
            if (file != INVALID_HANDLE_VALUE)
            {
                DWORD written = 0;
                WriteFile(file, message, static_cast<DWORD>(strlen(message)), &written, nullptr);
                CloseHandle(file);
            }
            OutputDebugStringA(message);
        }

        void DrawGameFpsLocked()
        {
            if (!m_coreSessionActive || !Ryujinx::Uwp::Native::g_showFpsCounter.load(std::memory_order_relaxed)
                || !m_imguiShell.IsInitialized()) return;
            HRESULT hr = m_imguiShell.RenderGameOverlay(m_gameFps);
            if (FAILED(hr) && !m_fpsOverlayFailed)
            {
                m_fpsOverlayFailed = true;
                wchar_t message[128]{};
                swprintf_s(message, L"Ryubox FPS overlay failed: HRESULT=0x%08X\n", static_cast<unsigned>(hr));
                OutputDebugStringW(message);
            }
        }

        void CountGameFrameLocked()
        {
            if (!m_coreSessionActive) return;
            if (m_freshGuestFrame) ++m_gameFrameSamples;
            m_freshGuestFrame = false;
            const ULONGLONG now = GetTickCount64();
            const ULONGLONG elapsed = now - m_gameFpsSampleAt;
            if (elapsed >= 500)
            {
                m_gameFps = static_cast<float>(1000.0 * m_gameFrameSamples / elapsed);
                m_gameFrameSamples = 0;
                m_gameFpsSampleAt = now;
            }
        }

        HRESULT CaptureBackBuffer(
            void* pixels,
            uint32_t capacity,
            uint32_t* width,
            uint32_t* height,
            uint32_t* rowPitch)
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);

            if (!m_initialized || !m_swapChain || !m_device || !m_context ||
                pixels == nullptr || width == nullptr || height == nullptr || rowPitch == nullptr)
            {
                return E_INVALIDARG;
            }

            ComPtr<ID3D11Texture2D> backBuffer;
            HRESULT result = m_swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
            if (FAILED(result))
            {
                return result;
            }

            D3D11_TEXTURE2D_DESC description{};
            backBuffer->GetDesc(&description);
            if (description.SampleDesc.Count != 1 || description.ArraySize != 1 ||
                description.Width > (UINT32_MAX / 4u))
            {
                return E_NOTIMPL;
            }

            const uint32_t packedRowPitch = description.Width * 4u;
            const uint64_t requiredBytes = static_cast<uint64_t>(packedRowPitch) * description.Height;
            if (requiredBytes > capacity)
            {
                return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
            }

            switch (description.Format)
            {
                case DXGI_FORMAT_R8G8B8A8_UNORM:
                case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
                case DXGI_FORMAT_B8G8R8A8_UNORM:
                case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
                    break;
                default:
                    return E_NOTIMPL;
            }

            D3D11_TEXTURE2D_DESC stagingDescription = description;
            stagingDescription.Usage = D3D11_USAGE_STAGING;
            stagingDescription.BindFlags = 0;
            stagingDescription.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            stagingDescription.MiscFlags = 0;

            ComPtr<ID3D11Texture2D> staging;
            result = m_device->CreateTexture2D(&stagingDescription, nullptr, &staging);
            if (FAILED(result))
            {
                return result;
            }

            m_context->CopyResource(staging.Get(), backBuffer.Get());

            D3D11_MAPPED_SUBRESOURCE mapped{};
            result = m_context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped);
            if (FAILED(result))
            {
                return result;
            }

            auto* destination = static_cast<uint8_t*>(pixels);
            const auto* source = static_cast<const uint8_t*>(mapped.pData);
            const bool isBgra = description.Format == DXGI_FORMAT_B8G8R8A8_UNORM ||
                description.Format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;

            for (uint32_t y = 0; y < description.Height; y++)
            {
                const uint8_t* sourceRow = source + static_cast<size_t>(mapped.RowPitch) * y;
                uint8_t* destinationRow = destination + static_cast<size_t>(packedRowPitch) * y;
                if (!isBgra)
                {
                    std::memcpy(destinationRow, sourceRow, packedRowPitch);
                    continue;
                }

                for (uint32_t x = 0; x < description.Width; x++)
                {
                    destinationRow[x * 4u] = sourceRow[x * 4u + 2u];
                    destinationRow[x * 4u + 1u] = sourceRow[x * 4u + 1u];
                    destinationRow[x * 4u + 2u] = sourceRow[x * 4u];
                    destinationRow[x * 4u + 3u] = sourceRow[x * 4u + 3u];
                }
            }

            m_context->Unmap(staging.Get(), 0);
            *width = description.Width;
            *height = description.Height;
            *rowPitch = packedRowPitch;
            return S_OK;
        }

        HRESULT GetDeviceStatus()
        {
            std::unique_lock<std::recursive_mutex> guard(m_mutex, std::try_to_lock);

            if (!guard.owns_lock())
            {
                return DXGI_ERROR_WAS_STILL_DRAWING;
            }

            return m_initialized && m_device
                ? m_device->GetDeviceRemovedReason()
                : E_FAIL;
        }

        void SetImGuiInput(const Ryujinx::Uwp::Native::ImGuiShellInput& input)
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);
            if (!m_coreSessionActive && m_imguiShell.IsInitialized())
            {
                m_imguiShell.SetInput(input);
            }
        }

        void SetImGuiPointer(const Ryujinx::Uwp::Native::ImGuiShellPointer& pointer)
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);
            if (!m_coreSessionActive && m_imguiShell.IsInitialized())
            {
                m_imguiShell.SetPointer(pointer);
            }
        }

        void SetImGuiCatalogue(const wchar_t* titles)
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);
            if (m_imguiShell.IsInitialized())
            {
                m_imguiShell.SetCatalogue(titles);
            }
        }

        HRESULT SetImGuiCover(int index, const void* pixels, uint32_t width, uint32_t height, uint32_t rowPitch)
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);
            if (!m_imguiShell.IsInitialized() || m_coreSessionActive)
            {
                return E_UNEXPECTED;
            }
            return m_imguiShell.SetCover(index, pixels, width, height, rowPitch);
        }

        void SetImGuiStatus(const wchar_t* keysStatus, const wchar_t* firmwareStatus)
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);
            if (m_imguiShell.IsInitialized())
            {
                m_imguiShell.SetStatus(keysStatus, firmwareStatus);
            }
        }

        void SetImGuiOperation(const wchar_t* message, float progress, int state)
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);
            if (m_imguiShell.IsInitialized())
            {
                m_imguiShell.SetOperation(message, progress, state);
            }
        }

        void SetImGuiOnboardingState(bool completed, int language, int theme,
            bool keysReady, bool firmwareReady, bool gamesReady)
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);
            if (m_imguiShell.IsInitialized())
            {
                m_imguiShell.SetOnboardingState(completed, language, theme,
                    keysReady, firmwareReady, gamesReady);
            }
        }

        void SetImGuiPendingAction(int action)
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);
            if (!m_coreSessionActive && m_imguiShell.IsInitialized())
            {
                m_imguiShell.SetPendingAction(action);
            }
        }

        int ConsumeImGuiAction()
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);
            return m_imguiShell.IsInitialized() && !m_coreSessionActive
                ? m_imguiShell.ConsumeAction()
                : -1;
        }

        HRESULT Initialize(IUnknown* panel, uint32_t width, uint32_t height)
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);

            if (panel == nullptr)
            {
                return E_INVALIDARG;
            }

            Shutdown();

            HRESULT result = CreateDevice(D3D_DRIVER_TYPE_HARDWARE);
            if (FAILED(result))
            {
                // WARP keeps local validation possible on machines where a
                // hardware D3D11 device is unavailable. Xbox normally uses
                // the hardware path; this is only a safe fallback.
                result = CreateDevice(D3D_DRIVER_TYPE_WARP);
            }
            if (FAILED(result))
            {
                return result;
            }

            result = CreateSwapChain(width, height);
            if (FAILED(result))
            {
                return result;
            }

            ComPtr<ISwapChainPanelNative> panelNative;
            result = panel->QueryInterface(IID_PPV_ARGS(&panelNative));
            if (FAILED(result))
            {
                return result;
            }

            result = panelNative->SetSwapChain(m_swapChain.Get());
            if (FAILED(result))
            {
                return result;
            }

            result = CreateRenderResources();
            if (FAILED(result))
            {
                return result;
            }

            result = m_imguiShell.Initialize(
                m_device.Get(),
                m_context.Get(),
                m_renderTarget.Get(),
                m_width,
                m_height);
            if (FAILED(result))
            {
                return result;
            }

            m_initialized = true;
            m_presentationOwner = PresentationOwner::Frontend;
            m_presentationState = SessionPresentationState::FrontendVisible;
            result = ApplyCompositionScaleLocked();
            if (FAILED(result))
            {
                m_initialized = false;
                return result;
            }

            return Render();
        }

        HRESULT InitializeForWindow(HWND window, uint32_t width, uint32_t height)
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);

            if (window == nullptr)
            {
                return E_INVALIDARG;
            }

            wchar_t diagnosticNoPresent[2]{};
            m_skipPresentForDiagnostics = GetEnvironmentVariableW(
                L"RYUBOX_DIAGNOSTIC_NO_PRESENT", diagnosticNoPresent,
                static_cast<DWORD>(std::size(diagnosticNoPresent))) > 0 &&
                diagnosticNoPresent[0] == L'1';

            Shutdown();

            HRESULT result = CreateDevice(D3D_DRIVER_TYPE_HARDWARE);
            if (FAILED(result))
            {
                result = CreateDevice(D3D_DRIVER_TYPE_WARP);
            }
            if (FAILED(result))
            {
                return result;
            }

            result = CreateSwapChainForWindow(window, width, height);
            if (FAILED(result))
            {
                return result;
            }

            result = CreateRenderResources();
            if (FAILED(result))
            {
                return result;
            }

            result = m_imguiShell.Initialize(
                m_device.Get(),
                m_context.Get(),
                m_renderTarget.Get(),
                m_width,
                m_height);
            if (FAILED(result))
            {
                return result;
            }

            m_initialized = true;
            m_presentationOwner = PresentationOwner::Frontend;
            m_presentationState = SessionPresentationState::FrontendVisible;
            return Render();
        }

        HRESULT Resize(uint32_t width, uint32_t height)
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);

            if (!m_initialized || !m_swapChain)
            {
                return S_FALSE;
            }

            width = AtLeastOne(width);
            height = AtLeastOne(height);

            // The D3D11 Xbox path is fixed to the Switch handheld surface.
            // XAML SizeChanged can fire repeatedly while a title is running;
            // never tear down the active backbuffer for a no-op resize.
            if (width == m_width && height == m_height)
            {
                return S_FALSE;
            }

            if (m_coreSessionActive)
            {
                const DWORD currentThread = GetCurrentThreadId();
                if (m_graphicsThreadId == 0 || currentThread != m_graphicsThreadId)
                {
                    return HRESULT_FROM_WIN32(ERROR_BUSY);
                }
            }

            if (m_context)
            {
                m_context->OMSetRenderTargets(0, nullptr, nullptr);
                m_context->Flush();
            }
            m_renderTarget.Reset();
            m_uploadTexture.Reset();
            m_imguiShell.ClearRenderTarget();

            HRESULT result = m_swapChain->ResizeBuffers(
                RenderTargetCount,
                width,
                height,
                DXGI_FORMAT_R8G8B8A8_UNORM,
                0);
            if (FAILED(result))
            {
                return result;
            }

            m_width = width;
            m_height = height;
            HRESULT resources = CreateRenderResources();
            if (FAILED(resources))
            {
                return resources;
            }

            if (m_imguiShell.IsInitialized())
            {
                HRESULT shellResize = m_imguiShell.Resize(m_renderTarget.Get(), m_width, m_height);
                if (FAILED(shellResize))
                {
                    return shellResize;
                }
            }

            return ApplyCompositionScaleLocked();
        }

        HRESULT Render()
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);

            if (!m_initialized || !m_context || !m_renderTarget)
            {
                return S_FALSE;
            }

            // Once the emulator takes ownership, only guest frames may touch
            // or present the immediate context. The frontend remains visible
            // through the same SwapChainPanel but stops drawing test frames.
            if (m_coreSessionActive)
            {
                return S_FALSE;
            }

            constexpr FLOAT clearColor[] = { 0.035f, 0.055f, 0.090f, 1.0f };
            ID3D11RenderTargetView* renderTargets[] = { m_renderTarget.Get() };
            m_context->OMSetRenderTargets(1, renderTargets, nullptr);
            m_context->ClearRenderTargetView(m_renderTarget.Get(), clearColor);

            if (m_imguiShell.IsInitialized())
            {
                HRESULT shellResult = m_imguiShell.Render();
                if (FAILED(shellResult))
                {
                    return shellResult;
                }
            }

            if (m_skipPresentForDiagnostics)
            {
                return S_OK;
            }

            InterlockedIncrement64(&m_frontendFramesSubmitted);
            return m_swapChain->Present(1, 0);
        }

        void Shutdown()
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);

            // The CoreHost owns the device and immediate context between
            // BeginCoreSession and EndCoreSession. XAML lifecycle or a stale
            // settings event must never release those COM objects underneath
            // the graphics thread.
            if (m_coreSessionActive)
            {
                return;
            }

            if (m_context)
            {
                m_context->OMSetRenderTargets(0, nullptr, nullptr);
                m_context->Flush();
            }

            m_imguiShell.Shutdown();
            m_renderTarget.Reset();
            m_uploadTexture.Reset();
            m_blitVertexShader.Reset();
            m_blitPixelShader.Reset();
            m_blitSampler.Reset();
            m_blitConstants.Reset();
            m_presentContext.Reset();
            m_swapChain.Reset();
            m_context.Reset();
            m_device.Reset();
            m_featureLevel = D3D_FEATURE_LEVEL_9_1;
            m_width = 0;
            m_height = 0;
            m_coreSessionActive = false;
            m_presentationOwner = PresentationOwner::Frontend;
            m_presentationState = SessionPresentationState::FrontendVisible;
            m_graphicsThreadId = 0;
            m_multithreadProtected = false;
            m_compositionScaleX = 1.0f;
            m_compositionScaleY = 1.0f;
            m_isCompositionSwapChain = false;
            m_initialized = false;
        }

        uint32_t GetFeatureLevel() const
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);
            return static_cast<uint32_t>(m_featureLevel);
        }

        HRESULT BeginCoreSession(uint64_t sessionId = 0)
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);
            if (!m_initialized || !m_device || !m_context || !m_swapChain)
            {
                return E_FAIL;
            }

            const HRESULT deviceStatus = m_device->GetDeviceRemovedReason();
            if (FAILED(deviceStatus))
            {
                InterlockedExchange(&m_deviceRemovedReason, deviceStatus);
                InterlockedExchange64(&m_deviceLostAt, static_cast<LONG64>(GetTickCount64()));
                m_presentationState = SessionPresentationState::Failed;
                return deviceStatus;
            }

            ResetSessionTelemetryLocked();
            m_sessionId = sessionId != 0 ? sessionId : ++m_nativeSessionSequence;
            InterlockedIncrement64(&m_beginCount);

            m_coreSessionActive = true;
            m_presentationOwner = PresentationOwner::Game;
            m_presentationState = SessionPresentationState::WaitingFirstGuestFrame;
            m_gameFps = 0.0f;
            m_gameFrameSamples = 0;
            m_gameFpsSampleAt = GetTickCount64();
            m_freshGuestFrame = false;
            m_fpsOverlayFailed = false;
            m_graphicsThreadId = 0;
            if (m_imguiShell.IsInitialized())
            {
                m_imguiShell.SetPendingAction(-1);
                m_imguiShell.SetInput({});
            }
            return S_OK;
        }

        HRESULT BindGraphicsThread()
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);
            InterlockedIncrement64(&m_bindCount);
            if (!m_initialized || !m_coreSessionActive)
            {
                return E_UNEXPECTED;
            }

            const DWORD currentThread = GetCurrentThreadId();
            if (m_graphicsThreadId != 0 && m_graphicsThreadId != currentThread)
            {
                return RPC_E_WRONG_THREAD;
            }

            m_graphicsThreadId = currentThread;
            return S_OK;
        }

        HRESULT GetPresentationTelemetry(D3D11PresentationTelemetry* telemetry) const
        {
            if (telemetry == nullptr)
            {
                return E_POINTER;
            }

            std::lock_guard<std::recursive_mutex> guard(m_mutex);
            D3D11PresentationTelemetry value{};
            value.beginCount = static_cast<uint64_t>(InterlockedCompareExchange64(const_cast<volatile LONG64*>(&m_beginCount), 0, 0));
            value.bindCount = static_cast<uint64_t>(InterlockedCompareExchange64(const_cast<volatile LONG64*>(&m_bindCount), 0, 0));
            value.copyAttempts = static_cast<uint64_t>(InterlockedCompareExchange64(const_cast<volatile LONG64*>(&m_copyAttempts), 0, 0));
            value.copySuccess = static_cast<uint64_t>(InterlockedCompareExchange64(const_cast<volatile LONG64*>(&m_copySuccess), 0, 0));
            value.presentAttempts = static_cast<uint64_t>(InterlockedCompareExchange64(const_cast<volatile LONG64*>(&m_presentAttempts), 0, 0));
            value.presentSuccess = static_cast<uint64_t>(InterlockedCompareExchange64(const_cast<volatile LONG64*>(&m_presentSuccess), 0, 0));
            value.uploadAttempts = static_cast<uint64_t>(InterlockedCompareExchange64(const_cast<volatile LONG64*>(&m_uploadAttempts), 0, 0));
            value.uploadSuccess = static_cast<uint64_t>(InterlockedCompareExchange64(const_cast<volatile LONG64*>(&m_uploadSuccess), 0, 0));
            value.lastCopyResult = InterlockedCompareExchange(const_cast<volatile LONG*>(&m_lastCopyResult), 0, 0);
            value.lastPresentResult = InterlockedCompareExchange(const_cast<volatile LONG*>(&m_lastPresentResult), 0, 0);
            value.lastUploadResult = InterlockedCompareExchange(const_cast<volatile LONG*>(&m_lastUploadResult), 0, 0);
            value.graphicsThreadId = m_graphicsThreadId;
            value.width = m_width;
            value.height = m_height;

            if (m_swapChain)
            {
                DXGI_SWAP_CHAIN_DESC1 description{};
                if (SUCCEEDED(m_swapChain->GetDesc1(&description)))
                {
                    value.format = static_cast<uint32_t>(description.Format);
            value.bufferCount = description.BufferCount;
                }
            }

            value.sessionId = m_sessionId;
            value.deviceGeneration = m_deviceGeneration;
            value.visibleGuestFrames = static_cast<uint64_t>(InterlockedCompareExchange64(
                const_cast<volatile LONG64*>(&m_visibleGuestFrames), 0, 0));
            value.guestFramesAcceptedByPresenter = static_cast<uint64_t>(InterlockedCompareExchange64(
                const_cast<volatile LONG64*>(&m_guestFramesAcceptedByPresenter), 0, 0));
            value.guestFramesSubmittedToSwapChain = static_cast<uint64_t>(InterlockedCompareExchange64(
                const_cast<volatile LONG64*>(&m_guestFramesSubmittedToSwapChain), 0, 0));
            value.frontendFramesSubmitted = static_cast<uint64_t>(InterlockedCompareExchange64(
                const_cast<volatile LONG64*>(&m_frontendFramesSubmitted), 0, 0));
            value.deviceLostAt = static_cast<uint64_t>(InterlockedCompareExchange64(
                const_cast<volatile LONG64*>(&m_deviceLostAt), 0, 0));
            value.deviceRemovedReason = InterlockedCompareExchange(
                const_cast<volatile LONG*>(&m_deviceRemovedReason), 0, 0);
            value.presentationState = static_cast<uint32_t>(m_presentationState);
            value.presentationOwner = static_cast<uint32_t>(m_presentationOwner);

            *telemetry = value;
            return S_OK;
        }

        HRESULT EndCoreSession(uint64_t sessionId = 0)
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);
            if (sessionId != 0 && m_sessionId != 0 && sessionId != m_sessionId)
            {
                return E_ABORT;
            }

            m_presentationState = SessionPresentationState::ReturningToFrontend;
            m_graphicsThreadId = 0;
            m_coreSessionActive = false;
            m_presentationOwner = PresentationOwner::Frontend;
            if (m_imguiShell.IsInitialized())
            {
                m_imguiShell.SetPendingAction(-1);
                m_imguiShell.SetInput({});
            }
            const HRESULT deviceStatus = m_device ? m_device->GetDeviceRemovedReason() : E_FAIL;
            if (FAILED(deviceStatus))
            {
                InterlockedExchange(&m_deviceRemovedReason, deviceStatus);
                InterlockedExchange64(&m_deviceLostAt, static_cast<LONG64>(GetTickCount64()));
                m_presentationState = SessionPresentationState::Failed;
            }
            else
            {
                m_presentationState = SessionPresentationState::FrontendVisible;
            }
            return deviceStatus;
        }

        uint32_t GetOwnershipState() const
        {
            std::lock_guard<std::recursive_mutex> guard(m_mutex);
            uint32_t state = 0;
            if (m_initialized) state |= 1u;
            if (m_coreSessionActive) state |= 2u;
            if (m_graphicsThreadId != 0) state |= 4u;
            if (m_multithreadProtected) state |= 8u;
            return state;
        }

    private:
        void MarkGuestFrameAcceptedLocked()
        {
            InterlockedIncrement64(&m_guestFramesAcceptedByPresenter);
            m_freshGuestFrame = true;
            if (m_presentationState == SessionPresentationState::WaitingFirstGuestFrame ||
                m_presentationState == SessionPresentationState::Launching)
            {
                m_presentationState = SessionPresentationState::TransitionToGame;
            }
        }

        void ResetSessionTelemetryLocked()
        {
            InterlockedExchange64(&m_beginCount, 0);
            InterlockedExchange64(&m_bindCount, 0);
            InterlockedExchange64(&m_copyAttempts, 0);
            InterlockedExchange64(&m_copySuccess, 0);
            InterlockedExchange64(&m_presentAttempts, 0);
            InterlockedExchange64(&m_presentSuccess, 0);
            InterlockedExchange64(&m_uploadAttempts, 0);
            InterlockedExchange64(&m_uploadSuccess, 0);
            InterlockedExchange64(&m_visibleGuestFrames, 0);
            InterlockedExchange64(&m_guestFramesAcceptedByPresenter, 0);
            InterlockedExchange64(&m_guestFramesSubmittedToSwapChain, 0);
            InterlockedExchange64(&m_frontendFramesSubmitted, 0);
            InterlockedExchange64(&m_deviceLostAt, 0);
            InterlockedExchange(&m_lastCopyResult, S_OK);
            InterlockedExchange(&m_lastPresentResult, S_OK);
            InterlockedExchange(&m_lastUploadResult, S_OK);
            InterlockedExchange(&m_deviceRemovedReason, S_OK);
        }

        HRESULT ValidateGraphicsThreadLocked() const
        {
            if (!m_coreSessionActive)
            {
                return E_UNEXPECTED;
            }

            return m_graphicsThreadId != 0 && GetCurrentThreadId() == m_graphicsThreadId
                ? S_OK
                : RPC_E_WRONG_THREAD;
        }

        HRESULT ApplyCompositionScaleLocked()
        {
            if (!m_isCompositionSwapChain || !m_swapChain)
            {
                // HWND swap chains do not participate in SwapChainPanel
                // composition scaling. Keep desktop validation unchanged.
                return S_FALSE;
            }

            ComPtr<IDXGISwapChain2> swapChain2;
            HRESULT result = m_swapChain.As(&swapChain2);
            if (FAILED(result))
            {
                return result;
            }

            DXGI_MATRIX_3X2_F inverseScale{};
            inverseScale._11 = 1.0f / m_compositionScaleX;
            inverseScale._22 = 1.0f / m_compositionScaleY;
            return swapChain2->SetMatrixTransform(&inverseScale);
        }

        HRESULT CreateDevice(D3D_DRIVER_TYPE driverType)
        {
            m_device.Reset();
            m_context.Reset();
            m_presentContext.Reset();

            UINT creationFlags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
            wchar_t debugLayer[4]{};
            const bool debugRequested = GetEnvironmentVariableW(
                L"RYUJINX_UWP_D3D11_DEBUG",
                debugLayer,
                ARRAYSIZE(debugLayer)) != 0 && debugLayer[0] == L'1';
            if (debugRequested)
            {
                creationFlags |= D3D11_CREATE_DEVICE_DEBUG;
            }

            const D3D_FEATURE_LEVEL featureLevels[] =
            {
                D3D_FEATURE_LEVEL_11_1,
                D3D_FEATURE_LEVEL_11_0,
                D3D_FEATURE_LEVEL_10_1,
            };

            HRESULT result = D3D11CreateDevice(
                nullptr,
                driverType,
                nullptr,
                creationFlags,
                featureLevels,
                ARRAYSIZE(featureLevels),
                D3D11_SDK_VERSION,
                &m_device,
                &m_featureLevel,
                &m_context);

            // Some older UWP device stacks reject 11_1 in the requested
            // array even though they support 11_0. Retry with the portable
            // list before reporting the device as unavailable.
            if (result == E_INVALIDARG)
            {
                const D3D_FEATURE_LEVEL fallbackLevels[] =
                {
                    D3D_FEATURE_LEVEL_11_0,
                    D3D_FEATURE_LEVEL_10_1,
                };

                result = D3D11CreateDevice(
                    nullptr,
                    driverType,
                    nullptr,
                    creationFlags,
                    fallbackLevels,
                    ARRAYSIZE(fallbackLevels),
                    D3D11_SDK_VERSION,
                    &m_device,
                    &m_featureLevel,
                    &m_context);
            }

            // The debug runtime is a development-machine component and is not
            // present on retail Xbox system images. Keep it strictly opt-in
            // and fall back to the production flags when the SDK layer is not
            // installed locally.
            if (FAILED(result) && debugRequested)
            {
                creationFlags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
                const D3D_FEATURE_LEVEL portableLevels[] =
                {
                    D3D_FEATURE_LEVEL_11_0,
                    D3D_FEATURE_LEVEL_10_1,
                };
                result = D3D11CreateDevice(
                    nullptr,
                    driverType,
                    nullptr,
                    creationFlags,
                    portableLevels,
                    ARRAYSIZE(portableLevels),
                    D3D11_SDK_VERSION,
                    &m_device,
                    &m_featureLevel,
                    &m_context);
            }

            if (FAILED(result))
            {
                return result;
            }

            // Presentation must not overwrite the state cached by the managed
            // GAL on the shared immediate context. Execute its commands with
            // RestoreContextState=TRUE instead.
            result = m_device->CreateDeferredContext(0, &m_presentContext);
            if (FAILED(result))
            {
                m_presentContext.Reset();
                return result;
            }

            // The managed GAL and the native composition host share this
            // immediate context. Protect it at the D3D runtime boundary so an
            // accidental lifecycle call cannot race a guest GPU command.
            ComPtr<ID3D11Multithread> multithread;
            if (SUCCEEDED(m_context.As(&multithread)))
            {
                m_multithreadProtected = multithread->SetMultithreadProtected(TRUE) != FALSE;
            }

            ComPtr<IDXGIDevice1> dxgiDevice;
            if (SUCCEEDED(m_device.As(&dxgiDevice)))
            {
                // Bound the composition queue to avoid consuming the Xbox
                // memory budget with frames the display has not consumed.
                dxgiDevice->SetMaximumFrameLatency(2);
            }

            ++m_deviceGeneration;
            InterlockedExchange(&m_deviceRemovedReason, S_OK);
            InterlockedExchange64(&m_deviceLostAt, 0);

            return S_OK;
        }

        HRESULT CreateSwapChain(uint32_t width, uint32_t height)
        {
            ComPtr<IDXGIFactory2> factory;
            HRESULT result = CreateDXGIFactory2(0, IID_PPV_ARGS(&factory));
            if (FAILED(result))
            {
                return result;
            }

            DXGI_SWAP_CHAIN_DESC1 description{};
            description.Width = AtLeastOne(width);
            description.Height = AtLeastOne(height);
            description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
            description.SampleDesc.Count = 1;
            description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
            description.BufferCount = RenderTargetCount;
            description.Scaling = DXGI_SCALING_STRETCH;
            description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
            description.AlphaMode = DXGI_ALPHA_MODE_IGNORE;

            m_width = description.Width;
            m_height = description.Height;

            ComPtr<IDXGISwapChain1> swapChain1;
            result = factory->CreateSwapChainForComposition(
                m_device.Get(),
                &description,
                nullptr,
                &swapChain1);
            if (FAILED(result))
            {
                return result;
            }

            result = swapChain1.As(&m_swapChain);
            if (SUCCEEDED(result))
            {
                m_isCompositionSwapChain = true;
            }

            return result;
        }

        HRESULT CreateSwapChainForWindow(HWND window, uint32_t width, uint32_t height)
        {
            ComPtr<IDXGIFactory2> factory;
            HRESULT result = CreateDXGIFactory2(0, IID_PPV_ARGS(&factory));
            if (FAILED(result))
            {
                return result;
            }

            DXGI_SWAP_CHAIN_DESC1 description{};
            description.Width = AtLeastOne(width);
            description.Height = AtLeastOne(height);
            description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
            description.SampleDesc.Count = 1;
            description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
            description.BufferCount = RenderTargetCount;
            description.Scaling = DXGI_SCALING_STRETCH;
            description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
            description.AlphaMode = DXGI_ALPHA_MODE_IGNORE;

            m_width = description.Width;
            m_height = description.Height;

            ComPtr<IDXGISwapChain1> swapChain1;
            result = factory->CreateSwapChainForHwnd(
                m_device.Get(),
                window,
                &description,
                nullptr,
                nullptr,
                &swapChain1);
            if (FAILED(result))
            {
                return result;
            }

            factory->MakeWindowAssociation(window, DXGI_MWA_NO_ALT_ENTER);
            result = swapChain1.As(&m_swapChain);
            if (SUCCEEDED(result))
            {
                m_isCompositionSwapChain = false;
            }

            return result;
        }

        HRESULT CreateRenderResources()
        {
            // D3D11 rotates its writable buffer behind index zero. Creating
            // RTVs for every DXGI buffer as in D3D12 fails on this host.
            ComPtr<ID3D11Texture2D> backBuffer;
            HRESULT result = m_swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
            if (FAILED(result)) return result;
            result = m_device->CreateRenderTargetView(backBuffer.Get(), nullptr, &m_renderTarget);
            if (FAILED(result)) return result;

            result = CreateUploadTexture();
            if (FAILED(result))
            {
                return result;
            }

            // Keep the CPU compatibility uploader as a fallback, but prefer
            // this small built-in GPU pass for matching-sized presentation
            // surfaces. It avoids a guest texture readback on every frame.
            CreatePresentationBlitResources();
            return S_OK;
        }

        HRESULT CreatePresentationBlitResources()
        {
            if (m_blitVertexShader && m_blitPixelShader && m_blitSampler && m_blitConstants)
            {
                return S_OK;
            }

            static constexpr char VertexShader[] = R"(
struct VsOut
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

VsOut main(uint vertexId : SV_VertexID)
{
    float2 positions[3] =
    {
        float2(-1.0, -1.0),
        float2(-1.0,  3.0),
        float2( 3.0, -1.0)
    };
    float2 uvs[3] =
    {
        float2(0.0, 1.0),
        float2(0.0, -1.0),
        float2(2.0, 1.0)
    };

    VsOut output;
    output.position = float4(positions[vertexId], 0.0, 1.0);
    output.uv = uvs[vertexId];
    return output;
}
)";

            static constexpr char PixelShader[] = R"(
cbuffer Transform : register(b0)
{
    float4 options;
    float4 crop;
};

Texture2D sourceTexture : register(t0);
SamplerState sourceSampler : register(s0);

float4 main(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    if (options.x > 0.5)
    {
        uv.y = 1.0 - uv.y;
    }
    
    // Scale and crop UV coordinates
    float u = crop.x + uv.x * (crop.z - crop.x);
    float v = crop.y + uv.y * (crop.w - crop.y);
    
    return sourceTexture.Sample(sourceSampler, float2(u, v));
}
)";

            ComPtr<ID3DBlob> vertexBytecode;
            ComPtr<ID3DBlob> pixelBytecode;
            ComPtr<ID3DBlob> errors;
            HRESULT result = D3DCompile2(
                VertexShader,
                std::strlen(VertexShader),
                "Ryubox.PresentationBlit.hlsl",
                nullptr,
                nullptr,
                "main",
                "vs_5_0",
                D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_OPTIMIZATION_LEVEL1,
                0,
                0,
                nullptr,
                0,
                &vertexBytecode,
                &errors);
            if (FAILED(result))
            {
                return result;
            }

            errors.Reset();
            result = D3DCompile2(
                PixelShader,
                std::strlen(PixelShader),
                "Ryubox.PresentationBlit.hlsl",
                nullptr,
                nullptr,
                "main",
                "ps_5_0",
                D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_OPTIMIZATION_LEVEL1,
                0,
                0,
                nullptr,
                0,
                &pixelBytecode,
                &errors);
            if (FAILED(result))
            {
                return result;
            }

            result = m_device->CreateVertexShader(
                vertexBytecode->GetBufferPointer(),
                vertexBytecode->GetBufferSize(),
                nullptr,
                &m_blitVertexShader);
            if (FAILED(result))
            {
                return result;
            }

            result = m_device->CreatePixelShader(
                pixelBytecode->GetBufferPointer(),
                pixelBytecode->GetBufferSize(),
                nullptr,
                &m_blitPixelShader);
            if (FAILED(result))
            {
                return result;
            }

            static constexpr char PixelShaderNV12[] = R"(
cbuffer Transform : register(b0)
{
    float4 options;
    float4 crop;
};

Texture2D<float> lumaTexture : register(t0);
Texture2D<float2> chromaTexture : register(t1);
SamplerState sourceSampler : register(s0);

float4 main(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    if (options.x > 0.5)
    {
        uv.y = 1.0 - uv.y;
    }
    
    // Scale and crop UV coordinates
    float u = crop.x + uv.x * (crop.z - crop.x);
    float v = crop.y + uv.y * (crop.w - crop.y);
    float2 scaledUv = float2(u, v);
    
    float y = lumaTexture.Sample(sourceSampler, scaledUv);
    float2 uv_chroma = chromaTexture.Sample(sourceSampler, scaledUv);
    float cb = uv_chroma.x - 0.5;
    float cr = uv_chroma.y - 0.5;
    
    // BT.709
    float r = y + 1.5748 * cr;
    float g = y - 0.1873 * cb - 0.4681 * cr;
    float b = y + 1.8556 * cb;
    
    return float4(saturate(r), saturate(g), saturate(b), 1.0);
}
)";
            ComPtr<ID3DBlob> pixelBytecodeNV12;
            errors.Reset();
            result = D3DCompile2(
                PixelShaderNV12,
                std::strlen(PixelShaderNV12),
                "Ryubox.PresentationBlitNV12.hlsl",
                nullptr,
                nullptr,
                "main",
                "ps_5_0",
                D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_OPTIMIZATION_LEVEL1,
                0,
                0,
                nullptr,
                0,
                &pixelBytecodeNV12,
                &errors);
            if (FAILED(result))
            {
                return result;
            }

            result = m_device->CreatePixelShader(
                pixelBytecodeNV12->GetBufferPointer(),
                pixelBytecodeNV12->GetBufferSize(),
                nullptr,
                &m_blitPixelShaderNV12);
            if (FAILED(result))
            {
                return result;
            }

            D3D11_SAMPLER_DESC samplerDescription{};
            samplerDescription.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
            samplerDescription.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
            samplerDescription.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
            samplerDescription.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
            samplerDescription.ComparisonFunc = D3D11_COMPARISON_NEVER;
            samplerDescription.MinLOD = 0.0f;
            samplerDescription.MaxLOD = D3D11_FLOAT32_MAX;

            result = m_device->CreateSamplerState(&samplerDescription, &m_blitSampler);
            if (FAILED(result))
            {
                return result;
            }

            D3D11_BUFFER_DESC constantDescription{};
            constantDescription.ByteWidth = 32;
            constantDescription.Usage = D3D11_USAGE_DYNAMIC;
            constantDescription.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
            constantDescription.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
            return m_device->CreateBuffer(&constantDescription, nullptr, &m_blitConstants);
        }

        HRESULT CreateUploadTexture()
        {
            D3D11_TEXTURE2D_DESC description{};
            description.Width = m_width;
            description.Height = m_height;
            description.MipLevels = 1;
            description.ArraySize = 1;
            description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
            description.SampleDesc.Count = 1;
            description.Usage = D3D11_USAGE_DEFAULT;

            return m_device->CreateTexture2D(&description, nullptr, &m_uploadTexture);
        }

        bool m_initialized = false;
        uint32_t m_width = 0;
        uint32_t m_height = 0;
        DWORD m_graphicsThreadId = 0;
        bool m_coreSessionActive = false;
        bool m_multithreadProtected = false;
        D3D_FEATURE_LEVEL m_featureLevel = D3D_FEATURE_LEVEL_9_1;
        mutable std::recursive_mutex m_mutex;
        ComPtr<ID3D11Device> m_device;
        ComPtr<ID3D11DeviceContext> m_context;
        ComPtr<ID3D11DeviceContext> m_presentContext;
        ComPtr<IDXGISwapChain1> m_swapChain;
        ComPtr<ID3D11RenderTargetView> m_renderTarget;
        ComPtr<ID3D11Texture2D> m_uploadTexture;
        ComPtr<ID3D11VertexShader> m_blitVertexShader;
        ComPtr<ID3D11PixelShader> m_blitPixelShader;
        ComPtr<ID3D11PixelShader> m_blitPixelShaderNV12;
        ComPtr<ID3D11SamplerState> m_blitSampler;
        ComPtr<ID3D11Buffer> m_blitConstants;
        Ryujinx::Uwp::Native::ImGuiD3D11Shell m_imguiShell;
        float m_gameFps = 0.0f;
        uint32_t m_gameFrameSamples = 0;
        ULONGLONG m_gameFpsSampleAt = 0;
        bool m_freshGuestFrame = false;
        bool m_fpsOverlayFailed = false;
        bool m_sourceStatsCaptured = false;
        uint64_t m_uploadStatsSamples = 0;
        float m_compositionScaleX = 1.0f;
        float m_compositionScaleY = 1.0f;
        bool m_isCompositionSwapChain = false;
        bool m_skipPresentForDiagnostics = false;
        uint64_t m_sessionId = 0;
        uint64_t m_nativeSessionSequence = 0;
        uint64_t m_deviceGeneration = 0;
        SessionPresentationState m_presentationState = SessionPresentationState::FrontendVisible;
        PresentationOwner m_presentationOwner = PresentationOwner::Frontend;
        volatile LONG64 m_beginCount = 0;
        volatile LONG64 m_bindCount = 0;
        volatile LONG64 m_copyAttempts = 0;
        volatile LONG64 m_copySuccess = 0;
        volatile LONG64 m_presentAttempts = 0;
        volatile LONG64 m_presentSuccess = 0;
        volatile LONG64 m_uploadAttempts = 0;
        volatile LONG64 m_uploadSuccess = 0;
        volatile LONG64 m_visibleGuestFrames = 0;
        volatile LONG64 m_guestFramesAcceptedByPresenter = 0;
        volatile LONG64 m_guestFramesSubmittedToSwapChain = 0;
        volatile LONG64 m_frontendFramesSubmitted = 0;
        volatile LONG64 m_deviceLostAt = 0;
        volatile LONG m_lastCopyResult = S_OK;
        volatile LONG m_lastPresentResult = S_OK;
        volatile LONG m_lastUploadResult = S_OK;
        volatile LONG m_deviceRemovedReason = S_OK;
    };

    struct AudioPacket final
    {
        AudioPacket* next = nullptr;
        BYTE* samples = nullptr;
        uint32_t capacity = 0;
        uint64_t identifier = 0;
        uint64_t frameCount = 0;
        volatile LONG completed = 0;
    };

    // XAudio2 is part of the UWP/Xbox platform and therefore avoids shipping
    // SDL/OpenAL desktop binaries in the package. Each Ryujinx output session
    // owns one source voice; sample storage remains native until XAudio2 calls
    // OnBufferEnd, so NativeAOT never exposes movable managed memory to it.
    class XAudio2Session final : public IXAudio2VoiceCallback
    {
    public:
        XAudio2Session() = default;

        HRESULT Initialize(uint32_t sampleRate, uint32_t channelCount, uint32_t bitsPerSample, bool floatingPoint, HANDLE updateEvent)
        {
            if (sampleRate == 0 || channelCount == 0 || channelCount > XAUDIO2_MAX_AUDIO_CHANNELS)
            {
                return E_INVALIDARG;
            }

            InitializeCriticalSection(&m_lock);
            m_lockInitialized = true;
            m_updateEvent = updateEvent;

            HRESULT result = XAudio2Create(&m_engine, 0, XAUDIO2_DEFAULT_PROCESSOR);
            if (FAILED(result))
            {
                return result;
            }

            // Route emulator output as game media explicitly. The default
            // GameEffects category can be ducked by Xbox shell/UI activity;
            // logs from Shovel Knight showed valid non-zero PCM being consumed
            // while the TV remained silent.
            result = m_engine->CreateMasteringVoice(
                &m_masteringVoice,
                XAUDIO2_DEFAULT_CHANNELS,
                XAUDIO2_DEFAULT_SAMPLERATE,
                0,
                nullptr,
                nullptr,
                AudioCategory_GameMedia);
            if (FAILED(result))
            {
                return result;
            }

            WAVEFORMATEX format{};
            format.wFormatTag = floatingPoint ? WAVE_FORMAT_IEEE_FLOAT : WAVE_FORMAT_PCM;
            format.nChannels = static_cast<WORD>(channelCount);
            format.nSamplesPerSec = sampleRate;
            format.wBitsPerSample = static_cast<WORD>(bitsPerSample);
            format.nBlockAlign = static_cast<WORD>((channelCount * bitsPerSample) / 8u);
            format.nAvgBytesPerSec = sampleRate * format.nBlockAlign;

            if (format.nBlockAlign == 0)
            {
                return E_INVALIDARG;
            }

            m_bytesPerFrame = format.nBlockAlign;
            result = m_engine->CreateSourceVoice(
                &m_sourceVoice,
                &format,
                0,
                XAUDIO2_DEFAULT_FREQ_RATIO,
                this);
            return result;
        }

        HRESULT Submit(uint64_t identifier, const void* samples, uint32_t byteCount)
        {
            if (m_sourceVoice == nullptr || samples == nullptr || byteCount == 0 || m_bytesPerFrame == 0)
            {
                return E_INVALIDARG;
            }

            EnterCriticalSection(&m_lock);
            // Renderer sessions reuse registered AudioBuffers without calling
            // UnregisterBuffer between submissions. Retaining every completed
            // packet until unregister would exhaust the packet bound even with
            // ZERO samples in flight. Absence already means consumed in IsConsumed.
            AudioPacket** completedLink = &m_packets;
            while (*completedLink != nullptr)
            {
                AudioPacket* completed = *completedLink;
                if (InterlockedCompareExchange(&completed->completed, 0, 0) != 0)
                {
                    *completedLink = completed->next;
                    FreePacket(completed);
                }
                else completedLink = &completed->next;
            }
            if (m_packetCount >= 256 || m_inFlight >= 64 || byteCount > MaxAudioBytes || m_inFlightBytes + byteCount > MaxAudioBytes)
            {
                LeaveCriticalSection(&m_lock);
                return E_OUTOFMEMORY;
            }
            AudioPacket* packet = static_cast<AudioPacket*>(HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(AudioPacket)));
            if (packet == nullptr)
            {
                LeaveCriticalSection(&m_lock);
                return E_OUTOFMEMORY;
            }

            for (uint32_t i = 0; i < m_poolCount; ++i)
            {
                if (m_pool[i].capacity >= byteCount && m_inFlightBytes + m_pool[i].capacity <= MaxAudioBytes)
                {
                    packet->samples = m_pool[i].samples;
                    packet->capacity = m_pool[i].capacity;
                    m_poolBytes -= packet->capacity;
                    m_pool[i] = m_pool[--m_poolCount];
                    ++m_poolHits;
                    break;
                }
            }
            if (packet->samples == nullptr)
            {
                packet->samples = static_cast<BYTE*>(HeapAlloc(GetProcessHeap(), 0, byteCount));
                packet->capacity = byteCount;
            }
            if (packet->samples == nullptr)
            {
                HeapFree(GetProcessHeap(), 0, packet);
                LeaveCriticalSection(&m_lock);
                return E_OUTOFMEMORY;
            }

            std::memcpy(packet->samples, samples, byteCount);
            packet->identifier = identifier;
            packet->frameCount = byteCount / m_bytesPerFrame;

            ++m_inFlight;
            ++m_packetCount;
            m_inFlightBytes += packet->capacity;
            packet->next = m_packets;
            m_packets = packet;
            LeaveCriticalSection(&m_lock);

            XAUDIO2_BUFFER buffer{};
            buffer.AudioBytes = byteCount;
            buffer.pAudioData = packet->samples;
            buffer.pContext = packet;

            HRESULT result = m_sourceVoice->SubmitSourceBuffer(&buffer);
            if (FAILED(result))
            {
                RemoveAndFreePacket(packet);
            }

            return result;
        }

        bool IsConsumed(uint64_t identifier)
        {
            bool consumed = true;
            EnterCriticalSection(&m_lock);
            for (AudioPacket* packet = m_packets; packet != nullptr; packet = packet->next)
            {
                if (packet->identifier == identifier)
                {
                    consumed = InterlockedCompareExchange(&packet->completed, 0, 0) != 0;
                    break;
                }
            }
            LeaveCriticalSection(&m_lock);
            return consumed;
        }

        void ReleasePacket(uint64_t identifier)
        {
            EnterCriticalSection(&m_lock);
            AudioPacket** link = &m_packets;
            while (*link != nullptr)
            {
                AudioPacket* packet = *link;
                if (packet->identifier == identifier && InterlockedCompareExchange(&packet->completed, 0, 0) != 0)
                {
                    *link = packet->next;
                    FreePacket(packet);
                    break;
                }
                link = &packet->next;
            }
            LeaveCriticalSection(&m_lock);
        }

        HRESULT Start()
        {
            return m_sourceVoice != nullptr ? m_sourceVoice->Start() : E_FAIL;
        }

        HRESULT Stop()
        {
            return m_sourceVoice != nullptr ? m_sourceVoice->Stop() : E_FAIL;
        }

        HRESULT SetVolume(float volume)
        {
            return m_sourceVoice != nullptr ? m_sourceVoice->SetVolume(volume) : E_FAIL;
        }

        uint64_t GetPlayedFrameCount() const
        {
            return static_cast<uint64_t>(InterlockedCompareExchange64(
                const_cast<volatile LONG64*>(&m_playedFrameCount), 0, 0));
        }

        HRESULT GetLastVoiceError() const
        {
            return static_cast<HRESULT>(InterlockedCompareExchange(
                const_cast<volatile LONG*>(&m_lastVoiceError), 0, 0));
        }

        void GetBufferStats(uint64_t* values)
        {
            EnterCriticalSection(&m_lock);
            values[0] = m_inFlight;
            values[1] = m_inFlightBytes;
            values[2] = m_poolCount;
            values[3] = m_poolBytes;
            values[4] = m_poolHits;
            values[5] = m_packetCount;
            LeaveCriticalSection(&m_lock);
        }

        void Shutdown()
        {
            if (InterlockedExchange(&m_closing, 1) != 0)
            {
                return;
            }

            if (m_sourceVoice != nullptr)
            {
                m_sourceVoice->Stop();
                m_sourceVoice->FlushSourceBuffers();
                m_sourceVoice->DestroyVoice();
                m_sourceVoice = nullptr;
            }

            if (m_masteringVoice != nullptr)
            {
                m_masteringVoice->DestroyVoice();
                m_masteringVoice = nullptr;
            }

            m_engine.Reset();

            if (m_lockInitialized)
            {
                EnterCriticalSection(&m_lock);
                AudioPacket* packet = m_packets;
                m_packets = nullptr;
                while (packet != nullptr)
                {
                    AudioPacket* next = packet->next;
                    FreePacket(packet);
                    packet = next;
                }
                while (m_poolCount != 0) HeapFree(GetProcessHeap(), 0, m_pool[--m_poolCount].samples);
                m_poolBytes = 0;
                LeaveCriticalSection(&m_lock);
                DeleteCriticalSection(&m_lock);
                m_lockInitialized = false;
            }
        }

        ~XAudio2Session()
        {
            Shutdown();
        }

        void STDMETHODCALLTYPE OnVoiceProcessingPassStart(UINT32) override { }
        void STDMETHODCALLTYPE OnVoiceProcessingPassEnd() override { }
        void STDMETHODCALLTYPE OnStreamEnd() override { }
        void STDMETHODCALLTYPE OnBufferStart(void*) override { }
        void STDMETHODCALLTYPE OnLoopEnd(void*) override { }

        void STDMETHODCALLTYPE OnBufferEnd(void* context) override
        {
            AudioPacket* packet = static_cast<AudioPacket*>(context);
            if (packet == nullptr)
            {
                return;
            }

            EnterCriticalSection(&m_lock);
            RecycleSamples(packet);
            InterlockedAdd64(&m_playedFrameCount, static_cast<LONG64>(packet->frameCount));
            InterlockedExchange(&packet->completed, 1);
            LeaveCriticalSection(&m_lock);
            if (m_updateEvent != nullptr)
            {
                SetEvent(m_updateEvent);
            }
        }

        void STDMETHODCALLTYPE OnVoiceError(void*, HRESULT error) override
        {
            InterlockedExchange(&m_lastVoiceError, static_cast<LONG>(error));
            if (m_updateEvent != nullptr)
            {
                SetEvent(m_updateEvent);
            }
        }

    private:
        // Called under m_lock. No allocation in the audio callback. A sample
        // block is reusable only after OnBufferEnd or a rejected submission.
        void RecycleSamples(AudioPacket* packet)
        {
            if (packet->samples != nullptr)
            {
                --m_inFlight;
                m_inFlightBytes -= packet->capacity;
                if (m_closing == 0 && m_poolCount < 32 && m_poolBytes + packet->capacity <= MaxAudioBytes)
                {
                    m_pool[m_poolCount++] = { packet->samples, packet->capacity };
                    m_poolBytes += packet->capacity;
                }
                else HeapFree(GetProcessHeap(), 0, packet->samples);
                packet->samples = nullptr;
            }
        }

        void FreePacket(AudioPacket* packet)
        {
            RecycleSamples(packet);
            --m_packetCount;
            HeapFree(GetProcessHeap(), 0, packet);
        }

        void RemoveAndFreePacket(AudioPacket* target)
        {
            EnterCriticalSection(&m_lock);
            AudioPacket** link = &m_packets;
            while (*link != nullptr)
            {
                if (*link == target)
                {
                    *link = target->next;
                    FreePacket(target);
                    break;
                }
                link = &(*link)->next;
            }
            LeaveCriticalSection(&m_lock);
        }

        bool m_lockInitialized = false;
        uint32_t m_bytesPerFrame = 0;
        HANDLE m_updateEvent = nullptr;
        CRITICAL_SECTION m_lock{};
        AudioPacket* m_packets = nullptr;
        static constexpr uint64_t MaxAudioBytes = 8ULL * 1024 * 1024;
        struct SampleBlock { BYTE* samples; uint32_t capacity; };
        SampleBlock m_pool[32]{};
        uint32_t m_poolCount = 0;
        uint64_t m_poolBytes = 0;
        uint64_t m_poolHits = 0;
        uint64_t m_inFlight = 0;
        uint64_t m_packetCount = 0;
        uint64_t m_inFlightBytes = 0;
        ComPtr<IXAudio2> m_engine;
        IXAudio2MasteringVoice* m_masteringVoice = nullptr;
        IXAudio2SourceVoice* m_sourceVoice = nullptr;
        volatile LONG m_closing = 0;
        volatile LONG m_lastVoiceError = S_OK;
        volatile LONG64 m_playedFrameCount = 0;
    };

    GraphicsHost g_host;
    D3D11GraphicsHost g_d3d11Host;
    CoreBridge g_coreBridge;
}

namespace Ryujinx::Uwp::Native
{
    RyuboxFrontendHost g_ryuboxHost;
    std::unique_ptr<UwpImGuiFrontend::IScreenScraperService> g_screenScraper;
    std::unique_ptr<UwpImGuiFrontend::ICompanionService> g_companion;
    std::unique_ptr<UwpImGuiFrontend::Frontend> g_frontend;
    std::unique_ptr<UwpImGuiFrontend::ShellRenderer> g_shellRenderer;
    UwpImGuiFrontend::RyuboxSettingsModel g_settingsModel;

    // Opening a settings page is requested by Frontend::HandleInput. Keep the
    // visibility flag atomic and commit it only at the beginning of the next
    // native frame, after the frontend navigation callback has returned.
    std::atomic<bool> g_showSettings{ false };
    UwpImGuiFrontend::SettingsView g_settingsView;
    std::vector<UwpImGuiFrontend::SettingsExtensionPage> g_settingsExtensions;
    UwpImGuiFrontend::RyuboxSettingsCallbacks g_ryuboxSettingsCallbacks;
    UwpImGuiFrontend::SettingsViewModel g_settingsViewModel;
    UwpImGuiFrontend::SettingsViewServices g_settingsServices;
    UwpImGuiFrontend::SettingsViewCallbacks g_settingsViewCallbacks;
    std::atomic<bool> g_showFpsCounter{ true };
    std::atomic<int> g_pendingFpsChange{ -1 };
    // Settings changes have their own mailbox: a toggle must not overwrite
    // a queued game launch/folder picker action or be lost while one is busy.
    std::atomic<int> g_pendingPtcChange{ -1 };
    std::atomic<bool> g_persistedPtcEnabled{ false };
    // Defer page selection until the next native frame. This prevents a rail
    // activation from mutating SettingsView while Frontend::HandleInput is
    // still unwinding its navigation callback.
    std::atomic<int> g_pendingSettingsPage{ 0 };
    // SetStatus can be called by the managed/UI side while the D3D11 thread
    // is reading the settings strings. Serialize the model to avoid a
    // concurrent std::string reallocation during settings rendering.
    std::mutex g_settingsModelMutex;

    void ShutdownFrontendModel()
    {
        // Stop service-owned workers before DLL/static teardown. Their thread
        // objects live in another translation unit: destruction order cannot
        // guarantee that ScreenScraperService runs before a joinable thread.
        if (g_frontend) g_frontend->Shutdown();
        g_shellRenderer.reset();
        g_frontend.reset();
        g_companion.reset();
        g_screenScraper.reset();
        g_ryuboxHost.ClearCatalogueTextures();
    }

    void EnsureFrontendInitialized()
    {
        if (!g_screenScraper)
        {
            g_screenScraper = UwpImGuiFrontend::CreateScreenScraperService();
            g_companion = UwpImGuiFrontend::CreateCompanionService();
            g_companion->Initialize(g_ryuboxHost, "Ryubox", "Nintendo Switch");
            g_frontend = std::make_unique<UwpImGuiFrontend::Frontend>(g_ryuboxHost, *g_screenScraper, *g_companion);
            g_frontend->Initialize();
            g_frontend->SetAnimateLaunches(true);
            g_shellRenderer = std::make_unique<UwpImGuiFrontend::ShellRenderer>(g_ryuboxHost);
            g_settingsModel = UwpImGuiFrontend::MakeDefaultRyuboxSettingsModel();
            g_settingsModel.cpu.ptc.value = g_persistedPtcEnabled.load(std::memory_order_acquire);
            g_settingsModel.graphics.showFpsCounter.value = g_showFpsCounter.load(std::memory_order_acquire);
            
            g_ryuboxSettingsCallbacks.action = [](UwpImGuiFrontend::RyuboxSettingsAction action) {
                using Action = UwpImGuiFrontend::RyuboxSettingsAction;
                switch (action) {
                case Action::AddGameDirectory: if (!QueueD3D12FrontendAction(-1000)) g_d3d11Host.SetImGuiPendingAction(-1000); break;
                case Action::InstallFirmware: if (!QueueD3D12FrontendAction(-1001)) g_d3d11Host.SetImGuiPendingAction(-1001); break;
                case Action::ImportProdKeys: if (!QueueD3D12FrontendAction(-1003)) g_d3d11Host.SetImGuiPendingAction(-1003); break;
                case Action::ImportTitleKeys: if (!QueueD3D12FrontendAction(-1004)) g_d3d11Host.SetImGuiPendingAction(-1004); break;
                case Action::RefreshLibrary: if (!QueueD3D12FrontendAction(-1005)) g_d3d11Host.SetImGuiPendingAction(-1005); break;
                case Action::RemoveGameDirectory: if (!QueueD3D12FrontendAction(-1006)) g_d3d11Host.SetImGuiPendingAction(-1006); break;
                case Action::PickCoversFolder: if (!QueueD3D12FrontendAction(-1007)) g_d3d11Host.SetImGuiPendingAction(-1007); break;
                default: break;
                }
            };
            g_ryuboxSettingsCallbacks.changed = [](std::string_view id) {
                if (id == "cpu.ptc") {
                    g_persistedPtcEnabled.store(g_settingsModel.cpu.ptc.value, std::memory_order_release);
                    g_pendingPtcChange.store(g_settingsModel.cpu.ptc.value ? 1 : 0,
                        std::memory_order_release);
                }
                if (id == "graphics.showFpsCounter") {
                    g_showFpsCounter.store(g_settingsModel.graphics.showFpsCounter.value,
                        std::memory_order_relaxed);
                    g_pendingFpsChange.store(g_settingsModel.graphics.showFpsCounter.value ? 1 : 0, std::memory_order_release);
                }
            };
            
            g_settingsExtensions = UwpImGuiFrontend::BuildRyuboxSettingsExtensions(g_settingsModel, g_ryuboxSettingsCallbacks);
            
            g_settingsViewModel.showInterface = false;
            g_settingsViewModel.showScreenScraper = false;
            g_settingsViewModel.showCompanion = false;
            g_settingsViewModel.showStorage = false;
            g_settingsViewModel.showCommunity = false;
            
            g_ryuboxHost.SetCommandCallback([](UwpImGuiFrontend::HostCommand cmd) {
                if (cmd == UwpImGuiFrontend::HostCommand::OpenSettings) {
                    g_pendingSettingsPage.store(1, std::memory_order_release);
                } else if (cmd == UwpImGuiFrontend::HostCommand::OpenControllers) {
                    g_pendingSettingsPage.store(2, std::memory_order_release);
                } else if (cmd == UwpImGuiFrontend::HostCommand::OpenGraphics) {
                    g_pendingSettingsPage.store(3, std::memory_order_release);
                } else if (cmd == UwpImGuiFrontend::HostCommand::OpenTitleManager) {
                    if (g_frontend) {
                        const bool grid = g_frontend->Configuration().catalogueLayout ==
                            UwpImGuiFrontend::CatalogueLayoutMode::Grid;
                        g_frontend->SetCatalogueLayout(grid ?
                            UwpImGuiFrontend::CatalogueLayoutMode::PremiumCarousel3D :
                            UwpImGuiFrontend::CatalogueLayoutMode::Grid);
                    }
                } else if (cmd == UwpImGuiFrontend::HostCommand::OpenHome) {
                    g_pendingSettingsPage.store(0, std::memory_order_release);
                    g_showSettings.store(false, std::memory_order_release);
                }
            });
            g_ryuboxHost.SetActionCallback([](int action) {
                if (!QueueD3D12FrontendAction(action)) g_d3d11Host.SetImGuiPendingAction(action);
            });
        }
    }
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphics_Initialize(
    IUnknown* panel,
    uint32_t width,
    uint32_t height)
{
    return g_host.Initialize(panel, width, height);
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphics_Resize(
    uint32_t width,
    uint32_t height)
{
    return g_host.Resize(width, height);
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphics_Render()
{
    return g_host.Render();
}

extern "C" __declspec(dllexport) void __stdcall RyujinxUwpGraphics_Shutdown()
{
    g_host.Shutdown();
}

extern "C" __declspec(dllexport) int __stdcall RyujinxUwpGraphicsD3D11_Probe()
{
    return 1;
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_Initialize(
    IUnknown* panel,
    uint32_t width,
    uint32_t height)
{
    Ryujinx::Uwp::Native::EnsureFrontendInitialized();
    return g_d3d11Host.Initialize(panel, width, height);
}

// Desktop-only validation entry point. It deliberately shares the exact
// D3D11 device, GAL and presentation implementation used by the UWP host; the
// HWND replaces only XAML's SwapChainPanel so CoreHost can be tested without
// involving the frontend.
extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_InitializeForWindow(
    HWND window,
    uint32_t width,
    uint32_t height)
{
    return g_d3d11Host.InitializeForWindow(window, width, height);
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_Resize(
    uint32_t width,
    uint32_t height)
{
    return g_d3d11Host.Resize(width, height);
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_SetCompositionScale(
    float scaleX,
    float scaleY)
{
    return g_d3d11Host.SetCompositionScale(scaleX, scaleY);
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_Render()
{
    return g_d3d11Host.Render();
}

extern "C" __declspec(dllexport) void __stdcall RyujinxUwpGraphicsD3D11_Shutdown()
{
    g_d3d11Host.Shutdown();
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_GetSurfaceSize(
    uint32_t* width,
    uint32_t* height)
{
    return g_d3d11Host.GetSurfaceSize(width, height);
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_GetNativeObjects(
    void** device,
    void** deviceContext,
    void** swapChain)
{
    return g_d3d11Host.GetNativeObjects(device, deviceContext, swapChain);
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_BeginCoreSession()
{
    return g_d3d11Host.BeginCoreSession();
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_BeginCoreSessionV2(
    uint64_t sessionId)
{
    return g_d3d11Host.BeginCoreSession(sessionId);
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_BindGraphicsThread()
{
    return g_d3d11Host.BindGraphicsThread();
}

extern "C" __declspec(dllexport) void __stdcall RyujinxUwpGraphicsD3D11_EndCoreSession()
{
    g_d3d11Host.EndCoreSession();
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_EndCoreSessionV2(
    uint64_t sessionId)
{
    return g_d3d11Host.EndCoreSession(sessionId);
}

extern "C" __declspec(dllexport) unsigned long __stdcall RyujinxUwpGraphicsD3D11_GetOwnershipState()
{
    return static_cast<unsigned long>(g_d3d11Host.GetOwnershipState());
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_CopyRendererTexture(
    IUnknown* resource,
    bool flipY)
{
    // Preserve the original two-argument ABI for existing CoreHost callers.
    // Zero right/bottom mean the full source, resolved after GetDesc().
    return g_d3d11Host.CopyRendererTexture(resource, flipY, 0, 0, 0, 0, true);
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_CopyRendererTextureV2(
    IUnknown* resource,
    bool flipY,
    float u0,
    float v0,
    float u1,
    float v1,
    bool preserveExplicitCrop)
{
    return g_d3d11Host.CopyRendererTexture(resource, flipY, u0, v0, u1, v1, preserveExplicitCrop);
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_PresentRendererFrame()
{
    Ryujinx::Uwp::Native::EnsureFrontendInitialized();
    // Assuming g_d3d11Host has a way to render ImGui over the frame or we use it here.
    return g_d3d11Host.PresentRendererFrame();
}

// New export rather than changing the stack/argument contract of the legacy host.
extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_PresentRendererFrameWithSyncInterval(UINT syncInterval)
{
    Ryujinx::Uwp::Native::EnsureFrontendInitialized();
    return g_d3d11Host.PresentRendererFrame(syncInterval);
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_CaptureBackBuffer(
    void* pixels,
    uint32_t capacity,
    uint32_t* width,
    uint32_t* height,
    uint32_t* rowPitch)
{
    return g_d3d11Host.CaptureBackBuffer(pixels, capacity, width, height, rowPitch);
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_GetPresentationTelemetry(
    D3D11PresentationTelemetry* telemetry)
{
    return g_d3d11Host.GetPresentationTelemetry(telemetry);
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_GetDeviceStatus()
{
    return g_d3d11Host.GetDeviceStatus();
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_CompileHlsl(
    uint32_t stage,
    const char* source,
    uint32_t sourceLength,
    void** bytecode,
    uint32_t* bytecodeLength,
    char** errorMessage)
{
    if (source == nullptr || sourceLength == 0 || bytecode == nullptr || bytecodeLength == nullptr)
    {
        return E_INVALIDARG;
    }

    *bytecode = nullptr;
    *bytecodeLength = 0;
    if (errorMessage != nullptr)
    {
        *errorMessage = nullptr;
    }

    const char* profile = nullptr;
    switch (stage)
    {
        case 0: profile = "cs_5_0"; break;
        case 1: profile = "vs_5_0"; break;
        case 2: profile = "hs_5_0"; break;
        case 3: profile = "ds_5_0"; break;
        case 4: profile = "gs_5_0"; break;
        case 5: profile = "ps_5_0"; break;
        default: return E_INVALIDARG;
    }

    ComPtr<ID3DBlob> compiled;
    ComPtr<ID3DBlob> errors;
    // UWP/Xbox pays the cold shader compilation cost on the foreground GPU
    // path. Prefer predictable first-frame latency and lower temporary memory
    // pressure over expensive FXC optimization passes while this backend is
    // still warming shaders at runtime.
    UINT compileFlags = D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_SKIP_OPTIMIZATION;
    wchar_t optimizedShaderCompile[4]{};
    if (GetEnvironmentVariableW(
            L"RYUJINX_UWP_OPTIMIZED_SHADER_COMPILE",
            optimizedShaderCompile,
            4) != 0 &&
        optimizedShaderCompile[0] == L'1')
    {
        compileFlags = D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_OPTIMIZATION_LEVEL1;
    }

    // Most generated Ryujinx shaders use the canonical `main` entrypoint,
    // while the internal D3D11 blit/diagnostic shaders use stage-specific
    // names (VSMain/PSMain).  The old fixed entrypoint made the diagnostic
    // path fail with X3501 before the first real draw, hiding the original
    // presentation problem behind a CoreHost crash.
    const char* entryPoint = "main";
    if (stage == 1 && std::strstr(source, "VSMain(") != nullptr)
    {
        entryPoint = "VSMain";
    }
    else if (stage == 5 && std::strstr(source, "PSMain(") != nullptr)
    {
        entryPoint = "PSMain";
    }

    HRESULT result = D3DCompile2(
        source,
        sourceLength,
        "Ryujinx.Generated.hlsl",
        nullptr,
        nullptr,
        entryPoint,
        profile,
        compileFlags,
        0,
        0,
        nullptr,
        0,
        &compiled,
        &errors);

    if (errors && errorMessage != nullptr)
    {
        const size_t errorLength = errors->GetBufferSize();
        char* copy = static_cast<char*>(CoTaskMemAlloc(errorLength + 1));
        if (copy != nullptr)
        {
            std::memcpy(copy, errors->GetBufferPointer(), errorLength);
            copy[errorLength] = '\0';
            *errorMessage = copy;
        }
    }

    if (FAILED(result))
    {
        return result;
    }

    if (compiled->GetBufferSize() > UINT32_MAX)
    {
        return HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW);
    }

    void* copy = CoTaskMemAlloc(compiled->GetBufferSize());
    if (copy == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    std::memcpy(copy, compiled->GetBufferPointer(), compiled->GetBufferSize());
    *bytecode = copy;
    *bytecodeLength = static_cast<uint32_t>(compiled->GetBufferSize());
    return S_OK;
}

extern "C" __declspec(dllexport) void __stdcall RyujinxUwpGraphicsD3D11_FreeBuffer(void* buffer)
{
    CoTaskMemFree(buffer);
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_UploadFrame(
    const void* pixels,
    uint32_t width,
    uint32_t height,
    uint32_t rowPitch)
{
    return g_d3d11Host.UploadFrame(pixels, width, height, rowPitch);
}

extern "C" __declspec(dllexport) unsigned long __stdcall RyujinxUwpGraphicsD3D11_GetFeatureLevel()
{
    return static_cast<unsigned long>(g_d3d11Host.GetFeatureLevel());
}

extern "C" __declspec(dllexport) void __stdcall RyujinxUwpGraphicsD3D11_ImGuiSetInput(
    uint32_t buttons,
    float leftX,
    float leftY,
    float rightX,
    float rightY,
    float leftTrigger,
    float rightTrigger)
{
    g_d3d11Host.SetImGuiInput({ buttons, leftX, leftY, rightX, rightY, leftTrigger, rightTrigger });
}

extern "C" __declspec(dllexport) void __stdcall RyujinxUwpGraphicsD3D11_ImGuiSetPointer(
    float x,
    float y,
    int leftDown,
    int rightDown,
    float wheel)
{
    g_d3d11Host.SetImGuiPointer({ x, y, wheel, leftDown != 0, rightDown != 0 });
}

extern "C" __declspec(dllexport) void __stdcall RyujinxUwpGraphicsD3D11_ImGuiSetCatalogue(
    const wchar_t* newlineSeparatedTitles)
{
    g_d3d11Host.SetImGuiCatalogue(newlineSeparatedTitles);
}

extern "C" __declspec(dllexport) void __stdcall RyujinxUwpGraphicsD3D11_ImGuiSetStatus(
    const wchar_t* keysStatus,
    const wchar_t* firmwareStatus)
{
    g_d3d11Host.SetImGuiStatus(keysStatus, firmwareStatus);
}

extern "C" __declspec(dllexport) void __stdcall RyujinxUwpGraphicsD3D11_ImGuiSetOperation(
    const wchar_t* message,
    float progress,
    int state)
{
    g_d3d11Host.SetImGuiOperation(message, progress, state);
}

extern "C" __declspec(dllexport) void __stdcall RyujinxUwpGraphicsD3D11_ImGuiSetOnboardingState(
    int completed,
    int language,
    int theme,
    int keysReady,
    int firmwareReady,
    int gamesReady)
{
    g_d3d11Host.SetImGuiOnboardingState(completed != 0, language, theme,
        keysReady != 0, firmwareReady != 0, gamesReady != 0);
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpGraphicsD3D11_ImGuiSetCover(
    int index,
    const void* rgbaPixels,
    uint32_t width,
    uint32_t height,
    uint32_t rowPitch)
{
    return g_d3d11Host.SetImGuiCover(index, rgbaPixels, width, height, rowPitch);
}

extern "C" __declspec(dllexport) int __stdcall RyujinxUwpGraphicsD3D11_ImGuiConsumeAction()
{
    return g_d3d11Host.ConsumeImGuiAction();
}

extern "C" __declspec(dllexport) int __stdcall RyujinxUwpGraphics_ProbeDozen()
{
    InterlockedExchange(&g_probeFlags, 0);
    InterlockedExchange(&g_probeError, ERROR_SUCCESS);

    HMODULE driver = LoadPackagedLibrary(L"vulkan_dzn.dll", 0);
    if (driver == nullptr)
    {
        InterlockedExchange(&g_probeError, static_cast<LONG>(GetLastError()));
        return 0;
    }

    InterlockedOr(&g_probeFlags, ProbeDriverLoaded);

    const bool hasEntryPoint = GetProcAddress(driver, "vkGetInstanceProcAddr") != nullptr
        || GetProcAddress(driver, "vk_icdGetInstanceProcAddr") != nullptr;

    if (hasEntryPoint)
    {
        InterlockedOr(&g_probeFlags, ProbeEntryPointAvailable);
    }
    else
    {
        InterlockedExchange(&g_probeError, static_cast<LONG>(ERROR_PROC_NOT_FOUND));
    }

    const struct
    {
        const wchar_t* name;
        LONG flag;
    } compilerModules[] =
    {
        { L"DXIL.dll", ProbeDxilPackaged },
        { L"dxcompiler_x.dll", ProbeXboxCompilerPackaged },
        { L"dxcompiler_xs.dll", ProbeScarlettCompilerPackaged },
    };

    for (const auto& compilerModule : compilerModules)
    {
        HMODULE compiler = LoadPackagedLibrary(compilerModule.name, 0);
        if (compiler != nullptr)
        {
            InterlockedOr(&g_probeFlags, compilerModule.flag);
            FreeLibrary(compiler);
        }
    }

    FreeLibrary(driver);
    return hasEntryPoint ? 1 : 0;
}

extern "C" __declspec(dllexport) unsigned long __stdcall RyujinxUwpGraphics_GetProbeFlags()
{
    return static_cast<unsigned long>(InterlockedCompareExchange(&g_probeFlags, 0, 0));
}

extern "C" __declspec(dllexport) unsigned long __stdcall RyujinxUwpGraphics_GetProbeError()
{
    return static_cast<unsigned long>(InterlockedCompareExchange(&g_probeError, 0, 0));
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpMemory_RegisterLazyCommit(
    void* base,
    uint64_t size)
{
    if (base == nullptr || size == 0)
    {
        return E_INVALIDARG;
    }

    void* const currentBase = InterlockedCompareExchangePointer(
        &g_lazyCommitBase,
        nullptr,
        nullptr);
    const uint64_t currentSize = static_cast<uint64_t>(
        InterlockedCompareExchange64(&g_lazyCommitSize, 0, 0));

    if (currentBase == base && currentSize == size)
    {
        return S_FALSE;
    }

    if (currentBase != nullptr || currentSize != 0)
    {
        return HRESULT_FROM_WIN32(ERROR_BUSY);
    }

    if (g_lazyCommitVeh == nullptr)
    {
        g_lazyCommitVeh = AddVectoredExceptionHandler(
            1,
            LazyGuestCommitExceptionHandler);
        if (g_lazyCommitVeh == nullptr)
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }
    }

    ResetLazyCommitTelemetry();
    InterlockedExchangePointer(&g_lazyCommitBase, base);
    InterlockedExchange64(&g_lazyCommitSize, static_cast<LONG64>(size));
    return S_OK;
}

extern "C" __declspec(dllexport) void __stdcall RyujinxUwpMemory_UnregisterLazyCommit(void* base)
{
    void* const currentBase = InterlockedCompareExchangePointer(
        &g_lazyCommitBase,
        nullptr,
        nullptr);
    // A terminated guest may leave a process-context reference alive briefly
    // while its Switch is already being disposed. A new session must never be
    // blocked by that stale registration. A null base is the CoreHost's
    // explicit session-cleanup request and is safe after its GPU/guest threads
    // have joined.
    if (base == nullptr || currentBase == base)
    {
        // Publish size=0 first so a new fault cannot be classified as part of
        // a region while its owning MemoryBlock is being released.
        InterlockedExchange64(&g_lazyCommitSize, 0);
        InterlockedExchangePointer(&g_lazyCommitBase, nullptr);
    }
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpMemory_GetLazyCommitTelemetry(
    LazyCommitTelemetry* telemetry)
{
    if (telemetry == nullptr)
    {
        return E_INVALIDARG;
    }

    telemetry->attempts = static_cast<uint64_t>(InterlockedCompareExchange64(&g_lazyCommitAttempts, 0, 0));
    telemetry->successes = static_cast<uint64_t>(InterlockedCompareExchange64(&g_lazyCommitSuccesses, 0, 0));
    telemetry->failures = static_cast<uint64_t>(InterlockedCompareExchange64(&g_lazyCommitFailures, 0, 0));
    telemetry->committedBytes = static_cast<uint64_t>(InterlockedCompareExchange64(&g_lazyCommittedBytes, 0, 0));
    telemetry->concurrentEntries = static_cast<uint64_t>(InterlockedCompareExchange64(&g_lazyCommitConcurrentEntries, 0, 0));
    telemetry->activeHandlers = static_cast<uint64_t>(InterlockedCompareExchange64(&g_lazyCommitActiveHandlers, 0, 0));
    telemetry->maxActiveHandlers = static_cast<uint64_t>(InterlockedCompareExchange64(&g_lazyCommitMaxActiveHandlers, 0, 0));
    telemetry->lastFaultAddress = static_cast<uint64_t>(InterlockedCompareExchange64(&g_lazyCommitLastFaultAddress, 0, 0));
    telemetry->lastSuccessAddress = static_cast<uint64_t>(InterlockedCompareExchange64(&g_lazyCommitLastSuccessAddress, 0, 0));
    telemetry->lastError = static_cast<uint32_t>(InterlockedCompareExchange(&g_lazyCommitLastError, 0, 0));
    telemetry->lastThreadId = static_cast<uint32_t>(InterlockedCompareExchange(&g_lazyCommitLastThreadId, 0, 0));
    telemetry->registered = InterlockedCompareExchange64(&g_lazyCommitSize, 0, 0) != 0 ? 1u : 0u;
    telemetry->reserved = 0;
    return S_OK;
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpCore_Start(
    const wchar_t* gamePath,
    const wchar_t* keysPath,
    const wchar_t* firmwarePath,
    const wchar_t* backend)
{
    return g_coreBridge.Start(gamePath, keysPath, firmwarePath, backend);
}

extern "C" __declspec(dllexport) void __stdcall RyujinxUwpCore_Stop()
{
    g_coreBridge.Stop();
}

extern "C" __declspec(dllexport) void __stdcall RyujinxUwpCore_SetInput(
    uint32_t buttons,
    float leftX,
    float leftY,
    float rightX,
    float rightY,
    float leftTrigger,
    float rightTrigger)
{
    g_coreBridge.SetInput({ buttons, leftX, leftY, rightX, rightY, leftTrigger, rightTrigger });
}

extern "C" __declspec(dllexport) int __stdcall RyujinxUwpCore_IsStarted()
{
    return g_coreBridge.IsStarted() ? 1 : 0;
}

extern "C" __declspec(dllexport) int __stdcall RyujinxUwpCore_IsBound()
{
    return g_coreBridge.IsCoreBound() ? 1 : 0;
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpAudio_CreateSession(
    uint32_t sampleRate,
    uint32_t channelCount,
    uint32_t bitsPerSample,
    int floatingPoint,
    HANDLE updateEvent,
    void** session)
{
    if (session == nullptr)
    {
        return E_INVALIDARG;
    }

    *session = nullptr;
    XAudio2Session* audio = new (std::nothrow) XAudio2Session();
    if (audio == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    HRESULT result = audio->Initialize(sampleRate, channelCount, bitsPerSample, floatingPoint != 0, updateEvent);
    if (FAILED(result))
    {
        delete audio;
        return result;
    }

    *session = audio;
    return S_OK;
}

extern "C" __declspec(dllexport) void __stdcall RyujinxUwpAudio_DestroySession(void* session)
{
    delete static_cast<XAudio2Session*>(session);
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpAudio_Submit(
    void* session,
    uint64_t identifier,
    const void* samples,
    uint32_t byteCount)
{
    return session != nullptr
        ? static_cast<XAudio2Session*>(session)->Submit(identifier, samples, byteCount)
        : E_INVALIDARG;
}

extern "C" __declspec(dllexport) int __stdcall RyujinxUwpAudio_IsConsumed(void* session, uint64_t identifier)
{
    return session == nullptr || static_cast<XAudio2Session*>(session)->IsConsumed(identifier) ? 1 : 0;
}

extern "C" __declspec(dllexport) void __stdcall RyujinxUwpAudio_ReleasePacket(void* session, uint64_t identifier)
{
    if (session != nullptr)
    {
        static_cast<XAudio2Session*>(session)->ReleasePacket(identifier);
    }
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpAudio_Start(void* session)
{
    return session != nullptr ? static_cast<XAudio2Session*>(session)->Start() : E_INVALIDARG;
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpAudio_Stop(void* session)
{
    return session != nullptr ? static_cast<XAudio2Session*>(session)->Stop() : E_INVALIDARG;
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpAudio_SetVolume(void* session, float volume)
{
    return session != nullptr ? static_cast<XAudio2Session*>(session)->SetVolume(volume) : E_INVALIDARG;
}

extern "C" __declspec(dllexport) uint64_t __stdcall RyujinxUwpAudio_GetPlayedFrameCount(void* session)
{
    return session != nullptr ? static_cast<XAudio2Session*>(session)->GetPlayedFrameCount() : 0;
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpAudio_GetLastError(void* session)
{
    return session != nullptr ? static_cast<XAudio2Session*>(session)->GetLastVoiceError() : E_INVALIDARG;
}

extern "C" __declspec(dllexport) HRESULT __stdcall RyujinxUwpAudio_GetBufferStats(void* session, uint64_t* values, uint32_t count)
{
    if (session == nullptr || values == nullptr || count < 6) return E_INVALIDARG;
    static_cast<XAudio2Session*>(session)->GetBufferStats(values);
    return S_OK;
}

extern "C" __declspec(dllexport) void __stdcall RyujinxUwpFrontend_SetIntSetting(const char* key, int value)
{
    Ryujinx::Uwp::Native::EnsureFrontendInitialized();
    std::scoped_lock settingsModelLock(Ryujinx::Uwp::Native::g_settingsModelMutex);
    if (key != nullptr && std::strcmp(key, "fpsLimit") == 0)
    {
        Ryujinx::Uwp::Native::g_settingsModel.graphics.fpsLimit.value = value;
    }
}

extern "C" __declspec(dllexport) void __stdcall RyujinxUwpFrontend_SetPtcEnabled(int enabled)
{
    // Called after the frontend has been initialized, on the UI thread.
    std::scoped_lock lock(Ryujinx::Uwp::Native::g_settingsModelMutex);
    Ryujinx::Uwp::Native::g_persistedPtcEnabled.store(enabled != 0, std::memory_order_release);
    Ryujinx::Uwp::Native::g_settingsModel.cpu.ptc.value = enabled != 0;
}

extern "C" __declspec(dllexport) int __stdcall RyujinxUwpFrontend_ConsumePtcChange()
{
    return Ryujinx::Uwp::Native::g_pendingPtcChange.exchange(-1, std::memory_order_acq_rel);
}

extern "C" __declspec(dllexport) void __stdcall RyujinxUwpFrontend_SetFpsEnabled(int enabled)
{
    std::scoped_lock lock(Ryujinx::Uwp::Native::g_settingsModelMutex);
    Ryujinx::Uwp::Native::g_showFpsCounter.store(enabled != 0, std::memory_order_release);
    Ryujinx::Uwp::Native::g_settingsModel.graphics.showFpsCounter.value = enabled != 0;
}

extern "C" __declspec(dllexport) int __stdcall RyujinxUwpFrontend_ConsumeFpsChange()
{
    return Ryujinx::Uwp::Native::g_pendingFpsChange.exchange(-1, std::memory_order_acq_rel);
}
