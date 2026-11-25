// Multithreaded benchmark - same workloads as mt.fg, standard Rust:
// std::thread::scope, AtomicI64, Mutex, Instant.
use std::sync::atomic::{AtomicI64, Ordering::SeqCst};
use std::sync::Mutex;
use std::thread;
use std::time::Instant;

fn is_prime(n: i64) -> bool {
    if n < 2 { return false; }
    if n % 2 == 0 { return n == 2; }
    let mut d = 3;
    while d * d <= n { if n % d == 0 { return false; } d += 2; }
    true
}

fn run<F: Fn() + Sync>(t: usize, f: F) {
    thread::scope(|s| { for _ in 0..t { s.spawn(&f); } });
}

fn primes(t: usize) -> i64 {
    let next = AtomicI64::new(0);
    let total = AtomicI64::new(0);
    let limit = 10_000_000i64;
    run(t, || {
        let mut local = 0;
        loop {
            let start = next.fetch_add(10000, SeqCst);
            if start >= limit { break; }
            let end = (start + 10000).min(limit);
            for n in start..end { if is_prime(n) { local += 1; } }
        }
        total.fetch_add(local, SeqCst);
    });
    total.load(SeqCst)
}

fn main() {
    let t = thread::available_parallelism().map(|n| n.get()).unwrap_or(1);
    println!("threads {} 0", t);
    let t0 = Instant::now(); let c = primes(1);
    println!("primes1 {} {}", c, t0.elapsed().as_micros());
    let t0 = Instant::now(); let c = primes(t);
    println!("primesN {} {}", c, t0.elapsed().as_micros());

    let counter = AtomicI64::new(0);
    let t0 = Instant::now();
    run(t, || { for _ in 0..1_000_000 { counter.fetch_add(1, SeqCst); } });
    println!("atomic {} {}", counter.load(SeqCst), t0.elapsed().as_micros());

    let m = Mutex::new(0i64);
    let t0 = Instant::now();
    run(t, || { for _ in 0..200_000 { *m.lock().unwrap() += 1; } });
    println!("mutex {} {}", *m.lock().unwrap(), t0.elapsed().as_micros());

    counter.store(0, SeqCst);
    let t0 = Instant::now();
    for _ in 0..500 { run(t, || { counter.fetch_add(1, SeqCst); }); }
    println!("spawn {} {}", counter.load(SeqCst), t0.elapsed().as_micros());
}
