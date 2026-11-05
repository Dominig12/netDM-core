using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace NetDM.Core;

/// <summary>Bounded route cache, with the topology version in every key.</summary>
public sealed class RouteCache(int capacity = 16_384)
{
    private readonly ConcurrentDictionary<(long Version, NodeId Source, NodeId Target), RouteResult> _items = new();
    private readonly ConcurrentQueue<(long Version, NodeId Source, NodeId Target)> _order = new();
    public int Count => _items.Count;

    public bool TryGet(long version, NodeId source, NodeId target, out RouteResult result)
    {
        if (_items.TryGetValue((version, source, target), out var found))
        {
            result = found;
            return true;
        }
        result = null!;
        return false;
    }

    public void Store(long connectivityVersion, RouteResult value, NodeId source, NodeId target)
    {
        if (capacity <= 0) return;
        var key = (connectivityVersion, source, target);
        if (!_items.TryAdd(key, value)) return;
        _order.Enqueue(key);
        while (_items.Count > capacity && _order.TryDequeue(out var eldest))
            _items.TryRemove(eldest, out _);
    }

    public void Clear()
    {
        _items.Clear();
        while (_order.TryDequeue(out _)) { }
    }
}

public sealed record RuntimeCounters(
    long Routes, long Found, long CacheHits, long Mutations, long Moves,
    double P50Microseconds, double P95Microseconds, double P99Microseconds,
    int CacheEntries, long ManagedBytes, int Gen2Collections,
    long TotalExploredNodes, long TotalRouteAllocatedBytes,
    double GcTotalPauseMilliseconds);

/// <summary>Rolling latency window + monotonic counters. Approximate percentiles.</summary>
public sealed class NetworkMetrics
{
    private const int Window = 2048;
    private readonly long[] _routeLatenciesUs = new long[Window];
    private long _latencyIndex;
    private long _routes, _found, _cacheHits, _mutations, _moves;
    private long _exploredNodes, _allocatedBytes;

    public void RouteCompleted(bool found, bool hit, long elapsedTicks,
        int explored, long allocatedBytes)
    {
        var us = Math.Max(1, elapsedTicks * 1_000_000 / Stopwatch.Frequency);
        var index = Interlocked.Increment(ref _latencyIndex) - 1;
        Interlocked.Exchange(ref _routeLatenciesUs[(int)(index % Window)], us);
        Interlocked.Increment(ref _routes);
        if (found) Interlocked.Increment(ref _found);
        if (hit) Interlocked.Increment(ref _cacheHits);
        Interlocked.Add(ref _exploredNodes, explored);
        Interlocked.Add(ref _allocatedBytes, Math.Max(0, allocatedBytes));
    }

    public void MutationCompleted(BatchResult result, int moves)
    {
        Interlocked.Add(ref _mutations, result.Applied);
        Interlocked.Add(ref _moves, moves);
    }

    public RuntimeCounters Capture(int cacheEntries)
    {
        var window = Math.Min(Volatile.Read(ref _latencyIndex), Window);
        var samples = new long[(int)window];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = Volatile.Read(ref _routeLatenciesUs[i]);
        Array.Sort(samples);
        double Percentile(double p) =>
            samples.Length == 0 ? 0 : samples[(int)Math.Ceiling((samples.Length - 1) * p)];
        return new RuntimeCounters(
            Volatile.Read(ref _routes), Volatile.Read(ref _found),
            Volatile.Read(ref _cacheHits), Volatile.Read(ref _mutations),
            Volatile.Read(ref _moves), Percentile(0.5), Percentile(0.95),
            Percentile(0.99), cacheEntries, GC.GetTotalMemory(false), GC.CollectionCount(2),
            Interlocked.Read(ref _exploredNodes),
            Interlocked.Read(ref _allocatedBytes),
            GC.GetTotalPauseDuration().TotalMilliseconds);
    }
}

/// <summary>One cohesive graph/query API, reusable by the pipe and dashboard hosts.</summary>
public sealed class NetworkRuntime
{
    private long _sampleCounter;
    private readonly ConcurrentDictionary<NodeId, long> _traffic = new();

    public TopologyStore Topology { get; } = new();
    public RouteCache Cache { get; } = new();
    public NetworkMetrics Metrics { get; } = new();

    public BatchResult Apply(IReadOnlyList<TopologyChange> changes)
    {
        var result = Topology.ApplyBatch(changes);
        if (result.Applied > 0)
        {
            // Position-only updates must not invalidate connectivity route caches.
            if (changes.Any(change => change.Operation != TopologyOperation.MoveNode))
                Cache.Clear();
            Metrics.MutationCompleted(result,
                changes.Count(c => c.Operation == TopologyOperation.MoveNode));
        }
        return result;
    }

    public long Reset()
    {
        Cache.Clear();
        _traffic.Clear();
        return Topology.Clear();
    }

    public RouteResult Route(NodeId from, NodeId to, int maxExplored = 100_000,
        CancellationToken cancellationToken = default)
    {
        var start = Stopwatch.GetTimestamp();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var snapshot = Topology.Snapshot;
        // Exploration-limited requests must not reuse or poison full-budget cached routes.
        // A bounded search returning Missing may simply have exceeded its work budget.
        var cacheEligible = maxExplored == 100_000;
        RouteResult cachedRoute = null!;
        var cached = cacheEligible &&
            Cache.TryGet(snapshot.ConnectivityVersion, from, to, out cachedRoute);
        var result = cached ? cachedRoute! :
            Router.Find(snapshot, from, to, maxExplored, cancellationToken);
        if (!cached && cacheEligible && result.Explored <= maxExplored)
            Cache.Store(snapshot.ConnectivityVersion, result, from, to);

        if (cached && result.Version != snapshot.Version)
            result = result with { Version = snapshot.Version };
        RecordExternalRoute(result, cached, Stopwatch.GetTimestamp() - start,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        return result;
    }

    /// <summary>
    /// Shared telemetry sink for alternate routing layers. The caller must
    /// measure elapsed ticks, not Stopwatch.ElapsedMilliseconds.
    /// </summary>
    public void RecordExternalRoute(RouteResult result, bool cached, long elapsedTicks,
        long allocatedBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(result);
        Metrics.RouteCompleted(result.Found, cached, elapsedTicks,
            cached ? 0 : result.Explored, allocatedBytes);

        // Sample one route in 16: sampled activity is not packet throughput.
        if (result.Found && (Interlocked.Increment(ref _sampleCounter) & 15) == 0)
            foreach (var node in result.Path)
                _traffic.AddOrUpdate(node, 1, (_, value) => value + 1);
    }

    public long Traffic(NodeId id) => _traffic.TryGetValue(id, out var hits) ? hits : 0;
}
