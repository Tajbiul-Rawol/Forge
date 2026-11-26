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
| `Imports.cs` | Multi-file programs: `Sources.Load` follows `import "x.fg";`, `Sources` position table (file, line, col per token); `Sources.Loc` prints file:line:col |
| `DebugInfo.cs` | `-g`: the debug model (line rows, functions, frames, variables, types) and the DWARF writer |
| `Pdb.cs` | `-g` on Windows: unwind info + debug directory in the .exe, and the PDB (MSF, TPI, DBI, module, GSI/PSI) |
| `BoundsCheck.cs` | Array bounds checks: the inline compare, cold stubs, `bounds_fail` |
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

### Session: indexers (2026-11-26)
- `x[i]` on a struct is `x.Get(i)` and `x[i] = v` is `x.Set(i, v)` (List<T>, Dictionary<K,V>, any struct with those methods). `TypeOf(IndexExpr)` on a struct builds the `.Get` call once (`_indexCalls`, by node) and types it, so the index is checked as a call argument and enclosing method calls (`rows[0].Add(4)`, typed early by `TypeOfCallName`) see the right type. `LowerExpr` replaces the node with that same call, resolved at once (a call typed through it is never typed again); `LowerStmt` turns `IndexAssign` into `ExprStmt(.Set)`. `IndexerType` gives "X cannot be indexed: it has no Get method" / "... no Set method".
- Compound forms come free from the parser (`list[i] += 1` is `Set(i, Get(i) + 1)`); `ClonePure` now accepts string and double literals (`dict["k"] += 1`).
- All 82 other example binaries are byte-identical to main. New: `examples/indexers.fg`, `tests/errors/indexer_misuse.fg`.

### Session: debug info, -g (2026-11-26)
- Model (`DebugInfo.cs`): `DbgRow` at each `EmitStmt` and before loop tests (deduped per source line), `DbgFuncStart/BodyStart/End` in `EmitFunc` (range, frame size, saved registers, `_locals` with slots; register-allocated or frameless variables marked InReg = optimized out; `$` temps and runtime functions left out). Runtime/stdlib sources are lexed as `Sources.NoFile` (-1), so inlined runtime code adds no rows.
- Linux: `DwarfSections` writes DWARF 4: abbrev, info (CU, base/pointer/struct types with members, subprograms with `DW_OP_fbreg` locals, frame base rbp), line program (file/line/column, a line-0 row after each function), `.debug_frame` (CIE + an FDE per function: push rbp / mov rbp,rsp / saved-register slots; frameless pushes). `Elf.Build` appends them with a section header table and `.symtab` (only with -g).
- Windows (`Pdb.cs`): after the code in .text, UNWIND_INFO (PUSH_NONVOL rbp, SET_FPREG, ALLOC) + RUNTIME_FUNCTION per function (data dir 3) and a debug directory + RSDS (data dir 6); the layout is re-planned for the longer .text. The PDB: MSF 7.0 with info (GUID = SHA-256 of the code, "/names"), TPI (fwd-ref structs, LF_POINTER, field lists, LF_PROCEDURE; hash stream with hashStringV1 / CRC), DBI (one module, section contributions, section map, file info, section-header stream), the module stream (S_OBJNAME, S_COMPILE3, S_GPROC32, S_FRAMEPROC, S_REGREL32 [rbp+off] or S_LOCAL optimized-out, S_END; C13 FILECHKSMS + LINES), GSI/PSI hashes with S_PROCREF / S_PUB32 records.
- Tests: mode `debug` in `tests/run.sh` (`examples/debugme.fg`, `imports`): gdb scripts `tests/debug/*.gdb` (natively, or `FORGE_GDB_DOCKER=forge-gdb` from `tests/debug/Dockerfile`), and on Windows `tests/debug/dbgtest` (Win32 debug API + dbghelp: breakpoints by line/name, StackWalk64, locals via SymEnumSymbols + SymGetTypeInfo, next, finish) running `tests/debug/*.win`. CI installs gdb on Ubuntu.
- Without -g all 82 example binaries are byte-identical to main (stdlib generic instances' bounds checks share `bounds_fail`).
- Not done: -g for the kernel target; block scopes; at -O2, register variables are optimized out and frameless functions' CFI ignores temporary pushes.

### Session: better errors (2026-11-26)
- Positions: a node's `Line` is now a position id, an index into `Sources`' table of (file, line, column), one entry per token (`Lexer.P()`, `_tokStart`/`_lineStart`). This replaced the `file << 20` line encoding from the imports session. Every node and every inlined or generic copy carries its column with no parser changes. Interpolation holes are lexed with the string's file/line and its column + offset. Errors print `file:line:col`; errors are sorted with `Sources.Order` (ids aren't in source order: hole tokens are lexed later). `Sources.Reset()` per compile (the fuzzer calls it; access is locked because a timed-out fuzz compile may still run).
- Syntax error recovery: `ParseBlock` and the top-level loop (`ParseDecl`) catch an error, `Record` it, and `Sync(start)`: brackets are counted from the statement's first token, so it skips past the statement's `;` or its `{...}` body, stops at the enclosing block's `}`, and a `{` drops unclosed parentheses (`int F(int a {`). `AtBoundary` skips nothing when the error was found after a whole statement was read (duplicate case, interrupt signature), only if ( [ { are all closed again. An error at end of file stops parsing (`StopParsing`). Max 20; syntax errors from all files are merged. Any syntax error: no type check.
- A missing `;` at the end of a line is reported there: `missing ';' after '4'`.
- All `.check` baselines gained columns and nothing else, except `no_main` (now points at the `module` line, 2:1). 80 example binaries are byte-identical to main.
- New tests: `tests/errors/syntax_many.fg`, `tests/errors/import_syntax_both.fg`.

