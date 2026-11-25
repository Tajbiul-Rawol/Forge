# JSON throughput benchmark (serialize + parse 20,000 records x 100 passes) on native Windows.
# Usage:  .\run.ps1 [-Forgec path\to\forgec.dll] [-Runs 5]
# Finds MSVC (via vswhere), Go and Rust even when they are not on PATH.
# Each language is skipped if its toolchain is missing.
param([string]$Forgec = "", [int]$Runs = 5)
$ErrorActionPreference = "Continue"
Set-Location $PSScriptRoot
if (-not $Forgec) { $Forgec = Join-Path $PSScriptRoot "..\..\bin\Release\net10.0\forgec.dll" }
$Expected = "161418380`n118578658335"

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

Remove-Item json_c.exe, json_cpp.exe, json_rust.exe, json_go.exe, json_forge.exe -ErrorAction SilentlyContinue
Write-Host "Building (optimized)..."
if ($vcvars) {
    $env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"   # vcvars needs vswhere
    cmd /c "`"$vcvars`" >nul && cl /nologo /O2 json.c /Fe:json_c.exe >nul && cl /nologo /O2 /EHsc /std:c++17 json.cpp /Fe:json_cpp.exe >nul"
    Remove-Item *.obj -ErrorAction SilentlyContinue
    if (Test-Path json_c.exe)   { "  C     built  (MSVC cl /O2)" }
    if (Test-Path json_cpp.exe) { "  C++   built  (MSVC cl /O2)" }
} else { "  C/C++ skipped (no MSVC found)" }
if ($rustc) { & $rustc -C opt-level=3 json.rs -o json_rust.exe 2>$null; Remove-Item json_rust.pdb -ErrorAction SilentlyContinue; if (Test-Path json_rust.exe) { "  Rust  built  (rustc -C opt-level=3)" } } else { "  Rust  skipped" }
if ($go)    { & $go build -o json_go.exe json.go; if (Test-Path json_go.exe) { "  Go    built  (go build)" } } else { "  Go    skipped" }
if ($dotnet) {
    Push-Location json_csharp; & $dotnet build -c Release -o out *> $null; Pop-Location
    if (Test-Path .\json_csharp\out\json_csharp.dll) { "  C#    built  (.NET Release, JIT)" }
    if (Test-Path $Forgec) { & $dotnet $Forgec build json.fg -o json_forge.exe --target win *> $null; if (Test-Path json_forge.exe) { "  Forge built  (forgec)" } }
}

$cmds = [ordered]@{}
if (Test-Path json_c.exe)    { $cmds["C"]     = @((Resolve-Path json_c.exe).Path, "") }
if (Test-Path json_cpp.exe)  { $cmds["C++"]   = @((Resolve-Path json_cpp.exe).Path, "") }
if (Test-Path json_rust.exe) { $cmds["Rust"]  = @((Resolve-Path json_rust.exe).Path, "") }
if (Test-Path json_go.exe)   { $cmds["Go"]    = @((Resolve-Path json_go.exe).Path, "") }
if (Test-Path .\json_csharp\out\json_csharp.dll) { $cmds["C#"] = @($dotnet, "`"$((Resolve-Path .\json_csharp\out\json_csharp.dll).Path)`"") }
if (Test-Path json_forge.exe){ $cmds["Forge"] = @((Resolve-Path json_forge.exe).Path, "") }

function Run-Once($exe, $argz) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo $exe, $argz
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try { $p = [Diagnostics.Process]::Start($psi) }
    catch { return @($null, "FAILED TO START: $($_.Exception.InnerException.Message) (antivirus block?)") }
    $out = $p.StandardOutput.ReadToEnd(); $p.WaitForExit()
    $sw.Stop()
    return @($sw.Elapsed.TotalSeconds, ($out.Trim() -replace "`r", ""))
}

Write-Host "`nCorrectness check (every language must print 161418380 / 118578658335):"
foreach ($k in @($cmds.Keys)) {
    $r = Run-Once $cmds[$k][0] $cmds[$k][1]; "  {0,-6} {1}" -f $k, ($r[1] -replace "`n", " / ")
    if ($r[1] -ne $Expected) { $cmds.Remove($k) }      # don't time broken/blocked builds
}

# 161,418,380 bytes are written and then parsed again, so 322.8 MB of JSON is processed per run.
$mb = 2 * 161418380 / 1e6
Write-Host "`nTiming - best of $Runs runs (wall clock, includes process start), lower is faster:"
$res = @{}
foreach ($k in $cmds.Keys) {
    $best = [double]::MaxValue
    for ($i = 0; $i -lt $Runs; $i++) { $t = (Run-Once $cmds[$k][0] $cmds[$k][1])[0]; if ($t -ne $null -and $t -lt $best) { $best = $t } }
    $res[$k] = $best
}
$fastest = ($res.Values | Measure-Object -Minimum).Minimum
$res.GetEnumerator() | Sort-Object Value | ForEach-Object { "  {0,-6} {1,7:N3}s   {2,6:N0} MB/s   {3,5:N1}x" -f $_.Key, $_.Value, ($mb / $_.Value), ($_.Value / $fastest) }
