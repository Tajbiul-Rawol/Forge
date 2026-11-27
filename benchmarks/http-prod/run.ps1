# Production HTTP server benchmark on Windows: Forge, C, C++, Rust (hyper), Go (net/http)
# and C# (Kestrel), each single-threaded and multithreaded, under three loads:
#   keepalive  one request at a time on reused connections
#   pipeline   16 requests per write on reused connections (TechEmpower "plaintext" style)
#   close      a new TCP connection for every request
# Every server must pass `load check` (the HTTP behaviour in README.md) before it is timed.
#
# Usage: .\run.ps1 [-Seconds 5] [-Runs 3] [-Conns 64] [-Only Forge,C] [-Modes keepalive,pipeline,close]
#                  [-Threads single,multi]
param([int]$Seconds = 5, [int]$Runs = 3, [int]$Conns = 64, [string[]]$Only = @(),
      [string[]]$Modes = @("keepalive", "pipeline", "close"), [string[]]$Threads = @("single", "multi"))
$ErrorActionPreference = "Continue"
Set-Location $PSScriptRoot
$port = 8100

function Find-Tool($name, $candidates) {
    $c = Get-Command $name -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    foreach ($p in $candidates) { if (Test-Path $p) { return $p } }
    return $null
}
$go     = Find-Tool go     @("C:\Program Files\Go\bin\go.exe", "C:\Go\bin\go.exe")
$cargo  = Find-Tool cargo  @("$env:USERPROFILE\.cargo\bin\cargo.exe")
$dotnet = Find-Tool dotnet @("C:\Program Files\dotnet\dotnet.exe")
$forgec = Join-Path $PSScriptRoot "..\..\bin\Release\net10.0\forgec.dll"
$vsw    = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vcvars = $null
if (Test-Path $vsw) {
    $vs = & $vsw -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($vs) { $vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat" }
}
if (-not $go) { "Go is required for the load generator (load/load.go)."; exit 1 }

# ---- build ----
Get-Process *_server, server -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$PSScriptRoot*" } | Stop-Process -Force
Write-Host "Building..."
Push-Location load; & $go build -o ..\load.exe load.go; Pop-Location
$servers = [ordered]@{}            # name -> exe path
if ($dotnet -and (Test-Path $forgec)) {
    & $dotnet $forgec build forge\server.fg -o forge_server.exe --target win *> $null
    if (Test-Path forge_server.exe) { $servers["Forge"] = "forge_server.exe" }
} else { "  Forge skipped (build the compiler first: dotnet build -c Release in the repo root)" }
if ($vcvars) {
    $env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
    cmd /c "`"$vcvars`" >nul && cl /nologo /O2 c\server.c /Fe:c_server.exe >nul && cl /nologo /O2 /EHsc /std:c++17 cpp\server.cpp /Fe:cpp_server.exe >nul"
    Remove-Item *.obj -ErrorAction SilentlyContinue
    if (Test-Path c_server.exe)   { $servers["C"]   = "c_server.exe" }
    if (Test-Path cpp_server.exe) { $servers["C++"] = "cpp_server.exe" }
} else { "  C and C++ skipped (no MSVC found)" }
if ($cargo) {
    Push-Location rust; & $cargo build --release -q 2>$null; Pop-Location
    if (Test-Path rust\target\release\server.exe) { $servers["Rust"] = "rust\target\release\server.exe" }
} else { "  Rust skipped (no cargo found)" }
Push-Location go; & $go build -o ..\go_server.exe .; Pop-Location
if (Test-Path go_server.exe) { $servers["Go"] = "go_server.exe" }
if ($dotnet) {
    Push-Location csharp; & $dotnet publish -c Release -o out -v q -nologo *> $null; Pop-Location
    if (Test-Path csharp\out\server.exe) { $servers["C#"] = "csharp\out\server.exe" }
}
if ($Only.Count) { foreach ($k in @($servers.Keys)) { if ($Only -notcontains $k) { $servers.Remove($k) } } }
foreach ($k in $servers.Keys) { "  $k built" }

# ---- run ----
$cpus = [Environment]::ProcessorCount
$allMask = [long]([math]::Pow(2, $cpus) - 1)

function Start-Server($exe, $threads, [bool]$pin) {
    $p = Start-Process -FilePath (Resolve-Path $exe).Path -ArgumentList "$port", "$threads" -PassThru -WindowStyle Hidden
    if ($pin) { try { $p.ProcessorAffinity = [IntPtr]1 } catch {} }
    for ($i = 0; $i -lt 50; $i++) {                  # wait until it accepts connections
        try { $t = New-Object Net.Sockets.TcpClient("127.0.0.1", $port); $t.Close(); return $p } catch { Start-Sleep -Milliseconds 100 }
    }
    return $p
}

function Run-Load($mode, [bool]$pin, $secs) {
    $psi = New-Object Diagnostics.ProcessStartInfo((Resolve-Path .\load.exe).Path)
    $psi.Arguments = "bench 127.0.0.1:$port $secs $Conns $mode"
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.CreateNoWindow = $true
    $lp = [Diagnostics.Process]::Start($psi)
    if ($pin) { try { $lp.ProcessorAffinity = [IntPtr]($allMask -bxor 1) } catch {} }   # every CPU but the server's
    $out = $lp.StandardOutput.ReadToEnd(); $lp.WaitForExit()
    if ($out -match '^(\d+) req/s p50 (\d+) p99 (\d+) errors (\d+)') {
        return [pscustomobject]@{ rps = [int]$Matches[1]; p50 = [int]$Matches[2]; p99 = [int]$Matches[3]; errors = [int]$Matches[4] }
    }
    return [pscustomobject]@{ rps = 0; p50 = 0; p99 = 0; errors = -1 }
}

$results = @()
Write-Host "`n$cpus CPUs, $Conns connections, ${Seconds}s per run, best of $Runs"
Write-Host "single: the server is pinned to CPU 0 and the load generator to the others; multi: no pinning`n"
foreach ($tm in $Threads) {
    $threadsArg = if ($tm -eq "single") { 1 } else { 0 }
    $pin = $tm -eq "single"
    foreach ($k in $servers.Keys) {
        $p = Start-Server $servers[$k] $threadsArg $pin
        $check = & .\load.exe check "127.0.0.1:$port" 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  $k ($tm): FAILED the HTTP check, not timed"
            $check.Trim().Split("`n") | Where-Object { $_ -match '^FAIL' } | ForEach-Object { Write-Host "      $_" }
            Stop-Process $p -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500; continue
        }
        $warn = ($check.Split("`n") | Where-Object { $_ -match '^WARN' }).Count
        $null = Run-Load "pipeline" $pin 1                  # warm up (JIT, caches)
        foreach ($m in $Modes) {
            $best = $null
            for ($i = 0; $i -lt $Runs; $i++) {
                $r = Run-Load $m $pin $Seconds
                if ($r.errors -ne 0) { $best = $r; break }
                if (-not $best -or $r.rps -gt $best.rps) { $best = $r }
            }
            $p.Refresh()
            $mem = [math]::Round($p.PeakWorkingSet64 / 1MB, 1)
            $results += [pscustomobject]@{ Threads = $tm; Mode = $m; Language = $k; rps = $best.rps; p50 = $best.p50;
                                           p99 = $best.p99; errors = $best.errors; MB = $mem; warn = $warn }
            $e = if ($best.errors) { "  ERRORS $($best.errors)" } else { "" }
            Write-Host ("  {0,-6} {1,-6} {2,-9} {3,10:N0} req/s   p50 {4,6} us   p99 {5,7} us   peak {6,6} MB{7}" -f $k, $tm, $m, $best.rps, $best.p50, $best.p99, $mem, $e)
        }
        Stop-Process $p -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }
}

# ---- ranked tables ----
foreach ($tm in $Threads) {
    foreach ($m in $Modes) {
        $rows = $results | Where-Object { $_.Threads -eq $tm -and $_.Mode -eq $m } | Sort-Object rps -Descending
        if (-not $rows) { continue }
        $top = [double]$rows[0].rps
        Write-Host "`n$tm-threaded, $m"
        Write-Host ("  {0,-9} {1,12} {2,7} {3,10} {4,10} {5,9}" -f "Language", "req/s", "vs best", "p50 us", "p99 us", "peak MB")
        foreach ($r in $rows) {
            $rel = if ($top -gt 0) { "{0:N2}x" -f ($r.rps / $top) } else { "-" }
            $flag = if ($r.errors) { "  (errors: $($r.errors))" } else { "" }
            Write-Host ("  {0,-9} {1,12:N0} {2,7} {3,10:N0} {4,10:N0} {5,9}{6}" -f $r.Language, $r.rps, $rel, $r.p50, $r.p99, $r.MB, $flag)
        }
    }
}
Write-Host "`nNOTE: Windows loopback is slow per packet, and much slower with a third-party firewall"
Write-Host "(ESET, Norton, ...) inspecting it. keepalive and close mostly measure that; pipeline"
Write-Host "sends 16 requests per packet and shows the servers' own speed."
