// Native keep-alive for the Parsec Virtual Display Driver (moonlight-vdd-keepalive.dll).
//
// The driver unplugs every virtual display when it gets no VDD_IOCTL_UPDATE for about a second. The hub used to ping
// from a managed thread, which stops whenever the .NET GC suspends the process and whenever one of its pages has to be
// read back from a busy disk: on 2026-09-30 15:06:40 the tablet display vanished that way while a 30 GB image restore
// kept the disk saturated. This thread never enters managed code (a GC does not suspend it), runs at
// THREAD_PRIORITY_TIME_CRITICAL and keeps its code, data and stack locked in the working set.
//
// Several contexts may run at once (hot replace opens a second handle in the same process for a moment).
// Build: build.ps1 (MSVC, static CRT) -> MoonlightHub\src\tools\moonlight-vdd-keepalive.dll

#include <windows.h>

#define VDD_IOCTL_UPDATE 0x0022A00Cu
#define IO_TIMEOUT_MS 5000u
#define CANCEL_TIMEOUT_MS 3000u
#define LONG_GAP_MS 300u
#define STACK_BYTES (64u * 1024u)

typedef struct VkaStats
{
    LONG running;              // 1 while the ping thread loops
    LONG faulted;              // 1 when an UPDATE did not complete (dead driver host); the thread has stopped
    LONG consecutiveFailures;
    LONG lastError;            // Win32 error of the last failure
    LONG64 pings;              // completed UPDATE round trips
    LONG64 failures;           // failed UPDATEs
    LONG64 msSinceLastPing;    // -1 before the first ping
    LONG maxGapMs;             // longest interval between two completed pings since the last reset
    LONG maxIoMs;              // longest UPDATE round trip since the last reset
    LONG longGaps;             // gaps >= LONG_GAP_MS since the last reset
    LONG memoryLocked;         // 1 when code, data and stack are locked in the working set
} VkaStats;

typedef struct IoBlock
{
    OVERLAPPED overlapped;
    BYTE input[32];
    BYTE output[32];
} IoBlock;

typedef struct VkaContext
{
    HANDLE device;
    HANDLE stopEvent;
    HANDLE ioEvent;
    HANDLE thread;
    DWORD intervalMs;
    IoBlock* io;               // leaked (not freed) when the driver never completed a request
    volatile LONG running;
    volatile LONG faulted;
    volatile LONG consecutiveFailures;
    volatile LONG lastError;
    volatile LONG64 pings;
    volatile LONG64 failures;
    volatile LONG64 lastPingTick;
    volatile LONG maxGapMs;
    volatile LONG maxIoMs;
    volatile LONG longGaps;
    volatile LONG memoryLocked;
} VkaContext;

static HMODULE g_module;
static volatile LONG g_workingSetRaised;

static void RaiseMax(volatile LONG* target, LONG value)
{
    LONG current = *target;
    while (value > current)
    {
        LONG seen = InterlockedCompareExchange(target, value, current);
        if (seen == current) break;
        current = seen;
    }
}

// VirtualLock only works within the minimum working set, whose default (~200 KB) is shared with everything else.
static void EnsureWorkingSetRoom(void)
{
    SIZE_T minimum = 0, maximum = 0;
    if (InterlockedExchange(&g_workingSetRaised, 1) != 0) return;
    if (!GetProcessWorkingSetSize(GetCurrentProcess(), &minimum, &maximum)) return;
    SetProcessWorkingSetSize(GetCurrentProcess(), minimum + 4u * 1024u * 1024u, maximum + 4u * 1024u * 1024u);
}

static BOOL LockRange(const void* start, SIZE_T bytes)
{
    return start != NULL && bytes != 0 && VirtualLock((LPVOID)start, bytes);
}

