# ForgeLang: progress notes

Where the project stands and how to pick it up again. Written 2026-11-20.

Forge is a C#-like language compiled by `forgec`, a compiler written in C# (.NET 10).
The compiler writes x86-64 machine code straight into a Windows `.exe` (PE32+), a Linux
ELF, or a bootable Multiboot kernel image. There is no assembler, linker, LLVM or libc
involved. The goal: a language that feels like C# but can also be used to write
bare-metal software (OSes, firmware).

---

## 1. Source layout

| File | What it does |
|------|--------------|
| `Frontend.cs` | Lexer, AST, parser (structs, methods, extern, interrupt, asm), and `MethodScope`, which rewrites bare field names inside methods to `this.field` |
| `CodeGen.cs` | Type checker, struct lowering, code generator, register allocator, runtime routines, PE/ELF/kernel layout |
| `Optimizer.cs` | Tail-recursion elimination, inlining (2 rounds), partial inlining, constant folding, loop-invariant `.length` hoisting; `Walker`, `AstCopier` |
| `Threads.cs` | spawn/join, per-thread arenas, region cache, atomics, SRWLOCK/futex lock, `heapmark` support (`arena_reset_to`) |
| `InlineAsm.cs` | The `asm("...")` mini assembler (Intel syntax) |
| `AsyncRuntime.cs` | async/await scheduler (written in Forge, injected) |
| `Asm.cs` | x86-64 encoder: labels, fixups, `MemSib` (full `[base+index*scale+disp]` form) |
| `Pe.cs` | PE writer: kernel32 imports, the program's extern DLLs, ws2_32 last (only if sockets are used) |
| `Driver.cs` | CLI (`build`/`run`/`check`/`complete`, `--target`, `-O0`, `--interp`) and the ELF64 writer (static, or dynamic when externs are used) |
| `Elf32.cs` | Multiboot ELF32 for the kernel target |
| `README.md` | User documentation (language, structs, memory, extern, bare metal, optimizer, threads) |
| `benchmarks/README.md` | Benchmark instructions and reference results |

Build: `dotnet build -c Release` in the repo root, which produces `bin\Release\net10.0\forgec.dll`.
Use: `dotnet bin\Release\net10.0\forgec.dll build file.fg -o out.exe --target win|linux|kernel [-O0]`

---

## 2. Work done, in order

### Earlier sessions
- async/await (coroutines, `Task.WhenAll`, `Task.Yield`), running in VS Code.
- Web requests / TCP sockets (`tcp_connect`, `tcp_listen`, `accept`, `send`, `recv`, `closefd`).
- Fib benchmark against C, C++, Rust, Go and C#. Added the optimizer, and Forge now wins fib.
- JSON and HTTP throughput benchmarks (`benchmarks/json`, `benchmarks/http-server`).
- **OS threads:**
  - `spawn F(arg)` / `join(h)`, atomics, `lock`/`unlock`, `cpu_count`, `sleep_ms`, `now_ns`.
  - Per-thread heap arenas found through TLS.
  - Multithreaded HTTP server.

### Session: multithreaded benchmarks and gaps
- `benchmarks/threads/` in all 6 languages: primes 1 thread / 16 threads, atomic counter, mutex, spawn+join.
- The lock went from a spin lock to SRWLOCK (Windows) and a futex mutex (Linux): 496 ms → 77–85 ms.
- `new T[n]` zeroes its elements.
- **Atomics:** builtin arguments that are literals or variables go straight into registers (`EmitArgsTo`). 210 → 170 ms, level with C.
- **Thread start-up:**
  - The real cost was Winsock: every DLL a process loads is notified on each thread start. `ws2_32` is now imported only by programs that use sockets (`_usesNet`).
  - Joined thread regions are zeroed and cached for reuse (up to 64).
  - Result: 374 → 257 ms, fastest of the OS-thread languages.
- Multithreaded HTTP servers in C, C++, Rust, Go, C# and Forge (ports 8090–8095), with `run_mt.ps1` (Windows) and `run_mt.sh` (Linux).
- Fix: a program's own function may now reuse a builtin's name (`uart.fg` defines `send`).

### Session: array/memory performance (JSON 0.896 s → 0.416 s)
- Indexing (`a[i]`, `p[i]`, `s[i]`, and stores) compiles to one instruction with an x86 address; `a[i+k]` folds `k` into the displacement (`EmitElemAddr`, `EmitElemStore`).
- Functions that use arrays or pointers may run without a stack frame.
- **Linear-scan register allocator** over statement-level live ranges, extended around loops and backward gotos:
  - Variables whose ranges don't overlap share a register.
  - Variables whose range contains no call can also use rsi, rdi and r8–r11 (11 registers in all).
  - Weights count loop depth, including loops inside inlined code.
  - Incoming parameters use a parallel move.
