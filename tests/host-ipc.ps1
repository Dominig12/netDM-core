param([string] $HostDll = 'src/NetDM.Host/bin/Release/net10.0/NetDM.Host.dll')
$ErrorActionPreference = 'Stop'
$work = [IO.Directory]::CreateTempSubdirectory('netdm-ipc-').FullName
$pipe = 'netdm-test-' + [guid]::NewGuid().ToString('N')
$socketPath = Join-Path $work 'core.sock'
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
$launch.Environment['NETDM_SOCKET_PATH'] = $socketPath
$launch.Environment['NetDM__HttpPort'] = "$httpPort"
$launch.Environment['Logging__LogLevel__Default'] = 'Error'
$launch.Environment['NetDM__ResearchDirectory'] = Join-Path $work 'research'

function Open-Stream {
    if ($IsWindows) {
        $pipeClient = [IO.Pipes.NamedPipeClientStream]::new('.', $pipe, 'InOut', 'Asynchronous')
        try { $pipeClient.Connect(250); return $pipeClient }
        catch { $pipeClient.Dispose(); throw }
    }
    $socket = [Net.Sockets.Socket]::new('Unix', 'Stream', 'Unspecified')
    try {
        $socket.Connect([Net.Sockets.UnixDomainSocketEndPoint]::new($socketPath))
        return [Net.Sockets.NetworkStream]::new($socket, $true)
    }
    catch { $socket.Dispose(); throw }
}

function Send-Frame($Stream, [hashtable] $Fields, [switch] $Fragment) {
    $body = [Text.Encoding]::UTF8.GetBytes(($Fields | ConvertTo-Json -Depth 6 -Compress))
    $header = [BitConverter]::GetBytes([Net.IPAddress]::HostToNetworkOrder($body.Length))
    if ($Fragment) {
        $Stream.Write($header, 0, 1)
        $Stream.Write($header, 1, 3)
        $Stream.Write($body, 0, 3)
        $Stream.Write($body, 3, $body.Length - 3)
    }
    else {
        $Stream.Write($header, 0, $header.Length)
        $Stream.Write($body, 0, $body.Length)
    }
}

function Read-Frame($Stream) {
    $deadline = [Threading.CancellationTokenSource]::new(5000)
    try {
        $header = [byte[]]::new(4)
        [void]$Stream.ReadExactlyAsync($header, 0, 4, $deadline.Token).AsTask().GetAwaiter().GetResult()
        $length = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($header, 0))
        if ($length -lt 1 -or $length -gt 49152) { throw "Invalid response size $length" }
        $body = [byte[]]::new($length)
        [void]$Stream.ReadExactlyAsync($body, 0, $length, $deadline.Token).AsTask().GetAwaiter().GetResult()
        return [Text.Encoding]::UTF8.GetString($body) | ConvertFrom-Json -AsHashtable
    }
    finally { $deadline.Dispose() }
}

function Assert-True($Condition, [string] $Context) {
    if (-not $Condition) { throw "Failed: $Context" }
}

function Get-Digest([string] $Value) {
    return [Convert]::ToHexString([Security.Cryptography.MD5]::HashData([Text.Encoding]::UTF8.GetBytes($Value)))
}

function Assert-StartupRejected([string] $Path) {
    $launch.Environment['NETDM_SOCKET_PATH'] = $Path
    $contender = [Diagnostics.Process]::Start($launch)
    try { Assert-True ($contender.WaitForExit(5000)) 'conflicting socket startup rejected' }
    finally {
        if (-not $contender.HasExited) { $contender.Kill($true); $contender.WaitForExit() }
        $contender.Dispose()
    }
}

function Get-GameReadiness {
    $response = Invoke-WebRequest "http://127.0.0.1:$httpPort/api/readiness/game" -SkipHttpErrorCheck -TimeoutSec 5
    return @{ Status = [int]$response.StatusCode; Body = ($response.Content | ConvertFrom-Json -AsHashtable) }
}

function Invoke-Request([hashtable] $Fields, [string] $Sequence) {
    $Fields.ownerFence = $session.session.ownerFence
    if (-not $Fields.streamId) { $Fields.streamId = $session.streamId }
    $Fields.commandSequence = $Sequence
    Send-Frame $owner $Fields
    return Read-Frame $owner
}