// Locks this DLL's image (code, static data), the committed top of the thread stack, the I/O block and the pages of
// the system functions the loop calls.
static BOOL LockHotPages(VkaContext* context)
{
    BOOL ok = TRUE;
    ULONG_PTR stackLow = 0, stackHigh = 0;
    const IMAGE_DOS_HEADER* dos = (const IMAGE_DOS_HEADER*)g_module;
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    HMODULE kernelBase = GetModuleHandleW(L"kernelbase.dll");
    static const char* const ntdllFunctions[] = { "NtDeviceIoControlFile", "NtWaitForSingleObject", "NtWaitForMultipleObjects", "NtCancelIoFileEx", "NtQueryPerformanceCounter" };
    static const char* const kernelBaseFunctions[] = { "DeviceIoControl", "WaitForSingleObject", "WaitForMultipleObjects", "GetOverlappedResult", "CancelIoEx", "GetTickCount64" };
    int i;

    EnsureWorkingSetRoom();
    if (dos != NULL && dos->e_magic == IMAGE_DOS_SIGNATURE)
    {
        const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*)((const BYTE*)dos + dos->e_lfanew);
        ok &= LockRange(dos, nt->OptionalHeader.SizeOfImage);
    }
    else
    {
        ok = FALSE;
    }

    GetCurrentThreadStackLimits(&stackLow, &stackHigh);
    if (stackHigh > stackLow + 16u * 1024u) ok &= LockRange((const void*)(stackHigh - 16u * 1024u), 16u * 1024u);
    ok &= LockRange(context, sizeof(*context));
    ok &= LockRange(context->io, sizeof(*context->io));

    for (i = 0; ntdll != NULL && i < (int)(sizeof(ntdllFunctions) / sizeof(ntdllFunctions[0])); i++)
    {
        FARPROC f = GetProcAddress(ntdll, ntdllFunctions[i]);
        if (f != NULL) LockRange((const void*)f, 64);
    }
    for (i = 0; kernelBase != NULL && i < (int)(sizeof(kernelBaseFunctions) / sizeof(kernelBaseFunctions[0])); i++)
    {
        FARPROC f = GetProcAddress(kernelBase, kernelBaseFunctions[i]);
        if (f != NULL) LockRange((const void*)f, 64);
    }
    return ok;
}

static void RecordFailure(VkaContext* context, DWORD error)
{
    InterlockedExchange(&context->lastError, (LONG)error);
    InterlockedIncrement(&context->consecutiveFailures);
    InterlockedIncrement64(&context->failures);
}

// One UPDATE round trip. Returns FALSE when the request is still owned by the driver (the I/O block must not be reused).
static BOOL PingOnce(VkaContext* context)
{
    IoBlock* io = context->io;
    HANDLE waits[2];
    DWORD transferred = 0, error, wait;
    ULONGLONG started = GetTickCount64(), now;
    int i;

    for (i = 0; i < (int)sizeof(io->overlapped); i++) ((BYTE*)&io->overlapped)[i] = 0;
    io->overlapped.hEvent = context->ioEvent;
    ResetEvent(context->ioEvent);

    if (!DeviceIoControl(context->device, VDD_IOCTL_UPDATE, io->input, sizeof(io->input), io->output, sizeof(io->output), NULL, &io->overlapped))
    {
        error = GetLastError();
        if (error != ERROR_IO_PENDING)
        {
            RecordFailure(context, error);
            return TRUE;
        }
        waits[0] = context->ioEvent;
        waits[1] = context->stopEvent;
        wait = WaitForMultipleObjects(2, waits, FALSE, IO_TIMEOUT_MS);
        if (wait != WAIT_OBJECT_0)
        {
            // timed out (driver host hung) or asked to stop mid-request: cancel and give the driver a moment
            CancelIoEx(context->device, &io->overlapped);
            if (WaitForSingleObject(context->ioEvent, CANCEL_TIMEOUT_MS) != WAIT_OBJECT_0)
            {
                RecordFailure(context, wait == WAIT_TIMEOUT ? WAIT_TIMEOUT : ERROR_OPERATION_ABORTED);
                return FALSE;
            }
            // a slow but cancellable UPDATE only counts as a failure (the watchdog reopens after 3 in a row)
            if (wait == WAIT_TIMEOUT) RecordFailure(context, WAIT_TIMEOUT);
            return TRUE;
        }
    }
    if (!GetOverlappedResult(context->device, &io->overlapped, &transferred, FALSE))
    {
        RecordFailure(context, GetLastError());
        return TRUE;
    }

    now = GetTickCount64();
    RaiseMax(&context->maxIoMs, (LONG)(now - started));
    if (context->lastPingTick != 0)
    {
        LONG gap = (LONG)(now - (ULONGLONG)context->lastPingTick);
        RaiseMax(&context->maxGapMs, gap);
        if (gap >= (LONG)LONG_GAP_MS) InterlockedIncrement(&context->longGaps);
    }
    InterlockedExchange64(&context->lastPingTick, (LONG64)now);
    InterlockedIncrement64(&context->pings);
    InterlockedExchange(&context->consecutiveFailures, 0);
    return TRUE;
}

static DWORD WINAPI PingThread(LPVOID parameter)
{
    VkaContext* context = (VkaContext*)parameter;
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
    InterlockedExchange(&context->memoryLocked, LockHotPages(context) ? 1 : 0);

    // ping first, then wait: a fresh handle must keep the displays of the process it replaces alive right away
    for (;;)
    {
        if (!PingOnce(context))
        {
            context->io = NULL; // still owned by the driver: leak it rather than let the kernel write into freed memory
            InterlockedExchange(&context->faulted, 1);
            break;
        }
        if (context->faulted) break;
        if (WaitForSingleObject(context->stopEvent, context->intervalMs) != WAIT_TIMEOUT) break;
    }
    InterlockedExchange(&context->running, 0);
    return 0;
}