- Division and remainder by a constant use magic multiply/shift (Hacker's Delight); checked against `idiv` on about 860k values.
- A binary expression whose right side only touches rax/rcx keeps its left value in rdx instead of push/pop.

### Session: production features
1. **Structs.**
   - Fields, methods with implicit `this`, `new T { f = v }`, `null`, `T x;` defaults, `sizeof(T)`.
   - C layout; `packed struct` removes padding.
   - Casting an address to a struct overlays it on memory; arrays of structs work.
   - Lowered in `Analyze` to `*(T*)((u8*)obj + off)`, which emits one load or store.
2. **Memory.** `heapmark()` / `heapreset(mark)`; `readfile` reads whole files of any size (GetFileSizeEx / fstat).
3. **extern / FFI.**
   - `extern "lib" Ret F(...)` and `[DllImport("lib")] static extern ...`.
   - Win64 and System V calling conventions, up to 12 arguments; narrow return values are extended.
   - Callbacks: `extern Ret F(...) { body }`, with `&F` as its address (Win64 thunk `F$c`).
   - Linux programs with externs are dynamically linked (PT_INTERP, `.dynamic`, dynsym, hash, GLOB_DAT relocations, GOT); `--interp` picks the loader.
   - Forge's Linux thread pointer moved from `fs` to `gs`, leaving `fs` to libc.
4. **Bare metal.**
   - Builtins: `inb`/`outb`/`inw`/`outw`/`inl`/`outl`, `cli`/`sti`/`hlt`/`pause`, `read_cr0/2/3/4`, `write_cr0/3/4`, `invlpg`, `rdmsr`/`wrmsr`, `lidt`/`lgdt`, `rdtsc`, `getc`.
   - `interrupt void H()` / `interrupt void H(long error)` with generated save/restore/`iretq` stubs (`H$isr`).
   - `asm("...")` inline assembly with `{var}` operands, local labels and system instructions.
5. **Loops.** Rotated loops (condition tested at the bottom); loop-invariant `.length` hoisting (`HoistLengths`).
6. **Smaller changes.** `$"..."` works as a string value everywhere; arrays and strings can be cast to pointers; `str()` accepts bool and pointers; asm variables count as used in the unused-variable lint.

### Session: regression tests and compiler hardening (2026-11-25)
- `tests/run.sh` + `tests/manifest.txt` + CI on Windows and Ubuntu (see section 5).
- **Errors.** `Analyze` collects errors per statement (`Recover`/`Report`, max 20) and drops follow-on errors that mention a local whose declaration failed (`_poisoned`). Errors come out sorted by line via `ForgeErrors`. Parse errors still stop at the first one.
- **Driver.** The compiler runs on a 256 MB-stack thread. Unexpected exceptions become `internal compiler error` (exit 3, `FORGE_DEBUG=1` prints the trace). Unknown commands, options or targets and missing values give exit 2. Output write failures are reported.
- **Fuzzer** (`tests/fuzz`) found and fixed:
  - integer/hex literals too big for 64 bits crashed the lexer;
  - `TypeOf` was uncached, so long expressions compiled in quadratic time (20,000-term sum: >40 s, now <0.1 s at -O0); it is now cached per node, and all 164 example binaries came out byte-identical;
  - builtin arguments the builtin didn't check itself (e.g. `substr`'s start/len) reached codegen unchecked; all builtin arguments are now type-checked (`CheckedBuiltins`);
  - interpolation holes weren't type-checked by `check`, reported line 1, and silently ignored extra tokens (`{a b}`).
- C# warnings are now errors (`TreatWarningsAsErrors`).

