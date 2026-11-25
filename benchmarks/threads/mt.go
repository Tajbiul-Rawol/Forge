// Multithreaded benchmark - same workloads as mt.fg, idiomatic Go:
// goroutines + WaitGroup, sync/atomic, sync.Mutex. Note: goroutines are
// scheduled by the Go runtime onto OS threads, so "spawn" measures goroutine
// start-up, not OS thread creation.
package main

import (
	"fmt"
	"runtime"
	"sync"
	"sync/atomic"
	"time"
)

func isPrime(n int64) bool {
	if n < 2 {
		return false
	}
	if n%2 == 0 {
		return n == 2
	}
	for d := int64(3); d*d <= n; d += 2 {
		if n%d == 0 {
			return false
		}
	}
	return true
}

func run(t int, f func()) {
	var wg sync.WaitGroup
	wg.Add(t)
	for i := 0; i < t; i++ {
		go func() { defer wg.Done(); f() }()
	}
	wg.Wait()
}

func primes(t int) int64 {
	var next, total int64
	const limit = 10000000
	run(t, func() {
		var local int64
		for {
			start := atomic.AddInt64(&next, 10000) - 10000
			if start >= limit {
				break
			}
			end := start + 10000
			if end > limit {
				end = limit
			}
			for n := start; n < end; n++ {
				if isPrime(n) {
					local++
				}
			}
		}
		atomic.AddInt64(&total, local)
	})
	return total
}

func us(t0 time.Time) int64 { return time.Since(t0).Microseconds() }

func main() {
	t := runtime.NumCPU()
	fmt.Printf("threads %d 0\n", t)
	t0 := time.Now()
	c := primes(1)
	fmt.Printf("primes1 %d %d\n", c, us(t0))
	t0 = time.Now()
	c = primes(t)
	fmt.Printf("primesN %d %d\n", c, us(t0))

	var counter int64
	t0 = time.Now()
	run(t, func() {
		for i := 0; i < 1000000; i++ {
			atomic.AddInt64(&counter, 1)
		}
	})
	fmt.Printf("atomic %d %d\n", counter, us(t0))

	var m sync.Mutex
	var count int64
	t0 = time.Now()
	run(t, func() {
		for i := 0; i < 200000; i++ {
			m.Lock()
			count++
			m.Unlock()
		}
	})
	fmt.Printf("mutex %d %d\n", count, us(t0))

	counter = 0
	t0 = time.Now()
	for r := 0; r < 500; r++ {
		run(t, func() { atomic.AddInt64(&counter, 1) })
	}
	fmt.Printf("spawn %d %d\n", counter, us(t0))
}
