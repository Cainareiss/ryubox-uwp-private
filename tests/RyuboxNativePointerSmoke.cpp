#include <windows.h>
#include <dbghelp.h>

#include <array>
#include <cstdint>
#include <iostream>

using SetIntSettingFn = void(__stdcall*)(const char*, int);
using InitializeFn = HRESULT(__stdcall*)(HWND, std::uint32_t, std::uint32_t);
using RenderFn = HRESULT(__stdcall*)();
using SetInputFn = void(__stdcall*)(std::uint32_t, float, float, float, float, float, float);
using SetPointerFn = void(__stdcall*)(float, float, int, int, float);
using SetStatusFn = void(__stdcall*)(const wchar_t*, const wchar_t*);
using SetCatalogueFn = void(__stdcall*)(const wchar_t*);
using SetOperationFn = void(__stdcall*)(const wchar_t*, float, int);
using ConsumeActionFn = int(__stdcall*)();

void PumpWindowMessages()
{
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
    {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
}

LONG WINAPI DumpAssertionStack(EXCEPTION_POINTERS* exception)
{
    if (!exception || (exception->ExceptionRecord->ExceptionCode != EXCEPTION_BREAKPOINT &&
        exception->ExceptionRecord->ExceptionCode != STATUS_STACK_BUFFER_OVERRUN))
        return EXCEPTION_CONTINUE_SEARCH;

    HANDLE process = GetCurrentProcess();
    const DWORD64 nativeBase = reinterpret_cast<DWORD64>(
        GetModuleHandleW(L"Ryujinx.Uwp.Native.dll"));
    void* frames[64]{};
    const USHORT count = CaptureStackBackTrace(0, 64, frames, nullptr);
    std::cerr << "Native assertion stack:" << std::endl;
    for (USHORT index = 0; index < count; ++index)
    {
        const DWORD64 address = reinterpret_cast<DWORD64>(frames[index]);
        if (nativeBase != 0 && address >= nativeBase)
            std::cerr << index << ": native+0x" << std::hex << (address - nativeBase);
        else
            std::cerr << index << ": 0x" << std::hex << address;
        std::cerr << std::endl;
    }
    TerminateProcess(process, 10);
    return EXCEPTION_EXECUTE_HANDLER;
}

int wmain(int argc, wchar_t** argv)
{
    AddVectoredExceptionHandler(1, DumpAssertionStack);
    SetEnvironmentVariableW(L"RYUBOX_DIAGNOSTIC_NO_PRESENT", L"1");
    const wchar_t* libraryPath = argc > 1 ? argv[1] :
        L"E:\\ports\\ryujinx uwp\\src\\Ryujinx.Uwp.Native\\bin\\x64\\Release\\Ryujinx.Uwp.Native.dll";
    HMODULE module = LoadLibraryW(libraryPath);
    if (!module)
    {
        std::cerr << "LoadLibrary failed: " << GetLastError() << '\n';
        return 2;
    }

    const auto setting = reinterpret_cast<SetIntSettingFn>(
        GetProcAddress(module, "RyujinxUwpFrontend_SetIntSetting"));
    const auto initialize = reinterpret_cast<InitializeFn>(
        GetProcAddress(module, "RyujinxUwpGraphicsD3D11_InitializeForWindow"));
    const auto render = reinterpret_cast<RenderFn>(
        GetProcAddress(module, "RyujinxUwpGraphicsD3D11_Render"));
    const auto input = reinterpret_cast<SetInputFn>(
        GetProcAddress(module, "RyujinxUwpGraphicsD3D11_ImGuiSetInput"));
    const auto pointer = reinterpret_cast<SetPointerFn>(
        GetProcAddress(module, "RyujinxUwpGraphicsD3D11_ImGuiSetPointer"));
    const auto status = reinterpret_cast<SetStatusFn>(
        GetProcAddress(module, "RyujinxUwpGraphicsD3D11_ImGuiSetStatus"));
    const auto catalogue = reinterpret_cast<SetCatalogueFn>(
        GetProcAddress(module, "RyujinxUwpGraphicsD3D11_ImGuiSetCatalogue"));
    const auto operation = reinterpret_cast<SetOperationFn>(
        GetProcAddress(module, "RyujinxUwpGraphicsD3D11_ImGuiSetOperation"));
    const auto consumeAction = reinterpret_cast<ConsumeActionFn>(
        GetProcAddress(module, "RyujinxUwpGraphicsD3D11_ImGuiConsumeAction"));
    if (!setting || !initialize || !render || !input || !pointer || !status ||
        !catalogue || !operation || !consumeAction)
        return 3;
#define render() (PumpWindowMessages(), render(), PumpWindowMessages())

    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"RyuboxPointerSmokeWindow";
    RegisterClassW(&windowClass);
    HWND window = CreateWindowExW(0, windowClass.lpszClassName, L"Ryubox pointer smoke",
        WS_OVERLAPPEDWINDOW, -32000, -32000, 1366, 696, nullptr, nullptr,
        windowClass.hInstance, nullptr);
    ShowWindow(window, SW_SHOWNOACTIVATE);
    UpdateWindow(window);

    setting("fpsLimit", 60);
    if (FAILED(initialize(window, 1366, 696)))
        return 4;
    status(L"prod.keys configuradas", L"Firmware instalado");
    catalogue(L"Game 1\nGame 2\nGame 3\nGame 4\nGame 5\nGame 6");

    input(0, 0, 0, 0, 0, 0, 0);
    pointer(40.0f, 412.0f, 0, 0, 0);
    for (int frame = 0; frame < 5; ++frame)
        render();

    std::cout << "Clicking the left settings rail..." << std::endl;
    pointer(40.0f, 412.0f, 1, 0, 0);
    render();
    pointer(40.0f, 412.0f, 0, 0, 0);
    render();
    std::cout << "Settings opened." << std::endl;

    // A enters the selected Setup page. Move from the firmware action to the
    // prod.keys folder action and activate it, proving that focus did not stop
    // at the tab list.
    input(1u << 4, 0, 0, 0, 0, 0, 0);
    render();
    input(0, 0, 0, 0, 0, 0, 0);
    render();
    input(1u << 1, 0, 0, 0, 0, 0, 0);
    render();
    input(0, 0, 0, 0, 0, 0, 0);
    render();
    input(1u << 4, 0, 0, 0, 0, 0, 0);
    render();
    input(0, 0, 0, 0, 0, 0, 0);
    render();
    const int keysAction = consumeAction();
    std::cout << "Setup prod.keys action=" << keysAction << std::endl;
    if (keysAction != -1003)
        return 11;
    // B returns to the tab list instead of closing the settings screen.
    input(1u << 5, 0, 0, 0, 0, 0, 0);
    render();
    input(0, 0, 0, 0, 0, 0, 0);
    render();
    std::cout << "Settings content navigation rendered." << std::endl;

    // Navigate all settings tabs and transfer focus into/out of their content.
    for (int tab = 0; tab < 8; ++tab)
    {
        input(1u << 1, 0, 0, 0, 0, 0, 0);
        render();
        input(0, 0, 0, 0, 0, 0, 0);
        render();
        std::cout << "Settings tab " << tab + 1 << " rendered." << std::endl;
    }
    input(1u << 3, 0, 0, 0, 0, 0, 0);
    render();
    input(0, 0, 0, 0, 0, 0, 0);
    render();
    std::cout << "Settings pane navigation rendered." << std::endl;
    input(1u << 2, 0, 0, 0, 0, 0, 0);
    render();
    input(0, 0, 0, 0, 0, 0, 0);
    render();

    // Return home, toggle grid, and open the dedicated controller/video pages.
    input(1u << 5, 0, 0, 0, 0, 0, 0);
    render();
    input(0, 0, 0, 0, 0, 0, 0);
    render();
    pointer(40.0f, 330.0f, 1, 0, 0);
    render();
    pointer(40.0f, 330.0f, 0, 0, 0);
    render();
    std::cout << "Grid toggle rendered." << std::endl;
    pointer(1320.0f, 255.0f, 1, 0, 0);
    render();
    pointer(1320.0f, 255.0f, 0, 0, 0);
    render();
    std::cout << "Controller page rendered." << std::endl;
    input(1u << 5, 0, 0, 0, 0, 0, 0);
    render();
    input(0, 0, 0, 0, 0, 0, 0);
    render();
    pointer(1320.0f, 340.0f, 1, 0, 0);
    render();
    pointer(1320.0f, 340.0f, 0, 0, 0);
    render();
    std::cout << "Graphics page rendered." << std::endl;

    operation(L"Validando popup de firmware", 0.73f, 1);
    for (int frame = 0; frame < 10; ++frame)
        render();
    operation(L"Firmware instalado", 1.0f, 2);
    for (int frame = 0; frame < 5; ++frame)
        render();
    std::cout << "Pointer settings smoke completed." << std::endl;

    // Avoid unrelated static-runtime teardown in the DLL masking the render result.
    TerminateProcess(GetCurrentProcess(), 0);
#undef render
}
