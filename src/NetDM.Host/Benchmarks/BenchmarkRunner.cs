using System.Collections.Concurrent;
using System.Diagnostics;
using NetDM.Core;

namespace NetDM.Host.Benchmarks;

public sealed record BenchmarkOptions(
    int Nodes = 2500,
    int RoutesPerSecond = 300,
    int MovesPerSecond = 120,
    int LinkChangesPerSecond = 4,
    int DurationSeconds = 60,
    int Seed = 1337,
    bool WirelessEnabled = false,
    int RadioRange = 14,
    string RadioModel = "pairwise",
    int MediumBytesPerTick = 8192,
    string SharedSearch = "dijkstra",
    bool PacketTransportEnabled = false,
    bool SpatialReuseEnabled = true,
    int PacketMaxAgeTicks = 900,
    int PacketMaxAdvancesPerTick = 1024,
    int PacketRerouteWorkers = 1,
    int PacketRerouteQueueCapacity = 32,
    int PacketRerouteCpuBudgetMs = 25,
    bool DedicatedPacketTicks = true,
    int PacketTickWorkBudgetMs = 25,
    int PacketMaxNoRouteRetryDelayTicks = 96,
    int PacketRerouteQueueRetryDelayTicks = 8,
    int PacketQueueCapacity = 2048,
    bool AdaptivePacketVisits = false,
    bool PacketPreRouteBackpressure = false,
    bool EventDrivenPacketWaiting = false,
    bool EventDrivenRerouteCapacity = false,
    string TrafficProfile = "uniform",
    bool PrioritizeInteractiveTraffic = false,
    int InteractiveReservedSlots = 0,
    int InteractiveTrafficPercent = 20,
    int LocalTrafficPercent = 85,
    int BurstPeriodSeconds = 20,
    int BurstDurationSeconds = 2,
    int BurstMultiplier = 3)
{
    public void Validate()
    {
        if (Nodes is < 9 or > 30_000) throw new ArgumentOutOfRangeException(nameof(Nodes));
        if (RoutesPerSecond is < 0 or > 20_000) throw new ArgumentOutOfRangeException(nameof(RoutesPerSecond));
        if (MovesPerSecond is < 0 or > 10_000) throw new ArgumentOutOfRangeException(nameof(MovesPerSecond));
        if (LinkChangesPerSecond is < 0 or > 1_000) throw new ArgumentOutOfRangeException(nameof(LinkChangesPerSecond));
        if (DurationSeconds is < 2 or > 1800) throw new ArgumentOutOfRangeException(nameof(DurationSeconds));
        if (RadioRange is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(RadioRange));
        if (PacketMaxAgeTicks is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(PacketMaxAgeTicks));
        if (PacketMaxAdvancesPerTick is < 1 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(PacketMaxAdvancesPerTick));
        if (PacketQueueCapacity is < 1 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(PacketQueueCapacity));
        if (PacketPreRouteBackpressure && !PacketTransportEnabled)
            throw new ArgumentException(
                "Pre-route packet backpressure requires hopwise transport.",
                nameof(PacketPreRouteBackpressure));
        if (PacketRerouteWorkers is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(PacketRerouteWorkers));
        if (PacketRerouteQueueCapacity is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(PacketRerouteQueueCapacity));
        if (PacketRerouteCpuBudgetMs is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(PacketRerouteCpuBudgetMs));
        if (PacketTickWorkBudgetMs is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(PacketTickWorkBudgetMs));
        if (PacketMaxNoRouteRetryDelayTicks is < 3 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(PacketMaxNoRouteRetryDelayTicks));
        if (PacketRerouteQueueRetryDelayTicks is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(PacketRerouteQueueRetryDelayTicks));
        if (PacketTransportEnabled && (!WirelessEnabled || RadioModel != "shared"))
            throw new ArgumentException(
                "Hop-by-hop packets require wireless Shared Medium mode.",
                nameof(PacketTransportEnabled));
        if (SharedSearch is not ("dijkstra" or "astar" or "astar-pooled" or "astar-dense"))
            throw new ArgumentException("SharedSearch must be dijkstra, astar, astar-pooled or astar-dense.", nameof(SharedSearch));
        if (RadioModel is not ("pairwise" or "shared"))
            throw new ArgumentException("RadioModel must be pairwise or shared.", nameof(RadioModel));
        if (MediumBytesPerTick is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(MediumBytesPerTick));
        if (TrafficProfile is not ("uniform" or "ss13-mixed"))
            throw new ArgumentException("Unknown traffic profile.", nameof(TrafficProfile));
        if (TrafficProfile == "ss13-mixed" && !PacketTransportEnabled)
            throw new ArgumentException("SS13 mixed traffic requires packet transport.", nameof(TrafficProfile));
        if (InteractiveTrafficPercent is < 0 or > 100 ||
            LocalTrafficPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(InteractiveTrafficPercent));
        if (BurstPeriodSeconds is < 2 or > 3600 ||
            BurstDurationSeconds < 1 || BurstDurationSeconds >= BurstPeriodSeconds ||
            BurstMultiplier is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(BurstPeriodSeconds));
        new PacketTransportOptions(Capacity: PacketQueueCapacity,
            EventDrivenPacketWaiting: EventDrivenPacketWaiting,
            PrioritizeInteractiveTraffic: PrioritizeInteractiveTraffic,
            EventDrivenRerouteCapacity: EventDrivenRerouteCapacity,
            InteractiveReservedSlots: InteractiveReservedSlots).Validate();
    }
}

public sealed record BenchmarkLoadStats(long Offered, long Admitted, long Rejected,
    long TransportBackpressured, long InteractiveOffered, long BackgroundOffered,
    long InteractiveAdmitted, long BackgroundAdmitted,
    long InteractiveRejected, long BackgroundRejected);

public sealed record BenchmarkState(
    string Status, BenchmarkOptions? Options, DateTimeOffset? StartedUtc,
    double ElapsedSeconds, int WorldWidth, int WorldHeight, string? Error,
    RadioStats? Radio, SharedMediumStatistics? SharedMedium, BenchmarkLoadStats Load,
    PacketTransportCounters? PacketTransport,
    WorkSchedulerCounters? RerouteScheduler,
    PacketTickRunnerCounters? TickRunner,
    ProcessStallCounters? ProcessStalls);