### Session: multi-file programs (2026-11-26)
- `Imports.cs`: `import "path.fg";` lines after `module x;` (paths relative to the importing file, transitive, each file once by full path, cycles fine). All files merge into one ModuleNode: one global namespace.
- Locations: file f is lexed with lines starting at `(f << 20) + 1` (`Sources.FileShift`), so every node and every inlined/generic copy carries its file in `Line`. The main file is file 0: single-file programs compile byte-identically (78 example binaries compared with main). `Sources.Loc(line)` prints `file:line` in the driver; `bounds_fail$f` per file prints `(geometry.fg line 20)`.
- Parsing: a scan `Parser` over every file's tokens collects struct and generic names (`StructNames`, `outer`), then each file gets its own parser, so a missing `}` is reported in its own file.
- Duplicates across files say `(first defined at file:line)`. `check` and `complete` follow imports too.
- Tests: `examples/imports.fg` (+ `examples/lib/`), `tests/errors/import_{missing,late,syntax,lib_errors,lib_syntax,duplicate}.fg`. The bounds_loop boot script now waits 4 s (2 s was flaky under a full suite).

### Session: array bounds checks (2026-11-26)
- `BoundsCheck.cs`: every array `a[i]` / `a[i] = v` is checked. The hot path is `cmp idx, [arr-8]` + `jae` (unsigned, so negative indexes fail too) with no scratch register; `a[i+k]` adds k to the index register around the compare, a constant index compares the length with an immediate. Each site has a cold stub after its function (push index, push length, line in rdx) that jumps to the shared `bounds_fail`, which prints `Index i is out of range for an array of length n (line L)` and exits 1. Runtime (`IsRuntime`) code, pointers and strings are not checked. `--no-bounds-check` turns it off.
- JSON benchmark: 595 ms checked vs 576 ms unchecked (best of 10 on a noisy machine), output unchanged.
- Fixed on the way: `NewLabel` glued the prefix to a global counter, so `or1`+86 and `or`+186 were both `.or186` (dup label crash in collections.fg at -O0 once the counter shifted). Labels are now `.prefix#N`.
- New: `examples/bounds.fg`, `examples/bounds_loop.fg`; `PRODUCTION.md` (the 1.0 checklist).

