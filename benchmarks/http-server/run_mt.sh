#!/usr/bin/env sh
# Multithreaded HTTP benchmark (Linux): the same server in each language - one
# worker per CPU, all blocking in accept() on one listening socket - hit by
# load.go with one TCP connection per request.
#   Usage: sh run_mt.sh [/path/to/forgec.dll] [total] [concurrency]
# Builds whatever toolchains are present. Binaries that already exist (for
# example cross-compiled on Windows) are used as they are.
FORGEC="${1:-}"; TOTAL="${2:-100000}"; CONC="${3:-64}"; RUNS=3
cd "$(dirname "$0")"
have(){ command -v "$1" >/dev/null 2>&1; }

echo "Building servers..."
have gcc   && gcc -O2 -pthread bench_c_mt.c -o bench_c_mt 2>/dev/null
have g++   && g++ -O2 -pthread -std=c++17 bench_cpp_mt.cpp -o bench_cpp_mt 2>/dev/null
have go    && go build -o bench_go_mt bench_go_mt.go 2>/dev/null && go build -o loadbin load.go 2>/dev/null
have rustc && rustc -C opt-level=3 bench_rust_mt.rs -o bench_rust_mt 2>/dev/null
have dotnet && ( cd bench_csharp_mt && dotnet build -c Release -o out >/dev/null 2>&1 )
if [ -n "$FORGEC" ] && have dotnet; then dotnet "$FORGEC" build bench_server_mt.fg -o bench_forge_mt --target linux >/dev/null 2>&1; fi
[ -x ./loadbin ] || { echo "Need Go (or a prebuilt ./loadbin) for the load client."; exit 1; }

bench(){ # name port command...
  name="$1"; port="$2"; shift 2
  "$@" >/dev/null 2>&1 &
  pid=$!
  sleep 2
  ./loadbin 127.0.0.1:$port 5000 $CONC >/dev/null 2>&1          # warmup
  best=0; failed=""
  i=0; while [ $i -lt $RUNS ]; do
    r=$(./loadbin 127.0.0.1:$port $TOTAL $CONC 2>&1)
    rps=$(echo "$r" | sed -n 's/^\([0-9]*\) req.*/\1/p'); ok=$(echo "$r" | sed -n 's/.*(\([0-9]*\) ok.*/\1/p')
    [ "${ok:-0}" -lt "$TOTAL" ] && failed="   (some requests failed)"
    [ "${rps:-0}" -gt "$best" ] && best=$rps
    i=$((i+1))
  done
  kill -9 $pid 2>/dev/null; wait $pid 2>/dev/null; sleep 1
  printf "  %-6s %8s req/s%s\n" "$name" "$best" "$failed"
  echo "$best $name" >> .mt_results
}

rm -f .mt_results
echo; echo "Benchmarking: $(nproc) CPUs, $TOTAL requests per run, $CONC concurrent clients, best of $RUNS"
[ -x ./bench_forge_mt ] && bench Forge 8090 ./bench_forge_mt
[ -x ./bench_c_mt ]     && bench C     8094 ./bench_c_mt
[ -x ./bench_cpp_mt ]   && bench "C++" 8095 ./bench_cpp_mt
[ -x ./bench_rust_mt ]  && bench Rust  8092 ./bench_rust_mt
[ -x ./bench_go_mt ]    && bench Go    8091 ./bench_go_mt
[ -f bench_csharp_mt/out/bench_csharp_mt.dll ] && have dotnet && bench "C#" 8093 dotnet bench_csharp_mt/out/bench_csharp_mt.dll
echo; echo "Ranked:"
sort -rn .mt_results | while read rps name; do printf "  %-6s %8s req/s\n" "$name" "$rps"; done
rm -f .mt_results
