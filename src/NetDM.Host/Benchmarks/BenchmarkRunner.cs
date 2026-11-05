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
    int Seed = 1337)
{
    public void Validate()
    {
        if (Nodes is < 9 or > 30_000) throw new ArgumentOutOfRangeException(nameof(Nodes));
        if (RoutesPerSecond is < 0 or > 20_000) throw new ArgumentOutOfRangeException(nameof(RoutesPerSecond));
        if (MovesPerSecond is < 0 or > 10_000) throw new ArgumentOutOfRangeException(nameof(MovesPerSecond));
        if (LinkChangesPerSecond is < 0 or > 1_000) throw new ArgumentOutOfRangeException(nameof(LinkChangesPerSecond));
        if (DurationSeconds is < 2 or > 1800) throw new ArgumentOutOfRangeException(nameof(DurationSeconds));
    }
}

public sealed record BenchmarkState(
    string Status, BenchmarkOptions? Options, DateTimeOffset? StartedUtc,
    double ElapsedSeconds, int WorldWidth, int WorldHeight, string? Error);

public sealed record BenchmarkSample(
    DateTimeOffset At, double ElapsedSeconds, double ActualRoutesPerSecond,
    double ActualMovesPerSecond, double ActualMutationsPerSecond,
    double P50Us, double P95Us, double P99Us, double CacheHitPercent,
    long ManagedBytes, int NodeCount, long Version, long ConnectivityVersion);

/// <summary>
/// Repeatable CPU/graph workload. Route and movement generators are independent;
/// reported throughput is observed throughput, never the requested rate.
/// </summary>
public sealed class BenchmarkRunner(NetworkRuntime runtime, ILogger<BenchmarkRunner> log)
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

    public BenchmarkState State()
    {
        lock (_gate)
            return new BenchmarkState(_status, _options, _started, _elapsed, _width, _height, _error);
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

            // Connected 2-D grid, with some redundant cross-links for routing diversity.
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
            lock (_gate)
            {
                _width = columns * 12;
                _height = ((options.Nodes + columns - 1) / columns) * 12;
                _status = "running";
            }

            clock.Start();
            var workerCount = options.RoutesPerSecond == 0 ? 0 :
                Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2));
            var workers = Enumerable.Range(0, workerCount).Select(worker =>
                Task.Run(() => RouteWorkerAsync(options, worker, workerCount, ct), ct)).ToList();
            if (options.MovesPerSecond > 0 || options.LinkChangesPerSecond > 0)
                workers.Add(Task.Run(() => MutationWorkerAsync(options, positions, ct), ct));

            var previous = runtime.Metrics.Capture(runtime.Cache.Count);
            var previousTime = clock.Elapsed.TotalSeconds;
            var finishedNaturally = false;

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                var seconds = clock.Elapsed.TotalSeconds;
                var current = runtime.Metrics.Capture(runtime.Cache.Count);
                var snapshot = runtime.Topology.Snapshot;
                var interval = Math.Max(0.001, seconds - previousTime);
                var deltaRoutes = current.Routes - previous.Routes;
                var deltaMoves = current.Moves - previous.Moves;
                var sample = new BenchmarkSample(
                    DateTimeOffset.UtcNow, seconds,
                    deltaRoutes / interval, deltaMoves / interval,
                    (current.Mutations - previous.Mutations) / interval,
                    current.P50Microseconds, current.P95Microseconds, current.P99Microseconds,
                    deltaRoutes == 0 ? 0 : 100.0 * (current.CacheHits - previous.CacheHits) / deltaRoutes,
                    current.ManagedBytes, snapshot.Nodes.Count, snapshot.Version,
                    snapshot.ConnectivityVersion);
                _history.Enqueue(sample);
                while (_history.Count > 1200 && _history.TryDequeue(out _)) { }
                lock (_gate) _elapsed = seconds;
                previous = current;
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

    private async Task RouteWorkerAsync(BenchmarkOptions options, int worker, int workers, CancellationToken ct)
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
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var a = new NodeId((ulong)(rng.Next(options.Nodes) + 1));
                var b = new NodeId((ulong)(rng.Next(options.Nodes) + 1));
                runtime.Route(a, b, cancellationToken: ct);
            }
            await Task.Delay(100, ct);
        }
    }

    private async Task MutationWorkerAsync(BenchmarkOptions options, NodePosition[] positions, CancellationToken ct)
    {
        var rng = new Random(options.Seed + 7331);
        double moveDue = 0, linksDue = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var changes = new List<TopologyChange>();
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
                changes.Add(new TopologyChange(TopologyOperation.MoveNode,
                    new NodeId((ulong)(index + 1)), Position: next));
            }

            linksDue += (double)options.LinkChangesPerSecond / 10;
            var links = (int)linksDue;
            linksDue -= links;
            var snapshot = runtime.Topology.Snapshot;
            for (var i = 0; i < links; i++)
            {
                var a = new NodeId((ulong)(rng.Next(options.Nodes) + 1));
                var b = new NodeId((ulong)(rng.Next(options.Nodes) + 1));
                if (a == b) continue;
                var linked = snapshot.Nodes[a].Links.ContainsKey(b);
                changes.Add(new TopologyChange(
                    linked ? TopologyOperation.Disconnect : TopologyOperation.Connect,
                    a, b, (uint)rng.Next(4, 14)));
            }
            if (changes.Count > 0) runtime.Apply(changes);
            await Task.Delay(100, ct);
        }
    }
}
