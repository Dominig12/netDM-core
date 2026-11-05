namespace NetDM.Core;

/// <summary>
/// Bidirectional Dijkstra for the current undirected, positive-weight topology.
/// Each search owns its state; safe to invoke concurrently on one immutable snapshot.
/// </summary>
public static class BidirectionalRouter
{
    public static RouteResult Find(TopologySnapshot snapshot, NodeId source, NodeId target,
        int maxExplored = 100_000, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExplored);
        if (!snapshot.Nodes.ContainsKey(source) || !snapshot.Nodes.ContainsKey(target))
            return RouteResult.Missing(snapshot.Version);
        if (source == target)
            return new RouteResult(snapshot.Version, true, 0, new[] { source }, 1);

        var forward = new Dictionary<NodeId, ulong> { [source] = 0 };
        var backward = new Dictionary<NodeId, ulong> { [target] = 0 };
        var parents = new Dictionary<NodeId, NodeId>();
        var successors = new Dictionary<NodeId, NodeId>();
        var settledF = new HashSet<NodeId>();
        var settledB = new HashSet<NodeId>();
        var queueF = new PriorityQueue<NodeId, ulong>();
        var queueB = new PriorityQueue<NodeId, ulong>();
        queueF.Enqueue(source, 0);
        queueB.Enqueue(target, 0);
        var best = ulong.MaxValue;
        var meeting = default(NodeId);
        var explored = 0;

        void Consider(NodeId node)
        {
            if (!forward.TryGetValue(node, out var a) || !backward.TryGetValue(node, out var b))
                return;
            var cost = checked(a + b);
            if (cost >= best) return;
            best = cost;
            meeting = node;
        }

        static ulong PeekValid(PriorityQueue<NodeId, ulong> queue,
            Dictionary<NodeId, ulong> costs, HashSet<NodeId> settled)
        {
            while (queue.TryPeek(out var id, out var distance))
            {
                if (!settled.Contains(id) && costs.TryGetValue(id, out var current)
                    && current == distance) return distance;
                queue.Dequeue();
            }
            return ulong.MaxValue;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lowerF = PeekValid(queueF, forward, settledF);
            var lowerB = PeekValid(queueB, backward, settledB);
            if (lowerF == ulong.MaxValue || lowerB == ulong.MaxValue) break;
            if (best != ulong.MaxValue && lowerF >= best - Math.Min(best, lowerB)) break;
            bool left = lowerF <= lowerB;
            var queue = left ? queueF : queueB;
            var costs = left ? forward : backward;
            var visited = left ? settledF : settledB;
            var chain = left ? parents : successors;
            if (!queue.TryDequeue(out var node, out var distance)) break;
            if (!visited.Add(node)) continue;
            if (++explored > maxExplored)
                return RouteResult.Missing(snapshot.Version, explored);
            Consider(node);
            foreach (var edge in snapshot.Nodes[node].Links.Values)
            {
                var neighbor = edge.To;
                var newCost = checked(distance + edge.Cost);
                if (!costs.TryGetValue(neighbor, out var prior) || newCost < prior)
                {
                    costs[neighbor] = newCost;
                    chain[neighbor] = node;
                    queue.Enqueue(neighbor, newCost);
                    Consider(neighbor);
                }
            }
        }

        if (best == ulong.MaxValue)
            return RouteResult.Missing(snapshot.Version, explored);
        var path = new List<NodeId> { meeting };
        var currentNode = meeting;
        while (currentNode != source)
        {
            currentNode = parents[currentNode];
            path.Add(currentNode);
        }
        path.Reverse();
        currentNode = meeting;
        while (currentNode != target)
        {
            currentNode = successors[currentNode];
            path.Add(currentNode);
        }
        return new RouteResult(snapshot.Version, true, best, path, explored);
    }
}
