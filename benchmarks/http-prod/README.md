# http-prod: production HTTP servers

The `../http-server/` benchmark is deliberately minimal: read once, answer, close. This
one compares servers that could actually be deployed. Each language uses its usual
production stack: Rust uses **hyper** on **tokio**, Go uses the standard library's
**net/http**, and C# uses **Kestrel** (ASP.NET Core). C, C++ and Forge have no standard
HTTP server, so they get hand-written event-loop servers with the same design.
Each server runs single-threaded or multithreaded.

| Language | Source | Server | Single-threaded | Multithreaded |
|----------|--------|--------|-----------------|---------------|
| Forge | `forge/server.fg` | hand-written: non-blocking Winsock, `WSAPoll` loop | 1 loop | 1 loop per CPU |
| C | `c/server.c` | hand-written: non-blocking sockets, `poll`/`WSAPoll` loop | 1 loop | 1 loop per CPU |
| C++ | `cpp/server.cpp` | the C design in C++17 (RAII, `string_view`, `std::thread`) | 1 loop | 1 loop per CPU |
| Rust | `rust/` | hyper 1 + tokio | current-thread runtime | multi-thread runtime |
| Go | `go/` | net/http | `GOMAXPROCS=1` | `GOMAXPROCS=CPUs` |
| C# | `csharp/` | Kestrel, minimal API | pinned to 1 CPU, 1 I/O queue | all CPUs |

Each server takes `server [port] [threads]`: `threads` 1 is single-threaded and 0 means
one per CPU. The default port is 8100.

## What every server must do

`load check host:port` tests this. `run.ps1` only times servers that pass it.

| Behaviour | Required |
|-----------|----------|
| `GET /` → `200`, body `Hello, World!`, with `Date` and `Content-Type` | yes |
| keep-alive: several requests on one connection | yes |
| pipelining: several requests in one packet, answered in order | yes |
| a request split across several writes | yes |
| a `Content-Length` request body is skipped, and the next request still works | yes |
| `Connection: close`, and HTTP/1.0 without keep-alive, close after the response | yes |
| an unknown path → `404`; another method on `/` → `405` | yes |
| a malformed request line → `400` | advisory (WARN) |
| 70 KB of headers → `431` | advisory (WARN) |

The last two are advisory because RFC 9112 lets a server simply close the connection
on a bad request. hyper does that.

The hand-written servers (Forge, C, C++) also have:
- an idle timeout of 30 s;
- a lingering close: on an error response, they shut down sending, then discard input
  until the client closes, so the client reads the error instead of a connection reset;
- an 8 KB header limit and a 1 MB body limit;
- `501` for `Transfer-Encoding`, since chunked request bodies aren't supported;
- a 16 KB output buffer per connection, so a client that pipelines without reading
  is slowed down instead of using unbounded memory;
- a `Date` header cached once a second.

The Forge server pools its connections and their buffers, so its memory stays fixed
while it runs. It doesn't need `heapreset()`.

## Running it (Windows)

You need the .NET 10 SDK and Go. MSVC and Rust are optional: a language whose
toolchain is missing is skipped. Build the compiler once (`dotnet build -c Release` in
the repo root), then:

```
cd benchmarks\http-prod
powershell -ExecutionPolicy Bypass -File .\run.ps1                      # everything
powershell -ExecutionPolicy Bypass -File .\run.ps1 -Only Forge,C -Modes pipeline
powershell -ExecutionPolicy Bypass -File .\run.ps1 -Seconds 10 -Runs 5 -Threads multi
```

The first build of the Rust server downloads its crates.

`load.exe` (`load/load.go`) applies three loads. Every response is checked:

- `keepalive`: one request at a time on 64 reused connections.
- `pipeline`: each connection writes 16 requests at once, then reads the 16
  responses. This is how the TechEmpower "plaintext" test works.
- `close`: a new TCP connection for every request.

In single-threaded runs, the server is pinned to CPU 0 and the load generator to the
other CPUs. Multithreaded runs share all CPUs. The `peak MB` column is the server's
peak working set.

## Results

These numbers are from Windows 11 on a Ryzen 7 7730U with 16 logical CPUs: 64
connections, 5 s per run, best of 3, with the load generator on the same machine.
All servers passed the check and every run had 0 errors. Higher req/s is better.

**Pipelined: the servers' own speed**

| Language | single-threaded | vs best | multithreaded | vs best | peak memory (MB) |
|----------|----------------:|--------:|--------------:|--------:|-----------------:|
| Forge | **401,658** | 1.00x | **644,464** | 1.00x | 8.7 |
| C | 377,038 | 0.94x | 639,224 | 0.99x | 7.5 |
| C++ | 368,508 | 0.92x | 624,516 | 0.97x | 6.5 |
| Rust (hyper) | 332,585 | 0.83x | 597,904 | 0.93x | 8.5 |
| C# (Kestrel) | 217,041 | 0.54x | 583,596 | 0.91x | 62.6 |
| Go (net/http) | 32,404 | 0.08x | 132,897 | 0.21x | 24.4 |

**Keep-alive and connection-per-request: limited by Windows**

| Language | keep-alive, single | keep-alive, multi | close, single | close, multi |
|----------|-------------------:|------------------:|--------------:|-------------:|
| Forge | 6,762 | 6,744 | 2,392 | 2,419 |
| C | 6,810 | 6,767 | 2,336 | 2,591 |
| C++ | 6,831 | 6,822 | 2,409 | 2,610 |
| Rust | 6,856 | 6,856 | 2,441 | 2,711 |
| Go | 6,734 | 6,844 | 2,228 | 2,743 |
| C# | 5,799 | 6,842 | 1,901 | 2,645 |

How to read these:

- **Without pipelining, every server lands at nearly the same number.** On this machine,
  ESET's firewall inspects loopback traffic, so each round trip costs about 0.7 ms
  whatever the server does. Keep-alive therefore tops out near 7,000 req/s, and
  connection-per-request near 2,500 req/s. On a machine without a third-party firewall,
  or on Linux, these numbers will be much higher.
- **Pipelining spreads that cost over 16 requests.** Forge is fastest single-threaded,
  6% ahead of C, and level with C multithreaded. C and C++ use exactly the same design
  as Forge, so the gap comes from code generation and from formatting each response
  with snprintf.
- **hyper and Kestrel** do more work per request: they parse headers into maps, route
  requests, and run the async machinery. Kestrel catches up multithreaded, but uses 7–9x
  the memory.
- **Go's net/http** handles pipelined requests one after another and makes a separate
  write for each response, so pipelining barely helps it. Its keep-alive numbers are
  level with the others.
- Peak memory is the highest figure from all six runs of each server. The hand-written
  servers stay under 10 MB.
