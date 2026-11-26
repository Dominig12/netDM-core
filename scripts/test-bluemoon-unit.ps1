param(
    [Parameter(Mandatory)] [string] $BuildDirectory,
    [Parameter(Mandatory)] [string] $BridgeDll,
    [Parameter(Mandatory)] [string] $TestList,
    [string] $HostDll = 'src/NetDM.Host/bin/Release/net10.0/NetDM.Host.dll',
    [string] $DreamDaemon = (Join-Path ${env:ProgramFiles(x86)} 'BYOND/bin/dd.exe'),
    [string] $LogDirectory = 'netdm-p3',
    [int] $TimeoutMinutes = 20,
    # Map config JSON for UNIT_TESTS builds without LOWMEMORYMODE, relative to the build directory.
    [string] $Map,
    [switch] $NoObserve,
    [switch] $NetworkCameras
)
# Runs focused BlueMoon unit tests in a real DreamDaemon with a live netDM Host on a private pipe.
$ErrorActionPreference = 'Stop'
$build = (Resolve-Path -LiteralPath $BuildDirectory).Path
$hostPath = (Resolve-Path -LiteralPath $HostDll).Path
$list = (Resolve-Path -LiteralPath $TestList).Path
Copy-Item -LiteralPath $BridgeDll -Destination (Join-Path $build 'netdm_byond.dll') -Force
$pipe = 'netdm-unit-' + [guid]::NewGuid().ToString('N')
$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()

function Start-Logged([string] $File, [string[]] $Arguments, [hashtable] $Environment, [string] $Directory, [string] $Log) {
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $info.WorkingDirectory = $Directory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($key in $Environment.Keys) { $info.Environment[$key] = $Environment[$key] }
    $process = [Diagnostics.Process]::Start($info)
    return @{ Process = $process; Out = $process.StandardOutput.ReadToEndAsync(); Err = $process.StandardError.ReadToEndAsync(); Log = $Log }
}

$common = @{ NETDM_IPC_MODE = 'v1'; NETDM_PIPE_NAME = $pipe }
$LogDirectory = "$LogDirectory-" + (Get-Date -Format 'HHmmss')
$logRoot = Join-Path $build "data/logs/$LogDirectory"
# The research ledger journal of this run's round lies next to its logs.
$hostEnv = $common + @{ NetDM__HttpPort = "$port"; Logging__LogLevel__Default = 'Warning'; Logging__LogLevel__NetDM = 'Information'
    NetDM__ResearchDirectory = (Join-Path $logRoot 'research'); NetDM__NetLife = '1' }
$host_ = $null
try {
    $host_ = Start-Logged (Get-Command dotnet).Source @($hostPath) $hostEnv (Split-Path $hostPath) 'host.log'
    $ready = $false
    for ($i = 0; $i -lt 100 -and -not $ready; $i++) {
        try { $ready = (Invoke-WebRequest "http://127.0.0.1:$port/api/readiness/ipc" -SkipHttpErrorCheck -TimeoutSec 2).StatusCode -eq 200 }
        catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $ready) { throw 'Host IPC did not become ready.' }
    $gameEnv = $common + @{ NETDM_SERVER_ID = 'unit-p3'; NETDM_TEST_HTTP = "http://127.0.0.1:$port" }
    if (-not $NoObserve) { $gameEnv.NETDM_OBSERVE = '1' }
    if ($NetworkCameras) { $gameEnv.NETDM_CAMERA_MODE = 'authoritative' }
# The dive layer is opt-in per round: the automated test round enables it explicitly.
$gameEnv.NETDM_NET_IMMERSION = '1'
    $params = "log-directory=$LogDirectory&unit-test-list=$list"
    if ($Map) { $params += "&unit-test-map=$Map" }
    $started = Get-Date
    $game = Start-Logged $DreamDaemon @('tgstation.m.dmb', '-close', '-trusted', '-verbose', '-params', $params) $gameEnv $build 'dd.log'
    $trace = Join-Path $build 'netdm-unit-trace.log'
    Set-Content -LiteralPath $trace -Value ''
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while (-not $game.Process.WaitForExit(2000)) {
        if ((Get-Date) -gt $deadline) { $game.Process.Kill($true); Write-Host 'DreamDaemon timed out.'; break }
        try {
            $ready = Invoke-WebRequest "http://127.0.0.1:$port/api/readiness/game" -SkipHttpErrorCheck -TimeoutSec 2
            $summary = Invoke-RestMethod "http://127.0.0.1:$port/api/game/summary" -TimeoutSec 2
            Add-Content -LiteralPath $trace -Value ("{0:HH:mm:ss} ready={1} {2} nodes={3} links={4} radios={5}" -f (Get-Date),
                $ready.StatusCode, $ready.Content, $summary.nodes, $summary.links, $summary.radios)
        }
        catch { Add-Content -LiteralPath $trace -Value ("{0:HH:mm:ss} probe failed: {1}" -f (Get-Date), $_.Exception.Message) }
    }
    Write-Host ("DreamDaemon finished in {0:N0} s, exit {1}" -f ((Get-Date) - $started).TotalSeconds, $game.Process.ExitCode)
    $ddOut = $game.Out.GetAwaiter().GetResult() + $game.Err.GetAwaiter().GetResult()
    Set-Content -LiteralPath (Join-Path $build 'netdm-unit-dd.log') -Value $ddOut
    $ddOut -split "`n" | Where-Object { $_ -match 'netDM|PASS|FAIL|unit_test|REASON' } | Select-Object -Last 60
    $results = Join-Path $logRoot 'unit_tests.json'
    if (Test-Path -LiteralPath $results) {
        $json = Get-Content -LiteralPath $results -Raw | ConvertFrom-Json -AsHashtable
        foreach ($name in $json.Keys) {
            $entry = $json[$name]
            Write-Host ("RESULT {0} status={1} wall={2} {3}" -f $name, $entry.status, $entry.wall, $entry.message)
        }
    }
    else { Write-Host 'unit_tests.json is missing.' }
    Write-Host ("clean_run.lk: {0}" -f (Test-Path -LiteralPath (Join-Path $logRoot 'clean_run.lk')))
    $summary = Invoke-RestMethod "http://127.0.0.1:$port/api/game/summary" -TimeoutSec 5 -ErrorAction SilentlyContinue
    if ($summary) { Write-Host ("Host summary after run: " + ($summary | ConvertTo-Json -Compress -Depth 4)) }
}
finally {
    if ($null -ne $host_) {
        if (-not $host_.Process.HasExited) { $host_.Process.Kill($true); $host_.Process.WaitForExit() }
        $hostOut = $host_.Out.GetAwaiter().GetResult() + $host_.Err.GetAwaiter().GetResult()
        Set-Content -LiteralPath (Join-Path $build 'netdm-unit-host.log') -Value $hostOut
    }
    Write-Host "Logs: $logRoot"
}