// Context and I/O block get pages of their own (VirtualAlloc, zeroed) so locking / unlocking one never touches another.
static void* AllocPages(SIZE_T bytes)
{
    return VirtualAlloc(NULL, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
}

static void FreePages(void* p)
{
    if (p == NULL) return;
    VirtualUnlock(p, 1);
    VirtualFree(p, 0, MEM_RELEASE);
}

static void FreeContext(VkaContext* context)
{
    if (context->thread != NULL) CloseHandle(context->thread);
    if (context->stopEvent != NULL) CloseHandle(context->stopEvent);
    if (context->ioEvent != NULL) CloseHandle(context->ioEvent);
    FreePages(context->io);
    FreePages(context);
}

// Starts pinging `device` (a handle opened with FILE_FLAG_OVERLAPPED) every `intervalMs` (clamped to 10..90).
// Returns an opaque context, or NULL with GetLastError() set.
__declspec(dllexport) void* __stdcall vka_start(HANDLE device, int intervalMs)
{
    VkaContext* context;
    DWORD error;

    if (device == NULL || device == INVALID_HANDLE_VALUE)
    {
        SetLastError(ERROR_INVALID_HANDLE);
        return NULL;
    }
    context = (VkaContext*)AllocPages(sizeof(VkaContext));
    if (context == NULL) return NULL;
    context->device = device;
    context->intervalMs = (DWORD)(intervalMs < 10 ? 10 : intervalMs > 90 ? 90 : intervalMs);
    context->io = (IoBlock*)AllocPages(sizeof(IoBlock));
    context->stopEvent = CreateEventW(NULL, TRUE, FALSE, NULL);
    context->ioEvent = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (context->io == NULL || context->stopEvent == NULL || context->ioEvent == NULL)
    {
        error = GetLastError();
        FreeContext(context);
        SetLastError(error == 0 ? ERROR_NOT_ENOUGH_MEMORY : error);
        return NULL;
    }
    context->running = 1;
    // STACK_BYTES is committed up front (no STACK_SIZE_PARAM_IS_A_RESERVATION), so the locked stack pages exist
    context->thread = CreateThread(NULL, STACK_BYTES, PingThread, context, 0, NULL);
    if (context->thread == NULL)
    {
        error = GetLastError();
        context->running = 0;
        FreeContext(context);
        SetLastError(error);
        return NULL;
    }
    return context;
}

// Stops the thread and frees the context. The device handle stays open (it belongs to the caller).
// Returns 1 when the thread ended; 0 when it is still stuck in the driver (the context is then leaked on purpose).
__declspec(dllexport) int __stdcall vka_stop(void* handle)
{
    VkaContext* context = (VkaContext*)handle;
    if (context == NULL) return 1;
    SetEvent(context->stopEvent);
    if (WaitForSingleObject(context->thread, IO_TIMEOUT_MS + CANCEL_TIMEOUT_MS + 1000u) != WAIT_OBJECT_0) return 0;
    FreeContext(context);
    return 1;
}

// Copies the counters; `resetPeaks` != 0 clears maxGapMs / maxIoMs / longGaps after reading them.
__declspec(dllexport) void __stdcall vka_stats(void* handle, VkaStats* stats, int resetPeaks)
{
    VkaContext* context = (VkaContext*)handle;
    LONG64 last;
    if (stats == NULL) return;
    if (context == NULL)
    {
        BYTE* p = (BYTE*)stats;
        int i;
        for (i = 0; i < (int)sizeof(*stats); i++) p[i] = 0;
        stats->msSinceLastPing = -1;
        return;
    }
    stats->running = context->running;
    stats->faulted = context->faulted;
    stats->consecutiveFailures = context->consecutiveFailures;
    stats->lastError = context->lastError;
    stats->pings = context->pings;
    stats->failures = context->failures;
    last = context->lastPingTick;
    stats->msSinceLastPing = last == 0 ? -1 : (LONG64)(GetTickCount64() - (ULONGLONG)last);
    stats->memoryLocked = context->memoryLocked;
    if (resetPeaks)
    {
        stats->maxGapMs = InterlockedExchange(&context->maxGapMs, 0);
        stats->maxIoMs = InterlockedExchange(&context->maxIoMs, 0);
        stats->longGaps = InterlockedExchange(&context->longGaps, 0);
    }
    else
    {
        stats->maxGapMs = context->maxGapMs;
        stats->maxIoMs = context->maxIoMs;
        stats->longGaps = context->longGaps;
    }
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}
