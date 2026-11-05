using System.Diagnostics;
using NetDM.Core;

namespace NetDM.Host.Benchmarks;

public sealed record ComparisonOptions(
    int Nodes = 10_000,
    int Routes = 160,
    int ViewportQueries = 120,
    int MoveBatches = 32,
    int Seed = 1337)
{
    public void Validate()
    {
        if (Nodes is < 100 or > 30_000) throw new ArgumentOutOfRangeException(nameof(Nodes));
        if (Routes is < 10 or > 1000) throw new ArgumentOutOfRangeException(nameof(Routes));
        if (ViewportQueries is < 10 or > 1000) throw new ArgumentOutOfRangeException(nameof(ViewportQueries));
        if (MoveBatches is < 0 or > 250) throw new ArgumentOutOfRangeException(nameof(MoveBatches));
    }
}

public sealed record AlgorithmMeasurement(
    string Name, int Routes, double P50Us, double P95Us, double P99Us,
    double MeanExploredNodes, double MeanAllocatedBytes,
    double SerialQueriesPerSecond);

public sealed record SpatialMeasurement(
    int Queries, double IndexedP50Us, double IndexedP95Us,
    double ScanP50Us, double ScanP95Us);

public sealed record MovementMeasurement(
    int Batches, int AppliedMoves, double BatchP50Us, double BatchP95Us,
    long AllocatedBytes);

public sealed record ComparisonReport(
    ComparisonOptions Options, DateTimeOffset CompletedAt,
    int Nodes, int UndirectedEdges,
    double GraphBuildMs, long GraphBuildAllocatedBytes,
    double CsrBuildMs, long CsrBuildAllocatedBytes, long CsrArrayPayloadBytes,
    AlgorithmMeasurement[] Algorithms, SpatialMeasurement Spatial,
    MovementMeasurement Movement);

public sealed record ComparisonState(
    string Status, string Stage, int Completed, int Total,
    ComparisonOptions? Options, ComparisonReport? Report, string? Error);

/// <summary>
/// Self-contained, single-worker, cold-cache comparison of identical requests
/// on one immutable graph. Does not mutate the live BlueMoon simulation.
/// </summary>
public sealed class RoutingComparisonRunner(ILogger<RoutingComparisonRunner> log)
{
    private readonly object _gate = new();
    private Task? _task;
    private CancellationTokenSource? _cts;
    private string _status = "idle", _stage = "not started";
    private int _completed, _total;
    private string? _error;
    private ComparisonOptions? _options;
    private ComparisonReport? _report;

    public ComparisonState State()
    {
        lock (_gate)
            return new ComparisonState(_status, _stage, _completed, _total,
                _options, _report, _error);
    }

    public bool Start(ComparisonOptions options)
    {
        options.Validate();
        lock (_gate)
        {
            if (_task is { IsCompleted: false }) return false;
            _cts?.Dispose();
            var cts = new CancellationTokenSource();
            _cts = cts;
            _options = options;
            _report = null;
            _error = null;
            _status = "running";
            _stage = "building graph";
            _completed = 0;
            _total = 4 * options.Routes + options.ViewportQueries + options.MoveBatches;
            _task = Task.Run(() => Execute(options, cts.Token));
            return true;
        }
    }

    public bool Stop()
    {
        lock (_gate)
        {
            if (_task is not { IsCompleted: false }) return false;
            _cts?.Cancel();
            _status = "stopping";
            return true;
        }
    }

    private void Step(string stage, int completed)
    {
        lock (_gate) { _stage = stage; _completed = completed; }
    }

