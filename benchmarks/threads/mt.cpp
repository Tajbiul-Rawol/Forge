// Multithreaded benchmark - same workloads as mt.fg, standard C++:
// std::thread, std::atomic, std::mutex, std::chrono.
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <mutex>
#include <thread>
#include <vector>
using i64 = std::int64_t;
static i64 now_us() {
    return std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}
static bool is_prime(i64 n) {
    if (n < 2) return false;
    if (n % 2 == 0) return n == 2;
    for (i64 d = 3; d * d <= n; d += 2) if (n % d == 0) return false;
    return true;
}
template <class F> static void run(int t, F f) {
    std::vector<std::thread> v;
    for (int i = 0; i < t; i++) v.emplace_back(f);
    for (auto& th : v) th.join();
}
static i64 primes(int t) {
    std::atomic<i64> next{0}, total{0};
    const i64 limit = 10000000;
    run(t, [&] {
        i64 local = 0;
        for (;;) {
            i64 start = next.fetch_add(10000);
            if (start >= limit) break;
            i64 end = std::min(start + 10000, limit);
            for (i64 n = start; n < end; n++) local += is_prime(n);
        }
        total += local;
    });
    return total;
}
int main() {
    int t = (int)std::thread::hardware_concurrency();
    std::printf("threads %d 0\n", t);
    i64 t0 = now_us(); i64 c = primes(1);
    std::printf("primes1 %lld %lld\n", (long long)c, (long long)(now_us() - t0));
    t0 = now_us(); c = primes(t);
    std::printf("primesN %lld %lld\n", (long long)c, (long long)(now_us() - t0));

    std::atomic<i64> counter{0};
    t0 = now_us();
    run(t, [&] { for (int i = 0; i < 1000000; i++) counter.fetch_add(1); });
    std::printf("atomic %lld %lld\n", (long long)counter.load(), (long long)(now_us() - t0));

    std::mutex m; i64 count = 0;
    t0 = now_us();
    run(t, [&] { for (int i = 0; i < 200000; i++) { std::lock_guard<std::mutex> g(m); count++; } });
    std::printf("mutex %lld %lld\n", (long long)count, (long long)(now_us() - t0));

    counter = 0;
    t0 = now_us();
    for (int r = 0; r < 500; r++) run(t, [&] { counter.fetch_add(1); });
    std::printf("spawn %lld %lld\n", (long long)counter.load(), (long long)(now_us() - t0));
}
