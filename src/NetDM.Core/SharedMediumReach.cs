namespace NetDM.Core;

/// <summary>The shortest route to one target of an analytic broadcast.</summary>
/// <param name="Hops">Links on the route; <paramref name="RadioHops"/> of them are radio.</param>
/// <param name="LastHop">The node that hands the transmission to the target, or the source itself.</param>
/// <param name="Ticks">Transport ticks the route takes, as the packet transport advances: one hop starts a
/// tick, wires after it continue in that tick up to the cable bound, and a further radio hop waits for
/// the next tick. Contention for the medium is not counted.</param>
/// <param name="Run">Hops taken in the route's last tick, for the cable bound.</param>
public readonly record struct BroadcastReach(ulong Cost, int Hops, int RadioHops, NodeId LastHop, int Ticks = 0, int Run = 0)
{
    /// <summary>A source that starts a transmission.</summary>
    public static BroadcastReach Start(NodeId source) => new(0, 0, 0, source);

    /// <summary>The route extended by one link.</summary>
    public BroadcastReach Then(NodeId from, ulong cost, bool radio, int cableHopsPerTick)
    {
        var (ticks, run) = Hops == 0 || Run == 0 ? (Ticks + 1, 1)
            : radio || Run >= cableHopsPerTick ? (Ticks + 1, 1)
            : (Ticks, Run + 1);
        return new BroadcastReach(cost, Hops + 1, RadioHops + (radio ? 1 : 0), from, ticks, run);
    }
}

public sealed partial class SharedMediumNetwork
{
    /// <summary>
    /// Shortest routes from any of <paramref name="sources"/> to each of <paramref name="targets"/> in one
    /// search: the analytic fanout of a broadcast (design §9.5) instead of one unicast route per listener.
    /// A source transmits even as an endpoint; any other node forwards only with <c>CanForward</c>, so an
    /// endpoint can be reached but never relays. The search stops once every present target is settled,
    /// or after <paramref name="maxExplored"/> settled nodes. Unreached targets are absent from the result.
    /// </summary>
    public static Dictionary<NodeId, BroadcastReach> ReachAll(SharedMediumSnapshot snapshot,
        IEnumerable<NodeId> sources, IReadOnlySet<NodeId> targets, int maxExplored = 100_000, int cableHopsPerTick = 16) =>
        ReachAll(snapshot, sources.Distinct().ToDictionary(source => source, BroadcastReach.Start), targets, maxExplored,
            cableHopsPerTick);

    public static Dictionary<NodeId, BroadcastReach> ReachAll(SharedMediumSnapshot snapshot, NodeId source,
        IReadOnlySet<NodeId> targets, int maxExplored, out bool exhausted) =>
        ReachAll(snapshot, new Dictionary<NodeId, BroadcastReach> { [source] = BroadcastReach.Start(source) },
            targets, maxExplored, 16, out exhausted);

    /// <summary>
    /// As above, with each source continuing a route that reached it: a gateway (a telecomms receiver,
    /// say) passes the transmission on within the same packet, so hops, ticks and the cable run carry over.
    /// Route choice still follows link cost; the start state only adds to what is reported.
    /// </summary>
    public static Dictionary<NodeId, BroadcastReach> ReachAll(SharedMediumSnapshot snapshot,
        IReadOnlyDictionary<NodeId, BroadcastReach> sources, IReadOnlySet<NodeId> targets, int maxExplored = 100_000,
        int cableHopsPerTick = 16) => ReachAll(snapshot, sources, targets, maxExplored, cableHopsPerTick, out _);

    private static Dictionary<NodeId, BroadcastReach> ReachAll(SharedMediumSnapshot snapshot,
        IReadOnlyDictionary<NodeId, BroadcastReach> sources, IReadOnlySet<NodeId> targets, int maxExplored,
        int cableHopsPerTick, out bool exhausted)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExplored);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cableHopsPerTick);
        var nodes = snapshot.Physical.Nodes;
        var reached = new Dictionary<NodeId, BroadcastReach>();
        var best = new Dictionary<NodeId, BroadcastReach>();
        var origins = new HashSet<NodeId>();
        var queue = new PriorityQueue<NodeId, ulong>();
        foreach (var (source, start) in sources)
            if (nodes.ContainsKey(source) && origins.Add(source))
            {
                best[source] = start with { Cost = 0 };
                queue.Enqueue(source, 0);
            }
        var remaining = targets.Count(nodes.ContainsKey);
        var explored = 0;
        exhausted = false;
        while (remaining > 0 && queue.TryDequeue(out var node, out var distance))
        {
            var reach = best[node];
            if (reach.Cost != distance)
                continue;
            if (++explored > maxExplored)
            {
                exhausted = true;
                break;
            }
            if (targets.Contains(node) && reached.TryAdd(node, reach) && --remaining == 0)
                break;
            if (!origins.Contains(node) && !nodes[node].CanForward)
                continue;
            foreach (var wired in nodes[node].Links.Values)
                Relax(wired.To, wired.Cost, false);
            foreach (var wireless in RadioNeighbors(snapshot, node))
                Relax(wireless.To, wireless.Cost, true);

            void Relax(NodeId to, uint weight, bool radio)
            {
                var next = checked(distance + weight);
                if (best.TryGetValue(to, out var known) && known.Cost <= next)
                    return;
                best[to] = reach.Then(node, next, radio, cableHopsPerTick);
                queue.Enqueue(to, next);
            }
        }
        return reached;
    }
}
