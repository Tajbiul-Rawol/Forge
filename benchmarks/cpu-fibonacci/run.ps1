# CPU benchmark (recursive fib(40)) on native Windows.
# Usage:  .\run.ps1 [-Forgec path\to\forgec.dll] [-Runs 5]
# Finds MSVC (via vswhere), Go and Rust even when they are not on PATH.
# Each language is skipped if its toolchain is missing.
param([string]$Forgec = "", [int]$Runs = 5)
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

Remove-Item fib_c.exe, fib_cpp.exe, fib_rust.exe, fib_go.exe, fib_forge.exe -ErrorAction SilentlyContinue
Write-Host "Building (optimized)..."
if ($vcvars) {
    $env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"   # vcvars needs vswhere
    cmd /c "`"$vcvars`" >nul && cl /nologo /O2 fib.c /Fe:fib_c.exe >nul && cl /nologo /O2 /EHsc fib.cpp /Fe:fib_cpp.exe >nul"
    Remove-Item *.obj -ErrorAction SilentlyContinue
    if (Test-Path fib_c.exe)   { "  C     built  (MSVC cl /O2)" }
    if (Test-Path fib_cpp.exe) { "  C++   built  (MSVC cl /O2)" }
} else { "  C/C++ skipped (no MSVC found)" }
if ($rustc) { & $rustc -C opt-level=3 fib.rs -o fib_rust.exe 2>$null; Remove-Item fib_rust.pdb -ErrorAction SilentlyContinue; if (Test-Path fib_rust.exe) { "  Rust  built  (rustc -C opt-level=3)" } } else { "  Rust  skipped" }
if ($go)    { & $go build -o fib_go.exe fib.go; if (Test-Path fib_go.exe) { "  Go    built  (go build)" } } else { "  Go    skipped" }
if ($dotnet) {
    Push-Location fib_csharp; & $dotnet build -c Release -o out *> $null; Pop-Location
    if (Test-Path .\fib_csharp\out\fib_csharp.dll) { "  C#    built  (.NET Release, JIT)" }
    if (Test-Path $Forgec) { & $dotnet $Forgec build fib.fg -o fib_forge.exe --target win *> $null; if (Test-Path fib_forge.exe) { "  Forge built  (forgec)" } }
}

$cmds = [ordered]@{}
if (Test-Path fib_c.exe)    { $cmds["C"]     = @((Resolve-Path fib_c.exe).Path, "") }
if (Test-Path fib_cpp.exe)  { $cmds["C++"]   = @((Resolve-Path fib_cpp.exe).Path, "") }
if (Test-Path fib_rust.exe) { $cmds["Rust"]  = @((Resolve-Path fib_rust.exe).Path, "") }
if (Test-Path fib_go.exe)   { $cmds["Go"]    = @((Resolve-Path fib_go.exe).Path, "") }
if (Test-Path .\fib_csharp\out\fib_csharp.dll) { $cmds["C#"] = @($dotnet, "`"$((Resolve-Path .\fib_csharp\out\fib_csharp.dll).Path)`"") }
if (Test-Path fib_forge.exe){ $cmds["Forge"] = @((Resolve-Path fib_forge.exe).Path, "") }

function Run-Once($exe, $argz) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo $exe, $argz
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try { $p = [Diagnostics.Process]::Start($psi) }
    catch { return @($null, "FAILED TO START: $($_.Exception.InnerException.Message) (antivirus block?)") }
    $out = $p.StandardOutput.ReadToEnd(); $p.WaitForExit()
    $sw.Stop()
    return @($sw.Elapsed.TotalSeconds, $out.Trim())
}

Write-Host "`nCorrectness check (every language must print 102334155):"
foreach ($k in @($cmds.Keys)) {
    $r = Run-Once $cmds[$k][0] $cmds[$k][1]; "  {0,-6} {1}" -f $k, $r[1]
    if ($r[1] -ne "102334155") { $cmds.Remove($k) }      # don't time broken/blocked builds
}

Write-Host "`nTiming - best of $Runs runs (wall clock, includes process start), lower is faster:"
$res = @{}
foreach ($k in $cmds.Keys) {
    $best = [double]::MaxValue
    for ($i = 0; $i -lt $Runs; $i++) { $t = (Run-Once $cmds[$k][0] $cmds[$k][1])[0]; if ($t -ne $null -and $t -lt $best) { $best = $t } }
    $res[$k] = $best
}
$fastest = ($res.Values | Measure-Object -Minimum).Minimum
$res.GetEnumerator() | Sort-Object Value | ForEach-Object { "  {0,-6} {1,7:N3}s   {2,5:N1}x" -f $_.Key, $_.Value, ($_.Value / $fastest) }
