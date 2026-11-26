# P10 load: how much the Host's background work costs on a station-sized map.
#
# A chain of APCs (one area each) carries shell devices. Every device offers network.shell, network.diagnostics
# and network.query; a watcher next to it watches its ports and its directory, and the device runs four
# rules (ports, a service type, a trace across the map, its own jobs). Then APC power flips at each rate
# in -Rates for -Seconds, and the Host's own timings of each background stage are read back per phase.
param(
    [string] $HostDll = 'src/NetDM.Host/bin/Release/net10.0/NetDM.Host.dll',
    [int] $Apcs = 200,
    [int] $Devices = 16,
    [int] $Seconds = 30,
    [int[]] $Rates = @(0, 2, 20),
    # Which of the four rules every device runs: 0 ports, 1 service type, 2 trace across the map, 3 own jobs.
    [int[]] $Rules = @(0, 1, 2, 3),
    [string] $Output
)
$ErrorActionPreference = 'Stop'
$work = [IO.Directory]::CreateTempSubdirectory('netdm-p10-').FullName
$pipe = 'netdm-p10-' + [guid]::NewGuid().ToString('N')
$portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$portProbe.Start()
$httpPort = $portProbe.LocalEndpoint.Port
$portProbe.Stop()
$launch = [Diagnostics.ProcessStartInfo]::new((Get-Command dotnet -ErrorAction Stop).Source)
$launch.ArgumentList.Add((Resolve-Path -LiteralPath $HostDll).Path)
$launch.UseShellExecute = $false
$launch.CreateNoWindow = $true
$launch.Environment['NETDM_IPC_MODE'] = 'v1'
$launch.Environment['NETDM_PIPE_NAME'] = $pipe
$launch.Environment['NETDM_SOCKET_PATH'] = Join-Path $work 'core.sock'
$launch.Environment['NetDM__HttpPort'] = "$httpPort"
$launch.Environment['Logging__LogLevel__Default'] = 'Error'
$launch.Environment['NetDM__ResearchDirectory'] = Join-Path $work 'research'

function Open-Stream {
    if ($IsWindows) {
        $client = [IO.Pipes.NamedPipeClientStream]::new('.', $pipe, 'InOut', 'Asynchronous')
        try { $client.Connect(250); return $client } catch { $client.Dispose(); throw }
    }
    $socket = [Net.Sockets.Socket]::new('Unix', 'Stream', 'Unspecified')
    try { $socket.Connect([Net.Sockets.UnixDomainSocketEndPoint]::new($launch.Environment['NETDM_SOCKET_PATH'])); return [Net.Sockets.NetworkStream]::new($socket, $true) }
    catch { $socket.Dispose(); throw }
}

function Send-Frame($Stream, [hashtable] $Fields) {
    $body = [Text.Encoding]::UTF8.GetBytes(($Fields | ConvertTo-Json -Depth 6 -Compress))
    $header = [BitConverter]::GetBytes([Net.IPAddress]::HostToNetworkOrder($body.Length))
    $Stream.Write($header, 0, 4)
    $Stream.Write($body, 0, $body.Length)
}

function Read-Frame($Stream) {
    $deadline = [Threading.CancellationTokenSource]::new(15000)
    try {
        $header = [byte[]]::new(4)
        [void]$Stream.ReadExactlyAsync($header, 0, 4, $deadline.Token).AsTask().GetAwaiter().GetResult()
        $length = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($header, 0))
        $body = [byte[]]::new($length)
        [void]$Stream.ReadExactlyAsync($body, 0, $length, $deadline.Token).AsTask().GetAwaiter().GetResult()
        return [Text.Encoding]::UTF8.GetString($body) | ConvertFrom-Json -AsHashtable
    }
    finally { $deadline.Dispose() }
}

function Assert-True($Condition, [string] $Context) { if (-not $Condition) { throw "Failed: $Context" } }
function Get-Digest([string] $Value) { [Convert]::ToHexString([Security.Cryptography.MD5]::HashData([Text.Encoding]::UTF8.GetBytes($Value))) }

