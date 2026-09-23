#include "D3D12FrontendHost.h"
#include "ImGuiD3D11Shell.h"
#include "third_party/imgui/imgui.h"
#include <mutex>
#include <memory>
#include <new>
#include <cmath>
#include <atomic>

namespace Ryujinx::Uwp::Native { void EnsureFrontendInitialized(); void ShutdownFrontendModel(); }
namespace Ryujinx::Uwp::Native { extern std::atomic<bool> g_showFpsCounter; }
namespace
{
std::recursive_mutex gate; // UI callbacks can enqueue an action during Render.
void* context = nullptr;
std::unique_ptr<Ryujinx::Uwp::Native::ImGuiD3D11Shell> shell;
UINT64 lease = 0;
bool releaseBarrier = true;
UINT pendingWidth=0, pendingHeight=0;
float pendingScaleX=1, pendingScaleY=1;
bool pendingResize=false, pendingScale=false;
ULONGLONG fpsSampleAt=0;
UINT fpsFrames=0;
float gameFps=0;
template<class F> HRESULT Guard(F fn)
{
    std::lock_guard<std::recursive_mutex> lock(gate);
    try { return fn(); }
    catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}
HRESULT UiReady() { return !context || !shell ? E_UNEXPECTED : lease ? HRESULT_FROM_WIN32(ERROR_BUSY) : S_OK; }
HRESULT Drain()
{
    UINT64 fence=0; HRESULT hr=RyuboxD3D12_Flush(context,&fence);
    return FAILED(hr) ? hr : RyuboxD3D12_Wait(context,fence);
}
}
namespace Ryujinx::Uwp::Native
{
HRESULT DrawD3D12FrontendOverlay(void* target)
{
    return Guard([&]() -> HRESULT {
        if (target!=context || !lease || !shell || !g_showFpsCounter.load(std::memory_order_relaxed)) return S_OK;
        return shell->RenderGameOverlay(gameFps);
    });
}
void NotifyD3D12FrontendPresent(void* target)
{
    std::lock_guard<std::recursive_mutex> lock(gate);
    if (target!=context || !lease) return;
    ++fpsFrames;
    const auto now=GetTickCount64();
    if (now-fpsSampleAt>=1000)
    {
        gameFps=1000.0f*fpsFrames/float(now-fpsSampleAt);
        fpsFrames=0; fpsSampleAt=now;
    }
}
bool QueueD3D12FrontendAction(int action)
{
    std::lock_guard<std::recursive_mutex> lock(gate);
    if (!context || !shell) return false;
    if (!lease && !releaseBarrier) shell->SetPendingAction(action);
    return true; // Active D3D12 always owns routing, even when input is blocked.
}
}
RYUBOX_D12 RyuboxD3D12Frontend_Initialize(HWND window, IUnknown* panel, UINT width, UINT height, UINT flags)
{
    return Guard([&]() -> HRESULT {
        if (context || shell) return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
        // Both backends reuse the same ImGui model; the old host must be shut
        // down before switching. Never destroy another host's current context.
        if (ImGui::GetCurrentContext()) return HRESULT_FROM_WIN32(ERROR_BUSY);
        HRESULT hr=RyuboxD3D12_Create(window,panel,width,height,flags,&context);
        if (FAILED(hr)) return hr;
        try
        {
            Ryujinx::Uwp::Native::EnsureFrontendInitialized();
            shell=std::make_unique<Ryujinx::Uwp::Native::ImGuiD3D11Shell>();
            hr=shell->InitializeD3D12(context,width,height);
        }
        catch (...) {
            Ryujinx::Uwp::Native::ShutdownFrontendModel();
            if (shell) shell->Shutdown();
            shell.reset(); RyuboxD3D12_Destroy(context); context=nullptr; throw;
        }
        if (FAILED(hr)) {
            Ryujinx::Uwp::Native::ShutdownFrontendModel();
            shell->Shutdown(); shell.reset(); RyuboxD3D12_Destroy(context); context=nullptr;
        }
        lease=0; releaseBarrier=true;
        pendingResize=false; pendingScale=false;
        return hr;
    });
}
RYUBOX_D12 RyuboxD3D12Frontend_Shutdown()
{
    return Guard([&]() -> HRESULT {
        if (lease) return HRESULT_FROM_WIN32(ERROR_BUSY);
        if (!context) return S_OK;
        HRESULT hr=Drain();
        Ryujinx::Uwp::Native::ShutdownFrontendModel();
        if (shell) shell->Shutdown(); shell.reset();
        HRESULT destroy=RyuboxD3D12_Destroy(context); context=nullptr;
        return FAILED(hr) ? hr : destroy;
    });
}
RYUBOX_D12 RyuboxD3D12Frontend_Render()
{
    return Guard([&]() -> HRESULT {
        HRESULT hr=UiReady(); if (FAILED(hr)) return hr;
        // Resize/DPI notifications can arrive while CoreHost owns commands.
        // Apply only on the UI owner, after the lease has been returned.
        if (pendingResize)
        {
            hr=RyuboxD3D12_Resize(context,pendingWidth,pendingHeight); if (FAILED(hr)) return hr;
            hr=shell->ResizeD3D12(pendingWidth,pendingHeight); if (FAILED(hr)) return hr;
            pendingResize=false;
        }
        if (pendingScale)
        {
            hr=RyuboxD3D12_SetCompositionScale(context,pendingScaleX,pendingScaleY); if (FAILED(hr)) return hr;
            pendingScale=false;
        }
        hr=RyuboxD3D12_BeginFrame(context,0.03f,0.02f,0.05f,1);
        if (FAILED(hr)) return hr;
        hr=shell->Render();
        UINT64 fence=0;
        if (FAILED(hr)) { RyuboxD3D12_Flush(context,&fence); return hr; }
        return RyuboxD3D12_EndFrame(context,1,&fence);
    });
}
RYUBOX_D12 RyuboxD3D12Frontend_GetDeviceStatus()
{
    return Guard([&]() -> HRESULT {
        HRESULT hr=UiReady(); if (FAILED(hr)) return hr;
        RyuboxD3D12Info info{}; info.size=sizeof(info);
        hr=RyuboxD3D12_GetInfo(context,&info);
        return FAILED(hr) ? hr : info.deviceRemovedReason;
    });
}
RYUBOX_D12 RyuboxD3D12Frontend_Resize(UINT width, UINT height)
{
    return Guard([&]() -> HRESULT {
        if (!width || !height) return S_FALSE; // Transient minimization.
        if (context && lease) { pendingWidth=width; pendingHeight=height; pendingResize=true; return S_FALSE; }
        HRESULT hr=UiReady(); if (FAILED(hr)) return hr;
        hr=RyuboxD3D12_Resize(context,width,height);
        return FAILED(hr) ? hr : shell->ResizeD3D12(width,height);
    });
}
RYUBOX_D12 RyuboxD3D12Frontend_SetCompositionScale(float x,float y)
{
    return Guard([&]() -> HRESULT {
        if (!std::isfinite(x) || !std::isfinite(y) || x<=0 || y<=0) return E_INVALIDARG;
        if (context && lease) { pendingScaleX=x; pendingScaleY=y; pendingScale=true; return S_FALSE; }
        HRESULT hr=UiReady(); return FAILED(hr) ? hr : RyuboxD3D12_SetCompositionScale(context,x,y);
    });
}
RYUBOX_D12 RyuboxD3D12Frontend_Acquire(UINT64 session,void** result)
{
    if (!result) return E_POINTER;
    *result=nullptr;
    return Guard([&]() -> HRESULT {
        if (!session) return E_INVALIDARG;
        HRESULT hr=UiReady(); if (FAILED(hr)) return hr;
        hr=Drain(); if (FAILED(hr)) return hr;
        shell->SetPendingAction(-1); shell->SetInput({}); shell->SetPointer({});
        releaseBarrier=true; lease=session; *result=context;
        gameFps=0; fpsFrames=0; fpsSampleAt=GetTickCount64(); return S_OK;
    });
}
RYUBOX_D12 RyuboxD3D12Frontend_Release(UINT64 session)
{
    return Guard([&]() -> HRESULT {
        if (!context || !session || lease!=session) return E_INVALIDARG;
        HRESULT hr=Drain();
        shell->SetPendingAction(-1); shell->SetInput({}); shell->SetPointer({});
        lease=0; releaseBarrier=true; return hr;
    });
}
RYUBOX_D12 RyuboxD3D12Frontend_SetInput(UINT buttons,float lx,float ly,float rx,float ry,float lt,float rt)
{
    return Guard([&]() -> HRESULT {
        if (!shell) return E_UNEXPECTED;
        if (lease) return S_FALSE;
        if (releaseBarrier)
        {
            if (buttons || lx < -0.2f || lx > 0.2f || ly < -0.2f || ly > 0.2f ||
                rx < -0.2f || rx > 0.2f || ry < -0.2f || ry > 0.2f || lt > 0.1f || rt > 0.1f) return S_FALSE;
            releaseBarrier=false; shell->SetInput({}); return S_OK;
        }
        shell->SetInput({buttons,lx,ly,rx,ry,lt,rt}); return S_OK;
    });
}
RYUBOX_D12 RyuboxD3D12Frontend_SetPointer(float x,float y,float wheel,BOOL left,BOOL right)
{
    return Guard([&]() -> HRESULT {
        if (!shell) return E_UNEXPECTED;
        if (lease || releaseBarrier) return S_FALSE;
        shell->SetPointer({x,y,wheel,left!=FALSE,right!=FALSE}); return S_OK;
    });
}
RYUBOX_D12 RyuboxD3D12Frontend_SetCatalogue(const wchar_t* titles)
{ return Guard([&]() -> HRESULT { HRESULT hr=UiReady(); if (FAILED(hr)) return hr; shell->SetCatalogue(titles); return S_OK; }); }
RYUBOX_D12 RyuboxD3D12Frontend_SetCover(int index,const void* rgba,UINT width,UINT height,UINT pitch)
{ return Guard([&]() -> HRESULT { HRESULT hr=UiReady(); return FAILED(hr) ? hr : shell->SetCover(index,rgba,width,height,pitch); }); }
RYUBOX_D12 RyuboxD3D12Frontend_SetStatus(const wchar_t* keys,const wchar_t* firmware)
{ return Guard([&]() -> HRESULT { if (!shell) return E_UNEXPECTED; shell->SetStatus(keys,firmware); return S_OK; }); }
RYUBOX_D12 RyuboxD3D12Frontend_SetOperation(const wchar_t* message,float progress,int state)
{ return Guard([&]() -> HRESULT { if (!shell) return E_UNEXPECTED; shell->SetOperation(message,progress,state); return S_OK; }); }
RYUBOX_D12 RyuboxD3D12Frontend_SetOnboardingState(BOOL completed,int language,int theme,
    BOOL keysReady,BOOL firmwareReady,BOOL gamesReady)
{ return Guard([&]() -> HRESULT { if (!shell) return E_UNEXPECTED; shell->SetOnboardingState(
    completed!=FALSE,language,theme,keysReady!=FALSE,firmwareReady!=FALSE,gamesReady!=FALSE); return S_OK; }); }
RYUBOX_D12 RyuboxD3D12Frontend_ConsumeAction(int* action)
{
    if (!action) return E_POINTER;
    *action=-1;
    return Guard([&]() -> HRESULT { HRESULT hr=UiReady(); if (FAILED(hr)) return hr; if (!releaseBarrier) *action=shell->ConsumeAction(); return S_OK; });
}
RYUBOX_D12 RyuboxD3D12Frontend_Capture(void* rgba,UINT64 bytes,RyuboxD3D12Info* info)
{
    return Guard([&]() -> HRESULT {
        HRESULT hr=UiReady(); if (FAILED(hr)) return hr;
        if (!info || !rgba) return E_POINTER;
        hr=RyuboxD3D12_GetInfo(context,info); if (FAILED(hr)) return hr;
        return RyuboxD3D12_Capture(context,rgba,bytes);
    });
}
