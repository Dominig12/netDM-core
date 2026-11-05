using System.Collections.Concurrent;

namespace NetDM.Core;

public sealed partial class SharedMediumNetwork
{
    /// <summary>
    /// Query-local dense arrays replace per-node Dictionary lookups. Each
    /// concurrent route owns a rented workspace. Epoch stamps avoid clearing
    /// thousands of entries between queries. The immutable dense index comes
    /// from the same versioned snapshot as radio cells and cable links.
    /// </summary>
    private sealed class DenseAStarWorkspace
    {
        public ulong[] Costs = [];
        public int[] Parents = [];
        public int[] Seen = [];
        public int Stamp;
        public readonly PriorityQueue<(int Node, ulong Cost), double> Frontier = new();
        public int PeakQueue;

        public void Prepare(int count)
        {
            if (Costs.Length < count)
            {
                int size = Math.Max(64, Costs.Length);
                while (size < count) size = checked(size * 2);
                Array.Resize(ref Costs, size);
                Array.Resize(ref Parents, size);
                Array.Resize(ref Seen, size);
            }
            if (Stamp == int.MaxValue)
            {
                Array.Clear(Seen);
                Stamp = 0;
            }
            Stamp++;
        }
    }

    private static readonly ConcurrentBag<DenseAStarWorkspace> DenseWorkspaces = new();
    private static int _retainedDenseWorkspaces;
    private const int MaxRetainedDenseWorkspaces = 8;
    private const int MaxRetainedDenseNodes = 65_536;
    private const int MaxRetainedDenseFrontier = 131_072;

    private static DenseAStarWorkspace RentDenseWorkspace()
    {
        if (DenseWorkspaces.TryTake(out var workspace))
        {
            Interlocked.Decrement(ref _retainedDenseWorkspaces);
            return workspace;
        }
        return new DenseAStarWorkspace();
    }

    private static void ReturnDenseWorkspace(DenseAStarWorkspace workspace)
    {
        bool retain = workspace.Costs.Length <= MaxRetainedDenseNodes &&
                      workspace.PeakQueue <= MaxRetainedDenseFrontier;
        workspace.Frontier.Clear();
        workspace.PeakQueue = 0;
        if (!retain) return;
        if (Interlocked.Increment(ref _retainedDenseWorkspaces) > MaxRetainedDenseWorkspaces)
        {
            Interlocked.Decrement(ref _retainedDenseWorkspaces);
            return;
        }
        DenseWorkspaces.Add(workspace);
    }

