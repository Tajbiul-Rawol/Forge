# Forge — a native compiler written in C#

`forgec` reads `.fg` source and emits a **native executable directly**: no
transpilation, no gcc, no assembler, no linker, no LLVM. The code generator
picks x86-64 machine instructions and writes their opcode bytes by hand, and the
driver writes the finished executable file itself.

- **Windows (default):** emits a PE32+ `.exe` that calls `GetStdHandle`,
  `WriteFile`, and `ExitProcess` from `kernel32.dll` through a hand-built PE
  import table.
- **Linux:** emits a static ELF64 binary that makes raw `write`/`exit` syscalls
  (pass `--target linux`).

The produced `.exe` is a standalone native binary — it does **not** need .NET,
a runtime, or anything else installed to run.

---

## What's in this folder

```
forgec/
├── README.md          <- this file
├── ForgeC.csproj      <- project file (targets .NET 10)
├── forge.bat          <- Windows helper:   forge build examples\hello.fg
├── forge.sh           <- Linux/macOS helper: ./forge.sh build examples/hello.fg
│
├── Frontend.cs        <- lexer, AST, parser
├── Asm.cs             <- x86-64 instruction encoder (opcode bytes + label fixups)
├── CodeGen.cs         <- type checker + machine-code generation + register allocator
├── Optimizer.cs       <- inlining, tail-recursion elimination, partial inlining, folding
├── Threads.cs         <- spawn/join, per-thread heap arenas, atomics, locks (Windows + Linux)
├── InlineAsm.cs       <- the asm("...") assembler (Intel syntax)
├── AsyncRuntime.cs    <- async/await: coroutine scheduler (written in Forge) + docs
├── Pe.cs              <- PE32+ writer: import tables for kernel32, ws2_32 and extern DLLs (Windows)
├── Driver.cs          <- CLI, target selection, and the ELF writer (Linux, static or dynamic)
│
└── examples/
    ├── hello.fg       <- minimal program
    ├── features.fg    <- errors, recursion, while loops, boolean logic
    ├── arrays.fg      <- heap arrays: sieve of Eratosthenes + sums
    ├── turing.fg      <- a Turing machine (binary increment) on an array tape
    ├── lowlevel.fg    <- pointers, sized memory access, hex, MMIO-style register writes
    ├── uart.fg        <- a UART serial-port driver (register map, status polling, bit ops)
    ├── forloop.fg     <- for-loop demo
    ├── kernel.fg      <- freestanding kernel (boots in QEMU, ring 0, serial output)
    ├── kernel_fib.fg  <- kernel computing Fibonacci in ring 0
    ├── bench_fib.fg   <- the benchmark workload (recursive Fibonacci)
    ├── threads.fg     <- real OS threads: spawn + join, shared memory
    ├── threads_test.fg <- spawn args, join results, atomics, locks, per-thread heaps
    ├── async.fg       <- async/await: interleaving, async recursion, WhenAll, producer/consumer
    ├── opt_test.fg    <- optimizer edge cases (output must match a -O0 build)
    ├── structs.fg     <- structs: fields, methods, initializers, linked lists, packed layouts
    ├── memory.fg      <- arenas: heapmark/heapreset(mark), whole-file readfile
    ├── ffi.fg         <- calling C (Windows DLLs), [DllImport], a qsort callback
    ├── ffi_linux.fg   <- calling libc on Linux (dynamically linked)
    ├── window.fg      <- a native Windows GUI window: Win32 structs + a Forge WndProc
    ├── inline_asm.fg  <- asm("...") with {variable} operands
    ├── kernel_os.fg   <- a tiny OS: IDT, exceptions, PIT timer, PS/2 keyboard, paging, shell
    └── typos.fg       <- deliberately broken: demo for `check` and "did you mean?""
```

A prebuilt `forgec.dll` is also included under `bin/Release/net10.0/` so you can
skip the build step if you like. Building from source is recommended.

---

## Prerequisites

| To...                              | You need                                            |
|------------------------------------|-----------------------------------------------------|
| Build/run the **compiler**         | the **.NET 10 SDK** (`dotnet` on your PATH)         |
| Run a compiled **Windows .exe**    | Windows x64 — nothing else (or Wine on Linux)       |
| Run a compiled **Linux binary**    | Linux x86-64                                         |

Get the .NET 10 SDK from https://dotnet.microsoft.com/download . Check it with:

```
dotnet --version      # should print 10.x
```

---

## Quick start (Windows)

From a terminal in this folder:

