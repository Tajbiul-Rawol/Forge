# Multithreaded HTTP benchmark on native Windows: the same server in each language
# (one worker per CPU, all blocking in accept() on one listening socket), hit by
# load.go with one TCP connection per request.
# Usage:  .\run_mt.ps1 [-Forgec path\to\forgec.dll] [-Total 20000] [-Conc 64] [-Runs 3]
# NOTE: on Windows every server tops out near the same rate - TCP connection setup
# in the OS is the limit, not the server. Run run_mt.sh on Linux to compare languages.
param([string]$Forgec = "", [int]$Total = 20000, [int]$Conc = 64, [int]$Runs = 3)
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
if (-not $go) { "Go is required for the load generator (load.go)."; exit 1 }

Get-Process bench_*_mt -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item bench_c_mt.exe, bench_cpp_mt.exe, bench_go_mt.exe, bench_rust_mt.exe, bench_forge_mt.exe -ErrorAction SilentlyContinue
Write-Host "Building servers..."
& $go build -o load.exe load.go
if ($vcvars) {
    $env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
    cmd /c "`"$vcvars`" >nul && cl /nologo /O2 bench_c_mt.c /Fe:bench_c_mt.exe >nul && cl /nologo /O2 /EHsc /std:c++17 bench_cpp_mt.cpp /Fe:bench_cpp_mt.exe >nul"
    Remove-Item *.obj -ErrorAction SilentlyContinue
} else { "  C/C++ skipped (no MSVC found)" }
& $go build -o bench_go_mt.exe bench_go_mt.go
if ($rustc) { & $rustc -C opt-level=3 bench_rust_mt.rs -o bench_rust_mt.exe 2>$null; Remove-Item bench_rust_mt.pdb -ErrorAction SilentlyContinue }
if ($dotnet) {
    Push-Location bench_csharp_mt; & $dotnet build -c Release -o out *> $null; Pop-Location
    if (Test-Path $Forgec) { & $dotnet $Forgec build bench_server_mt.fg -o bench_forge_mt.exe --target win *> $null }
}

# name -> exe, args, port
$servers = [ordered]@{}
if (Test-Path bench_forge_mt.exe) { $servers["Forge"] = @((Resolve-Path bench_forge_mt.exe).Path, "", 8090) }
if (Test-Path bench_c_mt.exe)     { $servers["C"]     = @((Resolve-Path bench_c_mt.exe).Path, "", 8094) }
if (Test-Path bench_cpp_mt.exe)   { $servers["C++"]   = @((Resolve-Path bench_cpp_mt.exe).Path, "", 8095) }
if (Test-Path bench_rust_mt.exe)  { $servers["Rust"]  = @((Resolve-Path bench_rust_mt.exe).Path, "", 8092) }
if (Test-Path bench_go_mt.exe)    { $servers["Go"]    = @((Resolve-Path bench_go_mt.exe).Path, "", 8091) }
if (Test-Path bench_csharp_mt\out\bench_csharp_mt.dll) { $servers["C#"] = @($dotnet, "`"$((Resolve-Path bench_csharp_mt\out\bench_csharp_mt.dll).Path)`"", 8093) }
foreach ($k in $servers.Keys) { "  $k built" }

function Run-Load($port, $n) {
    $out = & .\load.exe "127.0.0.1:$port" $n $Conc 2>&1 | Out-String
    if ($out -match '^(\d+) req/s\s+\((\d+) ok') { return @([int]$Matches[1], [int]$Matches[2]) }
    return @(0, 0)
}

Write-Host "`nBenchmarking ($([Environment]::ProcessorCount) CPUs, $Total requests per run, $Conc concurrent clients, best of $Runs)..."
$res = @{}
foreach ($k in $servers.Keys) {
    $s = $servers[$k]
    $p = if ($s[1]) { Start-Process -FilePath $s[0] -ArgumentList $s[1] -PassThru -WindowStyle Hidden } else { Start-Process -FilePath $s[0] -PassThru -WindowStyle Hidden }
    Start-Sleep -Milliseconds 1500
    $null = Run-Load $s[2] 2000                        # warmup
    $best = 0; $okAll = $true
    for ($i = 0; $i -lt $Runs; $i++) {
        $r = Run-Load $s[2] $Total
        if ($r[1] -lt $Total) { $okAll = $false }
        if ($r[0] -gt $best) { $best = $r[0] }
    }
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    $res[$k] = $best
    $note = if ($okAll) { "" } else { "   (some requests failed)" }
    "  {0,-6} {1,8:N0} req/s{2}" -f $k, $best, $note
    Start-Sleep -Milliseconds 500
}
Write-Host "`nRanked:"
$top = ($res.Values | Measure-Object -Maximum).Maximum
$res.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "  {0,-6} {1,8:N0} req/s   {2,5:N2}x" -f $_.Key, $_.Value, ($_.Value / [math]::Max(1, $top)) }
Write-Host "`nNOTE: on Windows all servers cluster together - the OS TCP stack (connect/accept/close)"
Write-Host "is the limit. Use run_mt.sh on Linux for a language comparison."
