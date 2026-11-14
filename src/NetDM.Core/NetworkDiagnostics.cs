namespace NetDM.Core;

/// <summary>
/// The wiring the world designed, as opposed to the links that carry traffic now: every map node with
/// the neighbours its cables lead to, and the nodes reported without power or broken. Published as one
/// immutable object with each change of the map or of a node's state.
/// </summary>
public sealed record WiringReport(IReadOnlyDictionary<NodeId, NodeId[]> Designed, IReadOnlyDictionary<NodeId, NodeState> Inactive)
{
    public static WiringReport Empty { get; } = new(new Dictionary<NodeId, NodeId[]>(), new Dictionary<NodeId, NodeState>());
    public IReadOnlySet<NodeId> ExecutionPaused { get; init; } = new HashSet<NodeId>();

    public bool CanExecute(NodeId node) => !Inactive.ContainsKey(node) && !ExecutionPaused.Contains(node);

    /// <summary>Builds the report from the designed cable pairs; a pair names its two ends in any order.</summary>
    public static WiringReport Create(IEnumerable<(NodeId A, NodeId B)> cables, IEnumerable<NodeId> nodes,
        IEnumerable<KeyValuePair<NodeId, NodeState>> inactive)
    {
        var designed = nodes.Distinct().ToDictionary(node => node, _ => new List<NodeId>());
        foreach (var (a, b) in cables)
        {
            if (a == b || !designed.TryGetValue(a, out var left) || !designed.TryGetValue(b, out var right))
                continue;
            if (!left.Contains(b))
                left.Add(b);
            if (!right.Contains(a))
                right.Add(a);
        }
        return new WiringReport(designed.ToDictionary(pair => pair.Key, pair => pair.Value.OrderBy(id => id.Value).ToArray()),
            inactive.Where(pair => !pair.Value.Active).ToDictionary(pair => pair.Key, pair => pair.Value));
    }
}

/// <summary>One designed neighbour of a device and why its cable carries traffic or not.</summary>
public readonly record struct NeighborLink(NodeId Node, string State);

/// <summary>What a network device knows about itself: its own state and the state of its own ports.</summary>
public sealed record LinkStateReport(string State, int Wired, int WiredUp, bool Radio, int Devices,
    IReadOnlyList<NeighborLink> Neighbors, bool More);

/// <summary>Whether the network carries a packet from a device to a node now, and how far that is.</summary>
public sealed record TraceReport(string State, int Hops, int RadioHops, ulong Cost, int Ticks);

/// <summary>
/// network.diagnostics (§7.6, §14.1): bounded answers a network device gives about the network around
/// it. A device reports its own ports: which designed neighbour is reachable over its cable and which is
/// not, never the state of the rest of the station. A trace is one bounded search from the device.
/// </summary>
public static class NetworkDiagnostics
{
    public const int MaxNeighbors = 16;
    public const int MaxTraceExplored = 20_000;
    public const string Active = "active", Unpowered = "unpowered", Broken = "broken";
    public const string Up = "up", Down = "down";
    public const string Reachable = "reachable", Unreachable = "unreachable", Unknown = "unknown";

    private static string StateOf(WiringReport wiring, NodeId node) =>
        !wiring.Inactive.TryGetValue(node, out var state) || state.Active ? Active : state.Powered ? Broken : Unpowered;

    /// <summary>
    /// The device's own state and ports. A designed neighbour is <c>up</c> when the cable carries traffic,
    /// otherwise it shows why not: the neighbour is <c>unpowered</c> or <c>broken</c>, or the link is
    /// <c>down</c> for another reason (this device itself is off, or the neighbour left the world).
    /// Neighbours beyond <see cref="MaxNeighbors"/> are counted but not listed.
    /// </summary>
    public static LinkStateReport? LinkState(SharedMediumSnapshot world, WiringReport wiring, NodeId node)
    {
        if (!world.Physical.Nodes.TryGetValue(node, out var live))
            return null;
        var designed = wiring.Designed.TryGetValue(node, out var cables) ? cables : [];
        var neighbors = new List<NeighborLink>(Math.Min(designed.Length, MaxNeighbors));
        var up = 0;
        foreach (var neighbor in designed)
        {
            var carries = live.Links.ContainsKey(neighbor);
            if (carries)
                up++;
            if (neighbors.Count >= MaxNeighbors)
                continue;
            var state = carries ? Up : StateOf(wiring, neighbor) is var theirs && theirs != Active ? theirs : Down;
            neighbors.Add(new NeighborLink(neighbor, state));
        }
        // Everything else on a live cable is a device plugged into this node (or, for a device, its port).
        var devices = live.Links.Keys.Count(other => !designed.Contains(other));
        return new LinkStateReport(StateOf(wiring, node), designed.Length, up,
            world.Devices.TryGetValue(node, out var radio) && radio.Enabled, devices, neighbors, designed.Length > neighbors.Count);
    }

    /// <summary>One bounded search from <paramref name="from"/>; a search that ran out of budget answers unreachable.</summary>
    public static TraceReport Trace(SharedMediumSnapshot world, NodeId from, NodeId to, int maxExplored = MaxTraceExplored)
    {
        if (!world.Physical.Nodes.ContainsKey(from) || !world.Physical.Nodes.ContainsKey(to))
            return new TraceReport(Unknown, 0, 0, 0, 0);
        if (from == to)
            return new TraceReport(Reachable, 0, 0, 0, 0);
        var reached = SharedMediumNetwork.ReachAll(world, [from], new HashSet<NodeId> { to }, maxExplored);
        return reached.TryGetValue(to, out var reach)
            ? new TraceReport(Reachable, reach.Hops, reach.RadioHops, reach.Cost, reach.Ticks)
            : new TraceReport(Unreachable, 0, 0, 0, 0);
    }
}
