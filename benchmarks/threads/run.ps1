# Multithreaded benchmark on native Windows: parallel compute, atomics, mutex, thread start-up.
# Usage:  .\run.ps1 [-Forgec path\to\forgec.dll] [-Runs 3]
# Each program times its own workloads (process start-up is not counted); the best of -Runs is kept.
param([string]$Forgec = "", [int]$Runs = 3)
$ErrorActionPreference = "Continue"
Set-Location $PSScriptRoot
if (-not $Forgec) { $Forgec = Join-Path $PSScriptRoot "..\..\bin\Release\net10.0\forgec.dll" }

function Find-Tool($name, $candidates) {
    $c = Get-Command $name -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    foreach ($p in $candidates) { if (Test-Path $p) { return $p } }
    return $null
}
$go     = Find-Tool go     @("C:\Program Files\Go\bin\go.exe", "C:\Go\bin\go.exe")
$rustc  = Find-Tool rustc  @("$env:USERPROFILE\.cargo\bin\rustc.exe")
$dotnet = Find-Tool dotnet @("C:\Program Files\dotnet\dotnet.exe")
$vsw    = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vcvars = $null
if (Test-Path $vsw) {
    $vs = & $vsw -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($vs) { $vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat" }
}

Remove-Item mt_c.exe, mt_cpp.exe, mt_rust.exe, mt_go.exe, mt_forge.exe -ErrorAction SilentlyContinue
Write-Host "Building (optimized)..."
if ($vcvars) {
    $env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
    cmd /c "`"$vcvars`" >nul && cl /nologo /O2 mt.c /Fe:mt_c.exe >nul && cl /nologo /O2 /EHsc /std:c++17 mt.cpp /Fe:mt_cpp.exe >nul"
    Remove-Item *.obj -ErrorAction SilentlyContinue
    if (Test-Path mt_c.exe)   { "  C     built  (MSVC cl /O2, Win32 threads + SRWLOCK)" }
    if (Test-Path mt_cpp.exe) { "  C++   built  (MSVC cl /O2, std::thread + std::mutex)" }
} else { "  C/C++ skipped (no MSVC found)" }
if ($rustc) { & $rustc -C opt-level=3 mt.rs -o mt_rust.exe 2>$null; Remove-Item mt_rust.pdb -ErrorAction SilentlyContinue; if (Test-Path mt_rust.exe) { "  Rust  built  (rustc -C opt-level=3, std::thread + Mutex)" } } else { "  Rust  skipped" }
if ($go)    { & $go build -o mt_go.exe mt.go; if (Test-Path mt_go.exe) { "  Go    built  (goroutines + sync.Mutex)" } } else { "  Go    skipped" }
if ($dotnet) {
    Push-Location mt_csharp; & $dotnet build -c Release -o out *> $null; Pop-Location
    if (Test-Path .\mt_csharp\out\mt_csharp.dll) { "  C#    built  (.NET Release, Thread + lock)" }
    if (Test-Path $Forgec) { & $dotnet $Forgec build mt.fg -o mt_forge.exe --target win *> $null; if (Test-Path mt_forge.exe) { "  Forge built  (spawn + SRWLOCK)" } }
}

$cmds = [ordered]@{}
if (Test-Path mt_c.exe)    { $cmds["C"]     = @((Resolve-Path mt_c.exe).Path, "") }
if (Test-Path mt_cpp.exe)  { $cmds["C++"]   = @((Resolve-Path mt_cpp.exe).Path, "") }
if (Test-Path mt_rust.exe) { $cmds["Rust"]  = @((Resolve-Path mt_rust.exe).Path, "") }
if (Test-Path mt_go.exe)   { $cmds["Go"]    = @((Resolve-Path mt_go.exe).Path, "") }
if (Test-Path .\mt_csharp\out\mt_csharp.dll) { $cmds["C#"] = @($dotnet, "`"$((Resolve-Path .\mt_csharp\out\mt_csharp.dll).Path)`"") }
if (Test-Path mt_forge.exe){ $cmds["Forge"] = @((Resolve-Path mt_forge.exe).Path, "") }

function Run-Once($exe, $argz) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo $exe, $argz
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true
    try { $p = [Diagnostics.Process]::Start($psi) } catch { return $null }
    $out = $p.StandardOutput.ReadToEnd(); $p.WaitForExit()
    $r = @{}
    foreach ($line in ($out -split "`n")) { $f = $line.Trim() -split ' '; if ($f.Count -eq 3) { $r[$f[0]] = @([long]$f[1], [long]$f[2]) } }
    return $r
}

$best = @{}; $threads = 0
Write-Host "`nRunning each program $Runs times (best time per workload is kept)..."
foreach ($k in $cmds.Keys) {
    for ($i = 0; $i -lt $Runs; $i++) {
        $r = Run-Once $cmds[$k][0] $cmds[$k][1]
        if (-not $r -or -not $r.ContainsKey("threads")) { "  $k failed to run"; break }
        $threads = $r["threads"][0]
        $expect = @{ primes1 = 664579; primesN = 664579; atomic = 1000000 * $threads; mutex = 200000 * $threads; spawn = 500 * $threads }
        foreach ($w in $expect.Keys) {
            if (-not $r.ContainsKey($w)) { continue }
            if ($r[$w][0] -ne $expect[$w]) { "  $k $w WRONG RESULT: $($r[$w][0]) (expected $($expect[$w]))"; continue }
            $key = "$k|$w"
            if (-not $best.ContainsKey($key) -or $r[$w][1] -lt $best[$key]) { $best[$key] = $r[$w][1] }
        }
    }
}

$titles = [ordered]@{
    primes1 = "Primes below 10M, 1 thread"
    primesN = "Primes below 10M, $threads threads"
    atomic  = "Atomic counter: $threads threads x 1M increments"
    mutex   = "Mutex: $threads threads x 200k lock/increment/unlock"
    spawn   = "Start + join $threads threads, 500 times ($(500 * $threads) threads)"
}
foreach ($w in $titles.Keys) {
    Write-Host "`n$($titles[$w])  (lower is faster)"
    $rows = @(); foreach ($k in $cmds.Keys) { if ($best.ContainsKey("$k|$w")) { $rows += [pscustomobject]@{ L = $k; T = $best["$k|$w"] / 1000.0 } } }
    if ($rows.Count -eq 0) { continue }
    $min = ($rows | Measure-Object T -Minimum).Minimum
    foreach ($row in ($rows | Sort-Object T)) {
        $extra = ""
        if ($w -eq "primesN" -and $best.ContainsKey("$($row.L)|primes1")) { $extra = "   speedup {0,4:N1}x" -f (($best["$($row.L)|primes1"] / 1000.0) / $row.T) }
        "  {0,-6} {1,9:N1} ms   {2,5:N1}x{3}" -f $row.L, $row.T, ($row.T / $min), $extra
    }
}
