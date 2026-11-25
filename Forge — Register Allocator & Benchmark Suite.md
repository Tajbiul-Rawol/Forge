# Forge — Register Allocator & Benchmark Suite

Nov 19, 2026 · @Rawol

## Scope

Two deliverables: a design for a **linear-scan register allocator** in Forge's code generator, and a **broader benchmark suite** so you measure more than call overhead. The allocator is the \~2x win on `fib`; the suite is how you tell whether future changes help across real workloads.

Where this fits: today Forge keeps every value on the stack and emits x86-64 directly with no allocation. The fastest path to real speed isn't a clever optimizer — it's keeping hot values in registers and off the memory bus. A simple linear-scan allocator does exactly that, and on a tight recursive benchmark the load/store traffic it removes is most of the cost.

This is a design sketch, not a line-by-line patch — adapt the shapes below to Forge's actual IR and emitter. If you want, connect the `D:\Code\ForgeLang` folder and I'll tailor the sketch to your real codegen instead of the generic form.

## Step 0: give yourself an IR to allocate over

You can't allocate registers over an AST walker that pushes/pops as it goes — there's nothing stable to assign a register *to*. The prerequisite is a flat **list of three-address instructions over virtual temps** (unlimited pseudo-registers), one per function. If Forge already has something like this, skip ahead. If codegen emits asm directly from the AST, insert this layer between the AST and the emitter:

```
; fib(n): the three-address form
t0 = param 0            ; n
t1 = t0 < 2
branch t1 -> L_base
t2 = t0 - 1
t3 = call fib(t2)
t4 = t0 - 2
t5 = call fib(t4)
t6 = t3 + t5
return t6
L_base:
return t0
```

Each instruction has explicit operands (temps or constants) and at most one destination temp. The emitter's job changes from "compute onto the stack" to "emit one instruction per IR op, reading/writing whatever register (or spill slot) the allocator assigned to each temp." This refactor is most of the work; the allocator itself is small.

You also need the instruction stream **linearized** (basic blocks laid out in order, branches as labels) so intervals are expressed as index ranges. For `fib` that's trivial; for loops, see the liveness note in the next section.

## The linear-scan algorithm

Linear scan (Poletto & Sarkar, 1999) is the right first allocator: near-linear, simple, and good enough to get most of the register benefit without the complexity of graph coloring.

**1. Compute live intervals.** For each virtual temp, find `[start, end]` = the index of its first definition and its last use in the linearized stream. One pass, tracking first-seen and last-seen index per temp. Straight-line code (like `fib`) needs nothing more. For loops, extend any interval that's live across the loop to reach the loop's end (a temp used inside a loop must stay live for the whole loop body) — a conservative backward pass over blocks is enough; you don't need full dataflow for a first version.

**2. Scan.** Walk intervals sorted by `start`, keeping an `active` set sorted by `end`:

```
sort intervals by start
active = []            ; currently live, holding a register
free  = [r1, r2, ...]  ; the allocatable pool (see next section)

for iv in intervals:
    expire(iv.start)                 ; free regs whose interval.end < iv.start
    if free is empty:
        spill(iv)                    ; pick victim = active interval with the
    else:                            ;   farthest end; whichever of it/iv ends
        iv.reg = free.pop()          ;   later goes to a spill slot
        active.add(iv)               ; keep active sorted by end

expire(point):
    for iv in active where iv.end < point:
        free.push(iv.reg); active.remove(iv)
```

**3. Spill choice.** When registers run out, spill the interval whose `end` is farthest in the future (it's the one you'll need a register for least soon). If the incoming interval ends even later than every active one, spill the incoming interval instead. Spilled temps live in a stack slot and are loaded/stored around each use (next section).

That's the whole core. No coalescing, no live-range splitting, no SSA — add those later only if profiling says so.

## Win64 registers and the recursion rule (the crux)

This is the part that decides whether `fib` actually speeds up. Get it wrong and the allocator just shuffles spills around with no gain.

