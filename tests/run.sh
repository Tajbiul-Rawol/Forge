#!/usr/bin/env bash
# Forge regression tests.
#
#   tests/run.sh             build the compiler, run every case in tests/manifest.txt
#   tests/run.sh --update    same, but rewrite tests/expected/* from the current output
#   tests/run.sh hello ffi   only the named examples
#
# Works in Git Bash on Windows and in bash on Linux. Needs the .NET 10 SDK.
# On Windows, FORGE_WSL=<distro> also runs the linux binaries inside that WSL distro.
# Exit code is 0 only if every case passes.

set -u
cd "$(dirname "$0")/.."
ROOT=$(pwd)

UPDATE=0
ONLY=()
for a in "$@"; do
    case "$a" in
        --update) UPDATE=1 ;;
        -h|--help) sed -n '2,9p' "$0"; exit 0 ;;
        *) ONLY+=("$a") ;;
    esac
done

case "$(uname -s)" in
    Linux*) HOST=linux ;;
    *)      HOST=win ;;
esac

echo "== building forgec"
if ! BUILD_LOG=$(dotnet build -c Release -nologo -v q "$ROOT/ForgeC.csproj" 2>&1); then
    printf '%s\n' "$BUILD_LOG"; echo "FAIL: compiler build"; exit 1
fi
FORGEC=(dotnet "$ROOT/bin/Release/net10.0/forgec.dll")

WORK=$(mktemp -d "${TMPDIR:-/tmp}/forge-tests.XXXXXX")
trap 'rm -rf "$WORK"' EXIT

PASS=0; FAIL=0; FAILED=()

pass() { PASS=$((PASS+1)); printf '  ok    %s\n' "$1"; }
fail() { FAIL=$((FAIL+1)); FAILED+=("$1"); printf '  FAIL  %s\n' "$1"; [ -n "${2:-}" ] && printf '%s\n' "$2" | sed 's/^/        /'; }

# Normalise output so the same baseline works on both hosts.
norm() { tr -d '\r'; }

# Windows path -> path inside the WSL distro (/mnt/host/c/... on Docker Desktop, /mnt/c/... elsewhere).
if [ "$HOST" = win ] && [ -n "${FORGE_WSL:-}" ]; then
    WSL_MNT=$(MSYS_NO_PATHCONV=1 wsl -d "$FORGE_WSL" -- sh -c '[ -d /mnt/host/c ] && echo /mnt/host || echo /mnt' | tr -d '\r')
    [ -n "$WSL_MNT" ] || { echo "FAIL: cannot reach WSL distro '$FORGE_WSL'"; exit 1; }
fi
wsl_path() {
    local w drive
    w=$(cygpath -m "$1")
    drive=$(printf %s "${w:0:1}" | tr 'A-Z' 'a-z')
    printf '%s/%s%s' "$WSL_MNT" "$drive" "${w:2}"
}

ext_for() { case "$1" in win) echo .exe ;; linux) echo "" ;; kernel) echo .elf ;; esac; }