public sealed record BenchmarkSample(
    DateTimeOffset At, double ElapsedSeconds, double ActualRoutesPerSecond,
    double ActualMovesPerSecond, double ActualMutationsPerSecond,
    double P50Us, double P95Us, double P99Us, double CacheHitPercent,
    long ManagedBytes, int NodeCount, long Version, long ConnectivityVersion,
    int RadioLinks, double RadioLinksAddedPerSecond, double RadioLinksRemovedPerSecond,
    int MediumDomains, int MediumCells, double AcceptedTransfersPerSecond,
    double RejectedTransfersPerSecond,
    double ActualFoundPerSecond, double FoundPercent,
    int MediumConfiguredDevices,
    double RejectedStalePerSecond, double RejectedInvalidPerSecond,
    double RejectedTooLargePerSecond, double RejectedCapacityPerSecond,
    double RejectedOldTickPerSecond,
    int SchedulerQueued, double SchedulerQueueWaitP95Us,
    double OfferedRoutesPerSecond, double AdmittedRoutesPerSecond,
    double RejectedRoutesPerSecond,
    double RevalidatedTransfersPerSecond,
    int PacketsQueued, double PacketsDeliveredPerSecond,
    double PacketsDroppedPerSecond, double PacketsExpiredPerSecond,
    double PacketWirelessHopsPerSecond, double CapacityDeferralsPerSecond,
    double ExploredPerRoute, double AllocatedBytesPerRoute,
    double GcPauseMillisecondsPerSecond, int Gen2CollectionsInInterval,
    double DeliveryP95AgeTicks, double AverageDeliveredHops,
    double NoRouteDeferralsPerSecond, double SpatialBusyHopsPerSecond,
    double SpatialSuccessfulHopsPerSecond, int SpatialActiveCells,
    int ReroutesQueued, int ReroutesInFlight,
    double RerouteStartedPerSecond, double RerouteCompletedPerSecond,
    double RerouteQueueDeferralsPerSecond, double RerouteRejectedPerSecond,
    double RerouteWaitP95Ticks, double PacketForwardingP95Milliseconds,
    double PacketSkippedTicksPerSecond, double PacketTickAdmissionFailuresPerSecond,
    int RerouteCpuQueued, double RerouteCpuWaitP95Us,
    double PacketFirstHopP50Ticks, double PacketFirstHopP95Ticks,
    double TickExecutedPerSecond, double TickRunnerSkippedPerSecond,
    double TickBudgetStopsPerSecond, double TickStartLagP95Milliseconds,
    double TickExecutionP95Milliseconds, long TickRunnerFailed,
    double NoRouteBackoffsPerSecond, double RerouteCooldownSkipsPerSecond,
    double LocalChangeWakeupsPerSecond, double RecoveredOriginalHopsPerSecond,
    double StaleNegativeResultsPerSecond, double AverageNoRouteBackoffTicks,
    int TrackedChangedRadioNodes,
    double PacketEnqueuedPerSecond, double PacketForwardingVisitsPerSecond,
    double PacketPreRouteRejectedPerSecond, int PacketReservedAdmissions,
    int PacketVisitLimit, double PacketVisitLimitIncreasesPerSecond,
    double PacketVisitLimitDecreasesPerSecond,
    double PacketVisitBudgetStopsPerSecond,
    double ProcessHeartbeatLatenessMilliseconds, double ProcessMaxHeartbeatLatenessMilliseconds,
    long ProcessHeartbeatStalls, double ProcessMaxThreadPoolDispatchMilliseconds,
    long ProcessThreadPoolStalls, int ProcessThreadPoolThreads,
    long ProcessThreadPoolPendingWorkItems, double ProcessCpuCoresUsed,
    double ProcessGcPauseMillisecondsSinceStart, double ObservedSampleIntervalMilliseconds,
    int ProcessGen0Collections, int ProcessGen1Collections, int ProcessGen2Collections,
    long ProcessGcLastIndex, int ProcessGcLastGeneration,
    double ProcessGcLastPauseMilliseconds, bool ProcessGcLastCompacted,
    bool ProcessGcLastConcurrent, long ProcessGcLastFullBlockingIndex,
    double ProcessGcLastFullBlockingPauseMilliseconds,
    long ProcessGcHeapSizeBytes, long ProcessGcFragmentedBytes,
    long ProcessGcCommittedBytes, long ProcessGcMemoryLoadBytes,
    long ProcessGcAllocatedBytesSinceStart,
    double InteractiveDeliveryP95Ticks, double BackgroundDeliveryP95Ticks,
    int InteractivePacketsQueued, int BackgroundPacketsQueued,
    double InteractivePacketsDeliveredPerSecond, double BackgroundPacketsDeliveredPerSecond);