### Session: everyday syntax (2026-11-25)
- Compound assignment, `++`/`--`, `break`/`continue` and `switch`, all lowered in the parser (`Frontend.cs`) so codegen, the optimizer and the register allocator are unchanged:
  - `x op= e` becomes `x = x op e`. The target is copied by `ClonePure`, which rejects calls (the target is evaluated twice).
  - `x++` is an `IncDecExpr`, turned into an assignment when it is a whole statement or a for step; elsewhere `TypeOf` reports it.
  - `break`/`continue` become `GotoStmt` to `brk$N`/`cont$N` labels (global asm labels; `$` keeps them apart from user names). The continue label ends the body, so a for step still runs. A trailing `continue`/`break` is dropped.
  - `switch` becomes `VarDecl sw$N = subject` (skipped for a plain variable) plus an if/else-if chain; default is the final else. Constant labels only, duplicates and fall-through are errors (C# rules).
  - Functions that use break/continue contain labels, so `InlineSafe` does not inline them.
- All 164 existing example binaries are byte-identical after the change. New: `examples/control_flow.fg`, `tests/errors/cf_*.fg`.

---

## 3. Examples added in these sessions

`structs.fg`, `memory.fg`, `ffi.fg` (Windows), `ffi_linux.fg`, `window.fg` (a real Win32 window), `inline_asm.fg`, `kernel_os.fg` (IDT, exceptions, PIT, PS/2 keyboard, paging demo, serial shell), `threads_test.fg`.

---

## 4. Benchmark results (Windows 11, Ryzen 7 7730U, 16 threads)

| Benchmark | Forge | Others |
|-----------|-------|--------|
| fib(40) | **0.18 s (1st)** | Rust 0.25, C++ 0.37, C# 0.41, C 0.42, Go 0.44 |
| JSON | 0.416 s (4th) | Rust 0.207, C++ 0.257, C 0.300, Go 0.464, C# 0.636 |
| Primes, 16 threads | 200 ms | Rust 180, C 191, C++ 193, C# 197, Go 201 |
| Atomic counter | 170 ms (2nd) | Go 163, C 171 |
| Mutex | 77 ms (2nd) | C 56, Rust 97, C++ 102, Go 128, C# 195 |
| Spawn + join 8,000 threads | **257 ms (1st of OS threads)** | C 264, C++ 310, Rust 319, C# 614; Go 3 ms with goroutines (not comparable) |
| HTTP multithreaded (Linux/WSL) | **107,919 req/s** | Go 74,442; C, C++, Rust, C# not measured on Linux (no toolchains in WSL) |
| HTTP on Windows | ~3,000 req/s | All languages the same: the OS connection setup is the limit |

---

## 5. How to verify after changes

Workflow: every change goes on a feature branch; run the tests before merging into `main` and pushing.

1. **Regression suite: `tests/run.sh`** (Git Bash or Linux bash). It builds forgec, then works through `tests/manifest.txt`:
   - `run` examples are built at -O2 and -O0, run in a temp folder, and stdout + exit code must match `tests/expected/<name>.out` (fib `102334155` is in `bench_fib`).
   - `build` examples (network, GUI, kernel) must compile for every listed target.
   - `check` examples must fail `forgec check` with the output in `tests/expected/<name>.check`.
   - `FORGE_WSL=docker-desktop tests/run.sh` also runs the Linux binaries inside WSL (musl). Use `MSYS_NO_PATHCONV=1` for manual WSL commands from Git Bash.
   - `tests/run.sh --update [names]` rewrites baselines. Review the diff before committing it.
   - CI (`.github/workflows/tests.yml`) runs the suite natively on Windows and Ubuntu on every push and PR.
2. **JSON benchmark output** must stay `161418380 / 118578658335`.
3. **Benchmarks.**
   - `benchmarks\json\run.ps1`, `benchmarks\cpu-fibonacci\run.ps1`, `benchmarks\threads\run.ps1`.
   - `benchmarks\http-server\run_mt.ps1` (Windows) and `run_mt.sh` (Linux).
4. **Encodings.** To check machine code: `dumpbin /disasm file.exe` (MSVC; needs vcvars64).

---

## 6. Not verified / known limits

- **The kernel features have never been booted.** No QEMU on this machine. Encodings were checked with dumpbin, but `kernel_os.fg` is untested at run time. Next step: `winget install qemu`, then `qemu-system-x86_64 -kernel os.elf -serial stdio`.
- **glibc dynamic linking is untested** (only musl was available). The default `--interp` is `/lib64/ld-linux-x86-64.so.2`.
- A string literal inside `{...}` of an interpolated string doesn't parse (the lexer ends the string early).
- Syntax errors stop the compile at the first one (type errors are all reported). Errors have a line but no column.
- `writefile`/`readfile` fail silently (return 0 / ""), with no error a program can test for.
- Interrupt handlers must not allocate while interrupting code that also allocates (the allocator isn't reentrant).
- C callbacks must run on the main thread or a Forge thread (they need Forge's thread pointer).
- On Linux, call `fflush(null)` before exiting if you used `printf` (Forge exits with `exit_group`).
- No float arithmetic, classes/inheritance, generics or destructors. Struct-by-value fields and inline fixed arrays inside structs aren't supported.
- `async`/`await` runs on the main thread only.

---

## 7. Suggested next steps

1. Install QEMU and boot-test `kernel_os.fg` (interrupts, keyboard, paging, shell). Fix what breaks.
2. Commit the regression tests (see section 5).
3. Language: `double` arithmetic, generics. (`+=`/`++`, `break`/`continue` and `switch` are done.)
4. Allow string literals inside interpolation holes.
5. Bare metal: a GDT/TSS helper example, APIC timer, framebuffer output, a bootable ISO (GRUB) for real hardware.
6. Performance: more loop optimizations (moving invariant arithmetic out of loops, strength reduction), fewer redundant moves, unrolling. JSON is still 1.4x behind C.
7. Measure C, C++, Rust and C# multithreaded HTTP on Linux (needs Docker Desktop running, or toolchains in WSL).
8. A glibc test of `extern` on Linux.