wanted() {
    [ ${#ONLY[@]} -eq 0 ] && return 0
    for o in "${ONLY[@]}"; do [ "$o" = "$1" ] && return 0; done
    return 1
}

run_case() {  # name target opt
    local name=$1 target=$2 opt=$3 label exe out expected actual rc
    label="$name [$target${opt:+ $opt}]"
    exe="$WORK/$name-$target${opt}$(ext_for "$target")"

    if ! "${FORGEC[@]}" build "$ROOT/examples/$name.fg" -o "$exe" --target "$target" $opt > "$WORK/build.log" 2>&1; then
        fail "$label build" "$(cat "$WORK/build.log")"; return
    fi

    # Only run binaries this host can execute (linux ones too, on Windows with FORGE_WSL).
    local via_wsl=0
    [ "$HOST" = win ] && [ "$target" = linux ] && [ -n "${FORGE_WSL:-}" ] && via_wsl=1
    if [ "$MODE" = build ] || { [ "$target" != "$HOST" ] && [ $via_wsl -eq 0 ]; }; then
        pass "$label build"; return
    fi

    local dir="$WORK/cwd-$name-$target$opt"; mkdir -p "$dir"
    if [ $via_wsl -eq 1 ]; then
        MSYS_NO_PATHCONV=1 wsl -d "$FORGE_WSL" --cd "$(wsl_path "$dir")" -- timeout 60 "$(wsl_path "$exe")" < /dev/null > "$WORK/out" 2>&1
    else
        ( cd "$dir" && timeout 60 "$exe" ) < /dev/null > "$WORK/out" 2>&1
    fi
    rc=$?
    actual="$( norm < "$WORK/out"; echo "exit: $rc")"
    expected="$ROOT/tests/expected/$name.out"

    if [ $UPDATE -eq 1 ] && [ -z "$opt" ]; then
        printf '%s\n' "$actual" > "$expected"; pass "$label (baseline written)"; return
    fi
    if [ ! -f "$expected" ]; then
        fail "$label" "no baseline: run tests/run.sh --update $name"; return
    fi
    if [ "$actual" = "$(norm < "$expected")" ]; then
        pass "$label"
    else
        fail "$label" "$(diff <(norm < "$expected") <(printf '%s\n' "$actual") | head -20)"
    fi
}

check_case() {  # name [dir] : `forgec check` must fail with exactly the expected diagnostics
    local name=$1 dir=${2:-$ROOT/examples} actual expected
    ( cd "$dir" && "${FORGEC[@]}" check "$name.fg" ) > "$WORK/out" 2>&1
    local rc=$?
    actual="$(norm < "$WORK/out"; echo "exit: $rc")"
    expected="$ROOT/tests/expected/$name.check"
    if [ $UPDATE -eq 1 ]; then printf '%s\n' "$actual" > "$expected"; pass "$name [check] (baseline written)"; return; fi
    if [ $rc -ne 1 ]; then fail "$name [check]" "expected exit 1 (errors), got $rc"$'\n'"$(head -5 "$WORK/out")"; return; fi
    if [ ! -f "$expected" ]; then fail "$name [check]" "no baseline: run tests/run.sh --update $name"; return; fi
    if [ "$actual" = "$(norm < "$expected")" ]; then pass "$name [check]"
    else fail "$name [check]" "$(diff <(norm < "$expected") <(printf '%s\n' "$actual") | head -20)"; fi
}

cli_case() {  # label expected-exit expected-text args... : command-line handling
    local label=$1 want=$2 text=$3; shift 3
    ( cd "$WORK" && "${FORGEC[@]}" "$@" ) > "$WORK/out" 2>&1
    local rc=$?
    if [ $rc -ne "$want" ]; then fail "cli: $label" "expected exit $want, got $rc"$'\n'"$(head -5 "$WORK/out")"
    elif ! grep -qF -- "$text" "$WORK/out"; then fail "cli: $label" "output lacks '$text'"$'\n'"$(head -5 "$WORK/out")"
    elif grep -q "Unhandled exception\|   at ForgeC\." "$WORK/out"; then fail "cli: $label" "printed a .NET stack trace"
    else pass "cli: $label"; fi
}

echo "== host: $HOST"
while read -r name targets MODE _; do
    case "$name" in ''|'#'*) continue ;; esac
    wanted "$name" || continue
    if [ "$MODE" = check ]; then check_case "$name"; continue; fi
    for t in ${targets//,/ }; do
        if [ "$t" = kernel ]; then run_case "$name" kernel ""; continue; fi
        run_case "$name" "$t" ""
        [ "$MODE" = run ] && run_case "$name" "$t" "-O0"
    done
done < <(tr -d "\r" < "$ROOT/tests/manifest.txt")

# Diagnostics: every tests/errors/*.fg must fail `forgec check` with tests/expected/<name>.check.
for f in "$ROOT"/tests/errors/*.fg; do
    name=$(basename "$f" .fg)
    wanted "$name" || continue
    check_case "$name" "$ROOT/tests/errors"
done

# Command line: bad usage is exit 2 with a message, never a stack trace.
if [ ${#ONLY[@]} -eq 0 ]; then
    printf 'module m;\nvoid Main() { Console.WriteLine(1); }\n' > "$WORK/ok.fg"
    cli_case "no arguments"        2 "usage:"
    cli_case "unknown command"     2 "unknown command 'frob'"       frob
    cli_case "missing file arg"    2 "missing source file"          build
    cli_case "unknown target"      2 "unknown target 'mips'"        build ok.fg --target mips
    cli_case "option needs value"  2 "option '-o' needs a value"    build ok.fg -o
    cli_case "unknown option"      2 "unknown option '--fast'"      build ok.fg --fast
    cli_case "missing source file" 1 "cannot read"                  build does_not_exist.fg
    cli_case "unwritable output"   1 "cannot write"                 build ok.fg -o "$WORK/no/such/dir/x.exe"
    cli_case "valid build"         0 "forgec: wrote"                build ok.fg -o ok.exe
    printf 'module k;
void Main() { string s = readfile("x"); }
' > "$WORK/kio.fg"
    cli_case "I/O on bare metal"   1 "readfile() needs an OS"       build kio.fg --target kernel
fi

echo
echo "== $PASS passed, $FAIL failed"
if [ $FAIL -gt 0 ]; then printf '   %s\n' "${FAILED[@]}"; exit 1; fi
exit 0
