#include <windows.h>
#include <dbghelp.h>

#include <array>
#include <cstdint>
#include <iostream>

int wmain(int argc, wchar_t** argv)
{
    if (argc != 3)
        return 2;

    wchar_t* end = nullptr;
    const DWORD64 rva = wcstoull(argv[2], &end, 16);
    HANDLE process = GetCurrentProcess();
    SymSetOptions(SYMOPT_LOAD_LINES | SYMOPT_UNDNAME | SYMOPT_DEFERRED_LOADS);
    if (!SymInitializeW(process, nullptr, FALSE))
        return 3;
    const DWORD64 base = SymLoadModuleExW(process, nullptr, argv[1], nullptr, 0, 0, nullptr, 0);
    if (!base)
    {
        std::wcerr << L"SymLoadModuleExW failed: " << GetLastError() << L'\n';
        return 4;
    }

    std::array<std::byte, sizeof(SYMBOL_INFO) + MAX_SYM_NAME> storage{};
    auto* symbol = reinterpret_cast<SYMBOL_INFO*>(storage.data());
    symbol->SizeOfStruct = sizeof(SYMBOL_INFO);
    symbol->MaxNameLen = MAX_SYM_NAME;
    DWORD64 displacement = 0;
    if (SymFromAddr(process, base + rva, &displacement, symbol))
        std::cout << symbol->Name << "+0x" << std::hex << displacement << '\n';

    IMAGEHLP_LINEW64 line{};
    line.SizeOfStruct = sizeof(line);
    DWORD lineDisplacement = 0;
    if (SymGetLineFromAddrW64(process, base + rva, &lineDisplacement, &line))
        std::wcout << line.FileName << L":" << std::dec << line.LineNumber << L"+0x"
                   << std::hex << lineDisplacement << L'\n';
    SymCleanup(process);
    return 0;
}