$hostProcess = [Diagnostics.Process]::Start($launch)
$owner = $visitor = $null
try {
    for ($i = 0; $i -lt 100 -and $null -eq $owner; $i++) {
        try { $owner = Open-Stream }
        catch {
            if ($hostProcess.HasExited) { throw }
            Start-Sleep -Milliseconds 50
        }
    }
    Assert-True ($null -ne $owner) 'IPC endpoint ready'
    $hello = @{ id = 'hello'; op = 'hello'; protocolMajor = 1; protocolMinor = 0
        serverId = 'station-β'; roundEpoch = [guid]::NewGuid().ToString() }
    Send-Frame $owner $hello -Fragment
    $session = Read-Frame $owner
    Assert-True ($session.ok -and $session.session.maxFrameBytes -eq 49152 -and
        $session.session.serverId -ceq $hello.serverId -and $session.nextSequence -is [string]) 'fragmented UTF-8 hello'

    $visitor = Open-Stream
    Send-Frame $visitor @{ op = 'game_reset'; ownerFence = $session.session.ownerFence
        streamId = $session.streamId; commandSequence = '1' }
    Assert-True ((Read-Frame $visitor).error -ceq 'owner_required') 'cannot replay owner tokens on another connection'
    $visitor.Dispose()
    $visitor = Open-Stream
    Send-Frame $visitor $hello
    Assert-True ((Read-Frame $visitor).error -ceq 'owner_busy') 'single owner'
    $visitor.Dispose()
    $visitor = Open-Stream
    $unsupported = $hello.Clone()
    $unsupported.protocolMajor = 2
    Send-Frame $visitor $unsupported
    Assert-True ((Read-Frame $visitor).error -ceq 'unsupported_protocol') 'unsupported protocol rejected'
    $visitor.Dispose()

    $a = '9007199254740993'
    $b = '9007199254740994'
    Assert-True ((Invoke-Request @{ op = 'game_add_node'; a = 1 } '1').error -ceq 'invalid_json') 'numeric node ID rejected'
    $topology = @{ op = 'game_batch'; changes = @(@{ op = 'add_node'; a = $a },
            @{ op = 'add_node'; a = $b }, @{ op = 'connect'; a = $a; b = $b }) }
    $ack = Invoke-Request $topology '1'
    Assert-True ($ack.applied -eq 3 -and $ack.streamId -ceq $session.streamId) 'exact distinct IDs and stamped batch ACK'
    $duplicate = Invoke-Request $topology '1'
    Assert-True ($duplicate.error -ceq 'sequence_mismatch' -and $duplicate.nextSequence -ceq '2') 'duplicate command'
    Assert-True ((Invoke-Request @{ op = 'game_remove_node'; a = $b } '2').applied -eq 1) 'remove endpoint'
    Assert-True ((Invoke-Request @{ op = 'game_add_node'; a = $b } '3').error -ceq 'node_id_reused') 'no reuse in same runtime'

    $oldStream = $session.streamId
    $late = @{ id = 'late'; op = 'packet_send'; a = $a; b = $a; bytes = 96
        ownerFence = $session.session.ownerFence; streamId = $oldStream; commandSequence = '4' }
    Send-Frame $owner $late
    Send-Frame $owner @{ id = 'reset'; op = 'game_reset'; ownerFence = $session.session.ownerFence
        streamId = $oldStream; commandSequence = '5' }
    $replies = @{}
    foreach ($i in 1..2) {
        $reply = Read-Frame $owner
        $replies[$reply.id] = $reply
    }
    Assert-True ($replies.late.streamId -ceq $oldStream) 'async ACK keeps prior stream identity'
    Assert-True ($replies.reset.ok -and $replies.reset.streamId -cne $oldStream) 'reset rotates stream'
    $session['streamId'] = $replies.reset.streamId
    $stale = Invoke-Request @{ op = 'packet_cancel'; packetId = '1'; streamId = $oldStream } '6'
    Assert-True ($stale.error -ceq 'stream_changed') 'stale cancel rejected'
    Assert-True ((Invoke-Request @{ op = 'game_add_node'; a = $a } '6').applied -eq 1) 'reuse after epoch rotation'
    $page = (Invoke-Request @{ op = 'packet_receipts'; afterSequence = '0' } '7').receiptPage
    Assert-True ($page.latestSequence -ceq '0' -and $page.receipts.Count -eq 0) 'old packet cannot appear in new stream'

    $oversize = [BitConverter]::GetBytes([Net.IPAddress]::HostToNetworkOrder([int]49153))
    $owner.Write($oversize, 0, 4) # No payload. Rejection must happen at the header.
    $deadline = [Threading.CancellationTokenSource]::new(2000)
    try {
        $probe = [byte[]]::new(1)
        Assert-True ($owner.ReadAsync($probe, 0, 1, $deadline.Token).GetAwaiter().GetResult() -eq 0) 'oversize closes stream'
    }
    finally { $deadline.Dispose() }
    $owner.Dispose()
    $owner = Open-Stream
    Send-Frame $owner $hello
    $newSession = Read-Frame $owner
    Assert-True ($newSession.ok -and $newSession.session.ownerFence -cne $session.session.ownerFence -and
        $newSession.streamId -cne $session.streamId) 'disconnect releases owner and resets stream'
    $session = $newSession
    Assert-True ((Invoke-Request @{ op = 'game_stats' } '1').game.nodes -eq 0) 'reconnected graph is empty'

    $owner.Dispose()
    $hello.protocolMinor = 1
    $session = $null
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $owner = Open-Stream
        Send-Frame $owner $hello
        $session = Read-Frame $owner
        if ($session.ok) { break }
        $owner.Dispose()
        Start-Sleep -Milliseconds 50
    }
    Assert-True ($session.ok -and $session.session.protocolMinor -eq 1) 'staged protocol negotiation'
    $readiness = Get-GameReadiness
    Assert-True ($readiness.Status -eq 503 -and $readiness.Body.phase -ceq 'awaiting_snapshot') 'game unready before snapshot'
    $initialStream = $session.streamId
    $start = @{ op = 'snapshot_begin'; snapshotId = 'base'; chunkCount = 1; factCount = 1; factSequence = '10' }
    $facts = '[{"op":"add_node","a":"9007199254740993","x":1,"y":1,"z":1}]'
    $digest = Get-Digest $facts
    $chunk = @{ op = 'snapshot_chunk'; snapshotId = 'base'; chunkIndex = 0; factsJson = $facts; digest = $digest }
    $commit = @{ op = 'snapshot_commit'; snapshotId = 'base'; digest = (Get-Digest $digest) }
    Assert-True ((Invoke-Request $start '1').ok) 'staged begin'
    $badChunk = $chunk.Clone()
    $badChunk.digest = '0' * 32
    Assert-True (-not (Invoke-Request $badChunk '2').ok) 'bad digest rejects whole chunk'
    $badChunk = $chunk.Clone()
    $badChunk.chunkIndex = 1
    Assert-True (-not (Invoke-Request $badChunk '3').ok) 'out-of-order chunk rejected'
    Assert-True (-not (Invoke-Request $commit '4').ok) 'incomplete manifest rejected'
    Assert-True ((Invoke-Request $chunk '5').nextChunk -eq 1) 'valid retry chunk'
    Assert-True ((Invoke-Request $commit '6').ok) 'candidate validated'
    Assert-True ((Invoke-Request @{ op = 'game_stats' } '7').game.nodes -eq 0) 'commit keeps candidate invisible'
    $delta = @{ op = 'world_delta'; snapshotId = 'base'; factSequence = '12'
        changes = @(@{ op = 'add_node'; a = $b; x = 2; y = 1; z = 1 }) }
    Assert-True (-not (Invoke-Request $delta '8').ok) 'gap in fact sequence rejected'
    $delta.factSequence = '11'
    Assert-True ((Invoke-Request $delta '9').lastFactSequence -ceq '11') 'delta staged against base sequence'
    $ready = @{ op = 'snapshot_ready'; snapshotId = 'base'; factSequence = '11' }
    $published = Invoke-Request $ready '10'
    Assert-True ($published.ok -and $published.streamId -cne $initialStream) 'snapshot publication rotates stream'
    $session['streamId'] = $published.streamId
    Assert-True ((Invoke-Request @{ op = 'game_stats' } '11').game.nodes -eq 2) 'candidate and deltas published together'
    $readiness = Get-GameReadiness
    Assert-True ($readiness.Status -eq 200 -and $readiness.Body.phase -ceq 'ready' -and
        $readiness.Body.streamId -ceq $published.streamId -and $readiness.Body.lastFactSequence -ceq '11') 'publication makes game ready'

    # Fault matrix: a silent DM must not keep gameplay admission open.
    Start-Sleep -Milliseconds 5300
    $readiness = Get-GameReadiness
    Assert-True ($readiness.Status -eq 503 -and -not $readiness.Body.heartbeatFresh -and
        $readiness.Body.workersReady) "stale heartbeat makes game unready while workers live: $($readiness.Status) $($readiness.Body | ConvertTo-Json -Compress)"
    $unready = Invoke-Request @{ id = 'unready'; op = 'packet_send'; a = $a; b = $b; bytes = 96; requestId = 'unready' } '12'
    Assert-True ($unready.error -ceq 'game_not_ready') 'unready game refuses packet admission'

    $invalid = @{ op = 'world_delta'; snapshotId = 'base'; factSequence = '12'
        changes = @(@{ op = 'add_node'; a = '3'; x = 3; y = 1 }, @{ op = 'remove_node'; a = '4' }) }
    Assert-True (-not (Invoke-Request $invalid '13').ok) 'invalid mixed live delta rejected atomically'
    Assert-True ((Invoke-Request @{ op = 'heartbeat'; snapshotId = 'base'; factSequence = '11' } '14').ok) 'invalid live delta does not advance sequence'
    Assert-True ((Get-GameReadiness).Status -eq 200) 'heartbeat restores readiness'
    $replacement = @{ op = 'snapshot_begin'; snapshotId = 'bad'; chunkCount = 1; factCount = 1; factSequence = '11' }
    Assert-True ((Invoke-Request $replacement '15').ok) 'begin replacement'
    $readiness = Get-GameReadiness
    Assert-True ($readiness.Status -eq 503 -and $readiness.Body.phase -ceq 'syncing') 'replacement snapshot fences readiness'
    $orphan = '[{"op":"connect","a":"1","b":"2"}]'
    $orphanDigest = Get-Digest $orphan
    Assert-True ((Invoke-Request @{ op = 'snapshot_chunk'; snapshotId = 'bad'; chunkIndex = 0
        factsJson = $orphan; digest = $orphanDigest } '16').ok) 'stage orphan link'
    Assert-True (-not (Invoke-Request @{ op = 'snapshot_commit'; snapshotId = 'bad'
        digest = (Get-Digest $orphanDigest) } '17').ok) 'invalid references rejected at commit'
    Assert-True ((Invoke-Request @{ op = 'game_stats' } '18').game.nodes -eq 2) 'invalid replacement leaves previous graph intact'
    Assert-True ((Invoke-Request @{ op = 'endpoint_attach'; endpoint = 'early'; generation = '1'; a = $a } '19').error -ceq 'game_not_ready') 'services require a published runtime'

    # P2: one typed service end to end over the simulated wired network.
    $owner.Dispose()
    $session = $null
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $owner = Open-Stream
        Send-Frame $owner $hello
        $session = Read-Frame $owner
        if ($session.ok) { break }
        $owner.Dispose()
        Start-Sleep -Milliseconds 50
    }
    Assert-True ($session.ok) 'service session negotiated'
    $script:seq = 0
    function Invoke-Next([hashtable] $Fields) { $script:seq++; return Invoke-Request $Fields "$script:seq" }
    # APC 1 and 3 carry radio access points; access points never pair with each other.
    $mapFacts = @(
        @{ op = 'add_node'; a = '1'; x = 1; y = 1; z = 1; accessRange = 4 }
        @{ op = 'add_node'; a = '2'; x = 2; y = 1; z = 1 }
        @{ op = 'add_node'; a = '3'; x = 3; y = 1; z = 2; accessRange = 4 }
        @{ op = 'area_span'; area = '1'; x = 1; endX = 1; y = 1; z = 1 }
        @{ op = 'area_span'; area = '2'; x = 2; endX = 2; y = 1; z = 1 }
        @{ op = 'area_span'; area = '2'; x = 2; endX = 2; y = 1; z = 2 }
        @{ op = 'area_span'; area = '3'; x = 3; endX = 3; y = 1; z = 2 }
        @{ op = 'area_apc'; a = '1'; area = '1'; x = 1; y = 1; z = 1 }
        @{ op = 'area_apc'; a = '2'; area = '2'; x = 2; y = 1; z = 1 }
        @{ op = 'area_apc'; a = '2'; area = '2'; x = 2; y = 1; z = 2 }
        @{ op = 'area_apc'; a = '3'; area = '3'; x = 3; y = 1; z = 2 }
    )
    $chain = ConvertTo-Json -InputObject $mapFacts -Compress
    $chainDigest = Get-Digest $chain
    Assert-True ((Invoke-Next @{ op = 'snapshot_begin'; snapshotId = 'svc'; profile = 'apc_map'
        chunkCount = 1; factCount = $mapFacts.Count; factSequence = '0' }).ok) 'service snapshot begin'
    Assert-True ((Invoke-Next @{ op = 'snapshot_chunk'; snapshotId = 'svc'; chunkIndex = 0; factsJson = $chain; digest = $chainDigest }).ok) 'service snapshot chunk'
    Assert-True ((Invoke-Next @{ op = 'snapshot_commit'; snapshotId = 'svc'; digest = (Get-Digest $chainDigest) }).ok) 'service snapshot commit'
    $ready = Invoke-Next @{ op = 'snapshot_ready'; snapshotId = 'svc'; factSequence = '0' }
    Assert-True ($ready.ok) 'service snapshot ready'
    $session['streamId'] = $ready.streamId

    $sensor = @{ endpoint = 'sensor-1'; generation = '1' }
    $display = @{ endpoint = 'display-1'; generation = '1' }
    Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = '1' } + $sensor)).ok) 'attach sensor'
    Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = '3' } + $display)).ok) 'attach display'
    Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = '42' } + @{ endpoint = 'ghost'; generation = '1' })).error -ceq 'unknown_node') 'endpoint needs a world node'
    $register = @{ op = 'service_register'; instance = 'atmos-1'; serviceType = 'atmos.telemetry'; schema = 'atmos.sample'; major = 1; minor = 0 }
    Assert-True ((Invoke-Next ($register + $sensor)).ok) 'register service'
    Assert-True ((Invoke-Next ($register + $display)).error -ceq 'forbidden') 'instance owner is fixed'
    $subscribe = @{ op = 'subscribe'; key = 'main'; instance = 'atmos-1'; schema = 'atmos.sample'; projection = 'panel.v1'; mode = 'latest_value' }
    Assert-True ((Invoke-Next ($subscribe + $display + @{ major = 2 })).error -ceq 'unsupported_schema') 'no shared schema major'
    $subscribed = Invoke-Next ($subscribe + $display + @{ major = 1 })
    Assert-True ($subscribed.ok -and $subscribed.result.value.revision -ceq '1') 'subscribe display'

    $sample = @{ pressureKPa = 101.3; temperatureK = 293.15; sampleTick = 20150; quality = 'current' }
    $publish = @{ op = 'publish'; instance = 'atmos-1'; producerSequence = '1'; payload = $sample; trafficClass = 1; timeoutTicks = 30 }
    $published = Invoke-Next ($publish + $sensor)
    Assert-True ($published.ok -and $published.result.value.recipients -eq 1 -and $published.result.value.deliveries.Count -eq 1) 'publish accepted for fixed recipients'
    $deliveryId = $published.result.value.deliveries[0]
    $item = $null
    for ($poll = 0; $poll -lt 40 -and $null -eq $item; $poll++) {
        $page = (Invoke-Next @{ op = 'delivery_poll'; afterSequence = '0'; limit = 8 }).result
        $item = $page.items | Where-Object { $_.deliveryId -ceq $deliveryId } | Select-Object -First 1
        if ($null -eq $item) { Start-Sleep -Milliseconds 100 }
    }
    Assert-True ($null -ne $item -and $item.recipient -ceq 'display-1' -and $item.recipientGeneration -ceq '1' -and
        $item.payload.pressureKPa -eq 101.3 -and -not $item.payload.ContainsKey('sampleTick')) 'physical delivery presents the subscriber projection'
    $status = (Invoke-Next @{ op = 'delivery_status'; deliveryId = $deliveryId }).result.deliveries[0]
    Assert-True ($status.state -ceq 'transport_delivered') 'transport delivery is not application'
    $ack = (Invoke-Next @{ op = 'delivery_ack'; acks = @(@{ deliveryId = $deliveryId; applied = $true }) }).result.acks[0]
    Assert-True ($ack.status -ceq 'ok' -and $ack.state -ceq 'applied') 'application ACK'
    $ack = (Invoke-Next @{ op = 'delivery_ack'; acks = @(@{ deliveryId = $deliveryId; applied = $false; reason = 'late' }) }).result.acks[0]
    Assert-True ($ack.status -ceq 'duplicate' -and $ack.state -ceq 'applied') 'repeated ACK keeps the first outcome'
    Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = 'svc'; factSequence = '0' }).ok) 'heartbeat before publish'
    $again = Invoke-Next ($publish + $sensor)
    Assert-True ($again.ok -and $again.result.status -ceq 'duplicate' -and $again.result.value.messageId -ceq $published.result.value.messageId -and
        $again.result.value.deliveries.Count -eq 0) 'duplicate publish creates no delivery'
    $conflict = $publish.Clone()
    $conflict.payload = @{ pressureKPa = 5; temperatureK = 293.15; sampleTick = 20150; quality = 'current' }
    Assert-True ((Invoke-Next ($conflict + $sensor)).error -ceq 'idempotency_conflict') 'same sequence with other content'
    $raw = Invoke-Next @{ id = 'reserved'; op = 'packet_send'; a = '1'; b = '3'; bytes = 96; requestId = 'svc:1' }
    if ($raw.id -cne 'reserved') { $raw = Read-Frame $owner }
    Assert-True (-not $raw.ok) 'raw packets cannot use the delivery request prefix'
    Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = '3' } + @{ endpoint = 'display-1'; generation = '2' })).ok) 'recreate display as generation 2'
    $publish.producerSequence = '2'
    $orphaned = Invoke-Next ($publish + $sensor)
    Assert-True ($orphaned.ok -and $orphaned.result.value.recipients -eq 0) 'old generation subscriptions are gone'
    Assert-True ((Invoke-Next ($subscribe + $display + @{ major = 1 })).error -ceq 'stale_generation') 'old generation is fenced'
    Assert-True ((Invoke-Next @{ op = 'game_stats' }).game.services.endpoints -eq 2) 'service counters exposed'

    # P2.2: command/result with a separate result leg, policies and network discovery.
    function Wait-Item([string] $Recipient, [string] $Kind) {
        for ($poll = 0; $poll -lt 40; $poll++) {
            $page = (Invoke-Next @{ op = 'delivery_poll'; afterSequence = '0'; limit = 16 }).result
            $found = $page.items | Where-Object { $_.recipient -ceq $Recipient -and $_.kind -ceq $Kind } | Select-Object -First 1
            if ($null -ne $found) { return $found }
            Start-Sleep -Milliseconds 100
        }
        return $null
    }
    $console = @{ endpoint = 'console-1'; generation = '1' }
    $pump = @{ endpoint = 'pump-1'; generation = '1' }
    Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = '1' } + $console)).ok) 'attach console'
    Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = '3' } + $pump)).ok) 'attach pump'
    Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = '1'; domain = 'syndicate' } + @{ endpoint = 'rogue-1'; generation = '1' })).ok) 'attach rogue'
    Assert-True ((Invoke-Next (@{ op = 'service_register'; instance = 'pump-1'; serviceType = 'machine.control'; schema = 'machine.power_state'; major = 1; minor = 0 } + $pump)).ok) 'register machine control'
    Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = 'svc'; factSequence = '0' }).ok) 'heartbeat before invoke'
    $invoke = @{ op = 'invoke'; instance = 'pump-1'; command = 'set_power'; idempotencyKey = 'op-1'; arguments = @{ on = $true }; trafficClass = 1; timeoutTicks = 60 }
    $invoked = Invoke-Next ($invoke + $console)
    Assert-True ($invoked.ok -and $invoked.result.value.outcome -ceq 'pending') 'invoke accepted'
    $operationId = $invoked.result.value.operationId
    Assert-True ((Invoke-Next ($invoke + @{ endpoint = 'rogue-1'; generation = '1' })).error -ceq 'unknown_service') 'hidden from another domain'
    $command = Wait-Item 'pump-1' 'command'
    Assert-True ($null -ne $command -and $command.command -ceq 'set_power' -and $command.producer -ceq 'console-1' -and $command.payload.on) 'command crosses the network'
    $ack = (Invoke-Next @{ op = 'delivery_ack'; acks = @(@{ deliveryId = $command.deliveryId; applied = $true; result = @{ on = $true; changed = $true } }) }).result.acks[0]
    Assert-True ($ack.state -ceq 'applied') 'provider applies'
    $result = Wait-Item 'console-1' 'result'
    Assert-True ($null -ne $result -and $result.outcome -ceq 'applied' -and $result.payload.changed -and $result.operationId -ceq $operationId) 'result crosses the network back'
    Assert-True ((Invoke-Next @{ op = 'delivery_ack'; acks = @(@{ deliveryId = $result.deliveryId; applied = $true }) }).ok) 'initiator ACKs result'
    $status = (Invoke-Next (@{ op = 'command_status'; operationId = $operationId } + $console)).result.value
    Assert-True ($status.outcome -ceq 'applied' -and $status.result -ceq 'delivered') 'ResultDelivered'
    Assert-True ((Invoke-Next (@{ op = 'command_status'; operationId = $operationId } + $pump)).error -ceq 'unknown') 'other endpoints cannot read the operation'
    Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = 'svc'; factSequence = '0' }).ok) 'heartbeat before repeat'
    $repeat = Invoke-Next ($invoke + $console)
    Assert-True ($repeat.ok -and $repeat.result.status -ceq 'duplicate') 'repeated command is not applied twice'
    $scan = Invoke-Next (@{ op = 'discover'; serviceType = 'machine.control'; scanKey = 'scan-1'; trafficClass = 1 } + $console)
    Assert-True ($scan.ok -and $scan.result.value.probes -eq 1) 'discovery probes visible providers'
    $descriptor = Wait-Item 'console-1' 'result'
    Assert-True ($null -ne $descriptor -and $descriptor.command -ceq 'directory.describe' -and $descriptor.payload.instance -ceq 'pump-1' -and
        $descriptor.payload.commands -ceq 'set_power') 'descriptor returns over the network'

    # An area-only change cuts the cross-level leg while keeping the runtime, receipts and services.
    function Stage-Map([string] $UpdateId, [string] $Sequence, [string] $Facts, [int] $Count = $mapFacts.Count) {
        $base = @{ snapshotId = 'svc'; updateId = $UpdateId }
        $digest = Get-Digest $Facts
        Assert-True ((Invoke-Next ($base + @{ op = 'area_update_begin'; factSequence = $Sequence
            chunkCount = 1; factCount = $Count })).ok) 'begin staged area update'
        Assert-True ((Invoke-Next ($base + @{ op = 'area_update_chunk'; chunkIndex = 0
            factsJson = $Facts; digest = $digest })).ok) 'append staged area update'
        return $base + @{ op = 'area_update_commit'; digest = (Get-Digest $digest) }
    }
    $mapFacts[5].x = $mapFacts[5].endX = $mapFacts[9].x = 5
    $cut = Stage-Map 'cut' '1' (ConvertTo-Json -InputObject $mapFacts -Compress)
    Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = 'svc'; factSequence = '0' }).ok -and
        (Get-GameReadiness).Status -eq 200) 'heartbeat and old map remain live while staging'
    $packet = Invoke-Next @{ op = 'packet_send'; a = '1'; b = '3'; bytes = 96; requestId = 'before-cut' }
    Assert-True ($packet.submission.accepted) 'old cross-level route is live before commit'
    $receipt = $null
    for ($poll = 0; $poll -lt 40 -and $null -eq $receipt; $poll++) {
        $receipt = (Invoke-Next @{ op = 'packet_receipts'; afterSequence = '0' }).receiptPage.receipts |
            Where-Object { $_.requestId -ceq 'before-cut' } | Select-Object -First 1
        if ($null -eq $receipt) { Start-Sleep -Milliseconds 100 }
    }
    Assert-True ($receipt.status -eq 0 -and $receipt.successfulHops -eq 2) 'packet crosses two Z before the cut'
    $badCut = $cut.Clone()
    $badCut.digest = '0' * 32
    Assert-True (-not (Invoke-Next $badCut).ok -and
        (Get-GameReadiness).Body.lastFactSequence -ceq '0') 'invalid map commit leaves the old sequence active'
    $commit = Invoke-Next $cut
    Assert-True ($commit.ok -and $commit.lastFactSequence -ceq '1' -and $commit.streamId -ceq $ready.streamId) 'atomic area commit preserves stream'
    Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = 'svc'; factSequence = '1' }).ok) 'heartbeat uses the committed map sequence'
    Assert-True ((Invoke-Next @{ op = 'packet_send'; a = '1'; b = '3'; bytes = 96; requestId = 'after-cut' }).submission.rejection -eq 1) 'removed cross-level edge is unusable'
    $history = (Invoke-Next @{ op = 'packet_receipts'; afterSequence = '0' }).receiptPage.receipts
    Assert-True (($history | Where-Object { $_.packetId -ceq $receipt.packetId }).status -eq 0) 'packet receipt survives the commit'
    Assert-True ((Invoke-Next (@{ op = 'command_status'; operationId = $operationId } + $console)).result.value.result -ceq 'delivered') 'operation history survives the commit'

    $display.generation = '2'
    Assert-True ((Invoke-Next ($subscribe + $display + @{ major = 1 })).ok) 'subscribe before restoring the map'
    $restore = Stage-Map 'restore' '2' $chain
    Assert-True ((Invoke-Next $restore).ok) 'restore cross-level geometry on the same stream'
    Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = 'svc'; factSequence = '2' }).ok) 'heartbeat after map restore'
    $publish.producerSequence = '3'
    Assert-True ((Invoke-Next ($publish + $sensor)).result.value.recipients -eq 1) 'service and subscription survive restore'
    Assert-True ($null -ne (Wait-Item 'display-1' 'event')) 'restored route delivers to the existing subscriber'

    # P3.3: DM reports position and mounting of devices; the Host derives wired or radio attachment.
    function Send-Devices([string] $Sequence, [object[]] $Facts) {
        return Invoke-Next @{ op = 'world_delta'; snapshotId = 'svc'; factSequence = $Sequence; changes = $Facts }
    }
    function Get-MapNode([int] $Z, [string] $Id) {
        $map = Invoke-RestMethod "http://127.0.0.1:$httpPort/api/game/map?z=$Z" -TimeoutSec 5
        return $map.nodes | Where-Object { $_.id -ceq $Id } | Select-Object -First 1
    }
    function Wait-Receipt([string] $RequestId) {
        $after = '0'
        for ($poll = 0; $poll -lt 80; $poll++) {
            $page = (Invoke-Next @{ op = 'packet_receipts'; afterSequence = $after }).receiptPage
            $found = $page.receipts | Where-Object { $_.requestId -ceq $RequestId } | Select-Object -First 1
            if ($null -ne $found) { return $found }
            if ($page.receipts.Count -gt 0) { $after = $page.nextSequence } else { Start-Sleep -Milliseconds 100 }
        }
        return $null
    }
    function Send-DevicePacket([string] $Sequence, [string] $RequestId) {
        Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = 'svc'; factSequence = $Sequence }).ok) "heartbeat at $Sequence"
        return (Invoke-Next @{ op = 'packet_send'; a = '50'; b = '51'; bytes = 96; requestId = $RequestId }).submission
    }
    $fixed = @{ op = 'device_state'; a = '50'; x = 1; y = 1; z = 1; mount = 'fixed'; anchored = 1; range = 3 }
    $mobile = @{ op = 'device_state'; a = '51'; x = 3; y = 1; z = 2; mount = 'mobile'; anchored = 1; range = 3 }
    $attached = Send-Devices '3' @($fixed, $mobile)
    Assert-True ($attached.ok -and $attached.lastFactSequence -ceq '3') 'device facts advance the fact sequence'
    $kinds = @{}
    foreach ($device in $attached.result.devices) { $kinds[$device.a] = $device }
    Assert-True ($kinds['50'].attachment -ceq 'wired' -and $kinds['50'].apc -ceq '1' -and
        $kinds['51'].attachment -ceq 'radio') 'Host derives wired and radio attachment'
    Assert-True ((Invoke-Next @{ op = 'endpoint_attach'; a = '50'; endpoint = 'device-50'; generation = '1' }).ok) 'endpoint on a device node'
    Assert-True ((Send-DevicePacket '3' 'device-wired').accepted) 'wired device reaches a radio device'
    Assert-True ((Wait-Receipt 'device-wired').successfulHops -eq 4) 'wired port, APC chain and access point'

    $anchoredFixed = $fixed.Clone()
    $fixed.anchored = 0
    $unanchored = Send-Devices '4' @($fixed)
    $node = Get-MapNode 1 '50'
    Assert-True ($unanchored.result.devices[0].attachment -ceq 'radio' -and $node.links -eq 0 -and $node.radioEnabled) 'unanchor drops the wired port for radio'
    Assert-True ((Send-DevicePacket '4' 'device-radio').accepted -and (Wait-Receipt 'device-radio').successfulHops -eq 4) 'unanchored device uses the access point'

    $apcCollision = Send-Devices '5' @(@{ op = 'device_state'; a = '2'; x = 2; y = 1; z = 1; mount = 'fixed'; anchored = 1; range = 3 })
    $mixed = Send-Devices '5' @(@{ op = 'move_node'; a = '2'; x = 2; y = 1; z = 1 }, $anchoredFixed)
    $rawDevice = Send-Devices '5' @(@{ op = 'move_node'; a = '50'; x = 2; y = 1; z = 1 })
    $unknown = Send-Devices '5' @(@{ op = 'device_remove'; a = '77' })
    $noRange = Send-Devices '5' @(@{ op = 'device_state'; a = '60'; x = 1; y = 1; z = 1; mount = 'fixed'; anchored = 1 })
    $noLevel = Send-Devices '5' @(@{ op = 'device_state'; a = '60'; x = 1; y = 1; mount = 'fixed'; anchored = 1; range = 3 })
    $strayPeer = Send-Devices '5' @(@{ op = 'move_node'; a = '2'; b = '999'; x = 2; y = 1; z = 1 })
    $rawAccess = Send-Devices '5' @(@{ op = 'set_forwarding'; a = '2'; role = 'relay'; accessRange = 4 })
    Assert-True (-not $apcCollision.ok -and -not $mixed.ok -and -not $rawDevice.ok -and -not $unknown.ok -and
        -not $noRange.ok -and -not $noLevel.ok -and -not $strayPeer.ok -and -not $rawAccess.ok) 'invalid device deltas are rejected'
    Assert-True ((Invoke-Next @{ op = 'game_stats' }).game.nodes -eq 5) 'rejected deltas leave the graph intact'
    Assert-True ((Send-Devices '5' @($anchoredFixed)).result.devices[0].attachment -ceq 'wired') 'rejections do not consume the fact sequence'

    # Without the area_apc of APC 1 its zone has no wired port: the device follows atomically.
    $unserved = @($mapFacts | Where-Object { -not ($_.op -ceq 'area_apc' -and $_.a -ceq '1') })
    $unservedJson = ConvertTo-Json -InputObject $unserved -Compress
    Assert-True ((Invoke-Next (Stage-Map 'unserved' '6' $unservedJson $unserved.Count)).ok) 'commit map without APC 1 service'
    $node = Get-MapNode 1 '50'
    Assert-True ($node.links -eq 0 -and $node.radioEnabled) 'device falls back to radio in the map commit'
    Assert-True ((Send-DevicePacket '6' 'device-unserved').rejection -eq 1) 'a non-forwarding APC is not a transit hop'
    Assert-True ((Invoke-Next (Stage-Map 'served' '7' $chain)).ok) 'restore APC 1 service'
    $node = Get-MapNode 1 '50'
    Assert-True ($node.links -eq 1 -and -not $node.radioEnabled) 'restored map rewires the device'
    Assert-True ((Send-DevicePacket '7' 'device-rewired').accepted -and (Wait-Receipt 'device-rewired').successfulHops -eq 4) 'rewired route'

    # Bounded topology view for the session owner: flags 1 relay, 2 radio, 4 access point, 8 device, 16 APC.
    $lower = (Invoke-Next @{ op = 'topology_view'; z = 1 }).result
    $flags = @{}
    foreach ($row in $lower.nodes) { $flags[$row[0]] = $row[3] }
    $edgeKeys = @($lower.edges | ForEach-Object { "$($_[0])-$($_[1])" })
    Assert-True ($flags['1'] -eq 21 -and $flags['2'] -eq 17 -and $flags['50'] -eq 8 -and -not $lower.truncated) 'view flags APCs, access points and devices'
    Assert-True (($edgeKeys -contains '1-2') -and ($edgeKeys -contains '1-50') -and $lower.radio.Count -eq 0) 'view shows wired links'
    $upper = (Invoke-Next @{ op = 'topology_view'; z = 2 }).result
    Assert-True (($upper.nodes | Where-Object { $_[0] -ceq '51' })[3] -eq 10 -and
        (@($upper.radio | ForEach-Object { "$($_[0])-$($_[1])" }) -contains '51-3')) 'view shows radio associations of devices'
    Assert-True ((Invoke-Next @{ op = 'topology_view'; z = 1; sinceVersion = $lower.version }).result.unchanged) 'same version is unchanged'
    $cut = (Invoke-Next @{ op = 'topology_view'; z = 1; maxNodes = 1 }).result
    Assert-True ($cut.truncated -and $cut.nodes.Count -eq 1) 'view is bounded'

    # P3.5: power and operational state. An inactive APC loses its links and access point; its wired
    # device keeps the port silent without a radio bypass. Flags 32 without power, 64 not operational.
    function Get-View([int] $Z) {
        $view = (Invoke-Next @{ op = 'topology_view'; z = $Z }).result
        $view.flagsById = @{}
        foreach ($row in $view.nodes) { $view.flagsById[$row[0]] = $row[3] }
        $view.downKeys = @($view.down | ForEach-Object { "$($_[0])-$($_[1])" })
        return $view
    }
    $apcDown = @{ op = 'node_state'; a = '1'; powered = 0; operational = 1 }
    $down = Send-Devices '8' @($apcDown)
    $port = $down.result.devices | Where-Object { $_.a -ceq '50' }
    Assert-True ($down.ok -and $port.attachment -ceq 'wired' -and $port.apc -ceq '1' -and -not $port.live) 'APC without power silences its wired port'
    $apcNode = Get-MapNode 1 '1'
    $node = Get-MapNode 1 '50'
    Assert-True ($apcNode.links -eq 0 -and -not $apcNode.radioEnabled -and $node.links -eq 0 -and -not $node.radioEnabled) 'inactive APC has no links, no access point and no radio bypass'
    $view = Get-View 1
    Assert-True ($view.flagsById['1'] -eq 49 -and $view.flagsById['50'] -eq 8 -and $view.version -ne $lower.version -and
        ($view.downKeys -contains '1-2') -and ($view.downKeys -contains '50-1')) 'view flags the APC without power and lists silent links'
    Assert-True (-not (Send-DevicePacket '8' 'device-apc-down').accepted) 'no path through an APC without power'

    Assert-True ((Invoke-Next (Stage-Map 'down-map' '9' $chain)).ok) 'map update while an APC is down'
    $apcNode = Get-MapNode 1 '1'
    Assert-True ($apcNode.links -eq 0 -and -not $apcNode.radioEnabled -and (Get-MapNode 1 '50').links -eq 0) 'a map update keeps the reported state'
    $view = Get-View 1
    $repeat = Send-Devices '10' @($apcDown)
    Assert-True ($repeat.ok -and $repeat.applied -eq 0 -and
        (Invoke-Next @{ op = 'topology_view'; z = 1; sinceVersion = $view.version }).result.unchanged) 'a repeated state changes nothing'

    $broken = $mobile.Clone()
    $broken.operational = 0
    $brokenReply = Send-Devices '11' @($broken)
    Assert-True ($brokenReply.ok -and -not $brokenReply.result.devices[0].live -and -not (Get-MapNode 2 '51').radioEnabled -and
        (Get-View 2).flagsById['51'] -eq 72) 'a broken device has no radio and is flagged'
    $restored = Send-Devices '12' @(@{ op = 'node_state'; a = '1'; powered = 1; operational = 1 }, $mobile)
    Assert-True ($restored.ok -and ($restored.result.devices | Where-Object { $_.a -ceq '50' }).live) 'restored APC brings back its device port'
    $apcNode = Get-MapNode 1 '1'
    Assert-True ($apcNode.links -eq 2 -and $apcNode.radioEnabled -and (Get-MapNode 2 '51').radioEnabled -and
        (Get-View 1).down.Count -eq 0) 'restored state rebuilds links and the access point'
    Assert-True ((Send-DevicePacket '12' 'device-restored').accepted -and (Wait-Receipt 'device-restored').successfulHops -eq 4) 'route returns with the state'

    $deviceState = Send-Devices '13' @(@{ op = 'node_state'; a = '50'; powered = 0; operational = 1 })
    $unknownState = Send-Devices '13' @(@{ op = 'node_state'; a = '77'; powered = 0; operational = 1 })
    $partialState = Send-Devices '13' @(@{ op = 'node_state'; a = '1'; powered = 0 })
    $twiceState = Send-Devices '13' @($apcDown, @{ op = 'node_state'; a = '1'; powered = 1; operational = 1 })
    $rawState = Send-Devices '13' @(@{ op = 'move_node'; a = '2'; x = 2; y = 1; z = 1 }, $apcDown)
    Assert-True (-not $deviceState.ok -and -not $unknownState.ok -and -not $partialState.ok -and -not $twiceState.ok -and
        -not $rawState.ok -and (Get-MapNode 1 '1').links -eq 2) 'invalid node states are rejected without changes'

    $removed = Send-Devices '13' @(@{ op = 'device_remove'; a = '50' }, @{ op = 'device_remove'; a = '51' })
    Assert-True ($removed.ok -and (Invoke-Next @{ op = 'game_stats' }).game.nodes -eq 3) 'device removal keeps the APC map'
    Assert-True ((Invoke-Next @{ op = 'endpoint_attach'; a = '50'; endpoint = 'device-50'; generation = '2' }).error -ceq 'unknown_node') 'removed device takes its node'
    Assert-True (-not (Send-Devices '14' @($anchoredFixed)).ok) 'a removed device ID is never reused in the stream'
    Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = 'svc'; factSequence = '13' }).ok) 'heartbeat after devices'

    # P5: a research server's storage lives in the Host ledger. DM reports the catalog, the server and
    # its income; a console buys over the network. A killed Host replays the round's journal, and the
    # retried purchase returns its recorded answer instead of spending again.
    $general = 'General Research'
    $catalogNodes = @(
        @{ id = 'base'; prereqs = @(); designs = @('rdconsole'); costs = @{ $general = 0 }; starting = 1 }
        @{ id = 'engineering'; prereqs = @('base'); designs = @('welder'); costs = @{ $general = 300 }; starting = 0 }
        @{ id = 'advanced'; prereqs = @('engineering'); designs = @('drill'); costs = @{ $general = 500 } }
    )
    function Send-Catalog {
        $first = Invoke-Next @{ op = 'research_catalog'; payload = @{ catalog = 'cat-1'; chunk = 0; chunks = 2; pointTypes = @($general)
            nodes = @($catalogNodes[0], $catalogNodes[1]) } }
        Assert-True ($first.ok -and $first.result.next -eq 1) 'catalog chunk accepted'
        return Invoke-Next @{ op = 'research_catalog'; payload = @{ catalog = 'cat-1'; chunk = 1; chunks = 2; pointTypes = @($general)
            nodes = @($catalogNodes[2]) } }
    }
    function Get-Storage([string] $Id) { return (Invoke-Next @{ op = 'research_state'; payload = @{ storage = $Id } }).result }
    function Wait-Research([string] $Recipient) {
        for ($poll = 0; $poll -lt 40; $poll++) {
            $page = (Invoke-Next @{ op = 'delivery_poll'; afterSequence = '0'; limit = 16 }).result
            $found = $page.items | Where-Object { $_.recipient -ceq $Recipient -and $_.kind -ceq 'result' -and $_.command -ceq 'research' } |
                Select-Object -First 1
            if ($null -ne $found) { return $found }
            Start-Sleep -Milliseconds 100
        }
        return $null
    }
    $income = @{ op = 'research_income'; payload = @{ interval = '10'; grants = @(@{ storage = 'srv1'; generation = '1'; points = @{ $general = '400000' } }) } }
    $register = @{ op = 'research_storage'; payload = @{ storage = 'srv1'; generation = '1'; domain = 'station'; action = 'register' } }
    Assert-True ((Invoke-Next $register.Clone()).error -ceq 'catalog_unavailable') 'a server needs the catalog'
    $catalog = Send-Catalog
    Assert-True ($catalog.ok -and $catalog.result.status -ceq 'ok' -and $catalog.result.nodes -eq 3 -and $catalog.result.hash.Length -eq 64) 'catalog installed'
    $registered = Invoke-Next $register.Clone()
    Assert-True ($registered.ok -and $registered.result.storage.unlocked -eq 1) 'research server storage registered'
    Assert-True ((Invoke-Next $income.Clone()).result.statuses[0] -ceq 'ok') 'income of one interval'
    Assert-True ((Invoke-Next $income.Clone()).result.statuses[0] -ceq 'duplicate') 'an interval pays once'
    $serverEndpoint = @{ endpoint = 'rdserver-1'; generation = '1' }
    Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = '3' } + $serverEndpoint)).ok) 'attach research server'
    $researchService = @{ op = 'service_register'; instance = 'research.srv1'; serviceType = 'research.server'; schema = 'research.status'; major = 1; minor = 0 }
    Assert-True ((Invoke-Next ($researchService + $serverEndpoint)).ok) 'register research server service'
    Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = 'svc'; factSequence = '13' }).ok) 'heartbeat before purchase'
    $buy = @{ op = 'invoke'; instance = 'research.srv1'; command = 'research'; idempotencyKey = 'buy-1'
        arguments = @{ node = 'engineering'; quote = 300; revision = 1 }; trafficClass = 1; timeoutTicks = 60 }
    Assert-True ((Invoke-Next ($buy.Clone() + $console)).ok) 'purchase sent from the console'
    $bought = Wait-Research 'console-1'
    Assert-True ($null -ne $bought -and $bought.payload.outcome -ceq 'researched' -and $bought.payload.available -eq 100 -and
        $bought.producer -ceq 'rdserver-1') 'the server answers over the network'
    Assert-True ((Invoke-Next @{ op = 'delivery_ack'; acks = @(@{ deliveryId = $bought.deliveryId; applied = $true }) }).ok) 'console ACKs the purchase'
    $storage = Get-Storage 'srv1'
    Assert-True ($storage.available[$general] -eq 100 -and $storage.revision -eq 2 -and $storage.unlocked -eq 2) 'the purchase spent the server wallet'
    $state = (Invoke-Next @{ op = 'research_state'; payload = @{} }).result
    Assert-True ($state.totals.balanced -and $state.totals.minted[$general] -eq 400 -and $state.totals.spent[$general] -eq 300) 'ledger totals balance'
    # The purchase owes the world one effect (unlock for machines, bounty, log); reading the outbox settles nothing.
    $owed = (Invoke-Next @{ op = 'research_effects'; payload = @{ max = 8 } }).result
    Assert-True ($owed.pending -eq 1 -and @($owed.effects).Count -eq 1 -and $owed.effects[0].id -ceq 'srv1/buy-1' -and
        $owed.effects[0].kind -ceq 'researched' -and $owed.effects[0].storage -ceq 'srv1' -and $owed.effects[0].op -ceq 'buy-1' -and
        $owed.effects[0].node -ceq 'engineering' -and $owed.effects[0].price -eq 300) 'the purchase carries its world effect'
    Assert-True ((Invoke-Next @{ op = 'research_effects'; payload = @{} }).result.pending -eq 1) 'reading the outbox settles nothing'

    # The Host process dies; the same round reconnects and replays the journal.
    $owner.Dispose()
    $owner = $null
    $hostProcess.Kill($true)
    $hostProcess.WaitForExit()
    $hostProcess.Dispose()
    $hostProcess = [Diagnostics.Process]::Start($launch)
    for ($attempt = 0; $attempt -lt 100 -and $null -eq $owner; $attempt++) {
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
    Assert-True ($null -ne $owner -and $session.ok) 'restarted Host accepts the same round'
    $script:seq = 0
    $recovered = (Invoke-Next @{ op = 'research_state'; payload = @{} }).result
    Assert-True ($recovered.recoveredEntries -eq 6 -and $recovered.truncatedBytes -eq 0 -and -not $recovered.catalogInstalled -and
        $recovered.catalog -ceq $catalog.result.hash -and $recovered.journalFormat -eq 2) 'journal replayed after the Host died'
    Assert-True ((Get-Storage 'srv1').available[$general] -eq 100) 'the server wallet survives the restart'
    $owedAgain = (Invoke-Next @{ op = 'research_effects'; payload = @{} }).result
    Assert-True ($owedAgain.pending -eq 1 -and $owedAgain.effects[0].id -ceq 'srv1/buy-1' -and $owedAgain.effects[0].price -eq 300) 'the unsettled effect survives the Host under the same ID'
    $catalogAgain = Send-Catalog
    Assert-True ($catalogAgain.ok -and $catalogAgain.result.status -ceq 'duplicate') 'the recorded catalog is accepted again'
    Assert-True ((Invoke-Next $income.Clone()).result.statuses[0] -ceq 'duplicate') 'a recorded interval is not paid again'
    Assert-True ((Invoke-Next @{ op = 'snapshot_begin'; snapshotId = 'svc2'; profile = 'apc_map'
        chunkCount = 1; factCount = $mapFacts.Count; factSequence = '0' }).ok) 'resync begin'
    Assert-True ((Invoke-Next @{ op = 'snapshot_chunk'; snapshotId = 'svc2'; chunkIndex = 0; factsJson = $chain; digest = $chainDigest }).ok) 'resync chunk'
    Assert-True ((Invoke-Next @{ op = 'snapshot_commit'; snapshotId = 'svc2'; digest = (Get-Digest $chainDigest) }).ok) 'resync commit'
    $ready = Invoke-Next @{ op = 'snapshot_ready'; snapshotId = 'svc2'; factSequence = '0' }
    Assert-True ($ready.ok) 'resync ready'
    $session['streamId'] = $ready.streamId
    Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = '1' } + $console)).ok) 'console reattaches'
    Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = '3' } + $serverEndpoint)).ok) 'server reattaches'
    Assert-True ((Invoke-Next ($researchService + $serverEndpoint)).ok) 'server service registered again'
    Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = 'svc2'; factSequence = '0' }).ok) 'heartbeat before retry'
    Assert-True ((Invoke-Next ($buy.Clone() + $console)).ok) 'the console retries the lost answer'
    $retried = Wait-Research 'console-1'
    Assert-True ($null -ne $retried -and $retried.payload.outcome -ceq 'researched' -and $retried.payload.available -eq 100 -and
        $retried.payload.revision -eq 2) 'the retry returns the recorded purchase'
    Assert-True ((Get-Storage 'srv1').available[$general] -eq 100) 'no second spend after the restart'
    Assert-True ((Invoke-Next @{ op = 'delivery_ack'; acks = @(@{ deliveryId = $retried.deliveryId; applied = $true }) }).ok) 'retry ACKed'
    $more = @{ op = 'invoke'; instance = 'research.srv1'; command = 'research'; idempotencyKey = 'buy-2'
        arguments = @{ node = 'advanced'; quote = 500 }; trafficClass = 1; timeoutTicks = 60 }
    Assert-True ((Invoke-Next ($more + $console)).ok) 'second purchase sent'
    $poor = Wait-Research 'console-1'
    Assert-True ($null -ne $poor -and $poor.payload.outcome -ceq 'insufficient_points' -and $poor.payload.price -eq 500) 'a balance check needs real points'
    # The retried purchase and the refusal added no obligation. DM settles the one effect; a repeated
    # settlement (its reply was lost) changes nothing, and the next Host session still knows it.
    $settle = @{ op = 'research_effects'; payload = @{ settle = @(@{ id = 'srv1/buy-1'; result = 'paid' }) } }
    $settled = (Invoke-Next $settle.Clone()).result
    Assert-True ($settled.settled -eq 1 -and $settled.pending -eq 0 -and @($settled.effects).Count -eq 0) 'the effect is settled once; retry and refusal owe nothing'
    $repeated = Invoke-Next $settle.Clone()
    Assert-True ($repeated.ok -and $repeated.result.settled -eq 0 -and $repeated.result.pending -eq 0) 'a repeated settlement changes nothing'
    Assert-True ((Invoke-Next @{ op = 'research_effects'; payload = @{ settle = @(@{ id = 'srv1/buy-1'; result = 'not a code' }) } }).error -ceq 'invalid_request') 'a malformed settlement is refused'
    $state = (Invoke-Next @{ op = 'research_state'; payload = @{} }).result
    Assert-True ($state.effects.pending -eq 0 -and $state.effects.settled.paid -eq 1) 'the settlement is part of the ledger state'

    # P6: a second server of the same domain. Points move only by transfer (reserve on the source, the
    # authorization travels to the destination and commits once); knowledge moves by sync; disks hold knowledge.
    function Wait-Result([string] $Recipient, [string] $Command) {
        for ($poll = 0; $poll -lt 40; $poll++) {
            $page = (Invoke-Next @{ op = 'delivery_poll'; afterSequence = '0'; limit = 32 }).result
            $found = $page.items | Where-Object { $_.recipient -ceq $Recipient -and $_.kind -ceq 'result' -and $_.command -ceq $Command } |
                Select-Object -First 1
            if ($null -ne $found) {
                $null = Invoke-Next @{ op = 'delivery_ack'; acks = @(@{ deliveryId = $found.deliveryId; applied = $true }) }
                return $found
            }
            Start-Sleep -Milliseconds 100
        }
        return $null
    }
    $second = @{ endpoint = 'rdserver-2'; generation = '1' }
    Assert-True ((Invoke-Next @{ op = 'research_storage'; payload = @{ storage = 'srv2'; generation = '1'; domain = 'station'; action = 'register' } }).ok) 'second storage'
    Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = '2' } + $second)).ok) 'attach second server'
    Assert-True ((Invoke-Next (@{ op = 'service_register'; instance = 'research.srv2'; serviceType = 'research.server'; schema = 'research.status'; major = 1; minor = 0 } + $second)).ok) 'register second server'
    Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = 'svc2'; factSequence = '0' }).ok) 'heartbeat before transfer'
    $out = @{ op = 'invoke'; instance = 'research.srv1'; command = 'transfer_out'; idempotencyKey = 'tr-1-out'
        arguments = @{ transfer = 'tr-1'; to = 'srv2'; amount = 40 }; trafficClass = 1; timeoutTicks = 60 }
    Assert-True ((Invoke-Next ($out + $console)).ok) 'transfer requested from the console'
    $reserved = Wait-Result 'console-1' 'transfer_out'
    Assert-True ($null -ne $reserved -and $reserved.payload.state -ceq 'reserved' -and $reserved.payload.available -eq 60) 'the source reserves into escrow'
    $state = (Invoke-Next @{ op = 'research_state'; payload = @{ scope = 'all' } }).result
    Assert-True ($state.totals.escrow[$general] -eq 40 -and $state.totals.balanced) 'escrow is part of the balance'
    $authorization = @{ op = 'invoke'; instance = 'research.srv2'; command = 'transfer_in'; idempotencyKey = 'tr-1-in'
        arguments = @{ transfer = 'tr-1'; from = 'srv1'; amount = 40 }; trafficClass = 1; timeoutTicks = 60 }
    Assert-True ((Invoke-Next ($authorization + $serverEndpoint)).ok) 'the authorization leaves the source'
    $committed = Wait-Result 'rdserver-1' 'transfer_in'
    Assert-True ($null -ne $committed -and $committed.payload.state -ceq 'committed' -and $committed.payload.available -eq 40) 'the destination commits'
    $retry = $authorization.Clone()
    $retry.idempotencyKey = 'tr-1-in-retry'
    Assert-True ((Invoke-Next ($retry + $serverEndpoint)).ok) 'the source retries a lost answer'
    $again = Wait-Result 'rdserver-1' 'transfer_in'
    Assert-True ($null -ne $again -and $again.payload.state -ceq 'committed' -and (Get-Storage 'srv2').available[$general] -eq 40) 'a retried authorization commits nothing twice'
    $forged = $authorization.Clone()
    $forged.idempotencyKey = 'tr-1-forged'
    $forged.arguments = @{ transfer = 'tr-1'; from = 'srv1'; amount = 400 }
    Assert-True ((Invoke-Next ($forged + $serverEndpoint)).ok) 'forged amount sent'
    Assert-True ((Wait-Result 'rdserver-1' 'transfer_in').payload.reason -ceq 'mismatch') 'an authorization is only its own transfer'

    $pull = @{ op = 'invoke'; instance = 'research.srv1'; command = 'sync'; idempotencyKey = 'sync-1'; arguments = @{ since = 0 }; trafficClass = 1; timeoutTicks = 60 }
    Assert-True ((Invoke-Next ($pull + $second)).ok) 'the second server pulls the first'
    $delta = Wait-Result 'rdserver-2' 'sync'
    Assert-True ($null -ne $delta -and ($delta.payload.nodes -split ',') -contains 'engineering' -and -not $delta.payload.more) 'knowledge crosses the network'
    $replicated = Invoke-Next @{ op = 'research_replicate'; payload = @{ storage = 'srv2'; generation = '1'; peer = 'srv1'; upto = "$($delta.payload.upto)"
        nodes = @($delta.payload.nodes -split ','); boosts = @{ scope = 0 } } }
    Assert-True (-not $replicated.ok -and $replicated.error -ceq 'unknown_node') 'replicated content must be catalog knowledge'
    $replicated = Invoke-Next @{ op = 'research_replicate'; payload = @{ storage = 'srv2'; generation = '1'; peer = 'srv1'; upto = "$($delta.payload.upto)"
        nodes = @($delta.payload.nodes -split ',') } }
    Assert-True ($replicated.ok -and $replicated.result.added -eq 1 -and (Get-Storage 'srv2').unlocked -eq 2 -and
        (Get-Storage 'srv2').available[$general] -eq 40) 'sync teaches knowledge, never points'

    # The world reads what a server knows from the server's own log, in bounded pages, whatever brought it there.
    $log = (Invoke-Next @{ op = 'research_knowledge'; payload = @{ storage = 'srv2'; since = '0'; max = 1 } }).result
    Assert-True (@($log.nodes).Count -eq 1 -and $log.nodes[0] -ceq 'base' -and $log.more) 'a server knowledge log is read in bounded pages'
    $rest = (Invoke-Next @{ op = 'research_knowledge'; payload = @{ storage = 'srv2'; since = $log.upto } }).result
    Assert-True (@($rest.nodes) -ccontains 'engineering' -and -not $rest.more -and $rest.upto -ceq (Get-Storage 'srv2').knowledge) 'the log ends at the knowledge of the storage'
    Assert-True (@((Invoke-Next @{ op = 'research_knowledge'; payload = @{ storage = 'srv2'; since = $rest.upto } }).result.nodes).Count -eq 0) 'a log read to its end has nothing more'
    Assert-True ((Invoke-Next @{ op = 'research_knowledge'; payload = @{ storage = 'nowhere'; since = '0' } }).error -ceq 'storage_unavailable') 'no log without a storage'

    $backup = @{ op = 'invoke'; instance = 'research.srv1'; command = 'backup'; idempotencyKey = 'disk-1-w'; arguments = @{ disk = 'disk-1' }; trafficClass = 1; timeoutTicks = 60 }
    Assert-True ((Invoke-Next ($backup + $console)).ok) 'backup requested'
    Assert-True ((Wait-Result 'console-1' 'backup').payload.state -ceq 'written') 'the disk holds the knowledge'
    $restore = @{ op = 'invoke'; instance = 'research.srv2'; command = 'restore'; idempotencyKey = 'disk-1-r'; arguments = @{ disk = 'disk-1' }; trafficClass = 1; timeoutTicks = 60 }
    Assert-True ((Invoke-Next ($restore + $console)).ok) 'restore requested'
    $restored = Wait-Result 'console-1' 'restore'
    Assert-True ($restored.payload.state -ceq 'restored' -and $restored.payload.nodes -eq 0 -and (Get-Storage 'srv2').available[$general] -eq 40) 'a restore adds no points'
    $state = (Invoke-Next @{ op = 'research_state'; payload = @{ scope = 'all' } }).result
    Assert-True ($state.totals.balanced -and $state.totals.escrow[$general] -eq 0 -and $state.transfers[0].state -ceq 'committed') 'the transfer is closed and the ledger balanced'

    # 17.4: durable research storage is a signal of game readiness, never of network admission. Storage
    # that cannot take a write blocks research only, and comes back without a Host restart.
    $script:snapshot = 'svc2'
    function Wait-Storage([string] $State) {
        $probe = $null
        for ($poll = 0; $poll -lt 150; $poll++) {
            Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = $script:snapshot; factSequence = '0' }).ok) 'heartbeat while watching storage'
            $probe = Get-GameReadiness
            if ($probe.Body.research -ceq $State) { return $probe }
            Start-Sleep -Milliseconds 200
        }
        return $probe
    }
    function Read-Storage([string] $Id) {
        for ($poll = 0; $poll -lt 50; $poll++) {
            $reply = Invoke-Next @{ op = 'research_state'; payload = @{ storage = $Id } }
            if ($reply.ok) { return $reply.result }
            Start-Sleep -Milliseconds 100
        }
        throw "Failed: research state of $Id is readable"
    }
    $steady = Wait-Storage 'ready'
    Assert-True ($steady.Status -eq 200 -and $steady.Body.ready -and $steady.Body.networkReady -and $steady.Body.researchReady) 'an open journal is part of game readiness'
    $reward = @{ op = 'research_grant'; payload = @{ storage = 'srv1'; generation = '1'; source = 'analyzer-1'; kind = 'deconstruct'; points = @{ $general = '5000' } } }
    if ($IsWindows) {
        # A byte-range lock held through another handle refuses the write itself (mandatory on Windows only).
        $journal = Join-Path $work 'research' "research-$(([guid]$hello.roundEpoch).ToString('N')).journal"
        $before = [long](Read-Storage 'srv1').available[$general] # 64-bit values travel as text.
        $blocker = [IO.FileStream]::new($journal, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            $blocker.Lock($blocker.Length, [long]::MaxValue - $blocker.Length)
            $refused = Invoke-Next $reward.Clone()
            Assert-True (-not $refused.ok -and $refused.error -ceq 'journal_failed') "a refused journal write fails the operation: $($refused | ConvertTo-Json -Compress)"
            $faulted = Wait-Storage 'faulted'
            Assert-True ($faulted.Status -eq 503 -and -not $faulted.Body.ready -and -not $faulted.Body.researchReady -and
                $faulted.Body.networkReady -and $faulted.Body.phase -ceq 'ready') "a journal that cannot be written makes the game unready: $($faulted.Body | ConvertTo-Json -Compress)"
            Assert-True ((Invoke-Next @{ op = 'packet_send'; a = '1'; b = '2'; bytes = 96; requestId = 'storage-fault' }).ok) 'the network admits work without research storage'
            $again = Invoke-Next $reward.Clone()
            Assert-True (-not $again.ok -and $again.error -cin @('journal_failed', 'catalog_unavailable', 'research_unavailable')) "research writes stay refused: $($again.error)"
            Assert-True ((Read-Storage 'srv1').available[$general] -eq $before) 'the refused reward changed nothing'
            Start-Sleep -Milliseconds 1500
            Assert-True ((Wait-Storage 'faulted').Status -eq 503) 'a retry against the same fault does not report ready'
        }
        finally { $blocker.Dispose() }
        $restored = Wait-Storage 'ready'
        Assert-True ($restored.Status -eq 200 -and $restored.Body.ready) "the journal is replayed once the device takes writes: $($restored.Body | ConvertTo-Json -Compress)"
        Assert-True ((Send-Catalog).result.status -ceq 'duplicate') 'the replayed ledger takes its catalog again'
        Assert-True ((Read-Storage 'srv1').available[$general] -eq $before) 'the replayed ledger holds what the disk held'
        $retried = Invoke-Next $reward.Clone()
        Assert-True ($retried.ok -and $retried.result.status -ceq 'ok' -and (Read-Storage 'srv1').available[$general] -eq $before + 5) 'the refused reward is retried under its ID'
        Assert-True ((Invoke-Next $reward.Clone()).result.status -ceq 'duplicate' -and (Read-Storage 'srv1').available[$general] -eq $before + 5) 'and is paid once'
        $state = (Invoke-Next @{ op = 'research_state'; payload = @{} }).result
        Assert-True ($state.storage -ceq 'ready' -and $state.recoveries -ge 1 -and $state.totals.balanced -and -not $state.faulted) 'the recovery is part of the ledger status'
    }

    # A round whose journal cannot be opened: the session and the network come up, research stays blocked
    # until the path is usable. A directory in the journal's place refuses the open on every platform.
    $owner.Dispose()
    $owner = $null
    $blockedHello = $hello.Clone()
    $blockedHello.roundEpoch = [guid]::NewGuid().ToString()
    $blockedJournal = Join-Path $work 'research' "research-$(([guid]$blockedHello.roundEpoch).ToString('N')).journal"
    $null = New-Item -ItemType Directory -Path $blockedJournal
    for ($attempt = 0; $attempt -lt 100 -and $null -eq $owner; $attempt++) {
        $owner = Open-Stream
        Send-Frame $owner $blockedHello
        $session = Read-Frame $owner
        if (-not $session.ok) { $owner.Dispose(); $owner = $null; Start-Sleep -Milliseconds 50 }
    }
    Assert-True ($null -ne $owner -and $session.ok) 'a round with an unusable journal still gets its session'
    $script:seq = 0
    Assert-True ((Invoke-Next @{ op = 'snapshot_begin'; snapshotId = 'svc3'; profile = 'apc_map'
        chunkCount = 1; factCount = $mapFacts.Count; factSequence = '0' }).ok) 'blocked round begin'
    Assert-True ((Invoke-Next @{ op = 'snapshot_chunk'; snapshotId = 'svc3'; chunkIndex = 0; factsJson = $chain; digest = $chainDigest }).ok) 'blocked round chunk'
    Assert-True ((Invoke-Next @{ op = 'snapshot_commit'; snapshotId = 'svc3'; digest = (Get-Digest $chainDigest) }).ok) 'blocked round commit'
    $ready = Invoke-Next @{ op = 'snapshot_ready'; snapshotId = 'svc3'; factSequence = '0' }
    Assert-True ($ready.ok) 'blocked round ready'
    $session['streamId'] = $ready.streamId
    $script:snapshot = 'svc3'
    $blocked = Wait-Storage 'unavailable'
    Assert-True ($blocked.Status -eq 503 -and -not $blocked.Body.ready -and $blocked.Body.networkReady -and $blocked.Body.phase -ceq 'ready') "a journal that cannot be opened makes the game unready: $($blocked.Body | ConvertTo-Json -Compress)"
    Assert-True ((Invoke-Next $register.Clone()).error -ceq 'research_unavailable') 'research is refused without its journal'
    $network = Invoke-WebRequest "http://127.0.0.1:$httpPort/api/readiness/network" -SkipHttpErrorCheck -TimeoutSec 5
    Assert-True ([int]$network.StatusCode -eq 200 -and ($network.Content | ConvertFrom-Json).research -ceq 'unavailable') 'network readiness does not wait for research storage'
    Assert-True ((Invoke-Next @{ op = 'packet_send'; a = '1'; b = '2'; bytes = 96; requestId = 'storage-blocked' }).ok) 'the network admits work without the journal'
    Remove-Item -LiteralPath $blockedJournal
    $opened = Wait-Storage 'ready'
    Assert-True ($opened.Status -eq 200 -and $opened.Body.ready) "the journal opens once its path is usable: $($opened.Body | ConvertTo-Json -Compress)"
    $fresh = Send-Catalog
    Assert-True ($fresh.ok -and $fresh.result.status -ceq 'ok') 'the new round binds its catalog'
    Assert-True ((Invoke-Next $register.Clone()).ok) 'and registers its server'
    Assert-True ((Invoke-Next @{ op = 'research_state'; payload = @{} }).result.recoveries -ge 1) 'the late open counts as a recovery'

    # network.diagnostics: an APC answers about its own ports once the request reached it over the network.
    # The Host answers on the APC's behalf from the published world; the asker learns nothing without a route.
    $engineer = @{ endpoint = 'eng-1'; generation = '1' }
    $apc2 = @{ endpoint = 'apc-2'; generation = '1' }
    Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = '1' } + $engineer)).ok) 'attach the engineer console'
    Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = '2' } + $apc2)).ok) 'attach APC 2'
    Assert-True ((Invoke-Next (@{ op = 'service_register'; instance = 'netdiag.apc2'; serviceType = 'network.diagnostics'
        schema = 'network.link_state'; major = 1; minor = 0 } + $apc2)).ok) 'APC 2 offers diagnostics'
    function Invoke-Diagnostics([string] $Command, [hashtable] $Arguments, [string] $Key, [string] $Sequence) {
        Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = 'svc3'; factSequence = $Sequence }).ok) "heartbeat before $Key"
        Assert-True ((Invoke-Next (@{ op = 'invoke'; instance = 'netdiag.apc2'; command = $Command; idempotencyKey = $Key
            arguments = $Arguments; trafficClass = 1; timeoutTicks = 60 } + $engineer)).ok) "diagnostics $Key sent"
        $answer = Wait-Result 'eng-1' $Command
        Assert-True ($null -ne $answer -and $answer.producer -ceq 'apc-2') "diagnostics $Key answered by the APC"
        return $answer.payload
    }
    $ports = Invoke-Diagnostics 'link_state' @{ reason = 'inspect' } 'ls-1' '0'
    Assert-True ($ports.state -ceq 'active' -and $ports.wired -eq 2 -and $ports.wiredUp -eq 2 -and $ports.neighbors -ceq '1:up,3:up' -and
        -not $ports.more) "a healthy APC reports its ports: $($ports | ConvertTo-Json -Compress)"
    $reach = Invoke-Diagnostics 'trace' @{ node = '3' } 'tr-1' '0'
    Assert-True ($reach.state -ceq 'reachable' -and $reach.hops -eq 1 -and $reach.node -ceq '3') 'a trace counts the hops'
    Assert-True ((Invoke-Next @{ op = 'world_delta'; snapshotId = 'svc3'; factSequence = '1'
        changes = @(@{ op = 'node_state'; a = '3'; powered = 0; operational = 1 }) }).ok) 'APC 3 loses power'
    $ports = Invoke-Diagnostics 'link_state' @{ reason = 'refresh' } 'ls-2' '1'
    Assert-True ($ports.state -ceq 'active' -and $ports.wired -eq 2 -and $ports.wiredUp -eq 1 -and $ports.neighbors -ceq '1:up,3:unpowered') "the dead neighbour is named with its cause: $($ports | ConvertTo-Json -Compress)"
    Assert-True ((Invoke-Diagnostics 'trace' @{ node = '3' } 'tr-2' '1').state -ceq 'unreachable') 'a trace does not cross a dead cable'
    Assert-True ((Invoke-Diagnostics 'trace' @{ node = '999' } 'tr-3' '1').state -ceq 'unknown') 'a node outside the world is unknown'
    Assert-True ((Invoke-Next (@{ op = 'invoke'; instance = 'netdiag.apc2'; command = 'trace'; idempotencyKey = 'tr-bad'
        arguments = @{ node = 3 }; trafficClass = 1; timeoutTicks = 60 } + $engineer)).ok -eq $false) 'a trace argument outside its schema is refused'
    Assert-True ((Invoke-Next @{ op = 'world_delta'; snapshotId = 'svc3'; factSequence = '2'
        changes = @(@{ op = 'node_state'; a = '3'; powered = 1; operational = 1 }) }).ok) 'APC 3 has power again'
    Assert-True ((Invoke-Diagnostics 'link_state' @{ reason = 'refresh' } 'ls-3' '2').neighbors -ceq '1:up,3:up') 'the repaired neighbour is up again'
    # A multitool at the APC itself: the device asks its own service, with no hop.
    Assert-True ((Invoke-Next (@{ op = 'invoke'; instance = 'netdiag.apc2'; command = 'link_state'; idempotencyKey = 'ls-self'
        arguments = @{ reason = 'inspect' }; trafficClass = 1; timeoutTicks = 60 } + $apc2)).ok) 'the APC asks its own diagnostics'
    $own = Wait-Result 'apc-2' 'link_state'
    Assert-True ($null -ne $own -and $own.payload.wired -eq 2 -and $own.payload.neighbors -ceq '1:up,3:up') "a device answers its own request: $($own | ConvertTo-Json -Compress -Depth 4)"

    # State watch: the console subscribes to APC 2's link state. The Host publishes it on the APC's behalf
    # when it differs from what it published last; nothing is sent while nothing changes.
    function Wait-LinkEvent([string] $Sequence) {
        for ($poll = 0; $poll -lt 60; $poll++) {
            Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = 'svc3'; factSequence = $Sequence }).ok) 'heartbeat while watching links'
            $page = (Invoke-Next @{ op = 'delivery_poll'; afterSequence = '0'; limit = 16 }).result
            $found = $page.items | Where-Object { $_.recipient -ceq 'eng-1' -and $_.kind -ceq 'event' -and $_.instance -ceq 'netdiag.apc2' } |
                Select-Object -First 1
            if ($null -ne $found) {
                $null = Invoke-Next @{ op = 'delivery_ack'; acks = @(@{ deliveryId = $found.deliveryId; applied = $true }) }
                return $found
            }
            Start-Sleep -Milliseconds 100
        }
        return $null
    }
    $watch = Invoke-Next (@{ op = 'subscribe'; key = 'links'; instance = 'netdiag.apc2'; schema = 'network.link_state'; major = 1
        projection = 'full'; mode = 'latest_value' } + $engineer)
    Assert-True ($watch.ok) "the console watches the APC: $($watch.error)"
    $first = Wait-LinkEvent '2'
    Assert-True ($null -ne $first -and $first.producer -ceq 'apc-2' -and $first.payload.neighbors -ceq '1:up,3:up' -and $first.payload.wiredUp -eq 2) "a watched device publishes its state: $($first | ConvertTo-Json -Compress -Depth 4)"
    Assert-True ((Invoke-Next @{ op = 'world_delta'; snapshotId = 'svc3'; factSequence = '3'
        changes = @(@{ op = 'node_state'; a = '3'; powered = 1; operational = 0 }) }).ok) 'APC 3 breaks'
    $changed = Wait-LinkEvent '3'
    Assert-True ($null -ne $changed -and $changed.payload.neighbors -ceq '1:up,3:broken' -and $changed.payload.wiredUp -eq 1 -and
        [long]$changed.producerSequence -gt [long]$first.producerSequence) "a change of a port reaches the watcher: $($changed | ConvertTo-Json -Compress -Depth 4)"
    Start-Sleep -Milliseconds 1500
    Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = 'svc3'; factSequence = '3' }).ok) 'heartbeat while nothing changes'
    $quiet = (Invoke-Next @{ op = 'delivery_poll'; afterSequence = '0'; limit = 16 }).result.items |
        Where-Object { $_.kind -ceq 'event' -and $_.instance -ceq 'netdiag.apc2' }
    Assert-True ($null -eq $quiet) 'an unchanged state is not published again'
    Assert-True ((Invoke-Next @{ op = 'world_delta'; snapshotId = 'svc3'; factSequence = '4'
        changes = @(@{ op = 'node_state'; a = '3'; powered = 1; operational = 1 }) }).ok) 'APC 3 is repaired'
    $repaired = Wait-LinkEvent '4'
    Assert-True ($null -ne $repaired -and $repaired.payload.neighbors -ceq '1:up,3:up') 'the repair reaches the watcher'
    Assert-True ((Invoke-Next (@{ op = 'unsubscribe'; key = 'links' } + $engineer)).ok) 'the console stops watching'
    Assert-True ((Invoke-Next @{ op = 'world_delta'; snapshotId = 'svc3'; factSequence = '5'
        changes = @(@{ op = 'node_state'; a = '3'; powered = 0; operational = 1 }) }).ok) 'APC 3 loses power unwatched'
    Start-Sleep -Milliseconds 1500
    Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = 'svc3'; factSequence = '5' }).ok) 'heartbeat after the watch ended'
    $late = (Invoke-Next @{ op = 'delivery_poll'; afterSequence = '0'; limit = 16 }).result.items | Where-Object { $_.kind -ceq 'event' -and $_.instance -ceq 'netdiag.apc2' }
    Assert-True ($null -eq $late) 'a device nobody watches publishes nothing'
    Assert-True ((Invoke-Next @{ op = 'world_delta'; snapshotId = 'svc3'; factSequence = '6'
        changes = @(@{ op = 'node_state'; a = '3'; powered = 1; operational = 1 }) }).ok) 'APC 3 has power for the rest of the run'

    # P10 network.query: the console asks APC 2 which services the network offers. The answer lists what
    # the asker may discover and the APC reaches now, in instance order, in bounded pages.
    $doors = @{ endpoint = 'dev-3'; generation = '1' }
    Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = '3' } + $doors)).ok) 'attach a device behind APC 3'
    foreach ($door in 'door.a', 'door.b') {
        Assert-True ((Invoke-Next (@{ op = 'service_register'; instance = $door; serviceType = 'machine.door'; schema = 'machine.door_state'
            major = 1; minor = 0 } + $doors)).ok) "register $door"
    }
    Assert-True ((Invoke-Next (@{ op = 'service_register'; instance = 'netquery.apc2'; serviceType = 'network.query'; schema = 'query.page'
        major = 1; minor = 0 } + $apc2)).ok) 'APC 2 offers queries'
    function Invoke-Query([hashtable] $Arguments, [string] $Key, [string] $Sequence, [hashtable] $Asker = $engineer, [string] $Instance = 'netquery.apc2') {
        Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = 'svc3'; factSequence = $Sequence }).ok) "heartbeat before $Key"
        Assert-True ((Invoke-Next (@{ op = 'invoke'; instance = $Instance; command = 'services'; idempotencyKey = $Key
            arguments = $Arguments; trafficClass = 1; timeoutTicks = 60 } + $Asker)).ok) "query $Key sent"
        $answer = Wait-Result $Asker.endpoint 'services'
        Assert-True ($null -ne $answer) "query $Key answered"
        return $answer.payload
    }
    $page = Invoke-Query @{ type = 'machine.door' } 'q-1' '6'
    Assert-True ($page.status -ceq 'complete' -and $page.count -eq 2 -and $page.items -ceq 'door.a|machine.door|1,door.b|machine.door|1' -and
        -not $page.ContainsKey('cursor')) "a query lists the reachable services of a type: $($page | ConvertTo-Json -Compress)"
    $all = Invoke-Query @{ limit = 16 } 'q-2' '6'
    Assert-True ($all.status -ceq 'complete' -and $all.items -ceq 'door.a|machine.door|1,door.b|machine.door|1,netdiag.apc2|network.diagnostics|0,netquery.apc2|network.query|0') "without a type the whole reachable directory is listed in order: $($all.items)"
    $first = Invoke-Query @{ type = 'machine.door'; limit = 1 } 'q-3' '6'
    Assert-True ($first.status -ceq 'partial' -and $first.items -ceq 'door.a|machine.door|1' -and $first.cursor) "a page names its cursor: $($first | ConvertTo-Json -Compress)"
    $second = Invoke-Query @{ type = 'machine.door'; limit = 1; cursor = $first.cursor } 'q-4' '6'
    Assert-True ($second.status -ceq 'complete' -and $second.items -ceq 'door.b|machine.door|1' -and $second.revision -ceq $first.revision) 'the cursor continues the list'
    Assert-True ((Invoke-Query @{ type = 'machine.door'; limit = 1; cursor = $first.cursor } 'q-5' '6' $apc2).status -ceq 'invalid_cursor') 'a cursor belongs to its asker'
    Assert-True ((Invoke-Query @{ type = 'network.query'; limit = 1; cursor = $first.cursor } 'q-filter' '6').status -ceq 'invalid_cursor') 'a cursor belongs to its filter'
    Assert-True ((Invoke-Query @{ type = 'machine.door'; limit = 2; cursor = $first.cursor } 'q-limit' '6').status -ceq 'invalid_cursor') 'a cursor belongs to its page size'
    $bytes = [Convert]::FromBase64String($first.cursor)
    $bytes[0] = $bytes[0] -bxor 1
    Assert-True ((Invoke-Query @{ type = 'machine.door'; limit = 1; cursor = [Convert]::ToBase64String($bytes) } 'q-tampered' '6').status -ceq 'invalid_cursor') 'a tampered cursor is rejected'
    Assert-True ((Invoke-Next (@{ op = 'service_register'; instance = 'door.c'; serviceType = 'machine.door'; schema = 'machine.door_state'
        major = 1; minor = 0 } + $doors)).ok) 'the directory changes'
    $expired = Invoke-Query @{ type = 'machine.door'; limit = 1; cursor = $first.cursor } 'q-6' '6'
    Assert-True ($expired.status -ceq 'snapshot_expired' -and $expired.count -eq 0 -and [long]$expired.revision -gt [long]$first.revision) "a cursor of an older directory is refused, not continued: $($expired | ConvertTo-Json -Compress)"
    Assert-True ((Invoke-Next @{ op = 'world_delta'; snapshotId = 'svc3'; factSequence = '7'
        changes = @(@{ op = 'node_state'; a = '3'; powered = 0; operational = 1 }) }).ok) 'APC 3 loses power under the doors'
    $cut = Invoke-Query @{ type = 'machine.door' } 'q-7' '7'
    Assert-True ($cut.status -ceq 'empty' -and $cut.count -eq 0 -and $cut.items -ceq '') "services the network does not reach are absent: $($cut | ConvertTo-Json -Compress)"
    Assert-True ((Invoke-Next @{ op = 'world_delta'; snapshotId = 'svc3'; factSequence = '8'
        changes = @(@{ op = 'node_state'; a = '3'; powered = 1; operational = 1 }) }).ok) 'APC 3 has power again'
    Assert-True ((Invoke-Query @{ type = 'machine.door' } 'q-8' '8').count -eq 3) 'the repaired network lists them again'
    Assert-True ((Invoke-Next (@{ op = 'invoke'; instance = 'netquery.apc2'; command = 'services'; idempotencyKey = 'q-bad'
        arguments = @{ limit = 500 }; trafficClass = 1; timeoutTicks = 60 } + $engineer)).ok -eq $false) 'a limit outside the schema is refused'

    # WatchQuery: the console subscribes to APC 2's directory. A publication says that the set of services
    # the APC reaches changed; it names no service, the watcher asks again.
    Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = '2' } + @{ endpoint = 'apc-2w'; generation = '1' })).ok) 'attach a second endpoint on APC 2'
    $watchInstance = @{ endpoint = 'apc-2w'; generation = '1' }
    Assert-True ((Invoke-Next (@{ op = 'service_register'; instance = 'netquery.apc2w'; serviceType = 'network.query'; schema = 'query.directory'
        major = 1; minor = 0 } + $watchInstance)).ok) 'APC 2 offers a watchable directory'
    function Wait-DirectoryEvent([string] $Sequence) {
        for ($poll = 0; $poll -lt 60; $poll++) {
            Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = 'svc3'; factSequence = $Sequence }).ok) 'heartbeat while watching the directory'
            $found = (Invoke-Next @{ op = 'delivery_poll'; afterSequence = '0'; limit = 16 }).result.items |
                Where-Object { $_.recipient -ceq 'eng-1' -and $_.kind -ceq 'event' -and $_.instance -ceq 'netquery.apc2w' } | Select-Object -First 1
            if ($null -ne $found) {
                $null = Invoke-Next @{ op = 'delivery_ack'; acks = @(@{ deliveryId = $found.deliveryId; applied = $true }) }
                return $found.payload
            }
            Start-Sleep -Milliseconds 100
        }
        return $null
    }
    Assert-True ((Invoke-Next (@{ op = 'subscribe'; key = 'directory'; instance = 'netquery.apc2w'; schema = 'query.directory'; major = 1
        projection = 'full'; mode = 'latest_value' } + $engineer)).ok) 'the console watches the directory'
    $seen = Wait-DirectoryEvent '8'
    Assert-True ($null -ne $seen -and $seen.services -eq 6 -and $seen.digest.Length -eq 16 -and -not $seen.partial) "a watched directory publishes its state: $($seen | ConvertTo-Json -Compress)"
    Assert-True ((Invoke-Next (@{ op = 'service_register'; instance = 'door.d'; serviceType = 'machine.door'; schema = 'machine.door_state'
        major = 1; minor = 0 } + $doors)).ok) 'a service appears'
    $grown = Wait-DirectoryEvent '8'
    Assert-True ($null -ne $grown -and $grown.services -eq 7 -and $grown.digest -cne $seen.digest) "a new service moves the digest: $($grown | ConvertTo-Json -Compress)"
    Assert-True ((Invoke-Next @{ op = 'world_delta'; snapshotId = 'svc3'; factSequence = '9'
        changes = @(@{ op = 'node_state'; a = '3'; powered = 0; operational = 1 }) }).ok) 'APC 3 loses power under the doors'
    $cutOff = Wait-DirectoryEvent '9'
    Assert-True ($null -ne $cutOff -and $cutOff.services -eq 3 -and $cutOff.digest -cne $grown.digest) "services out of reach leave the watched set: $($cutOff | ConvertTo-Json -Compress)"
    Start-Sleep -Milliseconds 1500
    Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = 'svc3'; factSequence = '9' }).ok) 'heartbeat while the directory rests'
    $still = (Invoke-Next @{ op = 'delivery_poll'; afterSequence = '0'; limit = 16 }).result.items | Where-Object { $_.kind -ceq 'event' -and $_.instance -ceq 'netquery.apc2w' }
    Assert-True ($null -eq $still) 'an unchanged directory is not published again'
    Assert-True ((Invoke-Next @{ op = 'world_delta'; snapshotId = 'svc3'; factSequence = '10'
        changes = @(@{ op = 'node_state'; a = '3'; powered = 1; operational = 1 }) }).ok) 'APC 3 has power again'
    $back = Wait-DirectoryEvent '10'
    Assert-True ($null -ne $back -and $back.digest -ceq $grown.digest) 'the same set gives the same digest again'
    Assert-True ((Invoke-Next (@{ op = 'unsubscribe'; key = 'directory' } + $engineer)).ok) 'the console stops watching the directory'
    $scoped = Invoke-Query @{ type = 'machine.door'; limit = 1 } 'q-scoped' '10'
    Assert-True ((Invoke-Query @{ type = 'machine.door'; limit = 1; cursor = $scoped.cursor } 'q-other-provider' '10' $engineer 'netquery.apc2w').status -ceq 'invalid_cursor') 'a cursor cannot move to another query instance on the same APC'
    $renewedEngineer = @{ endpoint = $engineer.endpoint; generation = '2' }
    Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = '1' } + $renewedEngineer)).ok) 'replace the console generation'
    Assert-True ((Invoke-Query @{ type = 'machine.door'; limit = 1; cursor = $scoped.cursor } 'q-other-generation' '10' $renewedEngineer).status -ceq 'invalid_cursor') 'reused endpoint ID cannot reuse its previous generation cursor'
    $engineer = $renewedEngineer

    function Read-QueryInstance([string] $Command, [string] $Target, [string] $Key, [string] $FactSequence = '10') {
        Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = 'svc3'; factSequence = $FactSequence }).ok) "heartbeat before $Key"
        Assert-True ((Invoke-Next (@{ op = 'invoke'; instance = 'netquery.apc2'; command = $Command; idempotencyKey = $Key
            arguments = @{ instance = $Target }; trafficClass = 1; timeoutTicks = 60 } + $engineer)).ok) "query $Key sent"
        $answer = Wait-Result $engineer.endpoint $Command
        Assert-True ($null -ne $answer -and $answer.producer -ceq 'apc-2' -and $answer.outcome -ceq 'applied') "query $Key answered by APC 2"
        return $answer.payload
    }
    $schema = Read-QueryInstance 'schema' 'door.a' 'schema-visible'
    Assert-True ($schema.status -ceq 'complete' -and $schema.instance -ceq 'door.a' -and $schema.serviceType -ceq 'machine.door' -and
        $schema.schema -ceq 'machine.door_state' -and $schema.major -eq 1 -and $schema.minor -eq 0 -and $schema.commands -ceq 'set_state') 'query reads the registered contract'
    $idle = Read-QueryInstance 'state' 'door.a' 'state-idle'
    Assert-True ($idle.status -ceq 'complete' -and $idle.publication -ceq 'idle' -and $idle.sequence -ceq '0') 'the new service has not published'
    $doorState = @{ press = 'ipc-test'; state = 'closed'; changed = $true }
    $sequence = '9007199254740993'
    Assert-True ((Invoke-Next (@{ op = 'publish'; instance = 'door.a'; producerSequence = $sequence; payload = $doorState } + $doors)).ok) 'publish a door state'
    $published = Read-QueryInstance 'state' 'door.a' 'state-published'
    Assert-True ($published.status -ceq 'complete' -and $published.publication -ceq 'published' -and $published.sequence -ceq $sequence -and
        $published.Count -eq 4) 'query returns exact sequence and no application payload'

    $private = @{ endpoint = 'hidden-3'; generation = '1' }
    Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = '3'; domain = 'syndicate' } + $private)).ok) 'attach a hidden device'
    foreach ($name in 'hidden.door', 'public.contract') {
        $discoverScope = if ($name -eq 'hidden.door') { 'domain' } else { 'open' }
        Assert-True ((Invoke-Next (@{ op = 'service_register'; instance = $name; serviceType = 'machine.door'; schema = 'machine.door_state'
            major = 1; discoverScope = $discoverScope } + $private)).ok) "register $name"
    }
    foreach ($command in 'schema', 'state') {
        $absent = Read-QueryInstance $command 'absent.door' "$command-absent"
        $hidden = Read-QueryInstance $command 'hidden.door' "$command-hidden"
        Assert-True ($absent.status -ceq 'empty' -and $hidden.status -ceq 'empty' -and $absent.Count -eq 1 -and $hidden.Count -eq 1) 'hidden and missing instances have identical answers'
    }
    Assert-True ((Read-QueryInstance 'schema' 'public.contract' 'schema-public').status -ceq 'complete') 'an open schema is discoverable across domains'
    Assert-True ((Read-QueryInstance 'state' 'public.contract' 'state-private').status -ceq 'empty') 'discovery does not grant access to publication state'
    Assert-True ((Read-QueryInstance 'schema' 'not an instance' 'schema-invalid').status -ceq 'invalid_request') 'invalid instance name is rejected'
    Assert-True ((Invoke-Next @{ op = 'world_delta'; snapshotId = 'svc3'; factSequence = '11'
        changes = @(@{ op = 'node_state'; a = '3'; powered = 0; operational = 1 }) }).ok) 'APC 3 loses power behind the query APC'
    Assert-True ((Read-QueryInstance 'schema' 'door.a' 'schema-cut' '11').status -ceq 'empty') 'a cut-off service has no visible schema'
    Assert-True ((Read-QueryInstance 'state' 'door.a' 'state-cut' '11').status -ceq 'empty') 'a cut-off service has no visible state'
    Assert-True ((Invoke-Next @{ op = 'world_delta'; snapshotId = 'svc3'; factSequence = '12'
        changes = @(@{ op = 'node_state'; a = '3'; powered = 1; operational = 1 }) }).ok) 'APC 3 recovers'
    Assert-True ((Read-QueryInstance 'state' 'door.a' 'state-recovered' '12').sequence -ceq $sequence) 'the same publication is visible after repair'

    # P11 network.shell: the console sends one line to APC 2; the APC runs it against what it reaches and
    # what the console may discover, and the records come back over the network.
    Assert-True ((Invoke-Next (@{ op = 'endpoint_attach'; a = '2' } + @{ endpoint = 'apc-2s'; generation = '1' })).ok) 'attach the shell endpoint on APC 2'
    Assert-True ((Invoke-Next (@{ op = 'service_register'; instance = 'netshell.apc2'; serviceType = 'network.shell'; schema = 'shell.output'
        major = 1; minor = 0 } + @{ endpoint = 'apc-2s'; generation = '1' })).ok) 'APC 2 offers a shell'
    $script:shellRun = 0
    function Invoke-Shell([string] $Line, [string] $Sequence = '12', [switch] $Script) {
        $script:shellRun++
        $command = if ($Script) { 'run_script' } else { 'run' }
        $arguments = if ($Script) { @{ source = $Line } } else { @{ line = $Line } }
        Assert-True ((Invoke-Next @{ op = 'heartbeat'; snapshotId = 'svc3'; factSequence = $Sequence }).ok) "heartbeat before shell line $script:shellRun"
        Assert-True ((Invoke-Next (@{ op = 'invoke'; instance = 'netshell.apc2'; command = $command; idempotencyKey = "sh-$script:shellRun"
            arguments = $arguments; trafficClass = 1; timeoutTicks = 60 } + $engineer)).ok) "shell request sent: $Line"
        $answer = Wait-Result 'eng-1' $command
        Assert-True ($null -ne $answer -and $answer.producer -ceq 'apc-2s') "shell line answered: $Line"
        return $answer.payload
    }
    $out = Invoke-Shell 'services -type machine.door | sort instance | first 1'
    Assert-True ($out.status -ceq 'ok' -and $out.count -eq 1 -and $out.rows -ceq 'instance=door.a,type=machine.door,hops=1' -and $out.fuel -gt 0) "a pipeline runs on the device: $($out | ConvertTo-Json -Compress)"
    $out = Invoke-Shell 'services | where hops -eq 0 | where instance -like netshell.* | select instance'
    Assert-True ($out.status -ceq 'ok' -and $out.rows -ceq 'instance=netshell.apc2') "the device lists its own services at no hops: $($out.rows)"
    $out = Invoke-Shell 'links | where state -eq up | count'
    Assert-True ($out.rows -ceq 'count=2') "the device reads its own ports: $($out.rows)"
    $out = Invoke-Shell 'trace 3 | select state hops'
    Assert-True ($out.rows -ceq 'state=reachable,hops=1') "a trace is a record: $($out.rows)"
    $out = Invoke-Shell 'self | select state wired wiredUp'
    Assert-True ($out.rows -ceq 'state=active,wired=2,wiredUp=2') "the device describes itself: $($out.rows)"
    $bad = Invoke-Shell 'services | explode'
    Assert-True ($bad.status -ceq 'unknown_command' -and $bad.count -eq 0 -and $bad.position -eq 11 -and $bad.error) "an unknown stage is an answer with its position: $($bad | ConvertTo-Json -Compress)"
    Assert-True ((Invoke-Shell 'services | where hops').status -ceq 'invalid_argument') 'wrong arguments are an answer'
    # invoke: the line sends an ordinary command as the console, from the console's node, and does not wait.
    function Take-DoorCommand {
        for ($poll = 0; $poll -lt 40; $poll++) {
            $found = (Invoke-Next @{ op = 'delivery_poll'; afterSequence = '0'; limit = 16 }).result.items |
                Where-Object { $_.recipient -ceq 'dev-3' -and $_.kind -ceq 'command' } | Select-Object -First 1
            if ($null -ne $found) {
                $null = Invoke-Next @{ op = 'delivery_ack'; acks = @(@{ deliveryId = $found.deliveryId; applied = $false; reason = 'shadow' }) }
                return $found
            }
            Start-Sleep -Milliseconds 100
        }
        return $null
    }
    $sent = Invoke-Shell 'invoke door.a set_state press=p1 state=open'
    Assert-True ($sent.status -ceq 'ok' -and $sent.count -eq 1 -and $sent.rows -clike 'instance=door.a,command=set_state,status=sent,operation=*') "a line sends a command: $($sent | ConvertTo-Json -Compress)"
    $arrived = Take-DoorCommand
    Assert-True ($null -ne $arrived -and $arrived.instance -ceq 'door.a' -and $arrived.command -ceq 'set_state' -and $arrived.producer -ceq 'eng-1' -and
        $arrived.payload.press -ceq 'p1' -and $arrived.payload.state -ceq 'open') "the command reaches the door as the console's own: $($arrived | ConvertTo-Json -Compress -Depth 4)"
    $both = Invoke-Shell 'services -type machine.door | where instance -like door.* | sort instance | first 2 | invoke set_state press=p2 state=closed'
    Assert-True ($both.status -ceq 'ok' -and $both.count -eq 2 -and $both.rows -clike 'instance=door.a,*status=sent*;instance=door.b,*status=sent*') "a pipeline feeds the action: $($both.rows)"
    $one = Take-DoorCommand
    $two = Take-DoorCommand
    Assert-True ($null -ne $one -and $null -ne $two -and (@($one.instance, $two.instance) | Sort-Object) -join ',' -ceq 'door.a,door.b' -and $one.payload.press -ceq 'p2') 'each record of the pipeline got its command'
    $scripted = Invoke-Shell "if links | where state -eq up`r`n`tinvoke door.a set_state press=script1 state=open`r`n`tinvoke door.a set_state press=script2 state=closed`r`nend" -Script
    $one = Take-DoorCommand
    $two = Take-DoorCommand
    Assert-True ($scripted.status -ceq 'ok' -and $scripted.rows -clike '*status=sent*' -and $one.payload.press -ceq 'script1' -and
        $two.payload.press -ceq 'script2' -and $one.producer -ceq 'eng-1' -and $two.producer -ceq 'eng-1') 'two script lines reach the same service as distinct commands'
    $skipped = Invoke-Shell "if links | where state -eq missing`n  invoke door.a set_state press=skip state=open`nend" -Script
    Assert-True ($skipped.status -ceq 'ok' -and $skipped.count -eq 0) 'false script condition skips its action'
    Assert-True ((Invoke-Shell "services`ntrace 3`nservices" -Script).status -ceq 'source_failed') 'script graph searches share one budget'
    $wide = Invoke-Shell 'services | invoke set_state press=p3'
    Assert-True ($wide.status -ceq 'limit_exceeded' -and $wide.count -eq 0) "a pipeline too wide for an action is refused whole: $($wide | ConvertTo-Json -Compress)"
    Assert-True ((Invoke-Shell 'invoke door.a set_state').rows -cnotlike '*status=sent*') 'arguments outside the command schema send nothing'
    Assert-True ((Invoke-Shell 'invoke no.such set_state press=p4').rows -clike '*status=unknown_service*') 'a service the console cannot see is unknown to its line'
    Assert-True ((Invoke-Shell 'invoke netshell.apc2 run line=self').rows -clike '*status=forbidden*') 'a line may not start further lines'
    Start-Sleep -Milliseconds 500
    $stray = (Invoke-Next @{ op = 'delivery_poll'; afterSequence = '0'; limit = 16 }).result.items | Where-Object { $_.recipient -ceq 'dev-3' -and $_.kind -ceq 'command' }
    Assert-True ($null -eq $stray) 'refused lines sent no command'
    Assert-True ((Invoke-Shell 'trace nowhere').status -ceq 'source_failed') 'a command names its own usage'
    Assert-True ((Invoke-Next @{ op = 'world_delta'; snapshotId = 'svc3'; factSequence = '13'
        changes = @(@{ op = 'node_state'; a = '3'; powered = 0; operational = 1 }) }).ok) 'APC 3 loses power under the doors'
    $cut = Invoke-Shell 'services -type machine.door | count' '13'
    Assert-True ($cut.rows -ceq 'count=0') "services the device does not reach are not there: $($cut.rows)"
    $dead = Invoke-Shell 'links | where state -ne up | select node state' '13'
    Assert-True ($dead.rows -ceq 'node=3,state=unpowered') "the dead port is a record: $($dead.rows)"
    Assert-True ((Invoke-Next @{ op = 'world_delta'; snapshotId = 'svc3'; factSequence = '14'
        changes = @(@{ op = 'node_state'; a = '3'; powered = 1; operational = 1 }) }).ok) 'APC 3 has power again'
    Assert-True ((Invoke-Shell 'trace 3 | select state' '14').rows -ceq 'state=reachable') 'the repaired network is reachable again'

    if ($IsLinux) {
        Assert-StartupRejected $socketPath
        Assert-True ((Invoke-Next @{ op = 'game_stats' }).game.nodes -eq 3) 'second Host does not unlink live socket'
        $regular = Join-Path $work 'regular.sock'
        [IO.File]::WriteAllText($regular, 'preserve')
        Assert-StartupRejected $regular
        Assert-True ([IO.File]::ReadAllText($regular) -ceq 'preserve') 'regular file preserved'
        $link = Join-Path $work 'link.sock'
        $null = [IO.File]::CreateSymbolicLink($link, $regular)
        Assert-StartupRejected $link
        Assert-True ((Get-Item -LiteralPath $link).LinkTarget -ceq $regular) 'symlink preserved'
        Assert-True ([IO.File]::ReadAllText($regular) -ceq 'preserve') 'symlink target preserved'

        $owner.Dispose()
        & /bin/sh -c 'kill -TERM "$1"' sh $hostProcess.Id
        Assert-True ($LASTEXITCODE -eq 0 -and $hostProcess.WaitForExit(5000) -and $hostProcess.ExitCode -eq 0) 'graceful Host SIGTERM'
        Assert-True (!(Test-Path -LiteralPath $socketPath) -and (Test-Path -LiteralPath "$socketPath.lock")) 'listener releases socket and retains lock file'
    }
    Write-Host 'NETDM_IPC_PASS'
}
finally {
    if ($null -ne $owner) { $owner.Dispose() }
    if ($null -ne $visitor) { $visitor.Dispose() }
    if (-not $hostProcess.HasExited) { $hostProcess.Kill($true); $hostProcess.WaitForExit() }
    $hostProcess.Dispose()
}
