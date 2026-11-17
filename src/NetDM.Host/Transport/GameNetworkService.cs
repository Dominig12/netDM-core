using System.Diagnostics;
using System.Text.Json;
using NetDM.Core;
using NetDM.Services;

namespace NetDM.Host.Transport;

/// <summary>
/// Production packet engine owned by the Host, separate from the synthetic
/// Observatory benchmark. Topology changes are applied via its own radio
/// medium to preserve ownership and one coherent routing snapshot.
/// </summary>
public sealed class GameNetworkService : IHostedService, IAsyncDisposable
{
    /// <summary>Topology, transport and services share one runtime and reset together.</summary>
    internal sealed record Session(SharedMediumNetwork Medium, RadioPacketTransport Packets, ServiceRuntime Services);

    private readonly SchemaRegistry _schemas;
    /// <summary>
    /// Research servers keep their state in the Host: their commands are answered here, not by DM.
    /// A message server relays to every recipient from one endpoint, so an endpoint may run 32
    /// operations at once instead of 8.
    /// </summary>
    private readonly ServiceRuntimeOptions _serviceOptions;
    private const int MaxDeliverySubmissions = 256;
    private int _deliverySubmissions;
    private readonly object _drainGate = new();
    private readonly object _sessionGate = new();
    private Session _session;
    private Session Current => Volatile.Read(ref _session);
    private readonly PacketTickRunner _runner;
    private readonly PriorityWorkScheduler _scheduler;
    private readonly IHostApplicationLifetime? _application;
    private readonly ILogger<GameNetworkService>? _log;
    private CancellationTokenSource? _lifetime;
    private Task? _tickTask, _rerouteTask;
    private readonly object _lifecycle = new();
    private int _disposed;
    private const int QuantumMs = 100;
    /// <summary>EX_SOFTWARE: a supervisor restarts the Host and DM resynchronizes.</summary>
    public const int WorkerFaultExitCode = 70;
    private static readonly long QuantumTicks = Math.Max(1, Stopwatch.Frequency / (1000 / QuantumMs));

    public GameNetworkService(PriorityWorkScheduler scheduler,
        IHostApplicationLifetime? application = null, ILogger<GameNetworkService>? log = null,
        IEnumerable<IServiceContractModule>? contractModules = null,
        IEnumerable<IHostServiceExtension>? hostServices = null)
    {
        _schemas = SchemaRegistry.CreateDefault(contractModules);
        // Names and handlers share one DI registration: adding a Host-provided feature
        // no longer requires editing the GameNetworkService service-type list.
        var types = new HashSet<string>(StringComparer.Ordinal) { NetDM.Host.Research.ResearchHost.ServiceType };
        foreach (var extension in hostServices ?? [])
        {
            if (!ServiceNames.IsValid(extension.ServiceType) || !types.Add(extension.ServiceType))
                throw new ArgumentException($"Duplicate or invalid Host service type '{extension.ServiceType}'.");
        }
        _serviceOptions = new ServiceRuntimeOptions(
            HostProvidedTypes: types.ToArray(), MaxActiveOperationsPerEndpoint: 32);
        _session = CreateSession();
        _scheduler = scheduler;
        _application = application;
        _log = log;
        _runner = new PacketTickRunner(QuantumMs, (tick, ct) => Current.Packets.Advance(tick, ct));
    }

    private static long Now() => Stopwatch.GetTimestamp() / QuantumTicks;

    private Session CreateSession(NetworkRuntime? runtime = null)
    {
        var medium = new SharedMediumNetwork(runtime ?? new NetworkRuntime(), bytesPerTick: 8192,
            searchMode: SharedRadioSearchMode.AStarDense,
            airtimeScope: RadioAirtimeScope.SpatialCells);
        var services = new ServiceRuntime(_schemas, Now, _serviceOptions);
        var packets = new RadioPacketTransport(medium, new PacketTransportOptions(
            Capacity: 4096, MaxAgeTicks: 900, MaxAdvancesPerTick: 4096,
            MaxPendingReroutes: 64, MaxConcurrentReroutes: 2,
            AdaptivePacketVisits: true, EventDrivenPacketWaiting: true,
            EventDrivenRerouteCapacity: true,
            PrioritizeInteractiveTraffic: true, InteractiveReservedSlots: 128,
            PrioritizeInteractiveReroutes: true, InteractiveRerouteReservedSlots: 4,
            ReceiptHistoryCapacity: 8192, MaxCableHopsPerTick: 64, AdvanceInteractiveOnSubmit: true),
            services.OnTransportReceipt);
        return new Session(medium, packets, services);
    }