/// <summary>
/// Repeatable CPU/graph workload. Route and movement generators are independent;
/// reported throughput is observed throughput, never the requested rate.
/// </summary>
public sealed class BenchmarkRunner(NetworkRuntime runtime,
    PriorityWorkScheduler scheduler, ILogger<BenchmarkRunner> log)
{
    private readonly object _gate = new();
    private readonly ConcurrentQueue<BenchmarkSample> _history = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private bool _suiteReserved;
    private string _status = "idle";
    private string? _error;
    private BenchmarkOptions? _options;
    private DateTimeOffset? _started;
    private int _width, _height;
    private double _elapsed;
    private long _offeredRoutes, _admittedRoutes, _rejectedRoutes;
    private long _transportBackpressured;
    private long _interactiveOffered, _backgroundOffered;
    private long _interactiveAdmitted, _backgroundAdmitted;
    private long _interactiveRejected, _backgroundRejected;
    private DynamicRadioNetwork? _radioEngine;
    private SharedMediumNetwork? _sharedMedium;
    private RadioPacketTransport? _packets;
    private PriorityWorkScheduler? _rerouteScheduler;
    private PacketTickRunner? _tickRunner;
    private ProcessStallMonitor? _processStallMonitor;

    public PacketTransportCounters? PacketStatistics
    {
        get { lock (_gate) return _packets?.Capture(); }
    }

    /// <summary>Only for read-only observability; the service remains benchmark-owned.</summary>
    public SharedMediumNetwork? ActiveSharedMedium
    {
        get { lock (_gate) return _sharedMedium; }
    }

    public BenchmarkState State()
    {
        lock (_gate)
            return new BenchmarkState(_status, _options, _started, _elapsed, _width, _height, _error,
                _radioEngine?.Stats(), _sharedMedium?.Stats(), LoadStats(),
                _packets?.Capture(), _rerouteScheduler?.Capture(),
                _tickRunner?.Capture(), _processStallMonitor?.Capture());
    }

    public BenchmarkSample[] History() => _history.ToArray();

    private BenchmarkLoadStats LoadStats() => new(
        Interlocked.Read(ref _offeredRoutes),
        Interlocked.Read(ref _admittedRoutes),
        Interlocked.Read(ref _rejectedRoutes),
        Interlocked.Read(ref _transportBackpressured),
        Interlocked.Read(ref _interactiveOffered),
        Interlocked.Read(ref _backgroundOffered),
        Interlocked.Read(ref _interactiveAdmitted),
        Interlocked.Read(ref _backgroundAdmitted),
        Interlocked.Read(ref _interactiveRejected),
        Interlocked.Read(ref _backgroundRejected));

    /// <summary>
    /// Reserve the entire benchmark lifecycle for an automated experiment suite.
    /// Manual workloads cannot start or stop between the suite's scenarios.
    /// </summary>
    public bool TryReserveSuite()
    {
        lock (_gate)
        {
            if (_suiteReserved || _task is { IsCompleted: false }) return false;
            _suiteReserved = true;
            return true;
        }
    }

    public void ReleaseSuite()
    {
        lock (_gate) _suiteReserved = false;
    }

    public Task AwaitCompletionAsync(CancellationToken cancellationToken = default)
    {
        Task? current;
        lock (_gate) current = _task;
        return current is null ? Task.CompletedTask : current.WaitAsync(cancellationToken);
    }

    public bool Start(BenchmarkOptions options, bool fromSuite = false)
    {
        options.Validate();
        lock (_gate)
        {
            if (_suiteReserved != fromSuite || _task is { IsCompleted: false }) return false;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _options = options;
            _status = "building";
            _started = DateTimeOffset.UtcNow;
            _elapsed = 0;
            Interlocked.Exchange(ref _offeredRoutes, 0);
            Interlocked.Exchange(ref _admittedRoutes, 0);
            Interlocked.Exchange(ref _rejectedRoutes, 0);
            Interlocked.Exchange(ref _transportBackpressured, 0);
            Interlocked.Exchange(ref _interactiveOffered, 0);
            Interlocked.Exchange(ref _backgroundOffered, 0);
            Interlocked.Exchange(ref _interactiveAdmitted, 0);
            Interlocked.Exchange(ref _backgroundAdmitted, 0);
            Interlocked.Exchange(ref _interactiveRejected, 0);
            Interlocked.Exchange(ref _backgroundRejected, 0);
            _error = null;
            _width = _height = 0;
            _radioEngine = null;
            _sharedMedium = null;
            _packets = null;
            _rerouteScheduler = null;
            _tickRunner = null;
            _processStallMonitor = null;
            while (_history.TryDequeue(out _)) { }
            _task = Task.Run(() => RunAsync(options, _cts.Token));
            return true;
        }
    }

    public bool Stop(bool fromSuite = false)
    {
        lock (_gate)
        {
            if (_suiteReserved != fromSuite || _task is not { IsCompleted: false })
                return false;
            _status = "stopping";
            _cts?.Cancel();
            return true;
        }
    }

    private async Task RunAsync(BenchmarkOptions options, CancellationToken ct)
    {
        var clock = new Stopwatch();
        ProcessStallMonitor? monitor = null;
        try
        {
            runtime.Reset();
            var random = new Random(options.Seed);
            var columns = (int)Math.Ceiling(Math.Sqrt(options.Nodes));
            var positions = new NodePosition[options.Nodes];
            var commands = new List<TopologyChange>(options.Nodes * 4);

            for (var i = 0; i < options.Nodes; i++)
            {
                positions[i] = new NodePosition((i % columns) * 12, (i / columns) * 12);
                commands.Add(new TopologyChange(
                    TopologyOperation.AddNode, new NodeId((ulong)(i + 1)), Position: positions[i]));
            }

            // Wired baseline: connected grid with redundant shortcuts.
            // Wireless mode derives links from proximity (no permanent grid edges).
            if (!options.WirelessEnabled)
            for (var i = 0; i < options.Nodes; i++)
            {
                var from = new NodeId((ulong)(i + 1));
                var right = i + 1;
                var down = i + columns;
                if (i % columns != columns - 1 && right < options.Nodes)
                    commands.Add(new TopologyChange(TopologyOperation.Connect, from,
                        new NodeId((ulong)(right + 1)), (uint)random.Next(1, 7)));
                if (down < options.Nodes)
                    commands.Add(new TopologyChange(TopologyOperation.Connect, from,
                        new NodeId((ulong)(down + 1)), (uint)random.Next(1, 7)));
                if (i % 19 == 0)
                {
                    var jump = random.Next(options.Nodes);
                    if (jump != i)
                        commands.Add(new TopologyChange(TopologyOperation.Connect,
                            from, new NodeId((ulong)(jump + 1)), (uint)random.Next(5, 24)));
                }
            }

            ct.ThrowIfCancellationRequested();
            runtime.Apply(commands);
            DynamicRadioNetwork? radio = null;
            SharedMediumNetwork? medium = null;
            if (options.WirelessEnabled)
            {
                var profile = new RadioProfile(options.RadioRange, 1, 8);
                var registrations = Enumerable.Range(1, options.Nodes)
                    .Select(i => new RadioNodeUpdate(new NodeId((ulong)i),
                        Profile: profile, ChangeProfile: true)).ToArray();
                if (options.RadioModel == "shared")
                {
                    medium = new SharedMediumNetwork(runtime,
                        options.MediumBytesPerTick,
                        options.SharedSearch switch
                        {
                            "astar" => SharedRadioSearchMode.AStar,
                            "astar-pooled" => SharedRadioSearchMode.AStarPooled,
                            "astar-dense" => SharedRadioSearchMode.AStarDense,
                            _ => SharedRadioSearchMode.Dijkstra
                        },
                        maximumRadioRange: options.RadioRange,
                        minimumRadioCost: 8,
                        airtimeScope: options.PacketTransportEnabled && options.SpatialReuseEnabled
                            ? RadioAirtimeScope.SpatialCells : RadioAirtimeScope.ChannelWide);
                    medium.Update(registrations, ct);
                }
                else
                {
                    radio = new DynamicRadioNetwork(runtime);
                    radio.Update(registrations, ct);
                }
            }
            var packets = medium is not null && options.PacketTransportEnabled
                ? new RadioPacketTransport(medium, new PacketTransportOptions(
                    Capacity: options.PacketQueueCapacity,
                    MaxAgeTicks: options.PacketMaxAgeTicks,
                    MaxAdvancesPerTick: options.PacketMaxAdvancesPerTick,
                    MaxConcurrentReroutes: options.PacketRerouteWorkers,
                    MaxTickWorkMilliseconds: options.PacketTickWorkBudgetMs,
                    MaxNoRouteRetryDelayTicks: options.PacketMaxNoRouteRetryDelayTicks,
                    RerouteQueueRetryDelayTicks: options.PacketRerouteQueueRetryDelayTicks,
                    AdaptivePacketVisits: options.AdaptivePacketVisits,
                     EventDrivenPacketWaiting: options.EventDrivenPacketWaiting,
                     EventDrivenRerouteCapacity: options.EventDrivenRerouteCapacity,
                     PrioritizeInteractiveTraffic: options.PrioritizeInteractiveTraffic,
                     InteractiveReservedSlots: options.InteractiveReservedSlots))
                : null;
            // Reroute computation is never executed while holding the packet
            // queue lock. A separate bounded CPU executor prevents it from
            // monopolizing the live routing scheduler or forwarding ticks.
            var tickRunner = packets is not null && options.DedicatedPacketTicks
                ? new PacketTickRunner(packets)
                : null;
            await using var rerouteScheduler = packets is null ? null :
                new PriorityWorkScheduler(new WorkSchedulerOptions(
                    Workers: options.PacketRerouteWorkers,
                    QueueCapacity: options.PacketRerouteQueueCapacity,
                    BudgetMillisecondsPer100Ms: options.PacketRerouteCpuBudgetMs));
            lock (_gate)
            {
                _packets = packets;
                _rerouteScheduler = rerouteScheduler;
                _tickRunner = tickRunner;
                _width = columns * 12;
                _height = ((options.Nodes + columns - 1) / columns) * 12;
                _radioEngine = radio;
                _sharedMedium = medium;
                _status = "running";
            }

            monitor = new ProcessStallMonitor();
            monitor.Start();
            lock (_gate) _processStallMonitor = monitor;
            clock.Start();
            var workerCount = options.RoutesPerSecond == 0 ? 0 :
                Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2));
            var workers = Enumerable.Range(0, workerCount).Select(worker =>
                Task.Run(() => RouteWorkerAsync(options, worker, workerCount, medium, packets, ct), ct)).ToList();
            if (options.MovesPerSecond > 0 || options.LinkChangesPerSecond > 0)
                workers.Add(Task.Run(() => MutationWorkerAsync(options, positions, radio, medium, packets, ct), ct));
            if (packets is not null)
            {
                workers.Add(tickRunner is null
                    ? Task.Run(() => PacketWorkerAsync(packets, ct), ct)
                    : tickRunner.Start(ct));
                workers.Add(Task.Run(() => RerouteWorkerAsync(
                    packets, medium!, rerouteScheduler!, ct), ct));
            }

            var previous = runtime.Metrics.Capture(runtime.Cache.Count);
            var previousRadio = radio?.Stats();
            var previousMedium = medium?.Stats();
            var previousPackets = packets?.Capture();
            var previousTickRunner = tickRunner?.Capture();
            var previousLoad = LoadStats();
            var previousTime = clock.Elapsed.TotalSeconds;
            var finishedNaturally = false;

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                var seconds = clock.Elapsed.TotalSeconds;
                var current = runtime.Metrics.Capture(runtime.Cache.Count);
                var snapshot = runtime.Topology.Snapshot;
                var radioSnapshot = radio?.Stats();
                var mediumSnapshot = medium?.Stats();
                var packetSnapshot = packets?.Capture();
                var tickRunnerSnapshot = tickRunner?.Capture();
                var processStallSnapshot = monitor.Capture();
                var rerouteCpuSnapshot = rerouteScheduler?.Capture();
                var interval = Math.Max(0.001, seconds - previousTime);
                var schedulerSnapshot = scheduler.Capture();
                var loadSnapshot = LoadStats();
                var deltaRoutes = current.Routes - previous.Routes;
                var deltaMoves = current.Moves - previous.Moves;
                var sample = new BenchmarkSample(
                    DateTimeOffset.UtcNow, seconds,
                    deltaRoutes / interval, deltaMoves / interval,
                    (current.Mutations - previous.Mutations) / interval,
                    current.P50Microseconds, current.P95Microseconds, current.P99Microseconds,
                    deltaRoutes == 0 ? 0 : 100.0 * (current.CacheHits - previous.CacheHits) / deltaRoutes,
                    current.ManagedBytes, snapshot.Nodes.Count, snapshot.Version,
                    snapshot.ConnectivityVersion,
                    radioSnapshot?.ActiveWirelessLinks ?? 0,
                    ((radioSnapshot?.TotalWirelessLinksAdded ?? 0) - (previousRadio?.TotalWirelessLinksAdded ?? 0)) / interval,
                    ((radioSnapshot?.TotalWirelessLinksRemoved ?? 0) - (previousRadio?.TotalWirelessLinksRemoved ?? 0)) / interval,
                    mediumSnapshot?.Domains ?? 0, mediumSnapshot?.OccupiedCells ?? 0,
                    ((mediumSnapshot?.AcceptedTransfers ?? 0) - (previousMedium?.AcceptedTransfers ?? 0)) / interval,
                    ((mediumSnapshot?.RejectedTransfers ?? 0) - (previousMedium?.RejectedTransfers ?? 0)) / interval,
                    (current.Found - previous.Found) / interval,
                    deltaRoutes == 0 ? 0 : 100.0 * (current.Found - previous.Found) / deltaRoutes,
                    mediumSnapshot?.ConfiguredDevices ?? 0,
                    ((mediumSnapshot?.RejectedStaleRoute ?? 0) - (previousMedium?.RejectedStaleRoute ?? 0)) / interval,
                    ((mediumSnapshot?.RejectedInvalidPath ?? 0) - (previousMedium?.RejectedInvalidPath ?? 0)) / interval,
                    ((mediumSnapshot?.RejectedTransferTooLarge ?? 0) - (previousMedium?.RejectedTransferTooLarge ?? 0)) / interval,
                    ((mediumSnapshot?.RejectedCapacity ?? 0) - (previousMedium?.RejectedCapacity ?? 0)) / interval,
                    ((mediumSnapshot?.RejectedOldTick ?? 0) - (previousMedium?.RejectedOldTick ?? 0)) / interval,
                    schedulerSnapshot.Queued, schedulerSnapshot.QueueWaitP95Us,
                    (loadSnapshot.Offered - previousLoad.Offered) / interval,
                    (loadSnapshot.Admitted - previousLoad.Admitted) / interval,
                    (loadSnapshot.Rejected - previousLoad.Rejected) / interval,
                    ((mediumSnapshot?.AcceptedRevalidatedTransfers ?? 0) -
                     (previousMedium?.AcceptedRevalidatedTransfers ?? 0)) / interval,
                    packetSnapshot?.Queued ?? 0,
                    ((packetSnapshot?.Delivered ?? 0) - (previousPackets?.Delivered ?? 0)) / interval,
                    ((packetSnapshot?.Dropped ?? 0) - (previousPackets?.Dropped ?? 0)) / interval,
                    ((packetSnapshot?.Expired ?? 0) - (previousPackets?.Expired ?? 0)) / interval,
                    ((packetSnapshot?.WirelessHops ?? 0) - (previousPackets?.WirelessHops ?? 0)) / interval,
                    ((packetSnapshot?.BusyDeferrals ?? 0) - (previousPackets?.BusyDeferrals ?? 0)) / interval,
                    deltaRoutes == 0 ? 0 : (double)(current.TotalExploredNodes - previous.TotalExploredNodes) / deltaRoutes,
                    deltaRoutes == 0 ? 0 : (double)(current.TotalRouteAllocatedBytes - previous.TotalRouteAllocatedBytes) / deltaRoutes,
                    Math.Max(0, current.GcTotalPauseMilliseconds - previous.GcTotalPauseMilliseconds) / interval,
                    Math.Max(0, current.Gen2Collections - previous.Gen2Collections),
                    packetSnapshot?.DeliveryAgeP95Ticks ?? 0,
                    packetSnapshot?.MeanDeliveredHops ?? 0,
                    ((packetSnapshot?.NoRouteDeferrals ?? 0) -
                        (previousPackets?.NoRouteDeferrals ?? 0)) / interval,
                    ((mediumSnapshot?.SpatialBusyHops ?? 0) -
                        (previousMedium?.SpatialBusyHops ?? 0)) / interval,
                    ((mediumSnapshot?.SpatialSuccessfulHops ?? 0) -
                        (previousMedium?.SpatialSuccessfulHops ?? 0)) / interval,
                    mediumSnapshot?.SpatialActiveCells ?? 0,
                    packetSnapshot?.RerouteQueued ?? 0,
                    packetSnapshot?.RerouteInFlight ?? 0,
                    ((packetSnapshot?.Reroutes ?? 0) - (previousPackets?.Reroutes ?? 0)) / interval,
                    ((packetSnapshot?.RerouteCompleted ?? 0) -
                        (previousPackets?.RerouteCompleted ?? 0)) / interval,
                    ((packetSnapshot?.RerouteQueueDeferrals ?? 0) -
                        (previousPackets?.RerouteQueueDeferrals ?? 0)) / interval,
                    ((packetSnapshot?.RerouteSchedulerRejections ?? 0) -
                        (previousPackets?.RerouteSchedulerRejections ?? 0)) / interval,
                    packetSnapshot?.RerouteWaitP95Ticks ?? 0,
                    packetSnapshot?.ForwardingP95Milliseconds ?? 0,
                    ((packetSnapshot?.SkippedTicks ?? 0) - (previousPackets?.SkippedTicks ?? 0)) / interval,
                    ((packetSnapshot?.TickAdmissionFailures ?? 0) -
                        (previousPackets?.TickAdmissionFailures ?? 0)) / interval,
                    rerouteCpuSnapshot?.Queued ?? 0,
                    rerouteCpuSnapshot?.QueueWaitP95Us ?? 0,
                    packetSnapshot?.FirstHopAgeP50Ticks ?? 0,
                    packetSnapshot?.FirstHopAgeP95Ticks ?? 0,
                    ((tickRunnerSnapshot?.ExecutedTicks ?? 0) -
                        (previousTickRunner?.ExecutedTicks ?? 0)) / interval,
                    ((tickRunnerSnapshot?.SkippedIntervals ?? 0) -
                        (previousTickRunner?.SkippedIntervals ?? 0)) / interval,
                    ((packetSnapshot?.TickBudgetStops ?? 0) -
                        (previousPackets?.TickBudgetStops ?? 0)) / interval,
                    tickRunnerSnapshot?.StartLagP95Milliseconds ?? 0,
                    tickRunnerSnapshot?.ExecutionP95Milliseconds ?? 0,
                    tickRunnerSnapshot?.FailedTicks ?? 0,
                    ((packetSnapshot?.NoRouteBackoffs ?? 0) -
                        (previousPackets?.NoRouteBackoffs ?? 0)) / interval,
                    ((packetSnapshot?.CooldownSkips ?? 0) -
                        (previousPackets?.CooldownSkips ?? 0)) / interval,
                    ((packetSnapshot?.LocalChangeWakeups ?? 0) -
                        (previousPackets?.LocalChangeWakeups ?? 0)) / interval,
                    ((packetSnapshot?.RecoveredOriginalHops ?? 0) -
                        (previousPackets?.RecoveredOriginalHops ?? 0)) / interval,
                    ((packetSnapshot?.StaleNegativeResults ?? 0) -
                        (previousPackets?.StaleNegativeResults ?? 0)) / interval,
                    (packetSnapshot?.NoRouteBackoffs ?? 0) == (previousPackets?.NoRouteBackoffs ?? 0)
                        ? 0
                        : (double)((packetSnapshot?.NoRouteBackoffTicks ?? 0) -
                            (previousPackets?.NoRouteBackoffTicks ?? 0)) /
                            ((packetSnapshot?.NoRouteBackoffs ?? 0) -
                            (previousPackets?.NoRouteBackoffs ?? 0)),
                    packetSnapshot?.TrackedChangedNodes ?? 0,
                    ((packetSnapshot?.Enqueued ?? 0) -
                        (previousPackets?.Enqueued ?? 0)) / interval,
                    ((packetSnapshot?.ForwardingVisits ?? 0) -
                        (previousPackets?.ForwardingVisits ?? 0)) / interval,
                    (loadSnapshot.TransportBackpressured -
                        previousLoad.TransportBackpressured) / interval,
                    packetSnapshot?.ReservedAdmissions ?? 0,
                    packetSnapshot?.CurrentVisitLimit ?? 0,
                    ((packetSnapshot?.AdaptiveLimitIncreases ?? 0) -
                        (previousPackets?.AdaptiveLimitIncreases ?? 0)) / interval,
                    ((packetSnapshot?.AdaptiveLimitDecreases ?? 0) -
                        (previousPackets?.AdaptiveLimitDecreases ?? 0)) / interval,
                    ((packetSnapshot?.VisitBudgetStops ?? 0) -
                        (previousPackets?.VisitBudgetStops ?? 0)) / interval,
                    processStallSnapshot.LastHeartbeatLatenessMilliseconds,
                    processStallSnapshot.MaxHeartbeatLatenessMilliseconds,
                    processStallSnapshot.HeartbeatDelaysOver250Milliseconds,
                    processStallSnapshot.MaxThreadPoolDispatchMilliseconds,
                    processStallSnapshot.ThreadPoolDelaysOver250Milliseconds,
                    processStallSnapshot.ThreadPoolThreads,
                    processStallSnapshot.ThreadPoolPendingWorkItems,
                    processStallSnapshot.ProcessCpuCoresUsed,
                    processStallSnapshot.GcPauseMillisecondsSinceStart,
                    interval * 1000.0,
                    processStallSnapshot.Gen0CollectionsSinceStart,
                    processStallSnapshot.Gen1CollectionsSinceStart,
                    processStallSnapshot.Gen2CollectionsSinceStart,
                    processStallSnapshot.GcLastIndex,
                    processStallSnapshot.GcLastGeneration,
                    processStallSnapshot.GcLastPauseMilliseconds,
                    processStallSnapshot.GcLastCompacted,
                    processStallSnapshot.GcLastConcurrent,
                    processStallSnapshot.GcLastFullBlockingIndex,
                    processStallSnapshot.GcLastFullBlockingPauseMilliseconds,
                    processStallSnapshot.GcHeapSizeBytes,
                    processStallSnapshot.GcFragmentedBytes,
                    processStallSnapshot.GcCommittedBytes,
                    processStallSnapshot.GcMemoryLoadBytes,
                    processStallSnapshot.GcAllocatedBytesSinceStart,
                    packetSnapshot?.InteractiveDeliveryAgeP95Ticks ?? 0,
                    packetSnapshot?.BackgroundDeliveryAgeP95Ticks ?? 0,
                    packetSnapshot?.InteractiveQueued ?? 0,
                    packetSnapshot?.BackgroundQueued ?? 0,
                    ((packetSnapshot?.InteractiveDelivered ?? 0) -
                        (previousPackets?.InteractiveDelivered ?? 0)) / interval,
                    ((packetSnapshot?.BackgroundDelivered ?? 0) -
                        (previousPackets?.BackgroundDelivered ?? 0)) / interval);
                _history.Enqueue(sample);
                while (_history.Count > 1200 && _history.TryDequeue(out _)) { }
                lock (_gate) _elapsed = seconds;
                previous = current;
                previousRadio = radioSnapshot;
                previousMedium = mediumSnapshot;
                previousPackets = packetSnapshot;
                previousTickRunner = tickRunnerSnapshot;
                previousLoad = loadSnapshot;
                previousTime = seconds;
                if (seconds >= options.DurationSeconds) { finishedNaturally = true; break; }
            }

            // A scenario is "complete" only after all workload workers have
            // stopped. Otherwise the suite might capture incomplete telemetry
            // or reset the runtime while a previous scenario is still active.
            _cts?.Cancel();
            try { await Task.WhenAll(workers); }
            catch (OperationCanceledException) { }

            lock (_gate) _status = finishedNaturally ? "complete" : "stopped";
        }
        catch (OperationCanceledException)
        {
            lock (_gate) _status = "stopped";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Benchmark failed");
            lock (_gate) { _status = "failed"; _error = ex.Message; }
        }
        finally
        {
            monitor?.Dispose();
            lock (_gate) _elapsed = Math.Max(_elapsed, clock.Elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// Open-loop generator: submission timing is independent of search completion.
    /// The process-wide scheduler rejects overload; it does not create threads
    /// or allow unbounded queues for each new arrival.
    /// </summary>
    private async Task RouteWorkerAsync(BenchmarkOptions options, int worker, int workers,
        SharedMediumNetwork? medium, RadioPacketTransport? packets,
        CancellationToken ct)
    {
        var rng = new Random(options.Seed + 10_001 + worker);
        var clock = Stopwatch.StartNew();
        var rate = (double)options.RoutesPerSecond / workers;
        long offered = 0;
        double plannedMixedOffers = 0;
        double previousScheduleSeconds = 0;
        int columns = (int)Math.Ceiling(Math.Sqrt(options.Nodes));
        // Keep the lease until even a scheduler-cancelled (never executed)
        // task is observed. Work that ran consumes its lease in its finally.
        var pending = new List<(Task<RouteResult> Task, PacketAdmissionLease? Lease)>(384);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                double elapsed = clock.Elapsed.TotalSeconds;
                long target;
                if (options.TrafficProfile == "ss13-mixed")
                {
                    double midpoint = (elapsed + previousScheduleSeconds) / 2;
                    double factor = midpoint % options.BurstPeriodSeconds <
                        options.BurstDurationSeconds ? options.BurstMultiplier : 1;
                    plannedMixedOffers += (elapsed - previousScheduleSeconds) * rate * factor;
                    previousScheduleSeconds = elapsed;
                    target = (long)Math.Floor(plannedMixedOffers);
                }
                else target = (long)Math.Floor(elapsed * rate);
                var count = (int)Math.Min(512L, Math.Max(0L, target - offered));
                for (var i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    offered++;
                    Interlocked.Increment(ref _offeredRoutes);
                    var trafficClass = options.TrafficProfile == "ss13-mixed" &&
                        rng.Next(100) < options.InteractiveTrafficPercent
                            ? PacketTrafficClass.Interactive : PacketTrafficClass.Background;
                    if (trafficClass == PacketTrafficClass.Interactive)
                        Interlocked.Increment(ref _interactiveOffered);
                    else Interlocked.Increment(ref _backgroundOffered);
                    int sourceIndex = rng.Next(options.Nodes);
                    int destinationIndex;
                    if (options.TrafficProfile == "ss13-mixed" &&
                        rng.Next(100) < options.LocalTrafficPercent)
                    {
                        int sourceCol = sourceIndex % columns;
                        int sourceRow = sourceIndex / columns;
                        int destCol = Math.Clamp(sourceCol + rng.Next(-3, 4), 0, columns - 1);
                        int destRow = Math.Clamp(sourceRow + rng.Next(-3, 4), 0,
                            (options.Nodes - 1) / columns);
                        destinationIndex = Math.Min(options.Nodes - 1, destRow * columns + destCol);
                    }
                    else destinationIndex = rng.Next(options.Nodes);
                    // Keep the historical uniform RNG/path workload byte-for-byte:
                    // only the new gameplay profile avoids self-directed traffic.
                    if (options.TrafficProfile == "ss13-mixed" &&
                        destinationIndex == sourceIndex)
                        destinationIndex = (sourceIndex + 1) % options.Nodes;
                    var a = new NodeId((ulong)(sourceIndex + 1));
                    var b = new NodeId((ulong)(destinationIndex + 1));
                    var payload = options.TrafficProfile == "ss13-mixed"
                        ? trafficClass == PacketTrafficClass.Interactive
                            ? rng.Next(64, 257) : rng.Next(512, 4097)
                        : rng.Next(128, 1025);

                    PacketAdmissionLease? lease = null;
                    if (packets is not null && options.PacketPreRouteBackpressure)
                    {
                        lease = packets.TryReserveAdmission(trafficClass);
                        if (lease is null)
                        {
                            // This is offered load rejected by TRANSPORT, not
                            // a route search that was never admitted.
                            Interlocked.Increment(ref _transportBackpressured);
                            Interlocked.Increment(ref _rejectedRoutes);
                            if (trafficClass == PacketTrafficClass.Interactive)
                                Interlocked.Increment(ref _interactiveRejected);
                            else Interlocked.Increment(ref _backgroundRejected);
                            continue;
                        }
                    }

                    if (scheduler.TrySchedule(token =>
                        {
                            try
                            {
                                var route = medium is null
                                    ? runtime.Route(a, b, cancellationToken: token)
                                    : medium.Route(a, b, cancellationToken: token);
                                if (route.Found && medium is not null)
                                {
                                    var tick = Stopwatch.GetTimestamp() /
                                        Math.Max(1, Stopwatch.Frequency / 10);
                                    if (packets is not null)
                                    {
                                        if (lease is not null)
                                            packets.EnqueueReserved(route, payload, tick, lease);
                                        else
                                            packets.Enqueue(route, payload, tick, trafficClass);
                                    }
                                    else
                                        medium.TryTransmit(route, payload, tick);
                                }
                                return route;
                            }
                            finally
                            {
                                // No-route, cancellation, error: free the slot.
                                lease?.Dispose();
                            }
                        }, trafficClass == PacketTrafficClass.Interactive
                            ? WorkPriority.Interactive : WorkPriority.Background,
                            ct, out Task<RouteResult>? task))
                    {
                        Interlocked.Increment(ref _admittedRoutes);
                        if (trafficClass == PacketTrafficClass.Interactive)
                            Interlocked.Increment(ref _interactiveAdmitted);
                        else Interlocked.Increment(ref _backgroundAdmitted);
                        pending.Add((task!, lease));
                    }
                    else
                    {
                        lease?.Dispose();
                        Interlocked.Increment(ref _rejectedRoutes);
                        if (trafficClass == PacketTrafficClass.Interactive)
                            Interlocked.Increment(ref _interactiveRejected);
                        else Interlocked.Increment(ref _backgroundRejected);
                    }
                }
                for (var i = pending.Count - 1; i >= 0; i--)
                {
                    var (task, lease) = pending[i];
                    if (!task.IsCompleted) continue;
                    if (task.IsFaulted) _ = task.Exception;
                    // Also covers cancellation before the scheduled lambda ran.
                    lease?.Dispose();
                    pending.RemoveAt(i);
                }
                await Task.Delay(20, ct);
            }
        }
        finally
        {
            try { await Task.WhenAll(pending.Select(p => p.Task)); }
            catch (Exception) { } // Scheduler publishes failures/cancellations.
            finally
            {
                foreach (var item in pending)
                    item.Lease?.Dispose();
            }
        }
    }

    /// <summary>
    /// Schedule one bounded packet-forwarding pass per 100ms simulation tick.
    /// This work shares the global priority scheduler with route requests.
    /// </summary>
    private async Task PacketWorkerAsync(RadioPacketTransport packets, CancellationToken ct)
    {
        var tickLength = Math.Max(1, Stopwatch.Frequency / 10);
        long lastScheduledTick = -1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var currentTick = Stopwatch.GetTimestamp() / tickLength;
            if (currentTick != lastScheduledTick)
            {
                lastScheduledTick = currentTick;
                if (scheduler.TrySchedule(token => packets.Advance(currentTick, token),
                    WorkPriority.Interactive, ct, out Task<int>? pass))
                {
                    await pass!;
                }
                else
                    packets.RecordTickAdmissionFailure();
            }
            await Task.Delay(20, ct);
        }
    }

    /// <summary>
    /// Dedicated bounded reroute executor. Only the short ticket acquisition
    /// and result application touch the packet queue lock. Actual A*/Dijkstra
    /// work happens on a separate CPU worker with its own soft budget.
    /// </summary>
    private async Task RerouteWorkerAsync(RadioPacketTransport packets,
        SharedMediumNetwork medium, PriorityWorkScheduler rerouteScheduler,
        CancellationToken ct)
    {
        var pending = new List<Task>(packets.Options.MaxConcurrentReroutes);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                pending.RemoveAll(t => t.IsCompleted);
                while (pending.Count < packets.Options.MaxConcurrentReroutes &&
                    packets.TryTakeReroute(out var ticket))
                {
                    if (rerouteScheduler.TrySchedule(
                        token => medium.Route(ticket.Source, ticket.Destination,
                            cancellationToken: token),
                        WorkPriority.Background, ct, out Task<RouteResult>? result))
                    {
                        pending.Add(HandleRerouteAsync(ticket, result!));
                    }
                    else
                    {
                        packets.CancelReroute(ticket, schedulerRejected: true);
                        break;
                    }
                }
                await Task.Delay(20, ct);
            }
        }
        finally
        {
            // The benchmark cancellation token is propagated to every task.
            // Await the bounded set so a stale reroute never mutates a later run.
            try { await Task.WhenAll(pending); }
            catch (OperationCanceledException) { }
        }

        async Task HandleRerouteAsync(PacketRerouteRequest ticket, Task<RouteResult> result)
        {
            try
            {
                var path = await result.ConfigureAwait(false);
                packets.CompleteReroute(ticket, path);
            }
            catch (OperationCanceledException)
            {
                packets.CancelReroute(ticket);
            }
            catch (Exception ex)
            {
                packets.CancelReroute(ticket);
                log.LogError(ex, "Packet reroute worker failed for request {Id}", ticket.Id);
            }
        }
    }

    private async Task MutationWorkerAsync(BenchmarkOptions options, NodePosition[] positions,
        DynamicRadioNetwork? radio, SharedMediumNetwork? medium,
        RadioPacketTransport? packets, CancellationToken ct)
    {
        var rng = new Random(options.Seed + 7331);
        // Only synthetic shortcut links are removed. The base grid stays connected.
        var dynamicLinks = new List<(NodeId A, NodeId B)>();
        double moveDue = 0, linksDue = 0;
        var radioActive = Enumerable.Repeat(true, options.Nodes).ToArray();
        var profile = new RadioProfile(options.RadioRange, 1, 8);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var changes = new List<TopologyChange>();
            var radioUpdates = new Dictionary<NodeId, RadioNodeUpdate>();
            moveDue += (double)options.MovesPerSecond / 10;
            var moves = (int)moveDue;
            moveDue -= moves;
            for (var i = 0; i < moves; i++)
            {
                var index = rng.Next(options.Nodes);
                var last = positions[index];
                var next = new NodePosition(last.X + rng.Next(-2, 3),
                    last.Y + rng.Next(-2, 3), last.Z);
                positions[index] = next;
                var id = new NodeId((ulong)(index + 1));
                if (radio is null && medium is null)
                    changes.Add(new TopologyChange(TopologyOperation.MoveNode, id, Position: next));
                else if (radioUpdates.TryGetValue(id, out var prior))
                    radioUpdates[id] = prior with { Position = next };
                else
                    radioUpdates[id] = new RadioNodeUpdate(id, Position: next);
            }

            linksDue += (double)options.LinkChangesPerSecond / 10;
            var links = (int)linksDue;
            linksDue -= links;
            var snapshot = runtime.Topology.Snapshot;
            if (radio is not null || medium is not null)
            {
                for (var i = 0; i < links; i++)
                {
                    var index = rng.Next(options.Nodes);
                    radioActive[index] = !radioActive[index];
                    var id = new NodeId((ulong)(index + 1));
                    var change = radioUpdates.TryGetValue(id, out var existing)
                        ? existing
                        : new RadioNodeUpdate(id);
                    radioUpdates[id] = change with
                    {
                        Profile = radioActive[index] ? profile : null,
                        ChangeProfile = true
                    };
                }
                if (radioUpdates.Count > 0)
                {
                    var updates = radioUpdates.Values.ToArray();
                    if (radio is not null) radio.Update(updates, ct);
                    else
                    {
                        medium!.Update(updates, ct);
                        packets?.NotifyRadioUpdates(updates);
                    }
                }
                await Task.Delay(100, ct);
                continue;
            }
            for (var i = 0; i < links; i++)
            {
                if (dynamicLinks.Count > 0 && rng.Next(2) == 0)
                {
                    var index = rng.Next(dynamicLinks.Count);
                    var (from, to) = dynamicLinks[index];
                    dynamicLinks[index] = dynamicLinks[^1];
                    dynamicLinks.RemoveAt(dynamicLinks.Count - 1);
                    changes.Add(new TopologyChange(TopologyOperation.Disconnect, from, to));
                    continue;
                }

                var a = new NodeId((ulong)(rng.Next(options.Nodes) + 1));
                var b = new NodeId((ulong)(rng.Next(options.Nodes) + 1));
                if (a == b || snapshot.Nodes[a].Links.ContainsKey(b)) continue;
                dynamicLinks.Add((a, b));
                changes.Add(new TopologyChange(TopologyOperation.Connect,
                    a, b, (uint)rng.Next(4, 14)));
            }
            if (changes.Count > 0) runtime.Apply(changes);
            await Task.Delay(100, ct);
        }
    }
}