```
REM 1. build the compiler once
dotnet build -c Release

REM 2. compile a Forge program to a native .exe
dotnet bin\Release\net10.0\forgec.dll build examples\hello.fg -o hello.exe

REM 3. run it (it's a normal Windows program now)
hello.exe
```

Or use the helper, which builds the compiler automatically if needed:

```
forge run examples\hello.fg
forge build examples\features.fg -o features.exe
```

Expected output of `features.fg`:

```
=== errors ===
42
-1
25
=== recursion ===
0
1
1
2
3
5
8
13
21
34
55
=== logic ===
777
```

---

## Quick start (Linux / macOS)

The compiler runs anywhere .NET 10 does. By default it still produces a Windows
`.exe`; add `--target linux` to get a Linux binary you can run locally.

```
chmod +x forge.sh

# produce a Windows .exe (run it under Wine if you want to test on Linux)
./forge.sh build examples/hello.fg -o hello.exe
wine hello.exe                       # needs wine installed

# produce a native Linux binary instead
./forge.sh build examples/hello.fg -o hello --target linux
chmod +x hello
./hello
```

---

## Running the tests

```
tests/run.sh                          # all examples, -O2 and -O0, compared with tests/expected/
tests/run.sh hello structs            # only some examples
FORGE_WSL=docker-desktop tests/run.sh # on Windows: also run the Linux binaries in WSL
tests/run.sh --update hello           # accept new output as the baseline
```

Use Git Bash on Windows. `tests/manifest.txt` lists every example and whether it is run,
only built, or expected to fail `forgec check`. Each file in `tests/errors/` must fail
`forgec check` with exactly the diagnostics in `tests/expected/<name>.check`. The suite
also checks command-line handling (exit codes, no stack traces).

**Fuzzing.** `tests/fuzz` mutates the examples and compiles every variant for all three
targets at -O2 and -O0. Any outcome other than success or a clean error is a compiler
bug; the input is saved in `tests/fuzz/crashes/`.

```
dotnet run -c Release --project tests/fuzz -- 5000 1        # iterations, seed
dotnet run -c Release --project tests/fuzz -- --time file.fg # time each compiler phase
```

CI runs the suite on Windows and Ubuntu, plus a 3,000-input fuzz run.

---

## Command reference

```
dotnet bin/Release/net10.0/forgec.dll <build|run> <file.fg> [-o output] [--target win|linux|kernel] [-O0]
dotnet bin/Release/net10.0/forgec.dll check    <file.fg>
dotnet bin/Release/net10.0/forgec.dll complete <file.fg> <line> <col>
```

- `build` — compile to an executable file.
- `run`   — compile, then execute (uses Wine automatically for a Windows target on Linux).
- `check` — lint a file: report errors and warnings (unused variables, unreachable
  code, missing returns) without producing a binary.
- `complete` — print autocomplete candidates for the cursor at `line:col`
  (keywords, types, function names, and in-scope locals). This is the engine an
  editor extension would call; see "Tooling" below.
- `-o`    — output filename (`.exe` is appended automatically for the Windows target).
- `--target win` (default), `--target linux`, or `--target kernel`.
- `-O0`   — turn the optimizer off (see "Optimizer" below). Useful to compare
  output or speed, or to rule the optimizer out when chasing a bug.

**Errors and exit codes.** Problems are printed as `file:line: error: message`. Type
errors don't stop at the first one: `forgec` reports every independent error (up to 20)
and leaves out follow-on errors caused by a declaration that already failed. A syntax
error still stops the compile at that point.

| Exit code | Meaning |
|-----------|---------|
| 0 | Success |
| 1 | The program has errors, or a file could not be read or written |
| 2 | Bad command line (unknown command, option or target) |
| 3 | Internal compiler error: a bug in forgec. Set `FORGE_DEBUG=1` for a stack trace and please report it |

---

## Tooling: diagnostics, linting, and autocomplete

**Helpful error messages.** Errors include "did you mean?" hints for misspelled
variables, functions, types, and `.length`:

```
typos.fg:10: error: unknown function 'Squre' (did you mean 'Square'?)
typos.fg:11: error: use of undeclared variable 'cont' (did you mean 'count'?)
```

**Compile-time checks.** Beyond type errors, the compiler reports:
- missing returns ("'f' must return a int on every path") — an error
- unreachable code after `return`/`throw` — a warning
- local variables declared but never used — a warning

Warnings print during `build`; `check` runs the whole analysis without emitting
a binary:

```
forgec check examples\typos.fg
```

**Autocomplete.** `complete` returns ranked candidates (prefix matches first,
then near-misses) for a cursor position:

