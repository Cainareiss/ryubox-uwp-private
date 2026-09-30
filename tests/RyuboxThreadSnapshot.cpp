#include <windows.h>
#include <tlhelp32.h>
#include <dbghelp.h>
#include <iostream>
#include <vector>

int wmain(int argc, wchar_t** argv)
{
    if (argc < 3) return 2;
    DWORD pid = wcstoul(argv[1], nullptr, 10);
    HANDLE process = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE, pid);
    if (!process) return 3;
    SymSetOptions(SYMOPT_UNDNAME | SYMOPT_DEFERRED_LOADS | SYMOPT_FAIL_CRITICAL_ERRORS);
    if (!SymInitializeW(process, argv[2], TRUE)) return 4;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    THREADENTRY32 entry{}; entry.dwSize = sizeof(entry);
    for (BOOL found = Thread32First(snapshot, &entry); found; found = Thread32Next(snapshot, &entry))
    {
        if (entry.th32OwnerProcessID != pid) continue;
        HANDLE thread = OpenThread(THREAD_GET_CONTEXT | THREAD_SUSPEND_RESUME | THREAD_QUERY_INFORMATION, FALSE, entry.th32ThreadID);
        if (!thread) continue;
        PWSTR name = nullptr;
        GetThreadDescription(thread, &name);
        std::wcout << L"\nTHREAD " << entry.th32ThreadID << L" " << (name ? name : L"") << std::endl;
        LocalFree(name);
        std::vector<DWORD64> frames;
        if (SuspendThread(thread) != DWORD(-1))
        {
            CONTEXT context{}; context.ContextFlags = CONTEXT_FULL;
            if (GetThreadContext(thread, &context))
            {
                STACKFRAME64 frame{};
                frame.AddrPC.Offset = context.Rip; frame.AddrPC.Mode = AddrModeFlat;
                frame.AddrStack.Offset = context.Rsp; frame.AddrStack.Mode = AddrModeFlat;
                frame.AddrFrame.Offset = context.Rbp; frame.AddrFrame.Mode = AddrModeFlat;
                for (int i = 0; i < 35; ++i)
                {
                    if (!StackWalk64(IMAGE_FILE_MACHINE_AMD64, process, thread, &frame, &context,
                        nullptr, SymFunctionTableAccess64, SymGetModuleBase64, nullptr)) break;
                    if (!frame.AddrPC.Offset) break;
                    frames.push_back(frame.AddrPC.Offset);
                }
            }
            ResumeThread(thread);
        }
        CloseHandle(thread);
        for (DWORD64 address : frames)
        {
            alignas(SYMBOL_INFOW) unsigned char storage[sizeof(SYMBOL_INFOW) + 2048 * sizeof(wchar_t)]{};
            auto symbol = reinterpret_cast<SYMBOL_INFOW*>(storage);
            symbol->SizeOfStruct = sizeof(SYMBOL_INFOW); symbol->MaxNameLen = 2048;
            DWORD64 displacement = 0;
            if (SymFromAddrW(process, address, &displacement, symbol))
                std::wcout << L"  " << symbol->Name << L" +0x" << std::hex << displacement << std::dec << std::endl;
            else std::wcout << L"  0x" << std::hex << address << std::dec << std::endl;
        }
    }
    CloseHandle(snapshot); SymCleanup(process); CloseHandle(process);
}
