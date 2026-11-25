// Multithreaded benchmark - same workloads as mt.fg. Win32 threads,
// Interlocked atomics, SRWLOCK as the mutex (what MSVC's std::mutex uses too).
#include <windows.h>
#include <stdio.h>
#include <stdint.h>
typedef int64_t i64;

typedef struct { volatile i64 next, total, limit; SRWLOCK lock; volatile i64 count; } Shared;
static Shared sh;
static LARGE_INTEGER freq;
static i64 now_us(void) { LARGE_INTEGER t; QueryPerformanceCounter(&t); return t.QuadPart * 1000000 / freq.QuadPart; }

static int is_prime(i64 n) {
    if (n < 2) return 0;
    if (n % 2 == 0) return n == 2;
    for (i64 d = 3; d * d <= n; d += 2) if (n % d == 0) return 0;
    return 1;
}
static DWORD WINAPI prime_worker(LPVOID _) {
    i64 local = 0;
    for (;;) {
        i64 start = InterlockedExchangeAdd64(&sh.next, 10000);
        if (start >= sh.limit) break;
        i64 end = start + 10000; if (end > sh.limit) end = sh.limit;
        for (i64 n = start; n < end; n++) local += is_prime(n);
    }
    InterlockedExchangeAdd64(&sh.total, local);
    return 0;
}
static DWORD WINAPI atomic_worker(LPVOID _) { for (int i = 0; i < 1000000; i++) InterlockedIncrement64(&sh.next); return 0; }
static DWORD WINAPI mutex_worker(LPVOID _) {
    for (int i = 0; i < 200000; i++) { AcquireSRWLockExclusive(&sh.lock); sh.count++; ReleaseSRWLockExclusive(&sh.lock); }
    return 0;
}
static DWORD WINAPI spawn_worker(LPVOID _) { InterlockedIncrement64(&sh.next); return 0; }

static void run(int t, LPTHREAD_START_ROUTINE f, HANDLE* hs) {
    for (int i = 0; i < t; i++) hs[i] = CreateThread(NULL, 0, f, NULL, 0, NULL);
    for (int i = 0; i < t; i++) { WaitForSingleObject(hs[i], INFINITE); CloseHandle(hs[i]); }
}

int main(void) {
    QueryPerformanceFrequency(&freq);
    SYSTEM_INFO si; GetSystemInfo(&si);
    int t = (int)si.dwNumberOfProcessors;
    HANDLE* hs = malloc(sizeof(HANDLE) * t);
    InitializeSRWLock(&sh.lock);
    printf("threads %d 0\n", t);
    i64 t0 = now_us(); sh.next = 0; sh.total = 0; sh.limit = 10000000;
    run(1, prime_worker, hs);
    printf("primes1 %lld %lld\n", sh.total, now_us() - t0);
    t0 = now_us(); sh.next = 0; sh.total = 0;
    run(t, prime_worker, hs);
    printf("primesN %lld %lld\n", sh.total, now_us() - t0);
    sh.next = 0; t0 = now_us();
    run(t, atomic_worker, hs);
    printf("atomic %lld %lld\n", sh.next, now_us() - t0);
    sh.count = 0; t0 = now_us();
    run(t, mutex_worker, hs);
    printf("mutex %lld %lld\n", sh.count, now_us() - t0);
    sh.next = 0; t0 = now_us();
    for (int r = 0; r < 500; r++) run(t, spawn_worker, hs);
    printf("spawn %lld %lld\n", sh.next, now_us() - t0);
    return 0;
}