function Invoke-Next([hashtable] $Fields) {
    $script:seq++
    $Fields.ownerFence = $session.session.ownerFence
    if (-not $Fields.streamId) { $Fields.streamId = $session.streamId }
    $Fields.commandSequence = "$script:seq"
    Send-Frame $owner $Fields
    return Read-Frame $owner
}

function Get-Status { (Invoke-RestMethod "http://127.0.0.1:$httpPort/api/research/status" -TimeoutSec 10).background }

# Every delivery waiting for DM is taken and acknowledged, as the bridge would. Returns them.
function Receive-All {
    $all = @()
    for ($page = 0; $page -lt 16; $page++) {
        $items = @((Invoke-Next @{ op = 'delivery_poll'; afterSequence = '0'; limit = 32 }).result.items)
        if ($items.Count -eq 0) { break }
        $all += $items
        $null = Invoke-Next @{ op = 'delivery_ack'; acks = @($items | ForEach-Object { @{ deliveryId = $_.deliveryId; applied = $true } }) }
        if ($items.Count -lt 32) { break }
    }
    return $all
}

$hostProcess = [Diagnostics.Process]::Start($launch)
$owner = $null
try {
    $hello = @{ id = 'hello'; op = 'hello'; protocolMajor = 1; protocolMinor = 1; serverId = 'p10-load'; roundEpoch = [guid]::NewGuid().ToString() }
    for ($attempt = 0; $attempt -lt 200 -and $null -eq $owner; $attempt++) {
        try {
            $owner = Open-Stream
            Send-Frame $owner $hello
            $session = Read-Frame $owner
            if (-not $session.ok) { $owner.Dispose(); $owner = $null; Start-Sleep -Milliseconds 50 }
        }
        catch {
            if ($null -ne $owner) { $owner.Dispose(); $owner = $null }
            if ($hostProcess.HasExited) { throw }
            Start-Sleep -Milliseconds 50
        }
    }
    Assert-True ($null -ne $owner) 'session'
    $script:seq = 0

    # The map: APC i stands alone in area i at x = i; neighbouring areas share a side, so APCs i and i+1 are wired.
    $facts = [Collections.Generic.List[hashtable]]::new()
    for ($i = 1; $i -le $Apcs; $i++) {
        $facts.Add(@{ op = 'add_node'; a = "$i"; x = $i; y = 1; z = 1 })
        $facts.Add(@{ op = 'area_span'; area = "$i"; x = $i; endX = $i; y = 1; z = 1 })
        $facts.Add(@{ op = 'area_apc'; a = "$i"; area = "$i"; x = $i; y = 1; z = 1 })
    }
    $chunks = [Collections.Generic.List[string]]::new()
    for ($start = 0; $start -lt $facts.Count; $start += 120) {
        $chunks.Add((ConvertTo-Json -InputObject @($facts.GetRange($start, [Math]::Min(120, $facts.Count - $start))) -Compress))
    }
    $begin = Invoke-Next @{ op = 'snapshot_begin'; snapshotId = 'load'; profile = 'apc_map'; chunkCount = $chunks.Count
        factCount = $facts.Count; factSequence = '0' }
    Assert-True ($begin.ok) "snapshot begin $($begin.error) $($begin.message)" 
    $digests = ''
    for ($index = 0; $index -lt $chunks.Count; $index++) {
        $digest = Get-Digest $chunks[$index]
        $digests += $digest
        $reply = Invoke-Next @{ op = 'snapshot_chunk'; snapshotId = 'load'; chunkIndex = $index; factsJson = $chunks[$index]; digest = $digest }
        Assert-True ($reply.ok) "snapshot chunk $index $($reply.error)"
    }
    Assert-True ((Invoke-Next @{ op = 'snapshot_commit'; snapshotId = 'load'; digest = (Get-Digest $digests) }).ok) 'snapshot commit'
    $ready = Invoke-Next @{ op = 'snapshot_ready'; snapshotId = 'load'; factSequence = '0' }
    Assert-True ($ready.ok) "snapshot ready $($ready.error)"
    $session['streamId'] = $ready.streamId
    $script:facts = 0

    # Doors to find, spread over the map.
    for ($j = 0; $j -lt 8; $j++) {
        $door = @{ endpoint = "door-$j"; generation = '1' }
        Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = "$([int](($j + 0.5) * $Apcs / 8) + 1)" } + $door)).ok) "door $j"
        Assert-True ((Invoke-Next (@{ op = 'service_register'; instance = "door.$j"; serviceType = 'machine.door'; schema = 'machine.door_state'; major = 1; minor = 0 } + $door)).ok) "door service $j"
    }
    $deviceNodes = @()
    for ($k = 0; $k -lt $Devices; $k++) {
        $node = [int](($k + 0.5) * $Apcs / $Devices) + 1
        $deviceNodes += $node
        $device = @{ endpoint = "sh-$k"; generation = '1' }
        $watcher = @{ endpoint = "w-$k"; generation = '1' }
        Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = "$node" } + $device)).ok) "device $k"
        Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = "$node" } + $watcher)).ok) "watcher $k"
        foreach ($service in @(@('netshell', 'network.shell', 'shell.output'), @('netdiag', 'network.diagnostics', 'network.link_state'), @('netquery', 'network.query', 'query.directory'))) {
            Assert-True ((Invoke-Next (@{ op = 'service_register'; instance = "$($service[0]).sh-$k"; serviceType = $service[1]; schema = $service[2]; major = 1; minor = 0 } + $device)).ok) "$($service[0]) $k"
        }
        Assert-True ((Invoke-Next (@{ op = 'subscribe'; key = "links-$k"; instance = "netdiag.sh-$k"; schema = 'network.link_state'; major = 1; projection = 'full'; mode = 'latest_value' } + $watcher)).ok) "watch links $k"
        Assert-True ((Invoke-Next (@{ op = 'subscribe'; key = "dir-$k"; instance = "netquery.sh-$k"; schema = 'query.directory'; major = 1; projection = 'full'; mode = 'latest_value' } + $watcher)).ok) "watch directory $k"
    }

    # Four rules per device; their bodies only read, so the measurement is the rules, not door traffic.
    $starts = 0
    for ($k = 0; $k -lt $Devices; $k++) {
        $far = (($deviceNodes[$k] + [int]($Apcs / 2) - 1) % $Apcs) + 1
        $all = @(
            "on links | where state -ne up`nself",
            "on services -type machine.door | count`nself",
            "on trace $far | select state hops`nself",
            "on jobs | where outcome -eq pending | count`nself")
        $chosen = @($Rules | ForEach-Object { $all[$_] })
        for ($r = 0; $r -lt $chosen.Count; $r++) {
            $null = Invoke-Next (@{ op = 'invoke'; instance = "netshell.sh-$k"; command = 'start'; idempotencyKey = "start-$k-$r"
                arguments = @{ name = "rule-$r"; source = $chosen[$r] }; trafficClass = 1; timeoutTicks = 120 } + @{ endpoint = "sh-$k"; generation = '1' })
        }
    }
    for ($poll = 0; $poll -lt 100 -and $starts -lt $Devices * $Rules.Count; $poll++) {
        $null = Invoke-Next @{ op = 'heartbeat'; snapshotId = 'load'; factSequence = "$script:facts" }
        $starts += @(Receive-All | Where-Object { $_.kind -ceq 'result' -and $_.command -ceq 'start' -and $_.payload.status -ceq 'started' }).Count
        Start-Sleep -Milliseconds 100
    }
    Assert-True ($starts -eq $Devices * $Rules.Count) "all rules started: $starts"
    Start-Sleep -Seconds 3
    $null = Receive-All

    $powered = @{}
    for ($i = 1; $i -le $Apcs; $i++) { $powered[$i] = $true }
    $random = [Random]::new(1410)
    $results = @()
    foreach ($rate in $Rates) {
        $null = Invoke-RestMethod -Method Post "http://127.0.0.1:$httpPort/api/research/background/reset" -TimeoutSec 10
        Start-Sleep -Milliseconds 100
        $hostProcess.Refresh()
        $cpuBefore = $hostProcess.TotalProcessorTime
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $flips = 0
        $received = 0
        $nextDrain = 0
        while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
            if ($rate -gt 0 -and $flips -lt $clock.Elapsed.TotalSeconds * $rate) {
                $apc = $random.Next(1, $Apcs + 1)
                $powered[$apc] = -not $powered[$apc]
                $script:facts++
                $reply = Invoke-Next @{ op = 'world_delta'; snapshotId = 'load'; factSequence = "$script:facts"
                    changes = @(@{ op = 'node_state'; a = "$apc"; powered = [int]$powered[$apc]; operational = 1 }) }
                Assert-True ($reply.ok) "world delta $($reply.error)"
                $flips++
                continue
            }
            if ($clock.ElapsedMilliseconds -ge $nextDrain) {
                $nextDrain = $clock.ElapsedMilliseconds + 200
                $null = Invoke-Next @{ op = 'heartbeat'; snapshotId = 'load'; factSequence = "$script:facts" }
                $received += @(Receive-All).Count
                continue
            }
            Start-Sleep -Milliseconds 5
        }
        $hostProcess.Refresh()
        $cpu = ($hostProcess.TotalProcessorTime - $cpuBefore).TotalMilliseconds
        $background = Get-Status
        $runner = (Invoke-Next @{ op = 'game_stats' }).game.runner
        $results += [ordered]@{
            rate = $rate; seconds = $Seconds; flips = $flips; deliveries = $received
            hostCpuPercent = [Math]::Round(100 * $cpu / ($clock.Elapsed.TotalMilliseconds * [Environment]::ProcessorCount), 2)
            hostCpuMsPerSecond = [Math]::Round($cpu / $clock.Elapsed.TotalSeconds, 1)
            programs = $background.programs; linkWatch = $background.linkWatch; directoryWatch = $background.directoryWatch
            commands = $background.commands
            packetTick = @{ startLagP95Ms = $runner.startLagP95Milliseconds; executionP95Ms = $runner.executionP95Milliseconds; maxExecutionMs = $runner.maxExecutionMilliseconds }
        }
        # Back to full power before the next phase, so every phase starts from the same map.
        $restore = @($powered.Keys | Where-Object { -not $powered[$_] })
        foreach ($apc in $restore) {
            $powered[$apc] = $true
            $script:facts++
            $null = Invoke-Next @{ op = 'world_delta'; snapshotId = 'load'; factSequence = "$script:facts"
                changes = @(@{ op = 'node_state'; a = "$apc"; powered = 1; operational = 1 }) }
        }
        Start-Sleep -Seconds 2
        $null = Receive-All
    }

    Write-Host ("{0,5} {1,6} {2,9} {3,8} | {4,26} | {5,22} | {6,22} | {7,14}" -f 'rate', 'flips', 'cpu ms/s', 'deliv', 'programs eval/fired p95/max us', 'links pub p95/max us', 'dir pub p95/max us', 'tick lag p95 ms')
    foreach ($row in $results) {
        $p = $row.programs
        Write-Host ("{0,5} {1,6} {2,9} {3,8} | {4,26} | {5,22} | {6,22} | {7,14}" -f $row.rate, $row.flips, $row.hostCpuMsPerSecond, $row.deliveries,
            "$($p.evaluations)/$($p.fired) $($p.timing.p95Us)/$($p.timing.maxUs)",
            "$($row.linkWatch.publications) $($row.linkWatch.timing.p95Us)/$($row.linkWatch.timing.maxUs)",
            "$($row.directoryWatch.publications) $($row.directoryWatch.timing.p95Us)/$($row.directoryWatch.timing.maxUs)",
            $row.packetTick.startLagP95Ms)
    }
    $report = [ordered]@{ apcs = $Apcs; devices = $Devices; rules = $Rules; programs = $Devices * $Rules.Count; phases = $results }
    if ($Output) { $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output -Encoding utf8 }
    Write-Host 'NETDM_P10_LOAD_DONE'
}
finally {
    if ($null -ne $owner) { $owner.Dispose() }
    if (-not $hostProcess.HasExited) { $hostProcess.Kill($true); $hostProcess.WaitForExit() }
    $hostProcess.Dispose()
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
