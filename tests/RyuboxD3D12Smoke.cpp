#include "../src/Ryujinx.Uwp.Native/D3D12Backend.h"
#include <iostream>
#include <stdexcept>
#include <vector>
#include <array>
#include <cstring>
#include <thread>

template<class T> T Load(HMODULE dll, const char* name)
{
    auto address = GetProcAddress(dll, name);
    if (!address) throw std::runtime_error(name);
    return reinterpret_cast<T>(address);
}
void Check(HRESULT result, const char* stage)
{
    if (FAILED(result))
    {
        std::cerr << stage << " failed HRESULT=0x" << std::hex << result << std::dec << '\n';
        throw std::runtime_error(stage);
    }
}
void Require(bool success, const char* stage) { if (!success) throw std::runtime_error(stage); }

#include "D3D12ProgramTests.inl"
#include "D3D12DescriptorRingTests.inl"

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2) return 2;
    HMODULE dll = LoadLibraryW(argv[1]);
    if (!dll) return 3;
    void* context = nullptr;
    HWND window = nullptr;
    auto destroy = Load<decltype(&RyuboxD3D12_Destroy)>(dll, "RyuboxD3D12_Destroy");
    try
    {
        TestDescriptorRing();
#define API(name) auto name = Load<decltype(&RyuboxD3D12_##name)>(dll, "RyuboxD3D12_" #name)
        API(Create); API(GetInfo); API(BeginFrame); API(EndFrame); API(Wait); API(Resize);
        API(CreateTextureRgba8); API(ReleaseResource); API(UploadTextureRgba8); API(DrawTexture); API(Capture);
        API(CreateBuffer); API(UploadBuffer); API(CopyBuffer); API(ReadBuffer); API(ReadSubmittedBuffer);
        API(CreateComputePipeline); API(ReleaseComputePipeline); API(Dispatch);
        API(BeginCommands); API(MemoryBarrier); API(Flush);
#undef API
        bool useWindow = argc > 3 && wcscmp(argv[3], L"window") == 0;
        UINT flags = argc > 2 ? wcstoul(argv[2], nullptr, 10) : 0;
        if (useWindow)
        {
            WNDCLASSW wc{};
            wc.lpfnWndProc = DefWindowProcW;
            wc.hInstance = GetModuleHandleW(nullptr);
            wc.lpszClassName = L"RyuboxD3D12Smoke";
            RegisterClassW(&wc);
            // No visible window; the application is tested off the user's desktop.
            window = CreateWindowW(wc.lpszClassName, L"D3D12 validation", WS_OVERLAPPEDWINDOW,
                -32000, -32000, 400, 300, nullptr, nullptr, wc.hInstance, nullptr);
            Require(window != nullptr, "window creation");
        }
        Require(Create(window, nullptr, 0, 50, flags, &context) == E_INVALIDARG && !context, "invalid dimensions");
        Check(Create(window, nullptr, 70, 50, flags, &context), "create native D3D12");
        RyuboxD3D12Info info{}; info.size = sizeof(info);
        Check(GetInfo(context, &info), "device info");
        std::cout << "adapter vendor=0x" << std::hex << info.vendorId << " device=0x" << info.deviceId
            << " featureLevel=0x" << info.featureLevel << std::dec << " bindingTier=" << info.resourceBindingTier << '\n';
        Require(info.abiVersion == 1, "ABI version");
        TestPrograms(dll,context);
        uint64_t texture = 0, source = 0, destination = 0;
        Check(CreateTextureRgba8(context, 7, 5, &texture), "create odd texture");
        Check(CreateBuffer(context, 257, &source), "source buffer");
        Check(CreateBuffer(context, 257, &destination), "destination buffer");
        // A unique color per pixel plus row padding catches pitch, origin and scaling errors.
        std::vector<uint32_t> pattern(10 * 5, 0xdeadbeef);
        for (UINT y = 0; y < 5; ++y)
            for (UINT x = 0; x < 7; ++x) pattern[y * 10 + x] = 0xff000000 | (x * 31 + 7) | ((y * 43 + 11) << 8) | (71 << 16);
        std::array<BYTE, 113> bytes{};
        for (UINT i = 0; i < bytes.size(); ++i) bytes[i] = static_cast<BYTE>(i * 17 + 3);
        std::array<BYTE, 113> read{};
        uint64_t fence = 0;
        Check(BeginFrame(context,0,0,0,1), "begin");
        Require(BeginFrame(context,0,0,0,1) == HRESULT_FROM_WIN32(ERROR_INVALID_STATE), "reject nested frame");
        Require(UploadTextureRgba8(context, texture, pattern.data(), 4, 40) == E_INVALIDARG, "short upload rejected");
        Require(UploadBuffer(context,source,256,bytes.data(),bytes.size()) == E_INVALIDARG, "buffer bounds");
        Check(UploadTextureRgba8(context,texture,pattern.data(),pattern.size()*4,40), "padded texture upload");
        Check(UploadBuffer(context,source,17,bytes.data(),bytes.size()), "unaligned buffer upload");
        Check(CopyBuffer(context,source,destination,17,29,bytes.size()), "GPU buffer copy");
        Check(DrawTexture(context,texture,0,0,1,1,0), "GPU texture blit");
        Check(EndFrame(context,0,&fence), "submit");
        Check(ReadBuffer(context,destination,29,read.data(),read.size()), "buffer readback");
        Require(bytes == read, "exact buffer roundtrip");
        // A readback closes its private command list. A guest UAV barrier can
        // arrive before the next draw/present and must still be queued safely.
        Check(MemoryBarrier(context), "barrier after closed readback list");
        Check(BeginCommands(context), "idempotent begin while barrier list open");
        Check(Flush(context,&fence), "flush reopened barrier list");
        Check(Wait(context,fence), "reopened barrier fence");
        // A CPU guest-memory callback can read the last submitted GPU result
        // while the GPU owner has a newer, unsubmitted list open. It must not
        // submit or reset that list, and the newer upload must remain intact.
        Check(BeginCommands(context), "open list before background read");
        auto previous = bytes;
        bytes[0] ^= 0x5a;
        Check(UploadBuffer(context,destination,29,bytes.data(),bytes.size()), "pending overwrite");
        HRESULT backgroundResult = E_FAIL;
        std::thread reader([&] {
            backgroundResult = ReadSubmittedBuffer(context,destination,29,read.data(),read.size());
        });
        reader.join();
        Check(backgroundResult, "cross-thread submitted-only read");
        Require(read == previous, "background read observed pending commands");
        Check(Flush(context,&fence), "submit list after background read");
        Check(Wait(context,fence), "wait pending overwrite");
        Check(ReadBuffer(context,destination,29,read.data(),read.size()), "updated read");
        Require(read == bytes, "background read disturbed open list");
        std::vector<uint32_t> pixels(70 * 50);
        auto verify = [&](bool flip)
        {
            Check(Capture(context,pixels.data(),pixels.size()*4), "capture");
            for (UINT y = 0; y < 5; ++y)
                for (UINT x = 0; x < 7; ++x)
                    Require(pixels[(y*10+5)*70+x*10+5] == pattern[(flip ? 4-y : y)*10+x], "orientation/scale/pitch");
        };
        verify(false);
        Check(BeginFrame(context,0,0,0,1), "flip begin");
        Check(DrawTexture(context,texture,0,0,1,1,1), "single flip");
        Check(EndFrame(context,1,&fence), "vsync submit");
        verify(true);
        Check(BeginFrame(context,0,0,0,1), "crop begin");
        Check(DrawTexture(context,texture,2.0f/7,1.0f/5,5.0f/7,4.0f/5,0), "crop");
        Check(EndFrame(context,0,&fence), "crop submit");
        Check(Capture(context,pixels.data(),pixels.size()*4), "crop capture");
        Require(pixels[25*70+35] == pattern[2*10+3], "crop center");
        // Reuse upload pages, allocators and descriptor ranges under GPU load.
        for (UINT frame = 0; frame < 90; ++frame)
        {
            Check(BeginFrame(context,0,0,0,1), "ring begin");
            bytes[0] = static_cast<BYTE>(frame);
            Check(UploadBuffer(context,source,17,bytes.data(),bytes.size()), "ring upload");
            Check(CopyBuffer(context,source,destination,17,29,bytes.size()), "ring copy");
            Check(DrawTexture(context,texture,0,0,1,1,0), "ring draw");
            Check(EndFrame(context,0,&fence), "ring submit");
        }
        Check(Wait(context,fence), "last fence");
        Require(Wait(context,fence+100) == E_INVALIDARG, "reject future fence");
        Check(ReadBuffer(context,destination,29,read.data(),read.size()), "latest buffer");
        Require(bytes == read, "buffer state decay/reuse");
        verify(false);
        const char* compute = R"(
ByteAddressBuffer source : register(t0);
RWByteAddressBuffer destination : register(u0);
cbuffer Parameters : register(b0) { uint addend; };
[numthreads(1,1,1)] void main(uint3 id : SV_DispatchThreadID) {
    destination.Store(id.x*4,source.Load(id.x*4)+addend);
})";
        uint64_t pipeline = 0;
        Check(CreateComputePipeline(context,compute,strlen(compute),&pipeline), "compute PSO");
        uint32_t numbers[16]{}, calculated[16]{}, addend = 7;
        for(UINT i=0;i<16;++i) numbers[i]=i*29;
        Check(BeginFrame(context,0,0,0,1), "compute begin");
        Check(UploadBuffer(context,source,0,numbers,sizeof(numbers)), "compute upload");
        Check(Dispatch(context,pipeline,source,destination,&addend,1,16,1,1), "compute dispatch");
        Check(Dispatch(context,pipeline,destination,source,&addend,1,16,1,1), "compute RAW/WAR barriers");
        Check(ReleaseComputePipeline(context,pipeline), "in-flight PSO release");
        Check(EndFrame(context,0,&fence), "compute submit");
        Check(ReadBuffer(context,source,0,calculated,sizeof(calculated)), "compute readback");
        for(UINT i=0;i<16;++i) Require(calculated[i] == numbers[i]+14, "compute values and UAV ordering");
        Check(Resize(context,140,100), "resize");
        Check(BeginFrame(context,0,0,0,1), "resized begin");
        Check(DrawTexture(context,texture,0,0,1,1,0), "resized draw");
        // Delete handles before execution; command list retains their storage.
        Check(ReleaseResource(context,texture), "deferred release");
        Require(DrawTexture(context,texture,0,0,1,1,0) == E_INVALIDARG, "stale handle rejected");
        Check(EndFrame(context,0,&fence), "deferred submit");
        pixels.resize(140*100);
        Check(Capture(context,pixels.data(),pixels.size()*4), "resized capture");
        for (UINT y = 0; y < 5; ++y)
            for (UINT x = 0; x < 7; ++x)
                Require(pixels[(y*20+10)*140+x*20+10] == pattern[y*10+x], "resize and deferred lifetime");
        Check(ReleaseResource(context,source), "release source");
        Check(ReleaseResource(context,destination), "release destination");
        Check(GetInfo(context,&info), "final device info");
        Require(SUCCEEDED(info.deviceRemovedReason) && info.debugErrors == 0, "no device/debug errors");
        std::cout << "PASS native D3D12: upload, buffer copy, compute/UAV, pitch, scale, crop, flip, 90 frames, resize, deferred release; "
            << "submitted=" << info.submitted << " completed=" << info.completed
            << " uploadBytes=" << info.uploadBytes << " waits=" << info.cpuFenceWaits
            << " debugErrors=" << info.debugErrors << '\n';
        Check(destroy(context), "destroy"); context = nullptr;
        // Recovery/restart uses a fresh instance rather than stale global COM state.
        Check(Create(nullptr,nullptr,8,8,flags,&context), "restart");
        Check(BeginFrame(context,1,0,0,1), "restart clear");
        Check(EndFrame(context,0,&fence), "restart submit");
        Check(Wait(context,fence), "restart wait");
        Check(destroy(context), "restart dispose"); context = nullptr;
        if(window) DestroyWindow(window);
        FreeLibrary(dll);
        return 0;
    }
    catch(const std::exception& ex)
    {
        std::cerr << "FAIL " << ex.what() << '\n';
        if(context) destroy(context);
        if(window) DestroyWindow(window);
        FreeLibrary(dll);
        return 1;
    }
}
