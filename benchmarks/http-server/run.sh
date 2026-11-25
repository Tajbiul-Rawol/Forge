#!/usr/bin/env bash
# HTTP benchmark: same single-threaded "Hello, World!" server in each language.
# Builds whatever toolchains are present, benchmarks each (best of 2), prints req/s.
#   Usage: bash run.sh [/path/to/forgec.dll]
set -u
FORGEC="${1:-}"
HERE="$(cd "$(dirname "$0")" && pwd)"; cd "$HERE"
TOTAL=20000; CONC=10
have(){ command -v "$1" >/dev/null 2>&1; }

echo "Building servers..."
have gcc   && gcc -O2 bench_c.c -o bench_c 2>/dev/null && echo "  C    built"
have go    && go build -o bench_go bench_go.go 2>/dev/null && echo "  Go   built"
have cargo && ( cd bench_rust && cargo build --release >/dev/null 2>&1 ) && echo "  Rust built"
if have dotnet; then ( cd bench_csharp && dotnet build -c Release -o out >/dev/null 2>&1 ) && echo "  C#   built"; fi
if [ -n "$FORGEC" ] && have dotnet; then
  dotnet "$FORGEC" build bench_server.fg -o bench_forge --target linux >/dev/null 2>&1 && chmod +x bench_forge 2>/dev/null && echo "  Forge built"
fi

# Pick a load client
if have go; then go build -o loadbin load.go 2>/dev/null; LOAD="./loadbin ADDR TOTAL CONC"
elif have python3; then LOAD="python3 load.py HOST PORT TOTAL CONC"
else echo "Need Go or Python3 for the load client."; exit 1; fi

run_load(){ # host port
  local host="$1" port="$2"
  local cmd="${LOAD//ADDR/$host:$port}"; cmd="${cmd//HOST/$host}"; cmd="${cmd//PORT/$port}"
  cmd="${cmd//TOTAL/$TOTAL}"; cmd="${cmd//CONC/$CONC}"
  $cmd
}

bench(){ # name  start-cmd  host  port  kill-pattern
  local name="$1" cmd="$2" host="$3" port="$4" pat="$5"
  pkill -9 -f "$pat" 2>/dev/null; sleep 1
  $cmd >/dev/null 2>&1 &
  local pid=$!
  sleep 2.5                                   # let it bind
  run_load "$host" "$port" >/dev/null 2>&1    # warmup
  local best=0
  for i in 1 2; do
    local r; r=$(run_load "$host" "$port" 2>&1)
    local rps; rps=$(echo "$r" | grep -oE '^[0-9]+')
    [ "${rps:-0}" -gt "$best" ] && best=$rps
  done
  printf "  %-7s %8s req/s\n" "$name" "$best"
  kill -9 $pid 2>/dev/null; sleep 1
}

echo; echo "Benchmarking ($TOTAL requests, concurrency $CONC, best of 2)..."
[ -x ./bench_forge ] && bench "Forge" "./bench_forge" 127.0.0.1 8080 "bench_forge"
[ -x ./bench_c ]     && bench "C"     "./bench_c"     127.0.0.1 8084 "bench_c"
[ -x ./bench_go ]    && bench "Go"    "./bench_go"    127.0.0.1 8081 "bench_go"
[ -f bench_rust/target/release/bench_rust ] && bench "Rust" "./bench_rust/target/release/bench_rust" 127.0.0.1 8082 "target/release/bench_rust"
[ -f bench_csharp/out/bench_csharp.dll ]    && bench "C#"   "dotnet bench_csharp/out/bench_csharp.dll" 127.0.0.1 8083 "bench_csharp.dll"
echo
echo "NOTE: single-threaded servers + one connection per request => the kernel's"
echo "connection setup/teardown dominates, so all languages cluster together."
echo "This measures the OS, not language speed. For raw speed see ../cpu-fibonacci."
