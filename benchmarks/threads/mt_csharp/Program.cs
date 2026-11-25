// Multithreaded benchmark - same workloads as mt.fg, standard C#:
// System.Threading.Thread (real OS threads), Interlocked, lock (Monitor), Stopwatch.
using System;
using System.Diagnostics;
using System.Threading;

int t = Environment.ProcessorCount;
Console.WriteLine($"threads {t} 0");
var sw = Stopwatch.StartNew();
long c = Bench.Primes(1);
Console.WriteLine($"primes1 {c} {Bench.Us(sw)}");
sw.Restart(); c = Bench.Primes(t);
Console.WriteLine($"primesN {c} {Bench.Us(sw)}");

long counter = 0;
sw.Restart();
Bench.Run(t, () => { for (int i = 0; i < 1000000; i++) Interlocked.Increment(ref counter); });
Console.WriteLine($"atomic {counter} {Bench.Us(sw)}");

var gate = new object(); long count = 0;
sw.Restart();
Bench.Run(t, () => { for (int i = 0; i < 200000; i++) { lock (gate) { count++; } } });
Console.WriteLine($"mutex {count} {Bench.Us(sw)}");

counter = 0;
sw.Restart();
for (int r = 0; r < 500; r++) Bench.Run(t, () => Interlocked.Increment(ref counter));
Console.WriteLine($"spawn {counter} {Bench.Us(sw)}");

static class Bench {
    public static long Us(Stopwatch sw) => sw.Elapsed.Ticks / 10;
    public static void Run(int t, Action f) {
        var ts = new Thread[t];
        for (int i = 0; i < t; i++) { ts[i] = new Thread(() => f()); ts[i].Start(); }
        foreach (var th in ts) th.Join();
    }
    static bool IsPrime(long n) {
        if (n < 2) return false;
        if (n % 2 == 0) return n == 2;
        for (long d = 3; d * d <= n; d += 2) if (n % d == 0) return false;
        return true;
    }
    public static long Primes(int t) {
        long next = 0, total = 0; const long limit = 10000000;
        Run(t, () => {
            long local = 0;
            while (true) {
                long start = Interlocked.Add(ref next, 10000) - 10000;
                if (start >= limit) break;
                long end = Math.Min(start + 10000, limit);
                for (long n = start; n < end; n++) if (IsPrime(n)) local++;
            }
            Interlocked.Add(ref total, local);
        });
        return total;
    }
}