    public long CurrentTick => Now();
    internal Session CaptureSession() => Current;
    public Guid StreamId => Current.Packets.ReceiptStreamId;
    public SharedMediumSnapshot Snapshot => Current.Medium.Snapshot;
    internal bool WorkersReady => Volatile.Read(ref _tickTask) is { IsCompleted: false } &&
        Volatile.Read(ref _rerouteTask) is { IsCompleted: false } &&
        _runner.LastTick >= CurrentTick - 20;

    public (Guid StreamId, SharedMediumSnapshot Snapshot) CaptureTopology()
    {
        var session = Current;
        return (session.Packets.ReceiptStreamId, session.Medium.Snapshot);
    }

    // Game topology is independent from the synthetic benchmark graph.
    public IReadOnlyList<RadioMediumCellView> ViewCells(int left, int top,
        int right, int bottom, int z, SharedMediumSnapshot snapshot) =>
        Current.Medium.ViewCells(left, top, right, bottom, z, 1_500, snapshot);

    public object TopologySummary()
    {
        var (streamId, snapshot) = CaptureTopology();
        var physical = snapshot.Physical;
        var levels = physical.Nodes.Values.Where(n => n.Position.HasValue)
            .GroupBy(n => n.Position!.Value.Z).OrderBy(g => g.Key)
            .Select(g => new
            {
                z = g.Key,
                nodes = g.Count(),
                minX = g.Min(n => n.Position!.Value.X),
                minY = g.Min(n => n.Position!.Value.Y),
                maxX = g.Max(n => n.Position!.Value.X),
                maxY = g.Max(n => n.Position!.Value.Y)
            }).ToArray();
        return new
        {
            streamId,
            version = snapshot.Version,
            connectivityVersion = physical.ConnectivityVersion,
            nodes = physical.Nodes.Count,
            links = physical.Nodes.Values.Sum(n => n.Links.Count) / 2,
            radios = snapshot.Devices.Count,
            occupiedCells = snapshot.Cells.Count,
            levels
        };
    }

    public int ResetTopology() => ResetSession().Removed;

    internal (int Removed, Guid StreamId) ResetSession()
    {
        lock (_sessionGate)
        {
            var removed = Current.Medium.Snapshot.Physical.Nodes.Count;
            var session = CreateSession();
            // No queue, airtime reservation, reroute, or receipt crosses this barrier.
            Wiring = WiringReport.Empty;
            Volatile.Write(ref _session, session);
            return (removed, session.Packets.ReceiptStreamId);
        }
    }

    internal Session PrepareSnapshot(IReadOnlyList<TopologyChange> topology,
        IReadOnlyList<RadioNodeUpdate> radio)
    {
        var runtime = new NetworkRuntime();
        if (runtime.Apply(topology).Skipped != 0)
            throw new ArgumentException("Snapshot has duplicate nodes or invalid links.");
        var session = CreateSession(runtime);
        session.Medium.Update(radio);
        return session;
    }

