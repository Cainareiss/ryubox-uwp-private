#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <cstdint>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <vector>

using Microsoft::WRL::ComPtr;
using InitializeFn = HRESULT(__stdcall*)(HWND, uint32_t, uint32_t);
using CallFn = HRESULT(__stdcall*)();
using PresentIntervalFn = HRESULT(__stdcall*)(UINT);
using EndFn = void(__stdcall*)();
using ObjectsFn = HRESULT(__stdcall*)(void**, void**, void**);
using CopyFn = HRESULT(__stdcall*)(IUnknown*, bool);
using CopyV2Fn = HRESULT(__stdcall*)(IUnknown*, bool, float, float, float, float, bool);
using CaptureFn = HRESULT(__stdcall*)(void*, uint32_t, uint32_t*, uint32_t*, uint32_t*);
using ResizeFn = HRESULT(__stdcall*)(uint32_t, uint32_t);

template<class T> T Export(HMODULE module, const char* name)
{
    auto result = reinterpret_cast<T>(GetProcAddress(module, name));
    if (!result) throw std::runtime_error(name);
    return result;
}

void Check(HRESULT result, const char* operation)
{
    if (FAILED(result))
    {
        std::cerr << operation << " failed HRESULT=0x" << std::hex << result << std::endl;
        throw std::runtime_error(operation);
    }
}

