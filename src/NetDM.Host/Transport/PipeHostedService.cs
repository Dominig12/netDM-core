using System.IO.Pipes;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using NetDM.Core;
using NetDM.Services;

namespace NetDM.Host.Transport;

/// <summary>
/// Bootstrap x86-shim target. All graph mutations and routing happen in the x64 host.
/// Routing has global admission control, so unbounded client requests cannot create
/// unbounded routing tasks. One connection never writes concurrently to its stream.
/// </summary>
public sealed class PipeHostedService(NetworkRuntime runtime, PriorityWorkScheduler scheduler,
    GameNetworkService game, HostIpcModuleRouter modules, NetDM.Host.Research.ResearchHost research, ILogger<PipeHostedService> log)
    : BackgroundService
{
    private const int MaxConnections = 16;
    // Responses are written by one task per connection, so reading commands never
    // waits for the peer to drain replies. A peer that stops reading is fenced.
    private const int MaxPendingResponses = 256;
    private const int MaxPendingResponseBytes = 2 * 1024 * 1024;
    private const int MaxWriteBatchBytes = 64 * 1024;
    // Explicit pipe buffers: with the default the writer rendezvous with each read.
    private const int PipeBufferBytes = 64 * 1024;
    private readonly SemaphoreSlim _connectionSlots = new(MaxConnections, MaxConnections);
    private readonly SemaphoreSlim _gameOwner = new(1, 1);
    private volatile bool _ipcReady;
    private sealed record SyncState(string Phase, Guid? StreamId = null,
        long LastFactSequence = 0, long Heartbeat = 0);
    private volatile SyncState _sync = new("disconnected");

    public bool IpcReady => _ipcReady;
    /// <summary>
    /// <c>Ready</c> is the ability to serve the game (§17.4): the simulated network and durable research
    /// storage. <c>NetworkReady</c> alone admits network work: a research journal that cannot be written
    /// blocks research, not messages, radio or doors.
    /// </summary>
    public sealed record Readiness(bool Ready, bool NetworkReady, bool IpcReady, bool WorkersReady,
        bool HeartbeatFresh, bool ResearchReady, string Research, string Phase, Guid? StreamId, string LastFactSequence);

    public Readiness GameReadiness()
    {
        var (network, ipc, workers, heartbeat, state) = Network();
        var storage = research.Storage();
        return new Readiness(network && storage.Ready, network, ipc, workers, heartbeat, storage.Ready, storage.State,
            state.Phase, state.StreamId, state.LastFactSequence.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Admission of network work: published snapshot, fresh heartbeat, living workers.</summary>
    private bool NetworkReady => Network().Ready;

    private (bool Ready, bool Ipc, bool Workers, bool Heartbeat, SyncState State) Network()
    {
        var state = _sync;
        var ipc = _ipcReady;
        var workers = game.WorkersReady;
        var heartbeat = state.Heartbeat != 0 && Stopwatch.GetElapsedTime(state.Heartbeat) <= TimeSpan.FromSeconds(5);
        return (ipc && workers && heartbeat && state.Phase == "ready", ipc, workers, heartbeat, state);
    }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new DmBoolean() }
    };
    private static readonly JsonSerializerOptions FramedJsonOptions = new(JsonOptions)
    {
        Converters = { new WireInteger<long>(), new WireInteger<ulong>() }
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var framed = Environment.GetEnvironmentVariable("NETDM_IPC_MODE") switch
        {
            null or "legacy" => false,
            "v1" => true,
            _ => throw new InvalidOperationException("NETDM_IPC_MODE must be legacy or v1.")
        };
        var pipeName = Environment.GetEnvironmentVariable("NETDM_PIPE_NAME") ?? "netdm-core";
        if (pipeName.Length is < 1 or > 80 || pipeName.Any(c =>
            !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new InvalidOperationException("Invalid NETDM_PIPE_NAME.");

        using var unix = framed && !OperatingSystem.IsWindows() ? ListenUnix() : null;
        var listener = unix?.Listener;
        _ipcReady = listener is not null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var clients = new List<Task>(MaxConnections);
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await _connectionSlots.WaitAsync(lifetime.Token);
                try
                {
                    Stream stream = listener is not null
                        ? new NetworkStream(await listener.AcceptAsync(lifetime.Token), ownsSocket: true)
                        : await AcceptPipeAsync(pipeName, framed, lifetime.Token);
                    clients.RemoveAll(task => task.IsCompleted);
                    clients.Add(RunClientAsync(stream, framed, lifetime.Token));
                }
                catch
                {
                    _connectionSlots.Release();
                    throw;
                }
            }
        }
        finally
        {
            _ipcReady = false;
            lifetime.Cancel();
            await Task.WhenAll(clients);
        }
    }

    private static UnixSocketListener ListenUnix()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Framed IPC supports Windows and Linux.");
        var path = Environment.GetEnvironmentVariable("NETDM_SOCKET_PATH") ?? "/run/netdm/core.sock";
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("NETDM_SOCKET_PATH must be absolute.");
        var directory = Path.GetDirectoryName(path)!;
        const UnixFileMode other = UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((File.GetUnixFileMode(directory) & other) != 0)
            throw new InvalidOperationException("IPC socket directory must be private to its owner/group.");

        return UnixSocketListener.Open(path, MaxConnections);
    }

    private async Task<Stream> AcceptPipeAsync(string name, bool framed, CancellationToken ct)
    {
        var options = PipeOptions.Asynchronous;
        if (framed)
            options |= PipeOptions.CurrentUserOnly;
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, MaxConnections,
            PipeTransmissionMode.Byte, options, PipeBufferBytes, PipeBufferBytes);
        try
        {
            _ipcReady = true;
            await pipe.WaitForConnectionAsync(ct);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    private async Task RunClientAsync(Stream stream, bool framed, CancellationToken ct)
    {
        try
        {
            await using (stream)
                await ServeAsync(stream, framed, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                log.LogWarning(ex, "IPC client disconnected");
        }
        finally { _connectionSlots.Release(); }
    }

    private async Task ServeAsync(Stream stream, bool framed, CancellationToken token)
    {
        var wire = new IpcFrames(stream, framed);
        var json = framed ? FramedJsonOptions : JsonOptions;
        var maxBytes = framed ? IpcFrames.MaxBodyBytes : IpcFrames.LegacyMaxBodyBytes;
        using var disconnect = CancellationTokenSource.CreateLinkedTokenSource(token);
        var outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MaxPendingResponses)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        long pendingResponseBytes = 0;
        var writer = WriteResponsesAsync();
        var tasks = new List<Task>(64);
        Guid? fence = null;
        Guid? ownedStream = null;
        long nextSequence = 1;
        var seenNodes = framed ? new HashSet<NodeId>() : null;
        var stagedProtocol = false;
        WorldSnapshotStage? stage = null;
        WorldSnapshotStage? areaUpdate = null;
        string? publishedSnapshot = null;
        ApcMap? apcMap = null;
        var devices = new DeviceAttachmentTable();
        // Power and operational state of map nodes; devices carry theirs in their facts.
        var states = new NodeStateTable();
        // Counts accepted state changes: a node can go down without any topology change to show it.
        long stateRevision = 0;
        long lastFactSequence = 0;

        // Serializes on the caller and only enqueues. Ordering is the enqueue order.
        Task SendAsync(PipeResponse result)
        {
            if (framed && result.StreamId is null)
                result = result with { StreamId = ownedStream };
            var body = JsonSerializer.SerializeToUtf8Bytes(result, json);
            while (body.Length > maxBytes && result.ReceiptPage is { Receipts.Length: > 1 } page)
            {
                var receipts = page.Receipts[..(page.Receipts.Length / 2)];
                result = result with
                {
                    ReceiptPage = page with { Receipts = receipts, NextSequence = receipts[^1].Sequence }
                };
                body = JsonSerializer.SerializeToUtf8Bytes(result, json);
            }
            if (body.Length > maxBytes)
                body = JsonSerializer.SerializeToUtf8Bytes(new PipeResponse(result.Id, false,
                    "response_too_large", StreamId: result.StreamId), json);
            if (Interlocked.Add(ref pendingResponseBytes, body.Length) > MaxPendingResponseBytes ||
                !outbox.Writer.TryWrite(body))
            {
                Interlocked.Add(ref pendingResponseBytes, -body.Length);
                if (!disconnect.IsCancellationRequested)
                    log.LogWarning("IPC peer stopped reading responses; closing the session.");
                disconnect.Cancel();
                throw new IOException("IPC response queue is full or closed.");
            }
            log.LogTrace("IPC queued {Id} ({Bytes} bytes)", result.Id, body.Length);
            return Task.CompletedTask;
        }

        async Task WriteResponsesAsync()
        {
            var batch = new List<byte[]>(32);
            try
            {
                while (await outbox.Reader.WaitToReadAsync(disconnect.Token))
                {
                    batch.Clear();
                    var batchBytes = 0;
                    while (batchBytes < MaxWriteBatchBytes && outbox.Reader.TryRead(out var body))
                    {
                        batch.Add(body);
                        batchBytes += body.Length;
                    }
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(disconnect.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    var writeStarted = Stopwatch.GetTimestamp();
                    await wire.WriteBatchAsync(batch, deadline.Token);
                    var written = Stopwatch.GetElapsedTime(writeStarted);
                    if (written > TimeSpan.FromSeconds(1))
                        log.LogWarning("IPC response write of {Frames} frames took {Milliseconds} ms.",
                            batch.Count, (long)written.TotalMilliseconds);
                    Interlocked.Add(ref pendingResponseBytes, -batchBytes);
                    log.LogTrace("IPC wrote {Frames} frames ({Bytes} bytes)", batch.Count, batchBytes);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // A write that outlived its deadline fences the session; say so instead of failing silently.
                if (!disconnect.IsCancellationRequested)
                    log.LogWarning("IPC response writer stopped: {Reason}", ex.GetType().Name);
            }
            finally
            {
                // A failed or stalled write fences the connection and stops the reader.
                disconnect.Cancel();
            }
        }

        async ValueTask<byte[]?> ReadAsync()
        {
            if (!framed)
                return await wire.ReadAsync(disconnect.Token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(disconnect.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(fence.HasValue ? 30 : 5));
            return await wire.ReadAsync(deadline.Token);
        }

        try
        {
            await ReadCommandsAsync();
        }
        finally
        {
            if (fence.HasValue)
                _sync = new("disconnected");
            outbox.Writer.TryComplete();
            try
            {
                // Give already queued replies, such as a Hello rejection, a bounded flush.
                try { await writer.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); }
                catch (TimeoutException) { }
                disconnect.Cancel();
                await Task.WhenAll(tasks.Append(writer));
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            finally
            {
                disconnect.Cancel();
                if (fence.HasValue)
                {
                    try { game.ResetTopology(); }
                    finally { _gameOwner.Release(); }
                }
            }
        }

        async Task ReadCommandsAsync()
        {
            while (!disconnect.IsCancellationRequested && await ReadAsync() is { } body)
            {
                PipeRequest? request;
                try { request = JsonSerializer.Deserialize<PipeRequest>(body, json); }
                catch (JsonException)
                {
                    await SendAsync(new PipeResponse(null, false, "invalid_json"));
                    if (framed && !fence.HasValue)
                        return;
                    continue;
                }
                if (request is null || string.IsNullOrWhiteSpace(request.Op))
                {
                    await SendAsync(new PipeResponse(request?.Id, false, "Missing operation."));
                    if (framed && !fence.HasValue)
                        return;
                    continue;
                }
                if (request.Id?.Length > 128)
                {
                    await SendAsync(new PipeResponse(null, false, "invalid_request_id"));
                    if (framed && !fence.HasValue)
                        return;
                    continue;
                }

                if (framed)
                {
                    if (request.Op == "hello")
                    {
                        await SendAsync(Hello(request));
                        if (!fence.HasValue)
                            return;
                        continue;
                    }
                    if (!fence.HasValue || request.OwnerFence != fence)
                    {
                        await SendAsync(new PipeResponse(request.Id, false, "owner_required"));
                        if (!fence.HasValue)
                            return;
                        continue;
                    }
                    if (!Guid.TryParse(request.StreamId, out var streamId) || streamId != ownedStream)
                    {
                        await SendAsync(new PipeResponse(request.Id, false, "stream_changed",
                            StreamId: ownedStream));
                        continue;
                    }
                    if (request.CommandSequence != nextSequence || nextSequence == long.MaxValue)
                    {
                        await SendAsync(new PipeResponse(request.Id, false, "sequence_mismatch",
                            NextSequence: nextSequence));
                        continue;
                    }
                    nextSequence++;
                }

                if (request.Op == "packet_send")
                {
                    if (stagedProtocol && !NetworkReady)
                    {
                        await SendAsync(new PipeResponse(request.Id, false, "game_not_ready"));
                        continue;
                    }
                    tasks.RemoveAll(t => t.IsCompleted);
                    if (tasks.Count >= 64)
                    {
                        await SendAsync(new PipeResponse(request.Id, false, "busy"));
                        continue;
                    }
                    tasks.Add(SendPacketAsync(request));
                    continue;
                }

                if (!framed && request.Op == "route")
                {
                    // No per-request routing Task.Run: the process-wide scheduler owns
                    // CPU admission and worker concurrency for both players and benchmarks.
                    tasks.RemoveAll(t => t.IsCompleted);
                    if (tasks.Count >= 64 ||
                        !scheduler.TrySchedule(
                            ct => runtime.Route(new NodeId(request.A), new NodeId(request.B),
                                cancellationToken: ct),
                            WorkPriority.Interactive, disconnect.Token,
                            out Task<RouteResult>? scheduled))
                    {
                        await SendAsync(new PipeResponse(request.Id, false, "busy"));
                        continue;
                    }

                    tasks.Add(SendRouteAsync(scheduled!, request.Id));
                    continue;
                }

                try
                {
                    var started = Stopwatch.GetTimestamp();
                    var result = request.Op switch
                    {
                        "snapshot_begin" when stagedProtocol => BeginSnapshot(request),
                        "snapshot_chunk" when stagedProtocol => AppendSnapshot(request),
                        "snapshot_commit" when stagedProtocol => CommitSnapshot(request),
                        "snapshot_ready" when stagedProtocol => ReadySnapshot(request),
                        "area_update_begin" when stagedProtocol => BeginAreaUpdate(request),
                        "area_update_chunk" when stagedProtocol => AppendAreaUpdate(request),
                        "area_update_commit" when stagedProtocol => CommitAreaUpdate(request),
                        "world_delta" when stagedProtocol => ApplyWorldDelta(request),
                        "heartbeat" when stagedProtocol => Heartbeat(request),
                        "publish" or "invoke" or "discover" when stagedProtocol => NetworkReady
                            ? ServiceAdmission(request) : new PipeResponse(request.Id, false, "game_not_ready"),
                        "endpoint_attach" or "endpoint_detach" or "service_register" or "subscribe"
                            or "unsubscribe" or "delivery_poll" or "delivery_ack" or "delivery_status"
                            or "command_status" when stagedProtocol => ServiceCommand(request),
                        "topology_view" when stagedProtocol => TopologyView(request),
                        // Feature modules own their IPC commands and can be registered by DI.
                        _ when stagedProtocol && modules.TryGet(request.Op, out _) => ModuleCommand(request),
                        "stats" => Stats(request),
                        "game_stats" => new PipeResponse(request.Id, true, Game: game.Status()),
                        "game_batch" when !stagedProtocol => ApplyGameBatch(request, seenNodes),
                        "game_reset" when !stagedProtocol => Reset(request.Id),
                        "game_radio_batch" when !stagedProtocol => UpdateGameRadioBatch(request),
                        "game_radio" when !stagedProtocol => UpdateGameRadio(request),
                        "packet_receipts" when !stagedProtocol || NetworkReady => PollPackets(request, framed),
                        "packet_cancel" when !stagedProtocol || NetworkReady => CancelPacket(request),
                        "game_add_node" or "game_remove_node" or "game_connect"
                            or "game_disconnect" or "game_move_node" when !stagedProtocol =>
                            ApplyGameChanges(request.Id, [ToChange(request)], seenNodes),
                        "batch" when !framed => ApplyBatch(request),
                        "add_node" or "remove_node" or "connect" or "disconnect" or "move_node" when !framed =>
                            ApplyChanges(request.Id, [ToChange(request)]),
                        _ => new PipeResponse(request.Id, false, "Unknown operation.")
                    };
                    await SendAsync(result);
                    log.LogDebug("IPC {Id} {Operation} ok={Ok}", request.Id, request.Op, result.Ok);
                    var elapsed = Stopwatch.GetElapsedTime(started);
                    if (elapsed > TimeSpan.FromSeconds(1))
                        log.LogWarning("IPC {Operation} took {Milliseconds} ms.", request.Op, (long)elapsed.TotalMilliseconds);
                }
                catch (Exception ex) when (ex is ArgumentException or OverflowException)
                {
                    await SendAsync(new PipeResponse(request.Id, false, ex.Message));
                }
            }
        }

        PipeResponse Hello(PipeRequest request)
        {
            if (fence.HasValue)
                return new PipeResponse(request.Id, false, "already_negotiated");
            if (request.ProtocolMajor != 1 || request.ProtocolMinor is < 0 or > 1)
                return new PipeResponse(request.Id, false, "unsupported_protocol");
            if (request.ServerId is not { Length: > 0 and <= 64 } server ||
                string.IsNullOrWhiteSpace(server) || request.RoundEpoch is not { } round || round == Guid.Empty)
                return new PipeResponse(request.Id, false, "invalid_session_identity");
            if (!_gameOwner.Wait(0))
                return new PipeResponse(request.Id, false, "owner_busy");
            fence = Guid.NewGuid();
            // The round's research ledger survives sessions of the same round and Host restarts.
            research.OpenRound(round);
            ownedStream = game.ResetSession().StreamId;
            stagedProtocol = request.ProtocolMinor == 1;
            _sync = new(stagedProtocol ? "awaiting_snapshot" : "development", ownedStream);
            return new PipeResponse(request.Id, true, StreamId: ownedStream,
                Session: new Welcome(1, request.ProtocolMinor, server, round, fence.Value, IpcFrames.MaxBodyBytes),
                NextSequence: nextSequence);
        }

        PipeResponse BeginSnapshot(PipeRequest request)
        {
            if (request.FactSequence < lastFactSequence || request.Profile is not (null or "apc_map"))
                throw new ArgumentException("Invalid snapshot sequence or profile.");
            stage = CreateStage(request, request.SnapshotId, request.Profile == "apc_map");
            areaUpdate = null;
            _sync = new("syncing", ownedStream, lastFactSequence);
            return StageAck(request, stage);
        }

        WorldSnapshotStage RequireStage(PipeRequest request) => stage is not null && request.SnapshotId == stage.Id
            ? stage : throw new ArgumentException("Snapshot ID is not active.");

        PipeResponse AppendSnapshot(PipeRequest request) => AppendChunk(request, RequireStage(request));

        PipeResponse AppendChunk(PipeRequest request, WorldSnapshotStage snapshot)
        {
            if (request.FactsJson is not { } facts || request.Digest is not { Length: 32 } digest ||
                !string.Equals(digest, WorldSnapshotStage.Digest(facts), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Invalid snapshot chunk digest.");
            var (topology, radios, geometry, apcs, access) = ParseSnapshotFacts(facts);
            snapshot.Append(request.ChunkIndex, digest.ToUpperInvariant(), topology, radios, geometry, apcs, access);
            return StageAck(request, snapshot);
        }

        PipeResponse CommitSnapshot(PipeRequest request)
        {
            var snapshot = RequireStage(request);
            snapshot.Commit(request.Digest);
            return StageAck(request, snapshot);
        }

        PipeResponse ReadySnapshot(PipeRequest request)
        {
            var snapshot = RequireStage(request);
            if (!snapshot.Committed || request.FactSequence != snapshot.LastSequence)
                throw new ArgumentException("Snapshot is not committed at the requested sequence.");
            stage = null;
            var candidate = snapshot.Complete();
            seenNodes = snapshot.SeenNodes;
            publishedSnapshot = snapshot.Id;
            apcMap = snapshot.Map;
            game.ApcBridges = apcMap?.Bridges;
            devices = new DeviceAttachmentTable();
            states = new NodeStateTable();
            ownedStream = game.PublishSnapshot(candidate, apcMap?.Wiring(states, devices) ?? WiringReport.Empty);
            stateRevision = 0;
            lastFactSequence = snapshot.LastSequence;
            stage = null;
            _sync = new("ready", ownedStream, lastFactSequence, Stopwatch.GetTimestamp());
            return StageAck(request, snapshot) with { StreamId = ownedStream };
        }

        PipeResponse ApplyWorldDelta(PipeRequest request)
        {
            if (request.Changes is not { Length: > 0 and <= WorldSnapshotStage.MaxChunkFacts })
                throw new ArgumentException("Invalid world delta size.");
            if (request.Changes.Any(IsWorldFact))
                return ApplyWorldFacts(request);
            if (request.Changes.Any(change => change?.AccessRange is not null))
                throw new ArgumentException("An access range is a fact of the APC map.");
            var changes = request.Changes.Select(ToChange).ToArray();
            if ((stage is null ? apcMap : stage.Map) is not null)
                for (var i = 0; i < changes.Length; i++)
                    if (changes[i] is { Operation: TopologyOperation.AddNode, CanForward: null } change)
                        changes[i] = change with { CanForward = false };
            if (stage is { } snapshot)
            {
                RequireStage(request);
                snapshot.AppendDelta(request.FactSequence, changes, CollectNewNodes(changes, snapshot.SeenNodes));
                return StageAck(request, snapshot);
            }
            if (_sync.Phase != "ready" || publishedSnapshot is null || request.SnapshotId != publishedSnapshot ||
                lastFactSequence == long.MaxValue || request.FactSequence != lastFactSequence + 1)
                throw new ArgumentException("World delta sequence or snapshot ID is invalid.");
            var world = game.Snapshot;
            if (changes.Any(change => devices.Contains(change.A)))
                throw new ArgumentException("Raw topology cannot change device nodes.");
            WorldSnapshotStage.ValidateDelta(world.Physical, changes);
            WorldSnapshotStage.ValidateApcMoves(changes, apcMap?.Levels);
            // Devices wired to a removed APC fall back to radio in the same publication.
            var removed = changes.Where(change => change.Operation == TopologyOperation.RemoveNode)
                .Select(change => change.A).ToHashSet();
            var plan = removed.Count == 0 || devices.Count == 0 ? null : devices.Plan(world, apcMap?.Index,
                id => !removed.Contains(id) && world.Physical.Nodes.ContainsKey(id), states.IsActive, removed, [], [],
                devices.AttachedTo(apcMap?.Index, removed).ToArray());
            TopologyChange[] batch = plan is null ? changes : [.. changes, .. plan.Changes];
            var added = CollectNewNodes(batch, seenNodes!);
            var result = game.ApplyTopology(batch, plan?.Radios);
            if (plan is not null)
                devices.Commit(plan);
            states.RemoveWhere(removed.Contains);
            seenNodes!.UnionWith(added);
            lastFactSequence = request.FactSequence;
            _sync = _sync with { LastFactSequence = lastFactSequence };
            return new PipeResponse(request.Id, true, Applied: result.Applied, Skipped: result.Skipped,
                LastFactSequence: lastFactSequence);
        }

        /// <summary>
        /// Live world facts of devices and map nodes. DM reports position, mounting and state;
        /// the Host derives wired or radio attachments and live links from the published APC map
        /// and applies them as one atomic batch.
        /// </summary>
        PipeResponse ApplyWorldFacts(PipeRequest request)
        {
            if (stage is not null || apcMap is null || _sync.Phase != "ready" || publishedSnapshot is null ||
                request.SnapshotId != publishedSnapshot || lastFactSequence == long.MaxValue ||
                request.FactSequence != lastFactSequence + 1)
                throw new ArgumentException("Device facts require the published APC map and the next fact sequence.");
            var world = game.Snapshot;
            var map = apcMap;
            var upserts = new List<DeviceFact>();
            var removals = new List<NodeId>();
            var reported = new Dictionary<NodeId, NodeState>();
            foreach (var fact in request.Changes!)
            {
                switch (fact)
                {
                    case { Op: "device_state", X: { } x, Y: { } y, Z: { } z, Anchored: { } anchored, Range: { } range,
                        Mount: "fixed" or "mobile" }:
                        upserts.Add(new DeviceFact(new NodeId(fact.A), new NodePosition(x, y, z),
                            fact.Mount == "fixed" ? DeviceMount.Fixed : DeviceMount.Mobile, anchored, range,
                            fact.Powered ?? true, fact.Operational ?? true) { ExecutionPowered = fact.ExecutionPowered });
                        break;
                    case { Op: "device_remove" }:
                        removals.Add(new NodeId(fact.A));
                        break;
                    case { Op: "node_state", Powered: { } powered, Operational: { } operational }:
                        var node = new NodeId(fact.A);
                        if (!map.Nodes.ContainsKey(node) || !world.Physical.Nodes.ContainsKey(node) ||
                            !reported.TryAdd(node, new NodeState(powered, operational)))
                            throw new ArgumentException("node_state must name a distinct live node of the APC map.");
                        break;
                    default:
                        throw new ArgumentException("A device delta contains only valid device facts.");
                }
            }
            // An APC that changes activity rewires its map links, its access point and its devices.
            var flipped = reported.Where(pair => pair.Value.Active != states.IsActive(pair.Key))
                .Select(pair => pair.Key).ToHashSet();
            bool ApcActive(NodeId id) => reported.TryGetValue(id, out var state) ? state.Active : states.IsActive(id);
            List<TopologyChange> changes = flipped.Count == 0 ? [] : map.LinkChanges(world.Physical, flipped, ApcActive);
            List<RadioNodeUpdate> radios = flipped.Count == 0 ? [] : map.RadioChangesFrom(world, ApcActive, flipped);
            var plan = devices.Plan(world, map.Index,
                id => map.Nodes.ContainsKey(id) && world.Physical.Nodes.ContainsKey(id), ApcActive,
                [], upserts, removals, devices.AttachedTo(map.Index, flipped).ToArray());
            changes.AddRange(plan.Changes);
            radios.AddRange(plan.Radios);
            var stateChanged = reported.Any(pair => pair.Value != states.Get(pair.Key)) ||
                upserts.Any(fact => devices.TryGet(fact.Id, out var stored)
                    ? stored.State != fact.State || (stored.ExecutionPowered == false) != (fact.ExecutionPowered == false)
                    : !fact.State.Active || fact.ExecutionPowered == false) ||
                removals.Any(id => devices.TryGet(id, out var stored) && (!stored.State.Active || stored.ExecutionPowered == false));
            var added = CollectNewNodes(changes, seenNodes!);
            WiringReport CommitPower()
            {
                devices.Commit(plan);
                foreach (var (id, state) in reported)
                    states.Set(id, state);
                return map.Wiring(states, devices);
            }
            var result = stateChanged
                ? game.ApplyWorldTopology(changes, radios, CommitPower)
                : game.ApplyTopology(changes, radios);
            if (!stateChanged)
                devices.Commit(plan);
            if (stateChanged)
                stateRevision++;
            seenNodes!.UnionWith(added);
            lastFactSequence = request.FactSequence;
            _sync = _sync with { LastFactSequence = lastFactSequence };
            return new PipeResponse(request.Id, true, Applied: result.Applied, Skipped: result.Skipped,
                LastFactSequence: lastFactSequence, Result: new { devices = DescribeAttachments(plan) });
        }

        /// <summary>
        /// Bounded view of one level for the session owner. DM decides who may see it;
        /// the same version answers `unchanged`, so polling an open screen stays cheap.
        /// </summary>
        PipeResponse TopologyView(PipeRequest request)
        {
            if (_sync.Phase != "ready")
                return new PipeResponse(request.Id, false, "game_not_ready");
            if (request.Z is not { } level)
                return new PipeResponse(request.Id, false, "invalid_request");
            var world = game.Snapshot;
            // Both counters only grow, so their sum changes whenever the graph or a node state does.
            var version = world.Version + stateRevision;
            if (request.SinceVersion == version)
                return new PipeResponse(request.Id, true, Result: new { version, z = level, unchanged = true });
            var limit = Math.Clamp(request.MaxNodes ?? MaxViewNodes, 1, MaxViewNodes);
            while (true)
            {
                var response = new PipeResponse(request.Id, true,
                    Result: BuildView(world, version, level, limit, apcMap, devices, states));
                if (limit == 1 || JsonSerializer.SerializeToUtf8Bytes(response, json).Length <= MaxViewBytes)
                    return response;
                limit /= 2;
            }
        }

        void RequirePublishedMap(PipeRequest request)
        {
            if (_sync.Phase != "ready" || publishedSnapshot is null || request.SnapshotId != publishedSnapshot)
                throw new ArgumentException("Area update requires a published world snapshot.");
        }

        PipeResponse BeginAreaUpdate(PipeRequest request)
        {
            RequirePublishedMap(request);
            if (lastFactSequence == long.MaxValue || request.FactSequence != lastFactSequence + 1)
                throw new ArgumentException("Area update sequence is invalid.");
            areaUpdate = CreateStage(request, request.UpdateId, true);
            return StageAck(request, areaUpdate) with { SnapshotId = publishedSnapshot, UpdateId = areaUpdate.Id };
        }

        WorldSnapshotStage RequireAreaUpdate(PipeRequest request)
        {
            RequirePublishedMap(request);
            if (areaUpdate is null || request.UpdateId != areaUpdate.Id || lastFactSequence == long.MaxValue ||
                areaUpdate.LastSequence != lastFactSequence + 1)
                throw new ArgumentException("Area update ID or base sequence is no longer active.");
            return areaUpdate;
        }

        PipeResponse AppendAreaUpdate(PipeRequest request) => AppendChunk(request, RequireAreaUpdate(request)) with
        {
            SnapshotId = publishedSnapshot,
            UpdateId = request.UpdateId
        };

        PipeResponse CommitAreaUpdate(PipeRequest request)
        {
            var update = RequireAreaUpdate(request);
            var map = update.BuildMap(request.Digest);
            var world = game.Snapshot;
            // A node keeps its reported state across map revisions while it stays on the map.
            var changes = map.ChangesFrom(apcMap, world.Physical, states.IsActive);
            var radios = map.RadioChangesFrom(world, states.IsActive);
            // The client budget bounds the map's own changes; device follow-ups are bounded by the device cap.
            if (changes.Count > WorldSnapshotStage.MaxDeltaFacts || radios.Count > WorldSnapshotStage.MaxDeltaFacts)
                throw new ArgumentException("Area update exceeds the atomic topology change budget.");
            // Every device follows the new map in the same publication: no old and new path coexist.
            var removed = changes.Where(change => change.Operation == TopologyOperation.RemoveNode)
                .Select(change => change.A).ToHashSet();
            var plan = devices.Count == 0 ? null : devices.Plan(world, map.Index, map.Nodes.ContainsKey,
                states.IsActive, removed, [], [], devices.Ids.ToArray());
            if (plan is not null)
            {
                changes.AddRange(plan.Changes);
                radios.AddRange(plan.Radios);
            }
            var added = CollectNewNodes(changes, seenNodes!);
            var result = game.ApplyWorldTopology(changes, radios, () =>
            {
                if (plan is not null)
                    devices.Commit(plan);
                if (states.InactiveCount != 0)
                    states.RemoveWhere(id => !map.Nodes.ContainsKey(id));
                return map.Wiring(states, devices);
            });
            seenNodes!.UnionWith(added);
            apcMap = map;
            game.ApcBridges = map.Bridges;
            areaUpdate = null;
            lastFactSequence = update.LastSequence;
            _sync = _sync with { LastFactSequence = lastFactSequence };
            return new PipeResponse(request.Id, true, Applied: result.Applied, Skipped: result.Skipped,
                LastFactSequence: lastFactSequence, SnapshotId: publishedSnapshot, UpdateId: update.Id);
        }

        PipeResponse Heartbeat(PipeRequest request)
        {
            if (_sync.Phase == "ready")
            {
                if (request.SnapshotId != publishedSnapshot || request.FactSequence != lastFactSequence)
                    throw new ArgumentException("Heartbeat does not match the published snapshot.");
                _sync = _sync with { Heartbeat = Stopwatch.GetTimestamp() };
            }
            return new PipeResponse(request.Id, true, LastFactSequence: lastFactSequence);
        }

        PipeResponse Reset(string? id)
        {
            var (removed, streamId) = game.ResetSession();
            ownedStream = streamId;
            seenNodes?.Clear();
            return new PipeResponse(id, true, Applied: removed, StreamId: streamId);
        }

        async Task SendPacketAsync(PipeRequest request)
        {
            try
            {
                var submitted = await game.SendAsync(
                    request.RequestId ?? request.Id ?? "",
                    request.A, request.B, request.Bytes,
                    (PacketTrafficClass)request.TrafficClass,
                    request.TimeoutTicks, disconnect.Token);
                if (stagedProtocol && (_sync.Phase != "ready" || _sync.StreamId != submitted.StreamId))
                    return;
                if (!disconnect.IsCancellationRequested)
                    await SendAsync(new PipeResponse(request.Id, true,
                        Submission: submitted, StreamId: framed ? submitted.StreamId : null));
            }
            catch (OperationCanceledException) when (disconnect.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not IOException)
            {
                if (!disconnect.IsCancellationRequested)
                {
                    try { await SendAsync(new PipeResponse(request.Id, false, ex.Message,
                        StreamId: framed ? Guid.Parse(request.StreamId!) : null)); }
                    catch (IOException) { }
                }
            }
        }

        async Task SendRouteAsync(Task<RouteResult> scheduled, string? id)
        {
            try
            {
                var route = await scheduled;
                if (!disconnect.IsCancellationRequested)
                    await SendAsync(new PipeResponse(
                        id, true, Version: route.Version, Found: route.Found,
                        Cost: route.Cost, Path: route.Path.Select(n => n.Value).ToArray()));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not IOException)
            {
                if (!disconnect.IsCancellationRequested)
                {
                    try { await SendAsync(new PipeResponse(id, false, ex.Message)); }
                    catch (IOException) { } // Client closed its pipe.
                }
            }
        }
    }

    private static PipeResponse StageAck(PipeRequest request, WorldSnapshotStage snapshot) =>
        new(request.Id, true, SnapshotId: snapshot.Id, NextChunk: snapshot.NextChunk,
            LastFactSequence: snapshot.LastSequence);

    private WorldSnapshotStage CreateStage(PipeRequest request, string? id, bool apcMap)
    {
        if (id is not { Length: > 0 and <= 64 } || string.IsNullOrWhiteSpace(id) ||
            request.ChunkCount is not (>= 0 and <= WorldSnapshotStage.MaxChunks) ||
            request.FactCount is not (>= 0 and <= WorldSnapshotStage.MaxFacts) ||
            (request.ChunkCount == 0) != (request.FactCount == 0) ||
            request.FactCount < request.ChunkCount ||
            request.FactCount > request.ChunkCount * WorldSnapshotStage.MaxChunkFacts)
            throw new ArgumentException("Invalid snapshot declaration.");
        return new WorldSnapshotStage(id, request.ChunkCount.Value, request.FactCount.Value,
            request.FactSequence, game.PrepareSnapshot, apcMap);
    }

    /// <summary>Legacy game_radio facts default their range; device facts must state it.</summary>
    private const int DefaultRadioRange = 14;

    private const int MaxViewNodes = 2048;
    // Leaves room for the response envelope inside one 48 KiB frame.
    private const int MaxViewBytes = 40 * 1024;

    /// <summary>
    /// Nodes of one level as [id, x, y, flags], wired links as [a, b], radio associations of
    /// devices as [device, partner] and silent wired links as [a, b]: map links and device ports
    /// that exist in the world but carry no traffic because an end is inactive. Flags: 1 relay,
    /// 2 radio, 4 access point, 8 device, 16 APC, 32 without power, 64 not operational.
    /// Lists are cut at the node limit; `truncated` tells the reader.
    /// </summary>
    private static object BuildView(SharedMediumSnapshot world, long version, int level, int limit, ApcMap? map,
        DeviceAttachmentTable devices, NodeStateTable states)
    {
        var nodes = world.Physical.Nodes.Values
            .Where(node => node.Position is { } position && position.Z == level)
            .OrderBy(node => node.Id.Value).ToList();
        var truncated = nodes.Count > limit;
        if (truncated)
            nodes = nodes.Take(limit).ToList();
        var shown = nodes.Select(node => node.Id).ToHashSet();
        var rows = new List<object[]>(nodes.Count);
        var edges = new List<ulong[]>();
        var radio = new List<ulong[]>();
        var down = new List<ulong[]>();
        bool Silent(NodeId a, NodeId b) =>
            shown.Contains(b) && !world.Physical.Nodes[a].Links.ContainsKey(b) && down.Count < limit;
        foreach (var node in nodes)
        {
            var flags = node.CanForward ? 1 : 0;
            var hasRadio = world.Devices.TryGetValue(node.Id, out var profile);
            if (hasRadio)
                flags |= profile.AccessPoint ? 4 : 2;
            var device = devices.TryGet(node.Id, out var fact);
            if (device)
                flags |= 8;
            if (map?.Levels.ContainsKey(node.Id) == true)
                flags |= 16;
            var state = device ? fact.State : states.Get(node.Id);
            if (!state.Powered)
                flags |= 32;
            if (!state.Operational)
                flags |= 64;
            var position = node.Position!.Value;
            rows.Add([node.Id.Value, position.X, position.Y, flags]);
            if (device && map is not null)
            {
                var port = DeviceAttachmentTable.Resolve(fact, map.Index, map.Nodes.ContainsKey);
                if (port.Wired && Silent(node.Id, port.Apc))
                    down.Add([node.Id.Value, port.Apc.Value]);
            }
            foreach (var link in node.Links.Keys)
            {
                if (node.Id.Value >= link.Value || !shown.Contains(link))
                    continue;
                if (edges.Count >= limit * 2)
                {
                    truncated = true;
                    break;
                }
                edges.Add([node.Id.Value, link.Value]);
            }
            if (!device || !hasRadio)
                continue;
            foreach (var partner in SharedMediumNetwork.RadioPartners(world, node.Id))
            {
                // A pair of devices is listed once, from the smaller ID.
                if (!shown.Contains(partner) || (devices.Contains(partner) && partner.Value < node.Id.Value))
                    continue;
                if (radio.Count >= limit)
                {
                    truncated = true;
                    break;
                }
                radio.Add([node.Id.Value, partner.Value]);
            }
        }
        if (map is not null)
            foreach (var link in map.Links)
                if (shown.Contains(link.A) && Silent(link.A, link.B))
                    down.Add([link.A.Value, link.B.Value]);
        truncated |= down.Count >= limit;
        return new { version, z = level, truncated, nodes = rows, edges, radio, down };
    }

    private static bool IsWorldFact(PipeRequest? fact) => fact?.Op is "device_state" or "device_remove" or "node_state";

    private static object[] DescribeAttachments(DevicePlan plan) => plan.Attachments
        .Select(pair => (object)new
        {
            a = pair.Key.Value, attachment = pair.Value.Kind,
            apc = pair.Value.Wired ? pair.Value.Apc.Value : (ulong?)null,
            live = pair.Value.Live
        })
        .ToArray();

    private static (List<TopologyChange> Topology, List<RadioNodeUpdate> Radios,
        List<AreaSpan> Geometry, List<AreaApc> Apcs, List<(NodeId Node, int Range)> Access) ParseSnapshotFacts(string json)
    {
        PipeRequest?[]? facts;
        try { facts = JsonSerializer.Deserialize<PipeRequest?[]>(json, FramedJsonOptions); }
        catch (JsonException) { throw new ArgumentException("Invalid snapshot facts JSON."); }
        if (facts is not { Length: > 0 and <= WorldSnapshotStage.MaxChunkFacts })
            throw new ArgumentException("Invalid snapshot fact count.");
        var topology = new List<TopologyChange>(facts.Length);
        var radios = new List<RadioNodeUpdate>();
        var geometry = new List<AreaSpan>();
        var apcs = new List<AreaApc>();
        var access = new List<(NodeId Node, int Range)>();
        foreach (var fact in facts)
        {
            switch (fact?.Op)
            {
                case "add_node" when fact.X.HasValue && fact.Y.HasValue:
                    topology.Add(ToChange(fact));
                    if (fact.AccessRange is { } range)
                        access.Add((new NodeId(fact.A), range));
                    break;
                case "connect":
                    topology.Add(ToChange(fact));
                    break;
                case "game_radio" when fact.Enabled != false:
                    radios.Add(new RadioNodeUpdate(new NodeId(fact.A),
                        Profile: new RadioProfile(fact.Range ?? DefaultRadioRange, fact.Channel, fact.RadioCost), ChangeProfile: true));
                    break;
                case "area_span" when fact.X is { } start && fact.EndX is { } end && fact.Y is { } row:
                    geometry.Add(new AreaSpan(fact.Area, fact.Z ?? 0, row, start, end));
                    break;
                case "area_apc" when fact.X is { } x && fact.Y is { } y:
                    apcs.Add(new AreaApc(fact.Area, new NodeId(fact.A), new NodePosition(x, y, fact.Z ?? 0)));
                    break;
                default:
                    throw new ArgumentException("Invalid snapshot fact.");
            }
        }
        return (topology, radios, geometry, apcs, access);
    }

    // A page always fits a 48 KiB frame together with its envelope.
    private const int MaxDeliveryPageBytes = 32 * 1024;

    /// <summary>
    /// Domain commands are dispatched after the session fence and sequence checks.
    /// Each module explicitly declares whether it requires a published, live network.
    /// </summary>
    private PipeResponse ModuleCommand(PipeRequest request)
    {
        if (!modules.TryGet(request.Op, out var module))
            return new PipeResponse(request.Id, false, "Unknown operation.");
        if (module.RequiresNetworkReady && !NetworkReady)
            return new PipeResponse(request.Id, false, "game_not_ready");
        var response = module.Execute(request.Op, request.Payload);
        return new PipeResponse(request.Id, response.Ok, response.Error, Result: response.Value);
    }

    /// <summary>
    /// Local control plane of the published game runtime: endpoint lifecycle, service
    /// registration, the endpoint's own subscriptions and its inbox. None of these
    /// operations carries remote data; that only arrives through simulated delivery.
    /// </summary>
    private PipeResponse ServiceCommand(PipeRequest request)
    {
        if (_sync.Phase != "ready")
            return new PipeResponse(request.Id, false, "game_not_ready");
        try { return ServiceCommandCore(request); }
        // An ACK may start a result leg; its packet is routed right away.
        finally { game.DrainSubmissions(); }
    }

    private PipeResponse ServiceCommandCore(PipeRequest request)
    {
        var services = game.Services;
        var endpoint = new EndpointKey(request.Endpoint ?? "", request.Generation);
        switch (request.Op)
        {
            case "endpoint_attach":
                return Outcome(request, game.AttachEndpoint(endpoint, request.A, request.Domain ?? "station"));
            case "endpoint_detach":
                return Outcome(request, services.DetachEndpoint(endpoint));
            case "service_register":
                if (!TryScope(request.DiscoverScope, out var discover) || !TryScope(request.SubscribeScope, out var subscribe) ||
                    !TryScope(request.InvokeScope, out var invoke))
                    return Outcome(request, ServiceStatus.InvalidRequest);
                return Outcome(request, services.RegisterService(endpoint, request.Instance ?? "",
                    request.ServiceType ?? "", request.Schema ?? "", request.Major, request.Minor,
                    new ServicePolicy(discover, subscribe, invoke)));
            case "command_status":
                var (status, operation) = services.Operation(endpoint, request.OperationId);
                return Outcome(request, status, operation is null ? null : OperationDto(operation));
            case "subscribe":
                if (request.Mode is not ("latest_value" or "queue"))
                    return Outcome(request, ServiceStatus.InvalidRequest);
                var subscribed = services.Subscribe(endpoint, request.Key ?? "", request.Instance ?? "",
                    request.Schema ?? "", request.Major, request.Projection ?? "",
                    request.Mode == "queue" ? DeliveryMode.Queue : DeliveryMode.LatestValue, request.MaxPending);
                return Outcome(request, subscribed.Status,
                    new { subscriptionId = subscribed.SubscriptionId, revision = subscribed.Revision });
            case "unsubscribe":
                return Outcome(request, services.Unsubscribe(endpoint, request.Key ?? ""));
            case "delivery_poll":
                return new PipeResponse(request.Id, true,
                    Result: services.Poll(Math.Max(0, request.AfterSequence), request.Limit, MaxDeliveryPageBytes));
            case "delivery_ack":
                if (request.Acks is not { Length: > 0 and <= 64 })
                    return Outcome(request, ServiceStatus.InvalidRequest);
                var acks = request.Acks.Select(ack =>
                {
                    var (status, view) = services.Acknowledge(ack.DeliveryId, ack.Applied, ack.Reason, ack.Result);
                    return new { deliveryId = ack.DeliveryId, status = Code(status), state = view is null ? null : Code(view.State) };
                }).ToArray();
                return new PipeResponse(request.Id, true, Result: new { acks });
            default:
                var views = request.DeliveryId > 0
                    ? services.Delivery(request.DeliveryId) is { } one ? [one] : []
                    : services.MessageDeliveries(request.MessageId);
                return new PipeResponse(request.Id, true, Result: new
                {
                    deliveries = views.Select(view => new
                    {
                        deliveryId = view.DeliveryId, messageId = view.MessageId, kind = Code(view.Kind),
                        state = Code(view.State), recipient = view.Recipient,
                        recipientGeneration = view.RecipientGeneration, packetId = view.PacketId, reason = view.Reason
                    }).ToArray()
                });
        }
    }

    /// <summary>Operations that admit new network work: they require game readiness.</summary>
    private PipeResponse ServiceAdmission(PipeRequest request)
    {
        if (!Enum.IsDefined((PacketTrafficClass)request.TrafficClass))
            return Outcome(request, ServiceStatus.InvalidRequest);
        var endpoint = new EndpointKey(request.Endpoint ?? "", request.Generation);
        var trafficClass = (PacketTrafficClass)request.TrafficClass;
        switch (request.Op)
        {
            case "publish":
                var published = game.Publish(endpoint, request.Instance ?? "", request.ProducerSequence,
                    request.Payload ?? default, request.TimeoutTicks, trafficClass);
                // Accepted fixes the recipients; transport and application outcomes come later.
                return Outcome(request, published.Status, new
                {
                    messageId = published.MessageId, recipients = published.Recipients, deliveries = published.Deliveries
                });
            case "invoke":
                var invoked = game.Invoke(endpoint, request.Instance ?? "", request.Command ?? "",
                    request.IdempotencyKey ?? "", request.Arguments ?? default, request.TimeoutTicks, trafficClass);
                return Outcome(request, invoked.Status, invoked.Operation is null ? null : OperationDto(invoked.Operation));
            default:
                var scan = game.Discover(endpoint, request.ServiceType ?? "", request.ScanKey ?? "", trafficClass);
                return Outcome(request, scan.Status, new { probes = scan.Operations.Length, operations = scan.Operations });
        }
    }

    private static object OperationDto(OperationView operation) => new
    {
        operationId = operation.OperationId, instance = operation.Instance, command = operation.Command,
        outcome = Code(operation.Outcome), outcomeReason = operation.OutcomeReason,
        result = Code(operation.Result), resultReason = operation.ResultReason, resultAttempts = operation.ResultAttempts
    };

    private static bool TryScope(string? text, out AccessScope scope)
    {
        scope = text switch { null or "domain" => AccessScope.Domain, "open" => AccessScope.Open, "none" => AccessScope.None, _ => (AccessScope)255 };
        return Enum.IsDefined(scope);
    }

    private static PipeResponse Outcome(PipeRequest request, ServiceStatus status, object? result = null)
    {
        var ok = status is ServiceStatus.Ok or ServiceStatus.Duplicate;
        return new PipeResponse(request.Id, ok, ok ? null : Code(status),
            Result: result is null ? new { status = Code(status) } : new { status = Code(status), value = result });
    }

    private static string Code<T>(T value) where T : struct, Enum => ServiceNames.Code(value);

    private PipeResponse Stats(PipeRequest request)
    {
        var snapshot = runtime.Topology.Snapshot;
        return new PipeResponse(request.Id, true, Version: snapshot.Version,
            ConnectivityVersion: snapshot.ConnectivityVersion, NodeCount: snapshot.Nodes.Count);
    }

    private PipeResponse ApplyGameBatch(PipeRequest request, HashSet<NodeId>? seenNodes)
    {
        if (request.Changes is null || request.Changes.Length is < 1 or > 4096)
            throw new ArgumentException("Game batch size must be 1..4096.");
        return ApplyGameChanges(request.Id, request.Changes.Select(ToChange).ToArray(), seenNodes);
    }

    private PipeResponse ApplyGameChanges(string? requestId,
        IReadOnlyList<TopologyChange> changes, HashSet<NodeId>? seenNodes)
    {
        var added = seenNodes is null ? null : CollectNewNodes(changes, seenNodes);
        var result = game.ApplyTopology(changes);
        if (added is not null)
            seenNodes!.UnionWith(added);
        return new PipeResponse(requestId, true, Version: result.Version,
            Applied: result.Applied, Skipped: result.Skipped);
    }

    private static HashSet<NodeId> CollectNewNodes(IReadOnlyList<TopologyChange> changes, HashSet<NodeId> seen)
    {
        var added = new HashSet<NodeId>();
        foreach (var change in changes)
            if (change.Operation == TopologyOperation.AddNode && (seen.Contains(change.A) || !added.Add(change.A)))
                throw new ArgumentException("node_id_reused");
        if (seen.Count + added.Count > 65_536)
            throw new ArgumentException("node_id_budget_exceeded");
        return added;
    }

    private PipeResponse UpdateGameRadio(PipeRequest request)
    {
        if (request.A == 0)
            throw new ArgumentException("Game radio node ID must be nonzero.");
        RadioProfile? profile = request.Enabled == false ? null :
            new RadioProfile(request.Range ?? DefaultRadioRange, request.Channel, request.RadioCost);
        var result = game.UpdateRadio([new RadioNodeUpdate(new NodeId(request.A),
            Profile: profile, ChangeProfile: true)]);
        return new PipeResponse(request.Id, true, Version: result.Version);
    }

    private PipeResponse UpdateGameRadioBatch(PipeRequest request)
    {
        if (request.Changes is null || request.Changes.Length is < 1 or > 256)
            throw new ArgumentException("Game radio batch size must be 1..256.");
        if (request.Changes.Any(change => change is null ||
            change.Op != "game_radio" || change.A == 0))
            throw new ArgumentException("Radio batch requires valid game_radio entries.");
        var updates = request.Changes.Select(change =>
            new RadioNodeUpdate(new NodeId(change.A),
                Profile: change.Enabled == false ? null :
                    new RadioProfile(change.Range ?? DefaultRadioRange, change.Channel, change.RadioCost),
                ChangeProfile: true)).ToArray();
        var result = game.UpdateRadio(updates);
        return new PipeResponse(request.Id, true, Version: result.Version);
    }

    private PipeResponse PollPackets(PipeRequest request, bool framed)
    {
        // At most 32 * (128 UTF-16 code units escaped as \uXXXX + fixed receipt
        // fields) plus the page envelope always fits a 48 KiB frame.
        var page = game.Receipts(request.AfterSequence, framed ? Math.Min(request.Limit, 32) : request.Limit);
        if (!string.IsNullOrWhiteSpace(request.StreamId) &&
            (!Guid.TryParse(request.StreamId, out var epoch) || epoch != page.StreamId))
            return new PipeResponse(request.Id, false, "stream_changed",
                StreamId: page.StreamId);
        return new PipeResponse(request.Id, true, ReceiptPage: page);
    }

    private PipeResponse CancelPacket(PipeRequest request)
    {
        if (!Guid.TryParse(request.StreamId, out var epoch) || request.PacketId < 1)
            throw new ArgumentException("packet_cancel requires streamId and packetId.");
        var cancelled = game.Cancel(epoch, request.PacketId);
        return new PipeResponse(request.Id, true, Cancelled: cancelled,
            StreamId: game.StreamId);
    }

    private PipeResponse ApplyBatch(PipeRequest request)
    {
        if (request.Changes is null || request.Changes.Length is < 1 or > 4096)
            throw new ArgumentException("Batch size must be 1..4096.");
        return ApplyChanges(request.Id, request.Changes.Select(ToChange).ToArray());
    }

    private PipeResponse ApplyChanges(string? requestId, IReadOnlyList<TopologyChange> changes)
    {
        var applied = runtime.Apply(changes);
        return new PipeResponse(requestId, true, Version: applied.Version,
            Applied: applied.Applied, Skipped: applied.Skipped);
    }

    private static TopologyChange ToChange(PipeRequest? command)
    {
        if (command is null || string.IsNullOrWhiteSpace(command.Op))
            throw new ArgumentException("Missing topology operation.");
        var op = command.Op.StartsWith("game_", StringComparison.Ordinal)
            ? command.Op[5..] : command.Op;
        var operation = op switch
        {
            "add_node" => TopologyOperation.AddNode,
            "remove_node" => TopologyOperation.RemoveNode,
            "connect" => TopologyOperation.Connect,
            "disconnect" => TopologyOperation.Disconnect,
            "move_node" => TopologyOperation.MoveNode,
            "set_forwarding" => TopologyOperation.SetForwarding,
            _ => throw new ArgumentException("Unknown topology operation.")
        };
        var canForward = command.Role switch
        {
            null => (bool?)null,
            "endpoint" when operation is TopologyOperation.AddNode or TopologyOperation.SetForwarding => false,
            "relay" when operation is TopologyOperation.AddNode or TopologyOperation.SetForwarding => true,
            _ => throw new ArgumentException("Invalid topology role.")
        };
        return new TopologyChange(operation, new NodeId(command.A),
            new NodeId(command.B), command.Cost,
            command.X.HasValue && command.Y.HasValue
                ? new NodePosition(command.X.Value, command.Y.Value, command.Z ?? 0)
                : null) { CanForward = canForward };
    }

    private sealed record PipeRequest(
        string? Id, string Op, ulong A = 0, ulong B = 0, uint Cost = 1,
        int? X = null, int? Y = null, int? Z = null, PipeRequest[]? Changes = null,
        string? RequestId = null, int Bytes = 0, int TrafficClass = 0,
        int? TimeoutTicks = null, long PacketId = 0, string? StreamId = null,
        long AfterSequence = 0, int Limit = 128, bool? Enabled = null,
        int? Range = null, uint Channel = 1, uint RadioCost = 8,
        int ProtocolMajor = 0, int ProtocolMinor = 0, string? ServerId = null,
        Guid? RoundEpoch = null, Guid? OwnerFence = null, long CommandSequence = 0,
        string? SnapshotId = null, int? ChunkCount = null, int? FactCount = null,
        int? ChunkIndex = null, string? FactsJson = null, string? Digest = null, long FactSequence = -1,
        string? Endpoint = null, ulong Generation = 0, string? Instance = null, string? ServiceType = null,
        string? Schema = null, int Major = 0, int Minor = 0, string? Projection = null, string? Key = null,
        string? Mode = null, int MaxPending = 1, long ProducerSequence = 0, JsonElement? Payload = null,
        DeliveryAck[]? Acks = null, long DeliveryId = 0, long MessageId = 0, string? Domain = null,
        string? DiscoverScope = null, string? SubscribeScope = null, string? InvokeScope = null,
        string? Command = null, string? IdempotencyKey = null, JsonElement? Arguments = null,
        long OperationId = 0, string? ScanKey = null, ulong Area = 0, int? EndX = null, string? Role = null,
        string? Profile = null, string? UpdateId = null, int? AccessRange = null, string? Mount = null,
        bool? Anchored = null, long? SinceVersion = null, int? MaxNodes = null, bool? Powered = null,
        bool? Operational = null, bool? ExecutionPowered = null);

    private sealed record DeliveryAck(long DeliveryId, bool Applied, string? Reason = null, JsonElement? Result = null);

    private sealed record Welcome(int ProtocolMajor, int ProtocolMinor, string ServerId,
        Guid RoundEpoch, Guid OwnerFence, int MaxFrameBytes);

    private sealed record PipeResponse(
        string? Id, bool Ok, string? Error = null, long? Version = null,
        long? ConnectivityVersion = null, int? NodeCount = null,
        int? Applied = null, int? Skipped = null,
        bool? Found = null, ulong? Cost = null, ulong[]? Path = null,
        PacketSubmissionResult? Submission = null,
        PacketReceiptPage? ReceiptPage = null,
        bool? Cancelled = null, Guid? StreamId = null, object? Game = null,
        Welcome? Session = null, long? NextSequence = null,
        string? SnapshotId = null, int? NextChunk = null, long? LastFactSequence = null,
        object? Result = null, string? UpdateId = null);

    private sealed class WireInteger<T> : JsonConverter<T> where T : struct, INumberBase<T>
    {
        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String &&
                T.TryParse(reader.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                return value;
            throw new JsonException("Expected an unsigned decimal integer string.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(null, CultureInfo.InvariantCulture));
    }

    private sealed class DmBoolean : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                return reader.GetBoolean();
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value) && value is 0 or 1)
                return value == 1;
            throw new JsonException("Expected a boolean or DM's numeric 0/1.");
        }

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
            writer.WriteBooleanValue(value);
    }
}
