param([Parameter(Mandatory)] [string] $Bundle)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Bundle).Path
$state = Join-Path $root 'state'
if (Test-Path -LiteralPath $state) { throw 'Use a fresh bundle extraction for acceptance.' }
$listeners = @()
try {
    $ports = foreach ($index in 1..3) {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listeners += $listener
        $listener.Start()
        $listener.LocalEndpoint.Port
    }
}
finally { foreach ($listener in $listeners) { $listener.Stop() } }

$shell = Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
$info = [Diagnostics.ProcessStartInfo]::new($shell)
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardOutput = $info.RedirectStandardError = $true
$info.WorkingDirectory = $root
$info.Environment['PATH'] = "$env:WINDIR\System32;$env:WINDIR"
foreach ($argument in @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'Start-BlueMoon.ps1'),
        '-GamePort', $ports[0], '-HostPort', $ports[1], '-DatabasePort', $ports[2])) {
    $info.ArgumentList.Add([string]$argument)
}
$launcher = [Diagnostics.Process]::Start($info)
$launcherOut = $launcher.StandardOutput.ReadToEndAsync()
$launcherErr = $launcher.StandardError.ReadToEndAsync()
$base = "http://127.0.0.1:$($ports[1])"
$statusPath = Join-Path $state 'run.json'

function Wait-Ready([string] $PreviousStream) {
    $deadline = [DateTime]::UtcNow.AddSeconds(520)
    while ([DateTime]::UtcNow -lt $deadline -and !$launcher.HasExited) {
        try {
            $status = Get-Content -LiteralPath $statusPath -Raw -ErrorAction Stop | ConvertFrom-Json
            if ($status.streamId -ne $PreviousStream -and (Invoke-RestMethod "$base/api/readiness/game" -TimeoutSec 1).ready) {
                return $status
            }
        } catch { }
        Start-Sleep -Milliseconds 300
    }
    if ($launcher.HasExited) { Write-Host $launcherErr.GetAwaiter().GetResult() }
    throw "Bundle did not become ready. See $state."
}

function Read-RoundCount {
    $db = Join-Path $root 'runtime/mariadb/mariadb-11.4.13-winx64/bin/mariadb.exe'
    $client = Join-Path $state 'db-app.ini'
    $count = & $db "--defaults-extra-file=$client" --database=bluemoon --batch --skip-column-names --execute='SELECT COUNT(*) FROM round'
    if ($LASTEXITCODE -ne 0) { throw 'MariaDB round probe failed.' }
    return [int]$count
}

try {
    $initial = Wait-Ready
    $daemon = Get-CimInstance Win32_Process -Filter "ProcessId=$($initial.daemonPid)"
    if ($daemon.ExecutablePath -ne (Join-Path $root 'runtime/byond/bin/dd.exe')) { throw 'DreamDaemon is outside the bundle.' }
    $summary = Invoke-RestMethod "$base/api/game/summary"
    $rounds = Read-RoundCount
    if ($summary.nodes -lt 1 -or $initial.schema -ne '4.7' -or $rounds -lt 1) { throw 'Missing APCs or database round.' }
    $developerRoute = Invoke-WebRequest "$base/api/benchmark-suite" -SkipHttpErrorCheck
    if ($developerRoute.StatusCode -ne 404) { throw 'Deployment profile exposes benchmark endpoints.' }

    Stop-Process -Id $initial.hostPid
    $recovered = Wait-Ready $initial.streamId
    $newSummary = Invoke-RestMethod "$base/api/game/summary"
    if ($recovered.daemonPid -ne $initial.daemonPid -or $recovered.dbPid -ne $initial.dbPid -or
        $recovered.hostPid -eq $initial.hostPid -or $newSummary.nodes -lt 1 -or (Read-RoundCount) -ne $rounds) {
        throw 'Host recovery did not preserve the game and database round.'
    }
    [IO.File]::WriteAllText((Join-Path $state 'stop.request'), 'stop')
    if (!$launcher.WaitForExit(50000) -or $launcher.ExitCode -ne 0) { throw 'Bundle did not stop cleanly.' }
    Write-Host $launcherOut.GetAwaiter().GetResult()
    $gameLogs = @(Join-Path $state 'game.out.log'; Join-Path $state 'game.err.log')
    $gameLogs += Get-ChildItem -LiteralPath (Join-Path $root 'game/data/logs') -Filter '*.log' -Recurse |
        Select-Object -ExpandProperty FullName
    if (!(Select-String -LiteralPath $gameLogs -SimpleMatch 'Shutdown complete' -Quiet)) {
        throw 'DreamDaemon did not run the game shutdown path.'
    }
    Write-Host "BLUEMOON_WINDOWS_PASS nodes=$($summary.nodes) restored=$($newSummary.nodes) rounds=$rounds"
}
finally {
    if (!$launcher.HasExited -and (Test-Path -LiteralPath $state)) {
        [IO.File]::WriteAllText((Join-Path $state 'stop.request'), 'stop')
        if (!$launcher.WaitForExit(50000)) { $launcher.Kill($true); $launcher.WaitForExit() }
    }
    $launcher.Dispose()
}