int wmain(int argc, wchar_t** argv)
{
    try
    {
        if (argc != 2) throw std::runtime_error("Expected native DLL path");
        SetEnvironmentVariableW(L"RYUBOX_DIAGNOSTIC_NO_PRESENT", L"1");
        auto module = LoadLibraryW(argv[1]);
        if (!module) throw std::runtime_error("LoadLibrary");
        auto initialize = Export<InitializeFn>(module, "RyujinxUwpGraphicsD3D11_InitializeForWindow");
        auto render = Export<CallFn>(module, "RyujinxUwpGraphicsD3D11_Render");
        auto begin = Export<CallFn>(module, "RyujinxUwpGraphicsD3D11_BeginCoreSession");
        auto bind = Export<CallFn>(module, "RyujinxUwpGraphicsD3D11_BindGraphicsThread");
        auto end = Export<EndFn>(module, "RyujinxUwpGraphicsD3D11_EndCoreSession");
        auto objects = Export<ObjectsFn>(module, "RyujinxUwpGraphicsD3D11_GetNativeObjects");
        auto copy = Export<CopyFn>(module, "RyujinxUwpGraphicsD3D11_CopyRendererTexture");
        auto copyV2 = Export<CopyV2Fn>(module, "RyujinxUwpGraphicsD3D11_CopyRendererTextureV2");
        auto capture = Export<CaptureFn>(module, "RyujinxUwpGraphicsD3D11_CaptureBackBuffer");
        auto present = Export<CallFn>(module, "RyujinxUwpGraphicsD3D11_PresentRendererFrame");
        auto presentInterval = Export<PresentIntervalFn>(module, "RyujinxUwpGraphicsD3D11_PresentRendererFrameWithSyncInterval");
        auto resize = Export<ResizeFn>(module, "RyujinxUwpGraphicsD3D11_Resize");
        WNDCLASSW wc{};
        wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = L"RyuboxPresentationSmoke";
        RegisterClassW(&wc);
        HWND window = CreateWindowW(wc.lpszClassName, wc.lpszClassName, WS_OVERLAPPEDWINDOW,
            -32000, -32000, 320, 180, nullptr, nullptr, wc.hInstance, nullptr);
        if (!window) throw std::runtime_error("CreateWindow");
        Check(initialize(window, 320, 180), "initialize");
        Check(render(), "frontend render");

        void* deviceRaw = nullptr; void* contextRaw = nullptr; void* swapchainRaw = nullptr;
        Check(objects(&deviceRaw, &contextRaw, &swapchainRaw), "native objects");
        auto device = static_cast<ID3D11Device*>(deviceRaw);
        // RGBA quadrants: red/green on top, blue/yellow below. Deliberately
        // smaller than the presentation surface to exercise the shader blit.
        std::vector<uint32_t> source(64 * 32);
        for (int y = 0; y < 32; ++y)
            for (int x = 0; x < 64; ++x)
                source[y * 64 + x] = y < 16 ? (x < 32 ? 0xff0000ff : 0xff00ff00)
                    : (x < 32 ? 0xffff0000 : 0xff00ffff);
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = 64; desc.Height = 32; desc.MipLevels = 1; desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM; desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_IMMUTABLE; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA data{};
        data.pSysMem = source.data(); data.SysMemPitch = 64 * 4;
        ComPtr<ID3D11Texture2D> texture;
        Check(device->CreateTexture2D(&desc, &data, &texture), "source texture");

        auto verify = [&](uint32_t topLeft, uint32_t topRight, uint32_t bottomLeft, uint32_t bottomRight)
        {
            std::vector<uint32_t> pixels(640 * 360);
            uint32_t width = 0, height = 0, pitch = 0;
            Check(capture(pixels.data(), static_cast<uint32_t>(pixels.size() * 4), &width, &height, &pitch), "capture");
            uint32_t expected[] = { topLeft, topRight, bottomLeft, bottomRight };
            for (int i = 0; i < 4; ++i)
            {
                const auto actual = pixels[(height * (i < 2 ? 1 : 3) / 4) * (pitch / 4)
                    + width * (i % 2 == 0 ? 1 : 3) / 4];
                if ((actual & 0xffffff) != (expected[i] & 0xffffff))
                {
                    std::cerr << "quadrant=" << i << " expected=" << std::hex << expected[i]
                        << " actual=" << actual << std::endl;
                    throw std::runtime_error("Blit pixel mismatch");
                }
            }
        };

        for (int session = 0; session < 2; ++session)
        {
            Check(begin(), "begin session"); Check(bind(), "bind graphics thread");
            if (presentInterval(2) != E_INVALIDARG) throw std::runtime_error("Invalid sync interval accepted");
            for (int frame = 0; frame < 4; ++frame)
            {
                // The native ABI now carries the final top-left sampling flag;
                // managed GAL conversion happens before this boundary.
                Check(copy(texture.Get(), false), "legacy non-flipped full-source copy");
                if (render() != S_FALSE) throw std::runtime_error("Frontend overwrote active session");
                verify(0xff0000ff, 0xff00ff00, 0xffff0000, 0xff00ffff);
                Check(present(), "present");
                Check(copyV2(texture.Get(), true, 0, 0, 64, 32, true), "V2 explicit vertical flip");
                verify(0xffff0000, 0xff00ffff, 0xff0000ff, 0xff00ff00);
                Check(presentInterval(frame % 2), "present flipped with explicit sync interval 0/1");
                Check(copyV2(texture.Get(), false, 32, 0, 64, 16, true), "V2 explicit crop");
                verify(0xff00ff00, 0xff00ff00, 0xff00ff00, 0xff00ff00);
                Check(present(), "present cropped");
            }
            if (copyV2(texture.Get(), false, std::numeric_limits<float>::quiet_NaN(), 0, 64, 32, true) != E_INVALIDARG)
                throw std::runtime_error("Non-finite crop not rejected");
            end();
            Check(resize(640, 360), "resize");
            Check(render(), "return to frontend");
        }
        std::cout << "PASS: initialization, legacy/V2 blits, crop, flip, repeated presents, frontend isolation, resize, two sessions." << std::endl;
        // Avoid unrelated DLL static teardown masking the rendering test result.
        TerminateProcess(GetCurrentProcess(), 0);
    }
    catch (const std::exception& e)
    {
        std::cerr << "FAIL: " << e.what() << std::endl;
        TerminateProcess(GetCurrentProcess(), 1);
    }
}