### Session: collections and strings (2026-11-26)
- `StdLib.CollectionsSource` (Forge): `List<T>`, `Dictionary<K,V>` (open addressing, linear probing, tombstones), `StringBuilder`, string methods (`__str_*`), `__fail` (print + exit 1). `__hash(x)` / `__less(a,b)` in the library are resolved by argument type (`ResolveLibDispatch`); `s.Name(..)` on a string by `ResolveStringMethod`.
- `StdLib.Inject` now adds library types (List/Dictionary/StringBuilder, unless the program declares its own) and functions by closure over calls and type names. The parser knows those three names as types.
- Methods of generic structs are instantiated lazily, on first call (`InstantiateMethod`), so `List<Point>` needn't type-check `Sort`.
- `foreach (var x in coll)`: parsed in `ParseLoop` (labels as for `for`), lowered in `LowerForeach` to a for loop over an index for arrays, strings and `List<T>`; `BlockStmt` lets one statement lower to several (`LowerBody` flattens it). `.Length` is accepted for strings and arrays; string literals take postfix operators (`"a,b".Split(",")`).

### Session: Math library (2026-11-26)
- `StdLib.cs`: the standard library as Forge source, injected per program with only the functions it calls (transitively). The parser turns `Math.Name(..)` into a call named "Math.Name" and `Math.PI/E/Tau` into constants; `ResolveMathCall` maps it (int or double versions of Abs/Min/Max/Sign/Clamp by argument type). `Math.Sqrt` is the `__sqrt` builtin (sqrtsd).
- exp, log, log10, log2, pow (e_pow.c), sin/cos (k_sin, k_cos, rem_pio2 medium), atan, atan2 are fdlibm ports using `__hi/__lo/__make` for the IEEE words; floor/ceiling/round/truncate are exact SSE2-only code. Measured against .NET on 4,000 random inputs per function and 1,264 edge cases: within 1 ulp (tan and asin 2 ulp). A first pow (x^n * exp(f log x)) was 11 ulp off, hence the e_pow port.
- `Walker` now also descends into MemberExpr / MemberAssign / IncDecExpr (the stdlib and double-use scans run before lowering).