    private void Execute(ComparisonOptions options, CancellationToken ct)
    {
        try
        {
            var rng = new Random(options.Seed);
            var store = new TopologyStore();
            var columns = (int)Math.Ceiling(Math.Sqrt(options.Nodes));
            var width = columns * 12;
            var height = ((options.Nodes + columns - 1) / columns) * 12;
            var mutations = new List<TopologyChange>(options.Nodes * 4);
            for (var i = 0; i < options.Nodes; i++)
            {
                mutations.Add(new TopologyChange(
                    TopologyOperation.AddNode, new NodeId((ulong)(i + 1)),
                    Position: new NodePosition(i % columns * 12, i / columns * 12)));
            }
            for (var i = 0; i < options.Nodes; i++)
            {
                var source = new NodeId((ulong)(i + 1));
                if (i % columns < columns - 1 && i + 1 < options.Nodes)
                    mutations.Add(new TopologyChange(TopologyOperation.Connect, source,
                        new NodeId((ulong)(i + 2)), (uint)rng.Next(1, 7)));
                if (i + columns < options.Nodes)
                    mutations.Add(new TopologyChange(TopologyOperation.Connect, source,
                        new NodeId((ulong)(i + columns + 1)), (uint)rng.Next(1, 7)));
                if (i % 37 == 0)
                {
                    var randomNode = rng.Next(options.Nodes);
                    if (randomNode != i)
                        mutations.Add(new TopologyChange(TopologyOperation.Connect,
                            source, new NodeId((ulong)(randomNode + 1)),
                            (uint)rng.Next(6, 26)));
                }
            }

            ct.ThrowIfCancellationRequested();
            var beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            store.ApplyBatch(mutations);
            var buildMs = ElapsedUs(start) / 1000.0;
            var buildAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
            var snapshot = store.Snapshot;
            var edgeCount = snapshot.Nodes.Values.Sum(n => n.Links.Count) / 2;

            Step("building CSR", 0);
            beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
            start = Stopwatch.GetTimestamp();
            var compact = CompactGraph.Build(snapshot);
            var csrBuildMs = ElapsedUs(start) / 1000.0;
            var csrAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;

            var pairs = new (NodeId A, NodeId B)[options.Routes];
            for (var i = 0; i < pairs.Length; i++)
                pairs[i] = (new NodeId((ulong)(rng.Next(options.Nodes) + 1)),
                    new NodeId((ulong)(rng.Next(options.Nodes) + 1)));

            var implementations = new (string Name, Func<NodeId, NodeId, CancellationToken, RouteResult> Run)[]
            {
                ("Dictionary · Dijkstra", (a,b,c) => Router.Find(snapshot,a,b,cancellationToken:c)),
                ("Dictionary · Bidirectional", (a,b,c) => BidirectionalRouter.Find(snapshot,a,b,cancellationToken:c)),
                ("CSR · Dijkstra", (a,b,c) => CompactRouter.Dijkstra(compact,a,b,cancellationToken:c)),
                ("CSR · Bidirectional", (a,b,c) => CompactRouter.Bidirectional(compact,a,b,cancellationToken:c))
            };

            var baseline = new RouteResult[pairs.Length];
            var output = new List<AlgorithmMeasurement>();
            for (var algorithm = 0; algorithm < implementations.Length; algorithm++)
            {
                ct.ThrowIfCancellationRequested();
                Step("routing: " + implementations[algorithm].Name, algorithm * options.Routes);
                var fn = implementations[algorithm].Run;
                // Warm-up is NOT included in measurement. Results are never cached.
                for (var i = 0; i < Math.Min(8, pairs.Length); i++)
                {
                    var pair = pairs[i];
                    _ = fn(pair.A, pair.B, ct);
                }

                var microseconds = new double[pairs.Length];
                double totalUs = 0, totalExplored = 0;
                long totalAllocated = 0;
                for (var i = 0; i < pairs.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var pair = pairs[i];
                    beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
                    start = Stopwatch.GetTimestamp();
                    var result = fn(pair.A, pair.B, ct);
                    var us = ElapsedUs(start);
                    var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
                    if (algorithm == 0) baseline[i] = result;
                    else if (result.Found != baseline[i].Found || result.Cost != baseline[i].Cost)
                        throw new InvalidOperationException(
                            $"Route mismatch in {implementations[algorithm].Name}, pair {i}.");
                    ValidateRoute(snapshot, pair.A, pair.B, result);
                    microseconds[i] = us;
                    totalUs += us;
                    totalExplored += result.Explored;
                    totalAllocated += allocated;
                    if (i % 12 == 0)
                        Step("routing: " + implementations[algorithm].Name,
                            algorithm * options.Routes + i + 1);
                }
                Array.Sort(microseconds);
                output.Add(new AlgorithmMeasurement(
                    implementations[algorithm].Name, pairs.Length,
                    Percentile(microseconds, .50), Percentile(microseconds, .95),
                    Percentile(microseconds, .99), totalExplored / pairs.Length,
                    (double)totalAllocated / pairs.Length,
                    totalUs == 0 ? 0 : 1_000_000.0 * pairs.Length / totalUs));
            }

            Step("spatial index vs full scan", 4 * options.Routes);
            var spatialIndex = new double[options.ViewportQueries];
            var fullScan = new double[options.ViewportQueries];
            var viewportWidth = Math.Max(32, width / 9);
            var viewportHeight = Math.Max(32, height / 9);
            for (var i = 0; i < options.ViewportQueries; i++)
            {
                ct.ThrowIfCancellationRequested();
                var x = rng.Next(width + 1), y = rng.Next(height + 1);
                start = Stopwatch.GetTimestamp();
                var indexed = TopologyStore.QueryViewport(snapshot,
                    x, y, x + viewportWidth, y + viewportHeight, limit: 20_000);
                spatialIndex[i] = ElapsedUs(start);
                start = Stopwatch.GetTimestamp();
                var scanned = snapshot.Nodes.Values
                    .Where(n => n.Position is { } p &&
                        p.X >= x && p.X <= x + viewportWidth &&
                        p.Y >= y && p.Y <= y + viewportHeight && p.Z == 0)
                    .Select(n => n.Id).ToHashSet();
                fullScan[i] = ElapsedUs(start);
                if (indexed.Count != scanned.Count || indexed.Any(n => !scanned.Contains(n.Id)))
                    throw new InvalidOperationException("Spatial index result differs from full scan.");
                if (i % 12 == 0) Step("spatial index vs full scan", 4 * options.Routes + i + 1);
            }
            Array.Sort(spatialIndex); Array.Sort(fullScan);

            var movementTimings = new double[options.MoveBatches];
            long allocatedMovement = 0;
            var appliedMoves = 0;
            for (var i = 0; i < options.MoveBatches; i++)
            {
                ct.ThrowIfCancellationRequested();
                var moves = new List<TopologyChange>(64);
                for (var j = 0; j < 64; j++)
                {
                    var node = new NodeId((ulong)(rng.Next(options.Nodes) + 1));
                    moves.Add(new TopologyChange(TopologyOperation.MoveNode, node,
                        Position: new NodePosition(rng.Next(width), rng.Next(height))));
                }
                beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
                start = Stopwatch.GetTimestamp();
                var applied = store.ApplyBatch(moves);
                movementTimings[i] = ElapsedUs(start);
                allocatedMovement += GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
                appliedMoves += applied.Applied;
                if (i % 6 == 0)
                    Step("movement batches", 4 * options.Routes + options.ViewportQueries + i + 1);
            }
            Array.Sort(movementTimings);

            var report = new ComparisonReport(options, DateTimeOffset.UtcNow,
                options.Nodes, edgeCount, buildMs, buildAllocated,
                csrBuildMs, csrAllocated, compact.PayloadBytes, output.ToArray(),
                new SpatialMeasurement(options.ViewportQueries,
                    Percentile(spatialIndex, .5), Percentile(spatialIndex, .95),
                    Percentile(fullScan, .5), Percentile(fullScan, .95)),
                new MovementMeasurement(options.MoveBatches, appliedMoves,
                    Percentile(movementTimings, .5), Percentile(movementTimings, .95),
                    allocatedMovement));
            lock (_gate)
            {
                _report = report;
                _status = "complete";
                _stage = "finished";
                _completed = _total;
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate) { _status = "stopped"; _stage = "cancelled"; }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Algorithm comparison failed");
            lock (_gate) { _status = "failed"; _error = ex.Message; }
        }
    }

    private static double Percentile(double[] sorted, double fraction) =>
        sorted.Length == 0 ? 0 :
        sorted[(int)Math.Ceiling((sorted.Length - 1) * fraction)];

    private static double ElapsedUs(long fromTicks) =>
        (Stopwatch.GetTimestamp() - fromTicks) * 1_000_000.0 / Stopwatch.Frequency;

    private static void ValidateRoute(TopologySnapshot graph, NodeId start, NodeId target,
        RouteResult result)
    {
        if (!result.Found) return;
        if (result.Path.Count == 0 || result.Path[0] != start || result.Path[^1] != target)
            throw new InvalidOperationException("Malformed path returned by solver.");
        ulong sum = 0;
        for (var i = 1; i < result.Path.Count; i++)
        {
            if (!graph.Nodes[result.Path[i - 1]].Links.TryGetValue(result.Path[i], out var edge))
                throw new InvalidOperationException("Path includes missing edge.");
            sum = checked(sum + edge.Cost);
        }
        if (sum != result.Cost)
            throw new InvalidOperationException("Route path cost does not match the result.");
    }
}
