# HTTP benchmark on native Windows: the same single-threaded "Hello, World!" server
# in each language, hit by load.go (one TCP connection per request).
# Usage:  .\run.ps1 [-Forgec path\to\forgec.dll] [-Total 20000] [-Conc 10] [-Runs 3]
param([string]$Forgec = "", [int]$Total = 20000, [int]$Conc = 10, [int]$Runs = 3)
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
$cargo  = Find-Tool cargo  @("$env:USERPROFILE\.cargo\bin\cargo.exe")
$dotnet = Find-Tool dotnet @("C:\Program Files\dotnet\dotnet.exe")
$vsw    = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vcvars = $null
if (Test-Path $vsw) {
    $vs = & $vsw -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($vs) { $vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat" }
}
if (-not $go) { "Go is required for the load generator (load.go)."; exit 1 }

Get-Process bench_c, bench_go, bench_rust, bench_forge -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item bench_c.exe, bench_go.exe, bench_forge.exe, load.exe -ErrorAction SilentlyContinue
Write-Host "Building servers..."
& $go build -o load.exe load.go
if ($vcvars) {
    $env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
    cmd /c "`"$vcvars`" >nul && cl /nologo /O2 bench_c.c /Fe:bench_c.exe >nul"
    Remove-Item *.obj -ErrorAction SilentlyContinue
    if (Test-Path bench_c.exe) { "  C     built  (MSVC cl /O2)" }
} else { "  C     skipped (no MSVC found)" }
& $go build -o bench_go.exe bench_go.go; if (Test-Path bench_go.exe) { "  Go    built" }
if ($cargo) { Push-Location bench_rust; & $cargo build --release -q 2>$null; Pop-Location; if (Test-Path bench_rust\target\release\bench_rust.exe) { "  Rust  built  (cargo --release)" } } else { "  Rust  skipped" }
if ($dotnet) {
    Push-Location bench_csharp; & $dotnet build -c Release -o out *> $null; Pop-Location
    if (Test-Path bench_csharp\out\bench_csharp.dll) { "  C#    built  (.NET Release)" }
    if (Test-Path $Forgec) { & $dotnet $Forgec build bench_server.fg -o bench_forge.exe --target win *> $null; if (Test-Path bench_forge.exe) { "  Forge built  (forgec)" } }
}

# name -> exe, args, port
$servers = [ordered]@{}
if (Test-Path bench_c.exe)     { $servers["C"]     = @((Resolve-Path bench_c.exe).Path, "", 8084) }
if (Test-Path bench_go.exe)    { $servers["Go"]    = @((Resolve-Path bench_go.exe).Path, "", 8081) }
if (Test-Path bench_rust\target\release\bench_rust.exe) { $servers["Rust"] = @((Resolve-Path bench_rust\target\release\bench_rust.exe).Path, "", 8082) }
if (Test-Path bench_csharp\out\bench_csharp.dll) { $servers["C#"] = @($dotnet, "`"$((Resolve-Path bench_csharp\out\bench_csharp.dll).Path)`"", 8083) }
if (Test-Path bench_forge.exe) { $servers["Forge"] = @((Resolve-Path bench_forge.exe).Path, "", 8080) }

function Run-Load($port, $n) {
    $out = & .\load.exe "127.0.0.1:$port" $n $Conc 2>&1 | Out-String
    if ($out -match '^(\d+) req/s\s+\((\d+) ok') { return @([int]$Matches[1], [int]$Matches[2]) }
    return @(0, 0)
}

Write-Host "`nBenchmarking ($Total requests per run, $Conc concurrent clients, best of $Runs)..."
Write-Host "(Windows Firewall may ask about the Forge server the first time: it listens on all interfaces.)"
$res = @{}
foreach ($k in $servers.Keys) {
    $s = $servers[$k]
    $p = if ($s[1]) { Start-Process -FilePath $s[0] -ArgumentList $s[1] -PassThru -WindowStyle Hidden } else { Start-Process -FilePath $s[0] -PassThru -WindowStyle Hidden }
    Start-Sleep -Milliseconds 1500                     # let it bind (and the JIT start, for C#)
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
Write-Host "`nNOTE: single-threaded servers with one connection per request spend almost all their"
Write-Host "time in the OS TCP stack (connect/accept/close), so this mostly measures Windows, not the language."
