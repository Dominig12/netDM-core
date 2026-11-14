param(
    [Parameter(Mandatory)] [string] $BuildDirectory,
    [Parameter(Mandatory)] [string] $BridgeDll,
    [int] $GamePort = 51515,
    [int] $Minutes = 60,
    # Debug logs every IPC request id: use it to trace a lost reply.
    [string] $IpcLogLevel = 'Information',
    # 'std' restores the MinGW sleep_for idle wait of the bridge worker, for comparison.
    [string] $BridgeWait = 'win32',
    # Logs the bridge worker counters every two minutes. The status call costs most of a tick, so it is off by default.
    [switch] $BridgeTrace,
    # 'authoritative': the Host research ledger takes over research on the stand (P5 canary).
    [ValidateSet('shadow', 'authoritative')] [string] $ResearchMode = 'shadow',
    # 'authoritative': PDA messages go through message servers over netDM (P7).
    [ValidateSet('shadow', 'authoritative')] [string] $MessengerMode = 'shadow',
    # 'authoritative': radio speech reaches telecomms receivers over netDM (P8).
    [ValidateSet('shadow', 'authoritative')] [string] $RadioMode = 'shadow',
    [switch] $NetworkCameras,
    [string] $HostDll = 'src/NetDM.Host/bin/Release/net10.0/NetDM.Host.dll',
    [string] $DreamDaemon = (Join-Path ${env:ProgramFiles(x86)} 'BYOND/bin/dd.exe')
)
# Local manual stand: netDM Host plus a BlueMoon world with Observe, for checks in a real BYOND client.
# Connect DreamSeeker to byond://127.0.0.1:<GamePort>. Stop by creating <BuildDirectory>/netdm-stand.stop
# or after -Minutes. Logs: netdm-stand-*.log and data/logs in the build directory. Requires PowerShell 7.4+.
$ErrorActionPreference = 'Stop'
$build = (Resolve-Path -LiteralPath $BuildDirectory).Path
$hostPath = (Resolve-Path -LiteralPath $HostDll).Path
Copy-Item -LiteralPath $BridgeDll -Destination (Join-Path $build 'netdm_byond.dll') -Force
$stop = Join-Path $build 'netdm-stand.stop'
Remove-Item -LiteralPath $stop -ErrorAction SilentlyContinue
$pipe = 'netdm-stand-' + [guid]::NewGuid().ToString('N')
$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$probe.Start(); $httpPort = $probe.LocalEndpoint.Port; $probe.Stop()
$common = @{ NETDM_IPC_MODE = 'v1'; NETDM_PIPE_NAME = $pipe }
$log = { param($name) Join-Path $build "netdm-stand-$name.log" }
$hostProcess = $null
$game = $null
try {
    $hostProcess = Start-Process (Get-Command dotnet).Source -ArgumentList "`"$hostPath`"" -WorkingDirectory (Split-Path $hostPath) `
        -Environment ($common + @{ NetDM__HttpPort = "$httpPort"; 'Logging__LogLevel__Microsoft.AspNetCore' = 'Warning'
            # One research journal per round; a restarted Host of the same round replays it.
            NetDM__ResearchDirectory = (Join-Path $build 'data/netdm-research')
            Logging__Console__FormatterOptions__TimestampFormat = 'HH:mm:ss.fff '
            'Logging__LogLevel__NetDM.Host.Transport.PipeHostedService' = $IpcLogLevel }) -NoNewWindow -PassThru `
        -RedirectStandardOutput (& $log 'host') -RedirectStandardError (& $log 'host-err')
    $game = Start-Process $DreamDaemon -ArgumentList 'tgstation.m.dmb', "$GamePort", '-trusted', '-invisible' `
        -WorkingDirectory $build -Environment ($common + @{ NETDM_OBSERVE = '1'; NETDM_SERVER_ID = 'stand'; NETDM_RESEARCH_MODE = $ResearchMode
            NETDM_MESSENGER_MODE = $MessengerMode
            NETDM_RADIO_MODE = $RadioMode
            NETDM_CAMERA_MODE = $(if ($NetworkCameras) { 'authoritative' } else { 'legacy' })
            NETDM_BRIDGE_WAIT = $BridgeWait; NETDM_BRIDGE_TRACE = $(if ($BridgeTrace) { '1' } else { '0' }) }) -NoNewWindow -PassThru `
        -RedirectStandardOutput (& $log 'dd') -RedirectStandardError (& $log 'dd-err')
    Write-Host "STAND_READY game=byond://127.0.0.1:$GamePort host=http://127.0.0.1:$httpPort stop=$stop"
    $deadline = (Get-Date).AddMinutes($Minutes)
    while (-not $game.HasExited -and -not (Test-Path -LiteralPath $stop) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
    }
    Write-Host "STAND_STOPPING exited=$($game.HasExited)"
}
finally {
    foreach ($process in $game, $hostProcess) {
        if ($null -ne $process -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    }
}
