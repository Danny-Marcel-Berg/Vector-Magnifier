#include <Windows.h>
#include <cstdio>
#include <cstdlib>
#include <cwchar>
#include <string>

int wmain(int argc, wchar_t* argv[])
{
    if (argc < 2)
    {
        std::fwprintf(stderr, L"Usage: HookInjector.exe <PID>\n");
        return 1;
    }

    const DWORD pid = static_cast<DWORD>(std::wcstoul(argv[1], nullptr, 10));
    if (pid == 0)
    {
        std::fwprintf(stderr, L"Invalid PID: %ls\n", argv[1]);
        return 1;
    }

    wchar_t exePath[MAX_PATH]{};
    const DWORD len = GetModuleFileNameW(nullptr, exePath, MAX_PATH);
    if (len == 0 || len >= MAX_PATH)
    {
        std::fwprintf(stderr, L"GetModuleFileNameW failed (%lu).\n", GetLastError());
        return 1;
    }

    std::wstring dllPath(exePath);
    const size_t slashPos = dllPath.find_last_of(L"\\/");
    if (slashPos == std::wstring::npos)
    {
        std::fwprintf(stderr, L"Failed to resolve directory from %ls.\n", exePath);
        return 1;
    }
    dllPath = dllPath.substr(0, slashPos + 1) + L"ZoomHook64.dll";

    if (GetFileAttributesW(dllPath.c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        std::fwprintf(stderr, L"ZoomHook64.dll not found at: %ls\n", dllPath.c_str());
        return 1;
    }

    HANDLE hProcess = OpenProcess(
        PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
        FALSE,
        pid);

    if (!hProcess)
    {
        std::fwprintf(stderr, L"OpenProcess(%lu) failed (%lu).\n", pid, GetLastError());
        return 1;
    }

    const SIZE_T bytesNeeded = (dllPath.size() + 1) * sizeof(wchar_t);
    LPVOID remoteMem = VirtualAllocEx(hProcess, nullptr, bytesNeeded, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remoteMem)
    {
        std::fwprintf(stderr, L"VirtualAllocEx failed (%lu).\n", GetLastError());
        CloseHandle(hProcess);
        return 1;
    }

    if (!WriteProcessMemory(hProcess, remoteMem, dllPath.c_str(), bytesNeeded, nullptr))
    {
        std::fwprintf(stderr, L"WriteProcessMemory failed (%lu).\n", GetLastError());
        VirtualFreeEx(hProcess, remoteMem, 0, MEM_RELEASE);
        CloseHandle(hProcess);
        return 1;
    }

    HMODULE hKernel32 = GetModuleHandleW(L"kernel32.dll");
    FARPROC loadLibraryAddr = hKernel32 ? GetProcAddress(hKernel32, "LoadLibraryW") : nullptr;
    if (!loadLibraryAddr)
    {
        std::fwprintf(stderr, L"Failed to locate kernel32!LoadLibraryW.\n");
        VirtualFreeEx(hProcess, remoteMem, 0, MEM_RELEASE);
        CloseHandle(hProcess);
        return 1;
    }

    HANDLE hThread = CreateRemoteThread(
        hProcess,
        nullptr,
        0,
        reinterpret_cast<LPTHREAD_START_ROUTINE>(loadLibraryAddr),
        remoteMem,
        0,
        nullptr);

    if (!hThread)
    {
        std::fwprintf(stderr, L"CreateRemoteThread failed (%lu).\n", GetLastError());
        VirtualFreeEx(hProcess, remoteMem, 0, MEM_RELEASE);
        CloseHandle(hProcess);
        return 1;
    }

    WaitForSingleObject(hThread, 5000);

    DWORD remoteModule = 0;
    GetExitCodeThread(hThread, &remoteModule);

    CloseHandle(hThread);
    VirtualFreeEx(hProcess, remoteMem, 0, MEM_RELEASE);
    CloseHandle(hProcess);

    if (remoteModule == 0)
    {
        std::fwprintf(stderr, L"Remote LoadLibraryW returned NULL for %ls.\n", dllPath.c_str());
        return 1;
    }

    std::wprintf(L"Injected %ls into PID %lu.\n", dllPath.c_str(), pid);
    return 0;
}

