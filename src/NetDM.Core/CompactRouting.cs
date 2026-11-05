namespace NetDM.Core;

/// <summary>
/// Read-only CSR (compressed sparse row) representation for CPU locality.
/// Built from one immutable snapshot. This is a routing/benchmark projection,
/// NOT the live mutable store; rebuilding it for every movement would be wasteful.
/// </summary>
public sealed class CompactGraph
{
    public readonly record struct Arc(int Target, uint Cost);

    private readonly NodeId[] _ids;
    private readonly int[] _offsets;
    private readonly Arc[] _arcs;
    private readonly Dictionary<NodeId, int> _indices;

    private CompactGraph(long version, long connectivityVersion, NodeId[] ids,
        int[] offsets, Arc[] arcs, Dictionary<NodeId, int> indices)
    {
        Version = version;
        ConnectivityVersion = connectivityVersion;
        _ids = ids;
        _offsets = offsets;
        _arcs = arcs;
        _indices = indices;
    }

    public long Version { get; }
    public long ConnectivityVersion { get; }
    public int NodeCount => _ids.Length;
    public int DirectedArcCount => _arcs.Length;

    /// <summary>Array payload estimate only; excludes dictionary/object overhead.</summary>
    public long PayloadBytes => (long)_ids.Length * sizeof(ulong)
        + (long)_offsets.Length * sizeof(int)
        + (long)_arcs.Length * (sizeof(int) + sizeof(uint));

    public NodeId IdAt(int index) => _ids[index];
    public bool TryIndex(NodeId id, out int index) => _indices.TryGetValue(id, out index);
    public ReadOnlySpan<Arc> Neighbors(int index) =>
        _arcs.AsSpan(_offsets[index], _offsets[index + 1] - _offsets[index]);

    public static CompactGraph Build(TopologySnapshot snapshot)
    {
        var ids = snapshot.Nodes.Keys.OrderBy(id => id.Value).ToArray();
        var indices = new Dictionary<NodeId, int>(ids.Length);
        for (var i = 0; i < ids.Length; i++) indices.Add(ids[i], i);
        var offsets = new int[ids.Length + 1];
        var arcs = new List<Arc>(ids.Length * 4);
        for (var i = 0; i < ids.Length; i++)
        {
            offsets[i] = arcs.Count;
            foreach (var edge in snapshot.Nodes[ids[i]].Links.Values)
                arcs.Add(new Arc(indices[edge.To], edge.Cost));
        }
        offsets[ids.Length] = arcs.Count;
        return new CompactGraph(snapshot.Version, snapshot.ConnectivityVersion,
            ids, offsets, arcs.ToArray(), indices);
    }
}

public static class CompactRouter
{
    public static RouteResult Dijkstra(CompactGraph graph, NodeId source, NodeId target,
        int maxExplored = 100_000, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExplored);
        if (!graph.TryIndex(source, out var start) || !graph.TryIndex(target, out var end))
            return RouteResult.Missing(graph.Version);
        if (start == end)
            return new RouteResult(graph.Version, true, 0, new[] { source }, 1);
        var dist = new ulong[graph.NodeCount];
        Array.Fill(dist, ulong.MaxValue);
        var parent = new int[graph.NodeCount];
        Array.Fill(parent, -1);
        dist[start] = 0;
        var queue = new PriorityQueue<int, ulong>();
        queue.Enqueue(start, 0);
        var explored = 0;

        while (queue.TryDequeue(out var u, out var cost))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (dist[u] != cost) continue;
            if (++explored > maxExplored) return RouteResult.Missing(graph.Version, explored);
            if (u == end)
                return new RouteResult(graph.Version, true, cost,
                    Restore(graph, parent, start, end), explored);
            foreach (var arc in graph.Neighbors(u))
            {
                var next = checked(cost + arc.Cost);
                if (next >= dist[arc.Target]) continue;
                dist[arc.Target] = next;
                parent[arc.Target] = u;
                queue.Enqueue(arc.Target, next);
            }
        }
        return RouteResult.Missing(graph.Version, explored);
    }

    public static RouteResult Bidirectional(CompactGraph graph, NodeId source, NodeId target,
        int maxExplored = 100_000, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExplored);
        if (!graph.TryIndex(source, out var start) || !graph.TryIndex(target, out var end))
            return RouteResult.Missing(graph.Version);
        if (start == end)
            return new RouteResult(graph.Version, true, 0, new[] { source }, 1);

        var n = graph.NodeCount;
        var distF = new ulong[n]; var distB = new ulong[n];
        Array.Fill(distF, ulong.MaxValue); Array.Fill(distB, ulong.MaxValue);
        var parentF = new int[n]; var nextB = new int[n];
        Array.Fill(parentF, -1); Array.Fill(nextB, -1);
        var closedF = new bool[n]; var closedB = new bool[n];
        var queueF = new PriorityQueue<int, ulong>();
        var queueB = new PriorityQueue<int, ulong>();
        distF[start] = 0; distB[end] = 0;
        queueF.Enqueue(start, 0); queueB.Enqueue(end, 0);
        var best = ulong.MaxValue; var meeting = -1; var explored = 0;

        void Consider(int node)
        {
            if (distF[node] == ulong.MaxValue || distB[node] == ulong.MaxValue) return;
            var length = checked(distF[node] + distB[node]);
            if (length >= best) return;
            best = length; meeting = node;
        }

        static ulong Min(PriorityQueue<int, ulong> q, ulong[] distances, bool[] closed)
        {
            while (q.TryPeek(out var id, out var key))
            {
                if (!closed[id] && distances[id] == key) return key;
                q.Dequeue();
            }
            return ulong.MaxValue;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var minF = Min(queueF, distF, closedF);
            var minB = Min(queueB, distB, closedB);
            if (minF == ulong.MaxValue || minB == ulong.MaxValue) break;
            if (best != ulong.MaxValue && minF >= best - Math.Min(best, minB)) break;
            var left = minF <= minB;
            var q = left ? queueF : queueB;
            var distances = left ? distF : distB;
            var closed = left ? closedF : closedB;
            var predecessor = left ? parentF : nextB;
            if (!q.TryDequeue(out var node, out var distance)) break;
            if (closed[node]) continue;
            closed[node] = true;
            if (++explored > maxExplored) return RouteResult.Missing(graph.Version, explored);
            Consider(node);
            foreach (var arc in graph.Neighbors(node))
            {
                var next = checked(distance + arc.Cost);
                if (next >= distances[arc.Target]) continue;
                distances[arc.Target] = next;
                predecessor[arc.Target] = node;
                q.Enqueue(arc.Target, next);
                Consider(arc.Target);
            }
        }

        if (meeting < 0) return RouteResult.Missing(graph.Version, explored);
        var path = new List<NodeId> { graph.IdAt(meeting) };
        for (var current = meeting; current != start;)
        {
            current = parentF[current];
            path.Add(graph.IdAt(current));
        }
        path.Reverse();
        for (var current = meeting; current != end;)
        {
            current = nextB[current];
            path.Add(graph.IdAt(current));
        }
        return new RouteResult(graph.Version, true, best, path, explored);
    }

    private static IReadOnlyList<NodeId> Restore(CompactGraph graph, int[] parent,
        int source, int target)
    {
        var path = new List<NodeId> { graph.IdAt(target) };
        for (var current = target; current != source;)
        {
            current = parent[current];
            path.Add(graph.IdAt(current));
        }
        path.Reverse();
        return path;
    }
}
