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
    int MediumBytesPerTick = 8192)
{
    public void Validate()
    {
        if (Nodes is < 9 or > 30_000) throw new ArgumentOutOfRangeException(nameof(Nodes));
        if (RoutesPerSecond is < 0 or > 20_000) throw new ArgumentOutOfRangeException(nameof(RoutesPerSecond));
        if (MovesPerSecond is < 0 or > 10_000) throw new ArgumentOutOfRangeException(nameof(MovesPerSecond));
        if (LinkChangesPerSecond is < 0 or > 1_000) throw new ArgumentOutOfRangeException(nameof(LinkChangesPerSecond));
        if (DurationSeconds is < 2 or > 1800) throw new ArgumentOutOfRangeException(nameof(DurationSeconds));
        if (RadioRange is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(RadioRange));
        if (RadioModel is not ("pairwise" or "shared"))
            throw new ArgumentException("RadioModel must be pairwise or shared.", nameof(RadioModel));
        if (MediumBytesPerTick is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(MediumBytesPerTick));
    }
}

public sealed record BenchmarkState(
    string Status, BenchmarkOptions? Options, DateTimeOffset? StartedUtc,
    double ElapsedSeconds, int WorldWidth, int WorldHeight, string? Error,
    RadioStats? Radio, SharedMediumStatistics? SharedMedium);

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
    int SchedulerQueued, double SchedulerQueueWaitP95Us);

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
    private string _status = "idle";
    private string? _error;
    private BenchmarkOptions? _options;
    private DateTimeOffset? _started;
    private int _width, _height;
    private double _elapsed;
    private DynamicRadioNetwork? _radioEngine;
    private SharedMediumNetwork? _sharedMedium;

    /// <summary>Only for read-only observability; the service remains benchmark-owned.</summary>
    public SharedMediumNetwork? ActiveSharedMedium
    {
        get { lock (_gate) return _sharedMedium; }
    }

    public BenchmarkState State()
    {
        lock (_gate)
            return new BenchmarkState(_status, _options, _started, _elapsed, _width, _height, _error,
                _radioEngine?.Stats(), _sharedMedium?.Stats());
    }

    public BenchmarkSample[] History() => _history.ToArray();

    public bool Start(BenchmarkOptions options)
    {
        options.Validate();
        lock (_gate)
        {
            if (_task is { IsCompleted: false }) return false;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _options = options;
            _status = "building";
            _started = DateTimeOffset.UtcNow;
            _elapsed = 0;
            _error = null;
            _width = _height = 0;
            _radioEngine = null;
            _sharedMedium = null;
            while (_history.TryDequeue(out _)) { }
            _task = Task.Run(() => RunAsync(options, _cts.Token));
            return true;
        }
    }

    public bool Stop()
    {
        lock (_gate)
        {
            if (_task is not { IsCompleted: false }) return false;
            _status = "stopping";
            _cts?.Cancel();
            return true;
        }
    }

    private async Task RunAsync(BenchmarkOptions options, CancellationToken ct)
    {
        var clock = new Stopwatch();
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
                    medium = new SharedMediumNetwork(runtime, options.MediumBytesPerTick);
                    medium.Update(registrations, ct);
                }
                else
                {
                    radio = new DynamicRadioNetwork(runtime);
                    radio.Update(registrations, ct);
                }
            }
            lock (_gate)
            {
                _width = columns * 12;
                _height = ((options.Nodes + columns - 1) / columns) * 12;
                _radioEngine = radio;
                _sharedMedium = medium;
                _status = "running";
            }

            clock.Start();
            var workerCount = options.RoutesPerSecond == 0 ? 0 :
                Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2));
            var workers = Enumerable.Range(0, workerCount).Select(worker =>
                Task.Run(() => RouteWorkerAsync(options, worker, workerCount, medium, ct), ct)).ToList();
            if (options.MovesPerSecond > 0 || options.LinkChangesPerSecond > 0)
                workers.Add(Task.Run(() => MutationWorkerAsync(options, positions, radio, medium, ct), ct));

            var previous = runtime.Metrics.Capture(runtime.Cache.Count);
            var previousRadio = radio?.Stats();
            var previousMedium = medium?.Stats();
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
                var interval = Math.Max(0.001, seconds - previousTime);
                var schedulerSnapshot = scheduler.Capture();
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
                    schedulerSnapshot.Queued, schedulerSnapshot.QueueWaitP95Us);
                _history.Enqueue(sample);
                while (_history.Count > 1200 && _history.TryDequeue(out _)) { }
                lock (_gate) _elapsed = seconds;
                previous = current;
                previousRadio = radioSnapshot;
                previousMedium = mediumSnapshot;
                previousTime = seconds;
                if (seconds >= options.DurationSeconds) { finishedNaturally = true; break; }
            }

            // Stop workers at the end of the benchmark without cancelling the caller's token.
            if (finishedNaturally)
                lock (_gate) _status = "complete";
            else
                lock (_gate) _status = "stopped";

            _cts?.Cancel();
            try { await Task.WhenAll(workers); }
            catch (OperationCanceledException) { }
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
            lock (_gate) _elapsed = Math.Max(_elapsed, clock.Elapsed.TotalSeconds);
        }
    }

    private async Task RouteWorkerAsync(BenchmarkOptions options, int worker, int workers,
        SharedMediumNetwork? medium, CancellationToken ct)
    {
        var rng = new Random(options.Seed + 10_001 + worker);
        var quota = (double)options.RoutesPerSecond / workers / 10;
        double due = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            due += quota;
            var count = (int)due;
            due -= count;
            // Burst-submit the current 100 ms quota without spawning a task
            // per route. The shared scheduler bounds queued work and CPU workers.
            var pending = new List<Task<RouteResult>>(Math.Min(count, 300));
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var a = new NodeId((ulong)(rng.Next(options.Nodes) + 1));
                var b = new NodeId((ulong)(rng.Next(options.Nodes) + 1));
                var payload = rng.Next(128, 1025);
                if (scheduler.TrySchedule(token =>
                    {
                        var route = medium is null
                            ? runtime.Route(a, b, cancellationToken: token)
                            : medium.Route(a, b, cancellationToken: token);
                        if (route.Found && medium is not null)
                        {
                            var tick = Stopwatch.GetTimestamp() /
                                Math.Max(1, Stopwatch.Frequency / 10);
                            medium.TryTransmit(route, payload, tick);
                        }
                        return route;
                    }, WorkPriority.Background, ct, out Task<RouteResult>? result))
                    pending.Add(result!);
                // Rejected routes are measured by scheduler counters. Never queue
                // an unbounded backlog in the synthetic benchmark generator.
            }
            if (pending.Count > 0)
                await Task.WhenAll(pending);
            await Task.Delay(100, ct);
        }
    }

    private async Task MutationWorkerAsync(BenchmarkOptions options, NodePosition[] positions,
        DynamicRadioNetwork? radio, SharedMediumNetwork? medium, CancellationToken ct)
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
                    else medium!.Update(updates, ct);
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
