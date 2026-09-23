#pragma once
#include "D3D12Backend.h"
namespace Ryujinx::Uwp::Native {
bool QueueD3D12FrontendAction(int action);
HRESULT DrawD3D12FrontendOverlay(void* context);
void NotifyD3D12FrontendPresent(void* context);
HRESULT PrepareD3D12UiDraw(void* context);
}
RYUBOX_D12 RyuboxD3D12Frontend_Initialize(HWND window, IUnknown* panel, UINT width, UINT height, UINT flags);
RYUBOX_D12 RyuboxD3D12Frontend_Shutdown();
RYUBOX_D12 RyuboxD3D12Frontend_Render();
RYUBOX_D12 RyuboxD3D12Frontend_GetDeviceStatus();
RYUBOX_D12 RyuboxD3D12Frontend_Resize(UINT width, UINT height);
RYUBOX_D12 RyuboxD3D12Frontend_SetCompositionScale(float x, float y);
// Acquire transfers command recording to CoreHost. The caller must dispose
// every GAL resource before Release. Shutdown refuses a live lease.
RYUBOX_D12 RyuboxD3D12Frontend_Acquire(UINT64 session, void** context);
RYUBOX_D12 RyuboxD3D12Frontend_Release(UINT64 session);
RYUBOX_D12 RyuboxD3D12Frontend_SetInput(UINT buttons, float lx, float ly, float rx, float ry, float lt, float rt);
RYUBOX_D12 RyuboxD3D12Frontend_SetPointer(float x, float y, float wheel, BOOL left, BOOL right);
RYUBOX_D12 RyuboxD3D12Frontend_SetCatalogue(const wchar_t* titles);
RYUBOX_D12 RyuboxD3D12Frontend_SetCover(int index, const void* rgba, UINT width, UINT height, UINT pitch);
RYUBOX_D12 RyuboxD3D12Frontend_SetStatus(const wchar_t* keys, const wchar_t* firmware);
RYUBOX_D12 RyuboxD3D12Frontend_SetOperation(const wchar_t* message, float progress, int state);
RYUBOX_D12 RyuboxD3D12Frontend_SetOnboardingState(BOOL completed, int language, int theme,
    BOOL keysReady, BOOL firmwareReady, BOOL gamesReady);
RYUBOX_D12 RyuboxD3D12Frontend_ConsumeAction(int* action);
RYUBOX_D12 RyuboxD3D12Frontend_Capture(void* rgba, UINT64 bytes, RyuboxD3D12Info* info);
