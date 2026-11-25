# Forge benchmarks — reproduce them yourself

Every source file, load generator and script is here. Four benchmarks:

- `cpu-fibonacci/` — recursive `fib(40)`: function calls + integer math.
- `json/` — serialize 20,000 records to JSON and parse them back, 100 passes
  (323 MB of JSON per run): byte-level string work, branches, loads/stores.
- `http-server/` — a minimal HTTP server in each language, single-threaded
  (`run.ps1` / `run.sh`) and multithreaded (`run_mt.ps1` / `run_mt.sh`).
- `threads/` — parallel compute, atomics, a mutex and thread start-up, using
  each language's standard threads.

## Running on Windows (PowerShell)

Build the compiler once (`dotnet build -c Release` in the repo root), then:

```
cd benchmarks\cpu-fibonacci;  .\run.ps1 -Runs 9
cd ..\json;                   .\run.ps1 -Runs 5
cd ..\http-server;            .\run.ps1 -Runs 3;  .\run_mt.ps1
cd ..\threads;                .\run.ps1 -Runs 5
```

Each script finds MSVC (via vswhere), Rust, Go and .NET even when they are not on
PATH, skips any language whose toolchain is missing, checks every program's output
before timing it, and prints a ranked table. If scripts are disabled, use
`powershell -ExecutionPolicy Bypass -File .\run.ps1`. Run each one twice: antivirus
scans every new .exe on its first launch. The HTTP load generator needs Go.

On Linux / WSL / macOS, `cpu-fibonacci/run.sh`, `http-server/run.sh` and
`http-server/run_mt.sh` do the same (pass the path to forgec.dll).

## Reference numbers

Windows 11, Ryzen 7 7730U, MSVC `/O2`, `rustc -C opt-level=3`, Go, .NET 10.
Your hardware will differ.

### cpu-fibonacci (best of 9, lower is faster)

| Language | seconds |
|----------|---------|
| Forge    | 0.195 |
| Rust     | 0.241 |
| C++      | 0.393 |
| C#       | 0.413 |
| C        | 0.442 |
| Go       | 0.454 |

Forge wins because its optimizer removes most of the calls (tail-recursion
elimination + inlining); see the main README's Performance section.

### json (best of 5, lower is faster)

| Language | seconds | throughput |
|----------|---------|------------|
| Rust     | 0.207 | 1,557 MB/s |
| C++      | 0.257 | 1,258 MB/s |
| C        | 0.300 | 1,075 MB/s |
| Forge    | 0.416 |   776 MB/s |
| Go       | 0.464 |   696 MB/s |
| C#       | 0.636 |   508 MB/s |

Every language runs the same hand-written serializer and parser (no JSON
library: Forge has none, and serde/System.Text.Json/encoding/json would make it a
library contest). Records are parsed into flat integer arrays with zero-copy
string spans, because Forge has no structs.

Forge took 0.896 s here (last) until four code generator changes:
- array, pointer and string indexing became one instruction with an x86 address;
- a linear-scan register allocator lets variables share registers by live range,
  and gives scratch registers to variables that live across no call;
- functions that use arrays can now run without a stack frame;
- division by a constant became a multiply.

What still separates it from C/C++/Rust: no loop optimizations (moving unchanging
work out of loops, strength reduction, vectorization), values parked on the stack
inside complex expressions, and a few redundant moves per statement.

### http-server (best of 3, higher is better)

| Language | req/s |
|----------|-------|
| Rust     | 2,699 |
| C        | 2,630 |
| C#       | 2,513 |
| Go       | 2,235 |
| Forge    | 2,061 |

**This measures Windows, not the languages.** Each request opens and closes a TCP
connection against a single-threaded server, and the loopback connect/accept/close
(plus antivirus network filtering) costs ~400 µs, while the server's own work is
well under 1 µs. Linux gets ~10–13k req/s with the same code. A benchmark that
separates languages on serving needs concurrent servers (threads/async per
connection, all cores) and keep-alive connections.

### http-server, multithreaded (Linux, higher is better)

`run_mt.sh` / `run_mt.ps1` run the same server in every language: one worker
per CPU, all blocking in `accept()` on one listening socket, answering
`Hello, World!` and closing. Sources: `bench_server_mt.fg`, `bench_c_mt.c`,
`bench_cpp_mt.cpp`, `bench_rust_mt.rs`, `bench_go_mt.go`, `bench_csharp_mt/`.

Linux (WSL 2, 16 CPUs), 100,000 requests per run, 64 concurrent clients, best of 3,
load generator on the same machine:

| Server | req/s |
|--------|-------|
| Forge, 16 threads | 107,919 |
| Go, 16 accept loops | 74,442 |
| Forge, single thread (`bench_server.fg`) | 46,836 |
| Go, single accept loop (`bench_go.go`) | 27,774 |

C, C++, Rust and C# were not measured on Linux here: the WSL distro used has no
C compiler, Rust Linux target or .NET runtime. `run_mt.sh` builds and runs them
wherever those toolchains exist.

On Windows all six multithreaded servers land at ~2,900–3,100 req/s: connection
setup in the OS is the ceiling, not the server.

### threads (best of 5, lower is faster)

`threads/run.ps1`: every program uses its language's standard threads (Win32 +
SRWLOCK in C, `std::thread` + `std::mutex` in C++, `std::thread` + `Mutex` in
Rust, goroutines + `sync.Mutex` in Go, `Thread` + `lock` in C#, `spawn` + `lock`
in Forge), times itself, and has its results checked. 16 threads.

| Workload | Forge | C | C++ | Rust | Go | C# |
|----------|-------|---|-----|------|----|----|
| Primes below 10M, 1 thread | 1,398 ms | 1,390 | 1,390 | **1,194** | 1,401 | 1,387 |
| Primes below 10M, 16 threads | 200 ms | 191 | 193 | **180** | 201 | 197 |
| Atomic counter, 16 × 1M increments | 170 ms | 171 | 179 | 176 | **163** | 175 |
| Mutex, 16 × 200k lock/increment/unlock | 77 ms | **56** | 102 | 97 | 128 | 195 |
| Start + join 8,000 threads | **257 ms** | 264 | 310 | 319 | (3)* | 614 |

\* Go starts goroutines, not OS threads, so its spawn time is not comparable.

Forge on Linux (WSL 2, best of 3): primes 1 thread 1,502 ms, 16 threads 212 ms,
atomic 174 ms, mutex 229 ms, spawn 229 ms.

## Manual commands (if you skip the scripts / are on Windows)

CPU, per language:
```
gcc -O2 fib.c -o fib_c && ./fib_c
g++ -O2 fib.cpp -o fib_cpp && ./fib_cpp
rustc -O fib.rs -o fib_rust && ./fib_rust
go build -o fib_go fib.go && ./fib_go
dotnet run -c Release --project fib_csharp
dotnet /path/to/forgec.dll build fib.fg -o fib_forge --target linux && ./fib_forge
```
Time each with your shell (e.g. `time ./fib_c`, or PowerShell
`Measure-Command { .\fib_c.exe }`).

HTTP: start a server in one terminal, then in another run the load client:
```
go run load.go 127.0.0.1:8081 20000 10        # or:
python3 load.py 127.0.0.1 8081 20000 10
```

### Forge on Windows (native, no WSL)
The Forge HTTP server also builds as a native Windows .exe — sockets go through
ws2_32.dll instead of Linux syscalls:
```
dotnet \path\to\forgec.dll build bench_server.fg -o bench_forge.exe --target win
.\bench_forge.exe
```
Then point the load client at 127.0.0.1:8080. (Windows Firewall may prompt on
first listen — allow it.)