**The register pool (Windows x64 ABI):**

- **Volatile / caller-saved** — `rax, rcx, rdx, r8, r9, r10, r11`. A `call` may clobber these. Free to use, no save/restore needed, but their contents die across any call.
- **Non-volatile / callee-saved** — `rbx, rsi, rdi, r12, r13, r14, r15` (and `rbp` if you're not using it as a frame pointer). Survive calls, but you must push them in the prologue and pop in the epilogue if you use them.
- Reserved: `rsp` always; `rbp` if it's your frame pointer.

**The rule:** a temp whose live interval **crosses a `call`** must be assigned a **callee-saved** register (or be spilled). On `fib(n) = fib(n-1) + fib(n-2)`, the result of `fib(n-1)` (`t3`) is live across the `call fib(n-2)`. If `t3` lands in a volatile register, the second call clobbers it and the allocator is forced to spill it to memory anyway — so you pay the load/store you were trying to remove, and the number barely moves. Put `t3` in `rbx`/`r12`/etc. and it survives the call in a register. That single decision is most of the 2x.

**How to encode it in linear scan:** mark each interval with `crossesCall = true` if any `call` index falls within `[start, end]`. Then split the free pool: hand `crossesCall` intervals registers from the callee-saved set first, and non-crossing intervals from the volatile set first. Only dip into the other set when one runs dry. Also note that `rcx/rdx/r8/r9` are tied up as argument registers at each call site — either exclude them from the general pool initially (simplest) or model them as clobbered at call points.

**Other ABI musts (or it crashes/corrupts silently):**

- Pass the first four integer args in `rcx, rdx, r8, r9`; return value in `rax`.
- Reserve **32 bytes of shadow space** on the stack before every `call`.
- Keep `rsp` **16-byte aligned** at the point of each `call`.
- In the prologue, `push` exactly the callee-saved registers you actually allocated; `pop` them in reverse in the epilogue. Don't save ones you didn't use — that's pure overhead.

## Spilling, build order, and verifying it worked

**Spilling mechanics.** A spilled temp gets a slot in the function's stack frame. Rewrite its uses: load into a scratch register right before a read, store right after a write. Reserve one or two scratch registers (e.g. `r10`, `r11` — volatile, so no save needed) that the allocator never hands out, purely for materializing spilled operands. This keeps spill handling local and simple: the allocator decides *who* spills; the emitter inserts the loads/stores mechanically.

**Build order — ship in this sequence, testing after each:**

1. Introduce the three-address IR and re-point the emitter at it, with **every temp still forced to a stack slot** (no allocation yet). This must produce byte-identical behavior and pass the whole suite before you go further — it isolates the risky refactor from the allocator.
2. Add live-interval computation; dump intervals and eyeball them on `fib`.
3. Add the scan with the **volatile pool only** and the call-crossing rule forcing spills (no callee-saved yet). Correct, modest speedup.
4. Add callee-saved registers + prologue/epilogue save-restore. This is where `fib` drops.
5. Only then consider coalescing moves or better spill heuristics.

**Verify it actually worked (not just "ran"):**

- **Correctness first.** It still prints `102334155`, and the *entire* test suite passes. Register allocators are a classic source of silent miscompiles — a value in the wrong register produces wrong answers, not crashes.
- **ABI sanity.** No corruption across calls; stack stays 16-byte aligned; shadow space present. A mis-saved callee register shows up as random wrong results in *other* programs, so run the full suite, not just `fib`.
- **The speedup is real.** Re-run best-of-7. Target: `fib(40)` from \~0.82s toward \~0.4s. If it barely moves, dump the generated asm for `fib` and check the `fib(n-1)` result actually sits in a callee-saved register across the second call — that's the usual culprit.
- **Diff the asm.** Before/after on `fib` should show stack loads/stores replaced by register ops in the hot path. If you still see `mov [rsp+...]` round-trips around the add, the allocator isn't binding the hot temps.

## Broader benchmark suite

`fib(40)` only measures function-call overhead plus integer add. To know where Forge actually stands — and to catch regressions from codegen changes — test a spread of workloads. Each program must compute a **checksum/known result** so you verify correctness per language, use the **same algorithm** everywhere (no library shortcuts in one language), and be sized to run \~0.3–2s so timing is stable.

Start with the ones Forge can already express; add float/string/heap rows as the language gains those features.

| Category | What it stresses | Concrete benchmark | Verifiable output |
| --- | --- | --- | --- |
| Call / recursion | Call overhead, prologue/epilogue, callee-saved use | `fib(40)` (keep); `ackermann(3,10)` | fixed integer |
| Tight integer loop | Loop codegen, induction vars, comparisons | Sum 1..1e9 (wrapping); Collatz max-steps under 1e6 | fixed integer |
| Sieve / arrays | Array indexing, bounds, memory locality | Count primes < 1e7 (sieve of Eratosthenes) | prime count |
| Sort | Swaps, branches, random access | In-place quicksort of 1e6 ints (fixed seed PRNG) | checksum of sorted array |
| Matrix | Nested loops, multiply-add, cache behavior | Multiply two 512×512 int matrices | trace or element checksum |
| Float (when ready) | FP ops, transcendentals | Mandelbrot escape counts, 1000×1000 | total iteration count |
| Strings (when ready) | Byte loops, hashing, compares | Word count + FNV-1a hash of a generated text | count + hash |
| Heap / alloc (when ready) | Allocation path, pointer chasing, GC if any | binary-trees (CLBG); linked-list build/sum | node checksum |
| Branch-heavy | Branch prediction, dispatch | Tiny bytecode VM running a fixed program N times | VM result |

Use a small **fixed-seed PRNG implemented identically in every language** (e.g. a 64-bit xorshift) to generate inputs, so every language sorts/hashes the exact same data and the checksums line up. That also removes any dependency on reading input files.

## Methodology and harness

The current run is already solid (best-of-7, correctness print). Four changes make the suite fair and comparable across languages:

1. **Time the computation, not the process.** C#'s 0.395s is \~0.14s of runtime startup — that distortion grows as you add short benchmarks. Where a language lets you, print an elapsed time measured *inside* the program around the hot section (monotonic clock: `QueryPerformanceCounter` / `Stopwatch` / `std::chrono::steady_clock` / Go `time.Now`). Report that inner time. Keep whole-process wall time as a secondary column so Forge's lack of a runtime still shows as an advantage, but don't let startup dominate the headline.
2. **Warm up, then best-of-N.** Discard the first run (cold caches, JIT warm-up for C#), then take best-of-7 and also report the median — best-of-N hides variance, median shows it. A big gap between best and median means a noisy machine; close the browser/agents when measuring.
3. **Fair compiler flags, stated explicitly.** You're on MSVC for C/C++; add `clang -O2` and/or `gcc -O2` if available — they often beat MSVC on numeric loops, and right now C/C++ look artificially slow. Pin every flag in the results (`rustc -C opt-level=3 -C target-cpu=native`, `/O2`, `go build` default, `dotnet -c Release`). `target-cpu=native` is worth adding across the board for a fair "best each compiler can do" run.
4. **Report Forge's real strengths as columns.** Binary size and **compile time** belong in the table — a 4 KB binary and (presumably) a fast, dependency-free compile are genuine selling points that a pure runtime-speed table hides.

**Harness.** Generalize `run.ps1` into a matrix: for each `benchmark × language`, build (timed), run (warm-up + N timed runs), capture stdout, check the checksum against the expected value, record best/median/binary-size/compile-time. Emit both the pretty table and a **CSV** so you can track numbers across commits — that's how you'll see whether the register allocator (or the next change) actually helped, benchmark by benchmark. Keep the per-language build/run commands in a small config block at the top so adding a language or a benchmark is a one-line edit.