    /// <summary>
    /// Exact A* on dense, epoch-stamped cost and parent arrays. Radio and
    /// cable decisions, geometric lower bound, visit order and maxExplored
    /// retain the same semantics as RouteAStarPooledCore.
    /// </summary>
    public static RouteResult RouteAStarDenseCore(SharedMediumSnapshot snapshot,
        NodeId source, NodeId target, int maximumRadioRange = 128,
        uint minimumRadioCost = 1, int maxExplored = 100_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExplored);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRadioRange);
        ArgumentOutOfRangeException.ThrowIfZero(minimumRadioCost);

        var dense = snapshot.Dense ??
            DenseRadioIndex.Build(snapshot.Physical, snapshot.Devices);
        if (!dense.TryIndex(source, out int sourceIndex) ||
            !dense.TryIndex(target, out int targetIndex))
            return RouteResult.Missing(snapshot.Version);
        if (sourceIndex == targetIndex)
            return new RouteResult(snapshot.Version, true, 0, [source], 1);

        var start = dense.At(sourceIndex);
        var goal = dense.At(targetIndex);
        if (!snapshot.HasPhysicalLinks)
        {
            if (start.Radio is not { } startRadio ||
                goal.Radio is not { } goalRadio ||
                startRadio.Channel != goalRadio.Channel ||
                start.Position!.Value.Z != goal.Position!.Value.Z)
                return RouteResult.Missing(snapshot.Version);
        }

        var targetPosition = goal.Position;
        double Heuristic(int index)
        {
            if (snapshot.HasPhysicalLinks || targetPosition is null)
                return 0;
            var position = dense.At(index).Position;
            if (position is null) return 0;
            double dx = (double)position.Value.X - targetPosition.Value.X;
            double dy = (double)position.Value.Y - targetPosition.Value.Y;
            return Math.Floor(Math.Sqrt(dx * dx + dy * dy) / maximumRadioRange) *
                minimumRadioCost;
        }

        var work = RentDenseWorkspace();
        try
        {
            work.Prepare(dense.Count);
            int stamp = work.Stamp;
            var costs = work.Costs;
            var seen = work.Seen;
            var parents = work.Parents;
            var frontier = work.Frontier;
            costs[sourceIndex] = 0;
            seen[sourceIndex] = stamp;
            frontier.Enqueue((sourceIndex, 0), Heuristic(sourceIndex));
            work.PeakQueue = 1;
            int explored = 0;

            while (frontier.TryDequeue(out var item, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int current = item.Node;
                ulong cost = item.Cost;
                if (seen[current] != stamp || costs[current] != cost)
                    continue;
                if (++explored > maxExplored)
                    return RouteResult.Missing(snapshot.Version, explored);
                if (current == targetIndex)
                {
                    var path = new List<NodeId> { target };
                    for (int cursor = targetIndex; cursor != sourceIndex;)
                    {
                        cursor = parents[cursor];
                        path.Add(dense.IdAt(cursor));
                    }
                    path.Reverse();
                    return new RouteResult(snapshot.Version, true, cost, path, explored);
                }

                void Relax(int candidate, uint weight)
                {
                    ulong next = checked(cost + weight);
                    if (seen[candidate] == stamp && next >= costs[candidate])
                        return;
                    seen[candidate] = stamp;
                    costs[candidate] = next;
                    parents[candidate] = current;
                    frontier.Enqueue((candidate, next), next + Heuristic(candidate));
                    if (frontier.Count > work.PeakQueue)
                        work.PeakQueue = frontier.Count;
                }

                var node = dense.At(current);
                foreach (var cable in node.Cables.Values)
                    if (dense.TryIndex(cable.To, out int to))
                        Relax(to, cable.Cost);

                if (node.Radio is not { } radio) continue;
                var origin = node.Position!.Value;
                var domain = DomainOf(radio, origin);
                int fromX = FloorCell((int)Math.Clamp((long)origin.X - radio.Range,
                    int.MinValue, int.MaxValue));
                int toX = FloorCell((int)Math.Clamp((long)origin.X + radio.Range,
                    int.MinValue, int.MaxValue));
                int fromY = FloorCell((int)Math.Clamp((long)origin.Y - radio.Range,
                    int.MinValue, int.MaxValue));
                int toY = FloorCell((int)Math.Clamp((long)origin.Y + radio.Range,
                    int.MinValue, int.MaxValue));

                for (long x = fromX; x <= toX; x++)
                for (long y = fromY; y <= toY; y++)
                {
                    var cell = new RadioMediumCell(domain, (int)x, (int)y);
                    if (!snapshot.Cells.TryGetValue(cell, out var members))
                        continue;
                    foreach (var neighborId in members)
                    {
                        if (neighborId == dense.IdAt(current) ||
                            !dense.TryIndex(neighborId, out int nextIndex))
                            continue;
                        var neighbor = dense.At(nextIndex);
                        if (neighbor.Radio is not { } other) continue;
                        var destination = neighbor.Position!.Value;
                        if (!Within(origin, destination,
                            Math.Min(radio.Range, other.Range)))
                            continue;
                        Relax(nextIndex, Math.Max(radio.Cost, other.Cost));
                    }
                }
            }
            return RouteResult.Missing(snapshot.Version, explored);
        }
        finally
        {
            ReturnDenseWorkspace(work);
        }
    }
}