### Session: generics (2026-11-26)
- `Generics.cs`: monomorphization. The parser marks templates (`Func.TypeParams`, `StructDecl.TypeParams`, methods get `GenericOwner`); `ExtractGenerics` takes them out of the module in the CodeGen constructor and instantiates every generic struct type the concrete code mentions (`EnsureType` on all type strings, recursively, with the struct registered before its fields so `Node<T> next` works). Generic functions are instantiated in `TypeOf(CallExpr)` (`ResolveGenericCall`): explicit `Max<int>(..)` or inferred by unifying parameter and argument types (through `T[]`, `T*`, `List<T>`). Instances are named `List<int>`, `List<int>.Add`, `Max<double>` and are ordinary functions/structs, so codegen and the optimizer are unchanged; `Analyze` iterates by index so instances made during checking get checked.
- `GenericCloner` copies pre-lowering bodies (all node kinds), mapping type strings (`SubstType`; the struct's bare name in its own methods means the instance) and renaming labels (break/continue labels are global).
- Parser: a pre-scan finds generic structs and functions (so `T Max<T>` can use T in its return type, and calls can appear before the declaration); `Max<int>(..)` is parsed speculatively (only when the name is generic or a type follows the <), with `>>` split for `List<List<int>>` and undone on backtrack.
- Errors inside an instance get "(in Max<string>)"; duplicate lint warnings from several instances are merged. All 174 existing example binaries are byte-identical.

### Session: glibc extern test (2026-11-26)
- `ffi_linux.fg` worked on glibc 2.39 (Ubuntu 24.04 container via Docker Desktop) at once. New `examples/ffi_glibc.fg`: 10-argument snprintf (stack args), printf from depths 0-5 (alignment), a qsort callback calling abs(), string/memory/env/time functions, printf from a Forge thread. All correct.
- Manifest mode `glibc`: run natively on a glibc Linux host (CI Ubuntu), or in `$FORGE_DOCKER`; build-only elsewhere.
- Found and fixed:
  - `Console.WriteLine($"... {f()}")` printed the text before the hole and only then called `f()`, so output from `f` (or a joined thread) landed mid-line. When a hole runs user code (a Forge function, extern, join, await, spawn) the line is now built first (`InterpRunsUserCode` / `InterpConcat`), as in C#.
  - `str(n)` (and every `$"..."` used as a value) dropped the minus sign: `str(-1)` was "1" (itoa ignored it on purpose). Fixed, including long.MinValue.
  - `toint("-42")` returned 0: atoi now takes a leading - or +.

### Session: kernel boot tests (2026-11-26)
- QEMU installed (C:\Program Files\qemu). `kernel.fg` and `kernel_fib.fg` booted at once; `kernel_os.fg` exposed two boot bugs:
  - the page tables and stack sat at fixed addresses (0x101000..0x108000) that a kernel with more than ~3 KB of code overlapped: the stub wrote table entries over the code and the CPU took the code bytes as page-table entries (page fault with the reserved-bit error, then a triple fault). `LowerKernel` now puts PML4/PDPT/PD and a 64 KB stack in the zero-filled tail after the image, page aligned, and errors if that would reach the heap at 2 MB.
  - the GDT had no data segment, so SS kept the boot loader's selector 0x10 and the first `iretq` from an interrupt (it reloads SS) raised #GP. The stub now adds a flat data segment at 0x10 and loads DS/ES/SS.
- After that the whole OS worked: timer IRQ, PS/2 keyboard IRQ (keys injected with the QEMU monitor's sendkey), paging alias, #DE handler, shell. `uptime` printed 2.05 s as 2.5 s (fixed); new `timer` command; `paging` prints no addresses, so the output is deterministic.
- `tests/run.sh` mode `boot`: boots kernel images with `tests/boot/<name>.in` (wait / type / key) and compares serial output with `tests/expected/<name>.boot`; CI installs qemu-system-x86 on Ubuntu. Windows/Linux binaries are byte-identical.

### Session: double arithmetic (2026-11-26)
- Doubles are raw IEEE bits in GPRs like every other value, so locals, arrays, struct fields, params, returns, threads and the register allocator are unchanged. Each operation: `LoadDoubleOperands` (left -> xmm0, right -> xmm1; a plain literal/local is loaded last, so no push/pop), one SSE instruction, `movq rax, xmm0`. Comparisons use `ucomisd` with NaN-correct flags; `EmitDoubleJump` branches on the flags directly. Unary minus flips bit 63; casts use cvtsi2sd / cvttsd2si.
- Type checking: int operands and int values assigned to double are wrapped by `ToDouble`/`Coerce` (an IntLit becomes a FloatLit) at every assignment site: decls, assigns, element/pointer stores, args, returns, struct initializers, try-else, spawn.
- Printing: `DoubleRuntime.cs` is Forge source injected only into programs that use double (`FirstUse`), so other programs are byte-identical. `__dtoa` = G15, `__dtoaf` = F0..F15; the 15-digit integer is computed as a double-double (Dekker exact product) and rounded half-to-even, which matches .NET exactly on 20,000 random values (G15) and 12,851 (F3/F0).
- Kernel target: compile error (SSE not enabled at boot). extern: still rejected (`FfiType`).
- Fixed on the way (fuzzer): a declaration whose initializer mentioned the variable itself (`var t = f(t)`) had its own error suppressed as follow-on noise, so codegen crashed (`_poisonSources`).
- Speed: Mandelbrot 2000x1000x200 ~1.1 s vs .NET JIT ~0.3 s.

### Session: string literals in interpolation holes (2026-11-25)
- `$"..."` is lexed by `LexInterpString`: text outside holes is unescaped, holes are kept verbatim, and a string literal inside a hole is skipped whole with `Lexer.StringEnd` (which also skips the holes of a nested `$"..."`). `ParseInterp` uses `StringEnd` too, so braces and colons inside a nested literal no longer end the hole or start a format. All 160 example binaries are byte-identical.

### Session: runtime I/O errors (2026-11-25)
- `ioerr()` builtin: per-thread error code of the last I/O builtin, stored at arena block +48 (`BlkIoErr`, zeroed for each thread). Set by `io_ok` / `io_err_win` (TEB LastErrorValue, gs:[0x68]) / `io_err_neg` (-rax from a syscall), emitted in `EmitIoStatus`.
- All failures now return -1 (or "" for readfile/recv) on both systems; Linux used to return raw -errno from close/send/accept.
- `try` on I/O builtins (`FallibleBuiltins`, `EmitTryBuiltin`): tests ioerr after the call; propagates an `IOError`.
- I/O builtins are compile errors on `--target kernel` (they were emitted as Linux syscalls).
- Bugs fixed on the way:
  - Windows `recv`/`send`/`closesocket` results are 32-bit: they are now sign-extended. Before, a `recv` error (0xFFFFFFFF) looked like a 4 GB read and wrote the terminator 4 GB past the buffer.
  - Windows `writefile` never checked `CreateFileA` and returned an uninitialized "bytes written" on failure; `readfile` never checked `GetFileSizeEx`.
  - `tcp_listen` ignored `bind`/`listen` failures on both systems: a busy port gave a socket that silently listened on a random port.
  - Linux `setsockopt(SO_REUSEADDR)` passed its 4th/5th arguments in r8/r9 instead of r10/r8, so it always failed; a restarted server could not bind its port while old connections were in TIME_WAIT. (This only became visible once bind failures were reported.)

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

- **Kernel target:** booted in QEMU (TCG) by the test suite, not on real hardware yet (no GRUB ISO). Serial input from a Windows pipe arrives without CR, so the shell is fed LF line endings. Interrupt handlers must not use `double` (it is not available on the kernel target anyway).
- **glibc:** `extern` is tested on Ubuntu 24.04 (glibc 2.39) in CI and via `FORGE_DOCKER`; libc calls from Forge threads share the main thread's libc TLS (`fs`), so keep heavy libc use (printf, malloc) to one thread at a time.
- After a syntax error the type checker does not run, so type errors in the same compile appear only once the syntax is fixed.
- `ioerr()` gives the OS error code, not a portable one: compare codes per OS, or just test `!= 0`. `try` on a builtin cannot tell you the code either (use `ioerr()` after it). `tcp_connect` on Linux takes IP addresses only (no DNS without libc); a host name fails with EINVAL (22).
- Interrupt handlers must not allocate while interrupting code that also allocates (the allocator isn't reentrant).
- C callbacks must run on the main thread or a Forge thread (they need Forge's thread pointer).
- On Linux, call `fflush(null)` before exiting if you used `printf` (Forge exits with `exit_group`).
- No float (32-bit), classes/inheritance, interfaces, generic constraints, generic methods inside structs, or destructors. Struct-by-value fields and inline fixed arrays inside structs aren't supported. `double` isn't available on the kernel target or through `extern`; no exponent literals (`1e6`) yet. No `~` (bitwise not) operator.
- `async`/`await` runs on the main thread only.

---

## 7. Suggested next steps

1. (Done: kernel boot tests in QEMU.) Next for bare metal: enable SSE at boot so `double` works there, a bootable GRUB ISO for real hardware, APIC timer, framebuffer.
2. Commit the regression tests (see section 5).
3. Language: exponent literals, a `~` operator, keeping doubles in SSE registers, keeping doubles in SSE registers for speed. (`+=`/`++`, `break`/`continue`, `switch`, runtime I/O errors, `double`, generics and indexers are done.)
4. (Done: string literals inside interpolation holes.)
5. Bare metal: a GDT/TSS helper example, APIC timer, framebuffer output, a bootable ISO (GRUB) for real hardware.
6. Performance: more loop optimizations (moving invariant arithmetic out of loops, strength reduction), fewer redundant moves, unrolling. JSON is still 1.4x behind C.
7. Measure C, C++, Rust and C# multithreaded HTTP on Linux (needs Docker Desktop running, or toolchains in WSL).
8. (Done: glibc test of `extern` on Linux.)