```
forgec complete myfile.fg 12 6     # line 12, column 6
```

This is the backend an editor would call. Wiring it into VS Code or another
editor is a thin Language Server Protocol wrapper around this command — the
analysis it needs (scopes, symbols, ranking) is already implemented here.

## The language this compiler accepts

- `module name;` followed by function definitions
- types: `int`, `uint`, `ushort`, `long`, `bool`, `string`, `void`
  (all integers are 64-bit at the machine level)
- `var` with inferred type, typed locals, assignment
- `if` / `else`, `while`, `return`
- arithmetic `+ - * / %`, comparison `< > <= >= == !=`, logical `&& || !`
  (conditions must be `bool`, exactly like C#)
- function calls (up to 4 parameters). A program's own function may reuse a
  builtin's name (e.g. its own `send`): the program's function wins
- arrays: `int[] a = new int[n];`, element read/write `a[i]` / `a[i] = v`,
  and `a.length` (heap-backed, so the language is Turing complete)
- `for (init; cond; step)` loops (alongside `while`)
- compound assignment `+= -= *= /= %= &= |= ^= <<= >>=` on variables, array
  elements, `*p` and struct fields; `x++` / `x--` / `++x` / `--x` as statements
  and as `for` steps (`for (int i = 0; i < n; i++)`)
- `break` and `continue` in `while` and `for` loops
- `switch` on an integer, string or bool, C#-style (see `examples/control_flow.fg`):

  ```csharp
  switch (code) {
      case 200: case 204: ok++; break;
      case 404: Console.WriteLine("not found"); break;
      default: return -1;
  }
  ```
- sized integer types `i8 u8 i16 u16 i32 u32 i64 u64` (memory-access widths)
- pointers `T*`, address-of `&x`, dereference `*p` and `*p = v`, pointer
  indexing `p[i]`, pointer arithmetic `p + i` (scaled by element size)
- casts `(T)expr` including integer->pointer, the basis for memory-mapped I/O
- hex literals (`0xDEADBEEF`), bitwise ops `& | ^ << >>`, `malloc(n)` for raw
  heap bytes, and `putc(ch)` to emit one byte to stdout
- `Console.WriteLine("text")`, `Console.WriteLine(intExpr)`, and `$"..."`
  string literals with `{expr}` holes
- typed errors:
  - `int F(...) throws SomeError { ... throw new SomeError("msg"); ... }`
  - `var v = try F(a) else fallback;`  — handle inline
  - `var v = try F(a);`                — propagate (caller must also declare `throws`)

Anything outside the subset produces a line-numbered compile error, e.g.:

```
features.fg:3: error: 'D' can throw - call it with 'try'
```

- structs with fields, methods and object initializers; `null`; `sizeof(T)`
- `$"..."` interpolated strings anywhere a string is expected
- `extern` functions from DLLs / shared libraries, and C-callable Forge functions
- bare-metal builtins, `interrupt` handlers and `asm("...")` (see below)

Rules for the new statement forms:

- `x++` and `x--` change a variable, so they are whole statements: `a[i++]` or
  `y = x++` is an error. Write `a[i] = v; i++;`.
- `a[f()] += 1` is rejected, because the target is read and then written: put
  `f()` in a variable first. Targets without calls (`a[i + 1] += 2`, `p.count++`)
  are fine.
- A `switch` evaluates its subject once. Case labels are constants (numbers, strings,
  `true`/`false`); duplicates are an error. As in C#, a section can't fall through
  into the next one: end it with `break`, `return`, `continue` or `throw`. Several
  labels can share one section, and `default` can appear anywhere.
- `continue` inside a `switch` continues the enclosing loop; `break` leaves the switch.
- Functions that use `break` or `continue` are not inlined by the optimizer (they
  still compile to tight loops).

### Not implemented yet (future stages)

`double`/float arithmetic, classes with inheritance, generics, `own`/`move` and
destructors, `comptime`, `gc`. Keep `.fg` files within the subset above.

## Structs

```
struct Point {
    int x;
    int y;
    int LengthSq() { return x * x + y * y; }        // a method: 'this' is implicit
    void Move(int dx, int dy) { x = x + dx; y = y + dy; }
}

Point p = new Point { x = 3, y = 4 };   // zeroed memory, then the initializers
p.Move(1, 1);
int d = p.LengthSq();
Point none = null;                       // also: 'Point q;' declares q = null
```

- **References, like C# classes.** A struct variable holds a reference (8 bytes),
  so passing or returning one is cheap and two variables can share an object.
  Struct fields can be other structs (linked lists, trees), arrays, strings, pointers.
- **C layout.** Fields are laid out in order with natural alignment, exactly like
  a C struct, so Forge structs match C and OS structures (`examples/window.fg`
  passes `WNDCLASSEXA` and `MSG` straight to Win32). `packed struct` removes the
  padding, for hardware formats: an x86-64 IDT gate is a 16-byte `packed struct`.
- **Overlays.** Cast an address to a struct to read or write memory in place:
  `IdtGate g = (IdtGate)(idt + vector * 16);` - the basis for drivers and kernels.
- `sizeof(T)` is the byte size of a type; `new Point[n]` is an array of references.
- Field access compiles to a single load or store at a fixed offset.

## Memory without a garbage collector

Forge has no garbage collector and no `free`. Each thread allocates from its own
arena (a pointer bump: very fast) and frees in bulk:

| Builtin | What it frees |
|---------|---------------|
| `int m = heapmark();` then `heapreset(m);` | everything this thread allocated after the mark |
| `heapreset();` | everything this thread allocated |
| `join(t)` | everything thread `t` allocated |

So programs free memory per phase rather than per object:

```
Config cfg = new Config { ... };        // long-lived: allocate first
int mark = heapmark();
while (true) {
    HandleRequest(...);                 // allocate freely while handling one unit of work
    heapreset(mark);                    // then drop all of it at once (cfg stays)
}
```

The same works with threads: give each worker its own arena (`spawn`) and have it
call `heapreset()` after every request; shared long-lived data belongs to the main
thread. `readfile` reads files of any size.

## Calling C libraries (extern)

```
extern "user32.dll" i32 MessageBoxA(long hwnd, string text, string title, u32 type);
[DllImport("kernel32.dll")] static extern u32 GetTickCount();      // C# spelling
extern "libc.so.6" i32 printf(string fmt, long a);                   // Linux
```

- Arguments are integers, pointers, strings (passed as `char*`: Forge strings are
  NUL-terminated), struct references (a pointer to the fields) and arrays (a pointer
  to the elements); up to 12 of them. Declare C's `int` as `i32`: narrow return values
  are sign- or zero-extended according to the declared type.
- **Callbacks:** an `extern` function with a body can be called from C, and `&Name`
  is its address: window procedures, `qsort` comparators, thread entry points
  (`examples/ffi.fg`, `examples/window.fg`).
- **Windows:** each library becomes an import of the .exe and calls follow the Win64
  convention (shadow space, 16-byte aligned stack, arguments 5+ on the stack).
- **Linux:** a program with externs is a dynamically linked ELF (interpreter,
  `.dynamic`, symbol table, GOT filled by the system loader) and calls follow System V.
  The loader defaults to glibc's `/lib64/ld-linux-x86-64.so.2`; on musl (Alpine) pass
  `--interp /lib/ld-musl-x86_64.so.1` and use `libc.musl-x86_64.so.1`. Forge's own
  thread pointer lives in `gs`, leaving `fs` to libc. Call `fflush(null)` before
  exiting if you used `printf`.
- Programs without externs stay fully static, with no libc.

## Bare metal: ports, interrupts, paging, inline assembly

These builtins compile to the instructions themselves. They are meant for
`--target kernel`; the privileged ones fault in a normal Windows/Linux process.

| Builtin | Instruction |
|---------|-------------|
| `outb(port, v)` / `outw` / `outl`, `inb(port)` / `inw` / `inl` | port I/O |
| `cli()` `sti()` `hlt()` `pause()` | interrupt flag, halt, spin hint |
| `read_cr0()` `read_cr2()` `read_cr3()` `read_cr4()`, `write_cr0(v)` `write_cr3(v)` `write_cr4(v)` | control registers (paging, faults) |
| `invlpg(addr)` | flush one TLB entry after changing a page table |
| `rdmsr(msr)` / `wrmsr(msr, v)` | model-specific registers |
| `lidt(base, limit)` / `lgdt(base, limit)` | load the interrupt / descriptor table |
| `rdtsc()` | time-stamp counter (works everywhere) |
| `getc()` | next input byte: COM1 serial on bare metal (what you type into QEMU), stdin elsewhere; -1 at end |

**Interrupt handlers.** `interrupt void Timer() { ... }` or, for CPU exceptions that
push an error code (8, 10-14, 17, 21, 29, 30), `interrupt void PageFault(long error)`.
The compiler generates the entry stub: save every scratch register, call the handler,
restore, `iretq`. `&Timer` is the address to put in an IDT gate.

**Inline assembly.** `asm("mov rax, cr3", "mov {root}, rax");` - Intel syntax, one
instruction per string, line or `;`. `{name}` is a Forge variable (kept in memory
for that function). Local labels (`again:` / `jnz again`), memory operands
(`[rsi+rdx*8+16]`, `byte ptr [...]`), `lock`/`rep` prefixes and the system
instructions (`lgdt`, `lidt`, `ltr`, `mov cr3, rax`, `mov ds, ax`, `in`/`out`,
`iretq`, `retfq`, `swapgs`, `wrmsr`, `cpuid`, ...) are supported. Scratch registers
(rax rcx rdx rsi rdi r8-r11) are free; save rbx and r12-r15 if you touch them.

**Paging.** The boot stub identity-maps the first 1 GB with 2 MB pages
(PML4 at 0x101000 -> PDPT 0x102000 -> PD 0x103000). Walk or change the tables through
pointers from `read_cr3()`, then `invlpg` or `write_cr3`.

`examples/kernel_os.fg` puts it together: an IDT from a `packed struct`, exception
handlers, the PIC remapped, a 100 Hz PIT timer, a PS/2 keyboard driver (IRQ 1),
mapping a new virtual address, and a command shell over the serial port.

---

## Running an .exe in PowerShell

PowerShell does not run programs from the current folder by name. After building
`hello.exe`, run it with a leading `.\`:

```
.\hello.exe
```

(`hello.exe` alone gives "not recognized"; `.\hello.exe` is the fix.) Also run
each command on its own line — don't let a second command wrap onto a `>>`
continuation prompt.

## If `dotnet build` fails

The compiler builds only its own five `.cs` files (the project lists them
explicitly), so a stray `Program.cs` from `dotnet new console` will **not** break
it. If you still see errors, delete the `bin` and `obj` folders and rebuild:

```
rmdir /s /q bin obj   REM (cmd)   — or in PowerShell:  rm -r -fo bin,obj
dotnet build -c Release
```

A "dotnet-bin\...forgec.dll does not exist" message when you try to *run* the
compiler means the build hasn't produced `forgec.dll` yet — build first.

## Troubleshooting

- **"dotnet: command not found"** — install the .NET 10 SDK and reopen your terminal.
- **The `.exe` won't run on Linux directly** — that's expected; it's a Windows
  binary. Use `wine hello.exe`, or rebuild with `--target linux`.
- **"is not a valid Win32 application"** — make sure you're on 64-bit Windows;
  the output is x86-64 only.

## Performance vs other languages

Recursive `fib(40)` (102,334,155), identical source algorithm in every language,
optimized builds, best of 9 runs, wall clock including process start. Measured on
Windows 11 with a Ryzen 7 7730U using `benchmarks/cpu-fibonacci/run.ps1`. Lower is faster.

| Language                 | Time    | Relative |
|--------------------------|---------|----------|
| **Forge**                | 0.195 s | **1.0x** |
| Rust (`-C opt-level=3`)  | 0.241 s | 1.2x |
| C++ (MSVC `/O2`)         | 0.393 s | 2.0x |
| C# (.NET 10 Release)     | 0.413 s | 2.1x |
| C (MSVC `/O2`)           | 0.442 s | 2.3x |
| Go                       | 0.454 s | 2.3x |

Before the optimizer existed (`-O0`), Forge took 0.82 s here (4.7x slower than it does now).

How to read this honestly:

- **Same algorithm, fewer calls.** All six programs compute the same exponential
  recursion. Forge wins because its optimizer turns `return Fib(n-1) + Fib(n-2)` into a
  loop with an accumulator, unrolls the remaining recursive call two levels, and
  checks `n < 2` at each call site. That takes 46M real calls where a naive build
  makes 331M. LLVM does the same accumulator transform for Rust (166M calls), and
  MSVC does neither. Nothing is memoized or special-cased for this benchmark; the
  same passes apply to any function of that shape (see `examples/opt_test.fg`).
- **It's one benchmark.** Recursive fib is all calls and integer adds. See the JSON
  benchmark below for loops over memory, where Forge is behind C, C++ and Rust.
- **MSVC instead of GCC/Clang.** GCC and Clang usually beat MSVC on this exact
  benchmark. On Linux, gcc `-O2` reaches about 0.18 s.
- **C# pays for startup.** Its time includes about 0.14 s of .NET runtime startup.

JSON serialize + parse (`benchmarks/json`, 323 MB of JSON per run, same hand-written
algorithm in every language, best of 5):

| Language | Time    | Relative |
|----------|---------|----------|
| Rust     | 0.207 s | 1.0x |
| C++      | 0.257 s | 1.2x |
| C        | 0.300 s | 1.4x |
| **Forge**| 0.416 s | 2.0x |
| Go       | 0.464 s | 2.2x |
| C#       | 0.636 s | 3.1x |

This was 0.896 s (last place) before the register allocator, x86 address modes and
division by constants described below.

### Optimizer

Runs on every ordinary function (async functions and the async runtime are left
alone). `-O0` turns it off.

1. **Tail-recursion elimination with an accumulator** (`Optimizer.cs`):
   - `return f(a)` becomes "update the parameters and jump to the top".
   - `return L + f(a)` becomes `acc += L`, then the same jump.
2. **Inlining**: calls to small functions (≤ 48 AST nodes) are replaced by the callee's
   body, with its locals renamed. This runs for two rounds, so a recursive call is
   unrolled two levels.
3. **Partial inlining**: if a callee starts with `if (cond) return value;` (pure,
   parameters only), that test runs at the call site, and the call happens only when it fails.
4. **Constant folding**: `c1 op c2` and `(x ± c1) ± c2`.
4b. **Loop-invariant lengths**: `i < s.length` / `i < a.length` in a loop condition is
   computed once before the loop when the loop cannot change it (for strings: no
   pointer stores and no calls in the loop), which turns a `strlen` per iteration
   into one.
5. **Register allocation** (`CodeGen.cs`, linear scan over live ranges):
   - Each variable's live range is computed over the function's statements, extended
     around loops (and backward `goto`s from tail-recursion elimination) when its value
     survives to the next iteration.
   - Variables whose ranges don't overlap share a register. This matters most after
     inlining, where every inlined copy brings its own short-lived locals.
   - A variable whose range contains no call can use the scratch registers rsi, rdi
     and r8–r11 as well as rbx and r12–r15 (11 in all). A variable that lives across a
     call only gets the callee-saved ones.
   - When registers run out, the variable with the lowest weight (uses, weighted 8x per
     loop level, including loops inside inlined code) goes to the stack. Variables whose
     address is taken always stay in memory.
6. **Instruction selection**:
   - immediate and register operands (`add rax, rbx`, `cmp rbx, 2`, `lea rax, [rbx-1]`);
   - array, pointer and string indexing as one instruction with an x86 address
     (`movzx rax, byte [rdi+rsi+1]`, `mov [r12+r13*8], rdx`), with `a[i + k]` folding
     `k` into the displacement;
   - division and remainder by a constant as a multiply by a "magic" reciprocal plus
     shifts (or just shifts for powers of two) instead of the ~40-cycle `idiv`;
   - compare-and-jump for conditions, with `&&` / `||` short-circuiting without creating 0/1 values;
   - call arguments computed straight into rdi/rsi/rdx/rcx;
   - no `rbp` frame for functions that keep everything in registers;
   - rotated loops: the condition is tested at the bottom, one branch per iteration.

## What the language does and doesn't have

Supported: functions, recursion, `if`/`else`, `while`, `for`, integer and
bitwise arithmetic, sized integers, pointers and MMIO, arrays, typed errors
(`throws`/`try`), and native code generation for Windows, Linux, and a
freestanding kernel target.

Concurrency: **real OS threads** on the Windows and Linux targets (see "Threads" below).
For concurrency on every target, including bare metal, use `async`/`await` (below).

### Threads

```
int Worker(int* counter) {                   // at most one parameter: any 8-byte value
    for (int i = 0; i < 100000; i = i + 1) { atomic_add(counter, 1); }
    return 7;
}

int* counter = (int*)malloc(8);
long t = spawn Worker(counter);              // start a thread (spawn F for no argument)
int r = join(t);                             // wait; returns the thread's result (7)
```

| Builtin | What it does |
|---------|--------------|
| `spawn F(arg)` / `spawn F` | start an OS thread running `F`; returns a handle |
| `join(h)` | wait for the thread, return its result, free its memory |
| `atomic_add(p, v)` | `*p += v` atomically, returns the old value |
| `atomic_cas(p, expected, desired)` | if `*p == expected` set it to `desired`; returns whether it did |
| `atomic_xchg(p, v)` / `atomic_store(p, v)` / `atomic_load(p)` | atomic swap / store / load |
| `lock(p)` / `unlock(p)` | mutex on a zeroed `int` cell (Windows: SRWLOCK; Linux: futex, spins briefly then sleeps) |
| `cpu_count()` | logical CPUs available to the process |
| `sleep_ms(n)` | sleep for `n` milliseconds |
| `now_ns()` | monotonic clock in nanoseconds (for timing) |

- **Memory.** Every thread allocates from its own arena (found through thread-local
  storage), so `malloc`, `new` and strings never contend, and the heap grows by
  chunks from the OS instead of stopping at 16 MB. `heapreset()` frees only the
  calling thread's allocations. `join` frees everything the thread allocated, so
  to hand data back, have the thread write into memory the spawning thread
  allocated (pass it as the argument); return plain integers through the result.
  A joined thread's region is zeroed and kept for the next `spawn` (up to 64), so
  starting threads costs no memory allocation from the OS, and every thread
  still starts with zeroed memory. `new T[n]` zeroes its elements.
- **Sharing.** Pass a pointer or array as the argument (or use `heapbase()`). Plain
  reads and writes of shared cells are not synchronized: use the atomics or a lock.
- **Implementation.** Windows: `CreateThread` + a TEB TLS slot. Linux: raw
  `clone` (no libc), the `fs` register for TLS, futex-based `join`. Kernel target:
  atomics only (no scheduler). Windows executables import `ws2_32.dll` only when the
  program uses sockets: every loaded DLL is notified on each thread start.
- **Limits.** `async`/`await` runs on the main thread only. Console output from
  several threads can interleave mid-line. Don't spawn async or `throws` functions.

See `examples/threads_test.fg` (atomics, locks, allocation stress, compare-and-swap)
and `benchmarks/http-server/bench_server_mt.fg` (one accept loop per CPU).
`benchmarks/threads/` compares Forge's threads with C, C++, Rust, Go and C#
(Forge starts and joins threads fastest of the OS-thread languages and is level
on parallel compute and atomics); see `benchmarks/README.md`.

Now present (Linux target): **runtime strings** (concat, length, indexing,
comparison, `str()`/`toint()`/`substr()`), **file I/O** (`readfile`/`writefile`),
and **TCP networking** (`tcp_connect`, `tcp_listen`, `accept`, `send`, `recv`,
`closefd`). With these, Forge can run a real HTTP server and a Redis database
client (see examples below).

Strings, file I/O, and TCP sockets now work on **both the Linux and Windows
targets**. On Linux they use raw syscalls; on Windows they use ws2_32.dll
(Winsock) and kernel32 (CreateFileA/ReadFile/WriteFile). So the HTTP server and
Redis client run as native Windows .exe files -- no WSL required.

Still absent: floating point, structs/classes, and a broad standard library.

## Freestanding kernel target

`--target kernel` produces a Multiboot ELF32 image that QEMU boots directly with
no operating system. A 32-bit stub sets up paging and long mode, jumps to the
compiler-generated 64-bit code at ring 0, and output goes over the COM1 serial
port. Build and boot:

```
forgec build examples/kernel_fib.fg -o kernel.elf --target kernel
qemu-system-x86_64 -kernel kernel.elf -nographic -serial mon:stdio
```

This runs recursion, loops, and integer/string output entirely in ring 0 with no
OS underneath. It is a freestanding kernel, not a loadable OS driver (a Windows
.sys needs Microsoft signing; a Linux .ko needs kernel-version-matched headers).

## Strings, files, networking (Linux + Windows)

Forge now has a real runtime, not just string literals:

```
string a = "Hello, ";
string b = a + "Forge";        // concatenation
int    n = b.length;            // length
int    c = b[0];                // byte at index ('H' = 72)
bool   eq = (a == "Hello, ");   // comparison
string s = str(12345);          // int -> string
int    v = toint("6789");       // string -> int
string p = substr("abcdef",2,3);// "cde"
```

File I/O (raw Linux syscalls):

```
writefile("/tmp/out.txt", "hello\n");
string contents = readfile("/tmp/out.txt");
```

TCP sockets (raw Linux syscalls):

```
int fd = tcp_connect("127.0.0.1", 6379);   // client
send(fd, "ping");
string reply = recv(fd, 1024);
closefd(fd);

int srv = tcp_listen(8080);                 // server
int cli = accept(srv);
```

### Example: an HTTP server in Forge (`examples/http_server.fg`)

```
int server = tcp_listen(9200);
int client = accept(server);
string req = recv(client, 2048);
string body = "<h1>Served by Forge</h1>";
string resp = "HTTP/1.1 200 OK\r\nContent-Length: " + str(body.length) + "\r\n\r\n" + body;
send(client, resp); closefd(client);
```

Hit it with curl and you get a real `HTTP/1.1 200 OK` with the HTML body.

### Example: a Redis client in Forge (`examples/redis.fg`)

Speaks the RESP wire protocol over a socket: `SET`/`GET` against a real Redis
server, verified independently with `redis-cli`. This is a working database
client built from the socket + string primitives.

These run on **both targets**: `--target linux` (raw syscalls) and
`--target win` (Winsock + kernel32). The HTTP server and Redis client work as
native Windows .exe files. They are the foundation a real HTTP API + database
builds on -- the thing that was impossible a few iterations ago.

### Running the HTTP server on Windows

```
forgec build examples\http_server.fg -o http_server.exe --target win
http_server.exe
```

Then from another terminal (or a browser): `curl http://127.0.0.1:9200/`.
The server is single-threaded (one connection at a time) and uses a fixed 16 MB
bump heap with no free, so it's built for testing and light use, not load.

## async / await (all targets, including bare metal)

C#-style asynchronous functions, compiled to native code with no OS support needed:

```
async Task<int> Fib(int n) {
    if (n < 2) { return n; }
    var a = Fib(n - 1);              // starts a task
    var b = Fib(n - 2);
    return await a + await b;        // suspend until each finishes
}

async Task Worker(int id) {
    for (var i = 0; i < 3; i = i + 1) {
        Console.WriteLine($"worker {id} step {i}");
        await Task.Yield();          // let other tasks run
    }
}

void Main() {
    Console.WriteLine(await Fib(15));      // 610
    var w1 = Worker(1);
    var w2 = Worker(2);
    await Task.WhenAll(w1, w2);            // steps interleave: 1,2,1,2,1,2
}
```

What you can write:

| Syntax | Meaning |
|---|---|
| `async Task<T> F(...)` | async function whose result is a `T` (`return x;` returns a `T`) |
| `async Task F(...)` / `async void F(...)` | async function with no result |
| `F(args)` | starts the task and gives back a `Task<T>` / `Task` handle |
| `await t` | the task's result; suspends the current task until `t` is done |
| `await Task.Yield()` | moves the current task to the back of the ready queue |
| `await Task.WhenAll(t1, t2, ...)` | waits for every listed task |
| `Task<int> t = ...;`, `Task t = ...;` | task-typed locals and parameters (`Task<T>` converts to `Task`) |
| `(i64)t`, `(Task<int>)x` | store task handles in arrays or memory and get them back |
| `exit(code)` | new builtin: end the program with a status code |

How it behaves (same as C#, where it matters):

- Calling an async function **starts it right away** on its own stack. It runs until
  it finishes or first suspends, and then the caller continues with the handle.
- `await` can be used inside `async` functions and in `Main`.
- When `Main` returns, tasks that are still ready get to finish before the
  program exits, so fire-and-forget calls (`Worker(3);`) still complete.
- If every task is waiting on another one, the program prints
  `fatal: async deadlock - every task is waiting on another task` and exits with
  status 1 instead of hanging.
- Helpful errors: `await` outside an async function, `await` on a non-task,
  a `Task` return type without `async`, and `int x = F();` (which says
  "did you forget 'await'?").

How it works: **stackful coroutines** with a cooperative, single-threaded scheduler.
Each task gets a 32 KB stack carved from the heap; finished tasks' stacks are recycled.
The scheduler (ready queue, waiter lists, stack pool) is written *in Forge* and added
to your program by the compiler only when it uses `async`/`await`. Only the context
switch is hand-written machine code: save callee-saved registers, swap `rsp`, restore.
On Windows it also swaps the TEB stack bounds, just as `SwitchToFiber` does. There are
no syscalls in the async path, so it runs unchanged on `--target win`, `--target linux`
and the freestanding `--target kernel`.

Limits: up to 4 parameters per async function; async functions can't declare `throws`
yet; the scheduler is not thread-safe, so don't mix it with `spawn` threads touching
the same tasks. The 16 MB heap allows about 450 tasks *alive at the same time* (finished
tasks' stacks are reused, so the total number of tasks over a run is unlimited). There's
no timer yet, so there is no `Task.Delay`.

## Heap layout note

The first 4 KB of the heap are reserved: bytes 0-63 are the integer-printing scratch
buffer and `heapbase()` returns heap+64, a ~4 KB shared region. `malloc`/arrays start
after it. (Before this, the first allocation shared memory with the print buffer, so
`Console.WriteLine(n)` could corrupt the first array, and `heapbase()` overlapped the
first `malloc`.)
