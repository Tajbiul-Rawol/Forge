#!/usr/bin/env bash
# CPU benchmark: recursive fib(40) in every language, optimized builds, best of 3.
# Skips any language whose toolchain isn't installed.
#   Usage: bash run.sh [/path/to/forgec.dll]
set -u
FORGEC="${1:-}"
HERE="$(cd "$(dirname "$0")" && pwd)"; cd "$HERE"
have(){ command -v "$1" >/dev/null 2>&1; }

echo "Building (optimized)..."
have gcc   && gcc  -O2 fib.c   -o fib_c    2>/dev/null && echo "  C    built"
have g++   && g++  -O2 fib.cpp -o fib_cpp  2>/dev/null && echo "  C++  built"
have rustc && rustc -O fib.rs  -o fib_rust 2>/dev/null && echo "  Rust built"
have go    && go build -o fib_go fib.go    2>/dev/null && echo "  Go   built"
if have dotnet; then ( cd fib_csharp && dotnet build -c Release -o out >/dev/null 2>&1 ) && echo "  C#   built"; fi
if [ -n "$FORGEC" ] && have dotnet; then
  dotnet "$FORGEC" build fib.fg -o fib_forge --target linux >/dev/null 2>&1 && chmod +x fib_forge 2>/dev/null && echo "  Forge built"
fi

# Build a manifest of "name=command" for whatever compiled
MANIFEST=""
[ -x ./fib_c ]    && MANIFEST+="C=./fib_c"$'\n'
[ -x ./fib_cpp ]  && MANIFEST+="C++=./fib_cpp"$'\n'
[ -x ./fib_rust ] && MANIFEST+="Rust=./fib_rust"$'\n'
[ -x ./fib_go ]   && MANIFEST+="Go=./fib_go"$'\n'
[ -f fib_csharp/out/fib_csharp.dll ] && MANIFEST+="C#=dotnet fib_csharp/out/fib_csharp.dll"$'\n'
[ -x ./fib_forge ] && MANIFEST+="Forge=./fib_forge"$'\n'

echo; echo "Correctness check (every language must print 102334155):"
while IFS= read -r line; do [ -z "$line" ] && continue
  name="${line%%=*}"; cmd="${line#*=}"; printf "  %-6s %s\n" "$name" "$($cmd)"
done <<< "$MANIFEST"

echo; echo "Timing — best of 3 runs, lower is faster:"
python3 - <<PY
import subprocess, time
manifest = """$MANIFEST""".strip().splitlines()
res = {}
for line in manifest:
    name, cmd = line.split("=", 1)
    best = 1e9
    for _ in range(3):
        t0 = time.perf_counter()
        subprocess.run(cmd.split(), stdout=subprocess.DEVNULL)
        best = min(best, time.perf_counter() - t0)
    res[name] = best
base = min([v for k,v in res.items() if k in ("C","C++")] or [min(res.values())])
print(f"  {'language':<8}{'seconds':>10}{'vs C/C++':>11}")
for name, t in sorted(res.items(), key=lambda kv: kv[1]):
    print(f"  {name:<8}{t:>10.3f}{t/base:>10.1f}x")
PY
