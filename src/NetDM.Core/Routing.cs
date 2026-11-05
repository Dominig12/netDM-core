namespace NetDM.Core;

public sealed record RouteResult(long Version, bool Found, ulong Cost, IReadOnlyList<NodeId> Path, int Explored)
{
    public static RouteResult Missing(long version, int explored = 0) =>
        new(version, false, 0, Array.Empty<NodeId>(), explored);
}

public static class Router
{
    /// <summary>
    /// Shortest weighted route. Uses the caller's consistent immutable snapshot.
    /// A route is valid for the returned topology version, not necessarily the latest.
    /// </summary>
    public static RouteResult Find(TopologySnapshot snapshot, NodeId source, NodeId target,
        int maxExplored = 100_000, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExplored);
        if (!snapshot.Nodes.ContainsKey(source) || !snapshot.Nodes.ContainsKey(target))
            return RouteResult.Missing(snapshot.Version);

        if (source == target)
            return new RouteResult(snapshot.Version, true, 0, new[] { source }, 1);

        var costs = new Dictionary<NodeId, ulong> { [source] = 0 };
        var previous = new Dictionary<NodeId, NodeId>();
        var queue = new PriorityQueue<NodeId, ulong>();
        queue.Enqueue(source, 0);
        var explored = 0;

        while (queue.TryDequeue(out var current, out var priority))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!costs.TryGetValue(current, out var best) || best != priority)
                continue;

            if (++explored > maxExplored)
                return RouteResult.Missing(snapshot.Version, explored);

            if (current == target)
            {
                var path = new List<NodeId> { target };
                var cursor = target;
                while (cursor != source)
                {
                    cursor = previous[cursor];
                    path.Add(cursor);
                }
                path.Reverse();
                return new RouteResult(snapshot.Version, true, best, path, explored);
            }

            foreach (var edge in snapshot.Nodes[current].Links.Values)
            {
                var nextCost = checked(best + edge.Cost);
                if (costs.TryGetValue(edge.To, out var prior) && prior <= nextCost) continue;
                costs[edge.To] = nextCost;
                previous[edge.To] = current;
                queue.Enqueue(edge.To, nextCost);
            }
        }
        return RouteResult.Missing(snapshot.Version, explored);
    }
}