    internal Guid PublishSnapshot(Session session, WiringReport wiring)
    {
        lock (_sessionGate)
        {
            Wiring = wiring;
            Volatile.Write(ref _session, session);
            return session.Packets.ReceiptStreamId;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycle)
        {
            if (_lifetime is not null) throw new InvalidOperationException("Game engine already started.");
            _lifetime = new CancellationTokenSource();
            _tickTask = _runner.Start(_lifetime.Token);
            _rerouteTask = Task.Run(() => RunReroutesAsync(_lifetime.Token),
                CancellationToken.None);
            _ = SuperviseAsync(_tickTask, "packet tick", _lifetime.Token);
            _ = SuperviseAsync(_rerouteTask, "reroute", _lifetime.Token);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// A packet worker never restarts inside the same runtime: its queues may be
    /// half-advanced. Readiness turns false at once, then the process exits so a
    /// supervisor restarts it and DM performs the tested reconnect + snapshot.
    /// </summary>
    private async Task SuperviseAsync(Task worker, string name, CancellationToken lifetime)
    {
        try
        {
            await worker.ConfigureAwait(false);
            if (lifetime.IsCancellationRequested)
                return;
            _log?.LogCritical("Game {Worker} worker stopped unexpectedly.", name);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _log?.LogCritical(ex, "Game {Worker} worker faulted.", name);
        }
        Environment.ExitCode = WorkerFaultExitCode;
        _application?.StopApplication();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? lifetime;
        Task? tick, reroutes;
        lock (_lifecycle)
        {
            lifetime = _lifetime;
            tick = _tickTask;
            reroutes = _rerouteTask;
        }
        if (lifetime is null) return;
        lifetime.Cancel();
        try
        {
            await Task.WhenAll(new[] { tick, reroutes }.OfType<Task>())
                .WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        // DI can track the same service as both the singleton and IHostedService.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await StopAsync(CancellationToken.None); }
        finally { _lifetime?.Dispose(); }
    }

    /// <summary>Topology and radio profile replacements publish as one medium snapshot.</summary>
    public BatchResult ApplyTopology(IReadOnlyList<TopologyChange> changes,
        IReadOnlyList<RadioNodeUpdate>? radios = null) => ApplyWorldTopology(changes, radios, null);

    internal BatchResult ApplyWorldTopology(IReadOnlyList<TopologyChange> changes,
        IReadOnlyList<RadioNodeUpdate>? radios, Func<WiringReport>? commitWiring)
    {
        lock (_sessionGate)
        {
            var session = Current;
            var result = changes.Count != 0 || radios is { Count: > 0 }
                ? session.Medium.ApplyExternalTopology(changes, radios) : default;
            if (result.Applied > 0)
            {
                var affected = changes.SelectMany(c => new[] { c.A, c.B })
                    .Concat(radios?.Select(update => update.Id) ?? [])
                    .Where(id => id.Value != 0).Distinct()
                    .Select(id => new RadioNodeUpdate(id)).ToArray();
                session.Packets.NotifyRadioUpdates(affected);
                // A destroyed world object takes its endpoints, services and deliveries.
                session.Services.OnNodesRemoved(changes
                    .Where(c => c.Operation == TopologyOperation.RemoveNode).Select(c => c.A));
            }
            if (commitWiring is not null)
                Wiring = commitWiring();
            return result;
        }
    }

    public SharedMediumUpdateResult UpdateRadio(IReadOnlyList<RadioNodeUpdate> updates)
    {
        lock (_sessionGate)
        {
            var session = Current;
            var result = session.Medium.Update(updates);
            session.Packets.NotifyRadioUpdates(updates);
            return result;
        }
    }

    public PacketReceiptPage Receipts(long afterSequence, int limit) =>
        Current.Packets.CaptureReceipts(afterSequence, limit);

    public bool Cancel(Guid streamId, long packetId) =>
        Current.Packets.TryCancel(streamId, packetId);

    /// <summary>Radio bridges of the published APC map, for diagnostics.</summary>
    public ApcBridgeReport? ApcBridges
    {
        get => Volatile.Read(ref _apcBridges);
        set => Volatile.Write(ref _apcBridges, value);
    }

    private ApcBridgeReport? _apcBridges;

    /// <summary>Designed wiring of the published APC map and its inactive nodes, for network.diagnostics.</summary>
    public WiringReport Wiring
    {
        get => Volatile.Read(ref _wiring);
        set => Volatile.Write(ref _wiring, value);
    }

    private WiringReport _wiring = WiringReport.Empty;

    internal (SharedMediumSnapshot World, WiringReport Wiring)? CapturePowerState(Session session)
    {
        lock (_sessionGate)
            return ReferenceEquals(session, Current) ? (session.Medium.Snapshot, Wiring) : null;
    }

    public object Status()
    {
        var session = Current;
        return new
        {
            streamId = session.Packets.ReceiptStreamId,
            tick = CurrentTick,
            nodes = session.Medium.Snapshot.Physical.Nodes.Count,
            packet = session.Packets.Capture(),
            radio = session.Medium.Stats(),
            runner = _runner.Capture(),
            apcBridges = ApcBridges,
            services = session.Services.Capture(),
            // A late runner tick with little GC pause points at the machine (paging, a frozen process), not at the Host.
            process = new
            {
                gcPauseTotalMilliseconds = Math.Round(GC.GetTotalPauseDuration().TotalMilliseconds, 1),
                gen0 = GC.CollectionCount(0),
                gen1 = GC.CollectionCount(1),
                gen2 = GC.CollectionCount(2),
                heapMegabytes = GC.GetTotalMemory(false) / (1024 * 1024),
                workingSetMegabytes = Environment.WorkingSet / (1024 * 1024)
            }
        };
    }

    public ServiceRuntime Services => Current.Services;

    /// <summary>Attaches an endpoint to a node that exists in the published world.</summary>
    public ServiceStatus AttachEndpoint(EndpointKey key, ulong node, string domain = "station")
    {
        lock (_sessionGate)
        {
            var session = Current;
            return session.Medium.Snapshot.Physical.Nodes.ContainsKey(new NodeId(node))
                ? session.Services.AttachEndpoint(key, new NodeId(node), domain)
                : ServiceStatus.UnknownNode;
        }
    }

    /// <summary>Command to one instance: request leg now, result leg after the provider's ACK.</summary>
    public InvokeResult Invoke(EndpointKey initiator, string instance, string command, string idempotencyKey,
        JsonElement arguments, int? timeoutTicks, PacketTrafficClass trafficClass)
    {
        var session = Current;
        var result = session.Services.Invoke(initiator, instance, command, idempotencyKey, arguments,
            timeoutTicks, trafficClass);
        DrainSubmissions(session);
        return result;
    }

    /// <summary>Network scan: probes to policy-visible providers, descriptors back.</summary>
    public DiscoverResult Discover(EndpointKey requester, string serviceType, string scanKey,
        PacketTrafficClass trafficClass)
    {
        var session = Current;
        var result = session.Services.Discover(requester, serviceType, scanKey, trafficClass);
        DrainSubmissions(session);
        return result;
    }

    /// <summary>
    /// Accepts a publication; one physical delivery per fixed recipient follows.
    /// Routing runs on the bounded scheduler; the result is recorded in the same runtime.
    /// </summary>
    public PublishResult Publish(EndpointKey producer, string instance, long producerSequence,
        JsonElement payload, int? timeoutTicks, PacketTrafficClass trafficClass)
    {
        var session = Current;
        var result = session.Services.Publish(producer, instance, producerSequence, payload,
            timeoutTicks, trafficClass);
        DrainSubmissions(session);
        return result;
    }

    /// <summary>
    /// Starts routing for service packets created by the last operation. Waiting
    /// submissions stay in the runtime's bounded outbox and are retried every
    /// reroute cycle; nothing is lost while the submission pool is full.
    /// </summary>
    public void DrainSubmissions() => DrainSubmissions(Current);

    private void DrainSubmissions(Session session)
    {
        var lifetime = Volatile.Read(ref _lifetime);
        if (lifetime is null)
            return;
        lock (_drainGate)
        {
            var capacity = MaxDeliverySubmissions - Volatile.Read(ref _deliverySubmissions);
            if (capacity <= 0)
                return;
            foreach (var submission in session.Services.TakeSubmissions(capacity))
            {
                Interlocked.Increment(ref _deliverySubmissions);
                _ = SubmitDeliveryAsync(session, submission, lifetime.Token);
            }
        }
    }

    private async Task SubmitDeliveryAsync(Session session, DeliverySubmission submission, CancellationToken ct)
    {
        try
        {
            var result = await SendOnSessionAsync(session, submission.RequestId, submission.From.Value,
                submission.To.Value, submission.Bytes, submission.TrafficClass, submission.TimeoutTicks, ct)
                .ConfigureAwait(false);
            session.Services.OnSubmitted(submission.DeliveryId, result);
        }
        catch (OperationCanceledException)
        {
            session.Services.OnSubmissionFailed(submission.DeliveryId, DeliveryState.Cancelled, "host_stopping");
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Delivery {DeliveryId} could not be submitted.", submission.DeliveryId);
            session.Services.OnSubmissionFailed(submission.DeliveryId, DeliveryState.Unknown, "submission_error");
        }
        finally
        {
            Interlocked.Decrement(ref _deliverySubmissions);
        }
    }

    /// <summary>
    /// A route search is queued in the global bounded scheduler and is NEVER
    /// executed while holding the radio/packet lock or the named-pipe reader.
    /// A pre-route lease owns a real packet slot across that asynchronous work.
    /// </summary>
    public Task<PacketSubmissionResult> SendAsync(string requestId,
        ulong from, ulong to, int bytes, PacketTrafficClass trafficClass,
        int? timeoutTicks, CancellationToken ct)
    {
        // Service deliveries own this prefix; a raw packet must never complete one.
        if (requestId?.StartsWith(ServiceRuntime.RequestIdPrefix, StringComparison.Ordinal) == true)
            throw new ArgumentException("Request ID prefix is reserved.");
        return SendOnSessionAsync(Current, requestId!, from, to, bytes, trafficClass, timeoutTicks, ct);
    }

    private async Task<PacketSubmissionResult> SendOnSessionAsync(Session session, string requestId,
        ulong from, ulong to, int bytes, PacketTrafficClass trafficClass,
        int? timeoutTicks, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128)
            throw new ArgumentException("Request ID must contain 1..128 characters.");
        if (from == 0 || to == 0 || bytes is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        if (!Enum.IsDefined(trafficClass))
            throw new ArgumentOutOfRangeException(nameof(trafficClass));
        if (timeoutTicks is < 1 or > 900)
            throw new ArgumentOutOfRangeException(nameof(timeoutTicks));
        var streamId = session.Packets.ReceiptStreamId;
        using var lease = session.Packets.TryReserveAdmission(trafficClass, out var refusal);
        if (lease is null)
            return new PacketSubmissionResult(false, streamId, 0, requestId, refusal);

        if (!_scheduler.TrySchedule(token =>
                session.Medium.Route(new NodeId(from), new NodeId(to),
                    cancellationToken: token),
                trafficClass == PacketTrafficClass.Interactive
                    ? WorkPriority.Interactive : WorkPriority.Background,
                ct, out Task<RouteResult>? routeTask))
        {
            return new PacketSubmissionResult(false, streamId, 0, requestId,
                PacketSubmissionRejection.RouteSchedulerBusy);
        }

        var route = await routeTask!.WaitAsync(ct).ConfigureAwait(false);
        lock (_sessionGate)
        {
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(session, Current))
                return new PacketSubmissionResult(false, streamId, 0, requestId,
                    PacketSubmissionRejection.SessionChanged);
            return session.Packets.SubmitReserved(requestId, route, bytes, CurrentTick,
                lease, timeoutTicks);
        }
    }

    private async Task RunReroutesAsync(CancellationToken ct)
    {
        var jobs = new List<Task>(2);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                for (var i = jobs.Count - 1; i >= 0; i--)
                {
                    if (!jobs[i].IsCompleted) continue;
                    await jobs[i]; // A reroute fault must make the worker unready.
                    jobs.RemoveAt(i);
                }
                var session = Current;
                // Service replies created inside transport callbacks start here.
                DrainSubmissions(session);
                while (jobs.Count < 2 && session.Packets.TryTakeReroute(out var ticket))
                {
                    if (_scheduler.TrySchedule(token => session.Medium.Route(ticket.Source,
                            ticket.Destination, cancellationToken: token),
                        ticket.TrafficClass == PacketTrafficClass.Interactive
                            ? WorkPriority.Interactive : WorkPriority.Background,
                        ct, out Task<RouteResult>? result))
                        jobs.Add(CompleteRerouteAsync(session.Packets, ticket, result!));
                    else
                    {
                        session.Packets.CancelReroute(ticket, schedulerRejected: true);
                        break;
                    }
                }
                await Task.Delay(20, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            try { await Task.WhenAll(jobs); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
    }

    private static async Task CompleteRerouteAsync(RadioPacketTransport packets,
        PacketRerouteRequest ticket,
        Task<RouteResult> scheduled)
    {
        try { packets.CompleteReroute(ticket, await scheduled); }
        catch (OperationCanceledException) { packets.CancelReroute(ticket); }
        catch
        {
            packets.CancelReroute(ticket);
            throw;
        }
    }
}
