namespace NetDM.Core;

/// <summary>What the bridges joined on a map and what stayed apart.</summary>
/// <param name="Components">Parts of the wired APC network before bridging.</param>
/// <param name="Groups">Parts after bridging; 1 means every APC reaches every other.</param>
/// <param name="Lengths">Length of each bridge in tiles, in the order they were chosen.</param>
/// <param name="NearestApart">Shortest same-level distance between two parts still apart, or null.</param>
/// <param name="Apart">For each group still apart that shares a level with another, its distance to the
/// nearest other group, shortest first (at most 16). Groups alone on their level are not listed.</param>
public sealed record ApcBridgeReport(int Range, int Components, int Groups, int[] Lengths, int? NearestApart, int[] Apart)
{
    public static ApcBridgeReport Empty { get; } = new(ApcBridges.Range, 0, 0, [], null, []);
}

/// <summary>
/// Radio bridges between disconnected parts of the wired APC network (decided 11.11.2026). A map
/// whose parts are separated by space or by zones without an APC, such as a telecomms satellite, is
/// joined by a point-to-point radio link between the two nearest APCs of different parts on one level,
/// within <see cref="Range"/> tiles. The bridges form a minimum spanning forest over the parts, so a
/// part gets no more links than it needs and a connected map gets none. A bridge is an APC link like a
/// wire: it carries traffic only while both APCs are active, and it costs more than a wire, so routes
/// keep to wires where they can. Access points still never pair with each other.
/// </summary>
public static class ApcBridges
{
    /// <summary>
    /// Longest bridge in tiles: an APC transmitter's reach, far beyond its 7-tile access point. MetaStation's
    /// telecomms satellite is 37 tiles from the station; legacy subspace reaches the whole level.
    /// </summary>
    public const int Range = 64;
    /// <summary>Route cost of a bridge; a wire costs 1.</summary>
    public const uint Cost = 8;

    /// <param name="apcs">Forwarding APCs and their positions.</param>
    /// <param name="wired">Wired APC links of the map.</param>
    public static (TopologyChange[] Bridges, ApcBridgeReport Report) Build(
        IReadOnlyDictionary<NodeId, NodePosition> apcs, IEnumerable<TopologyChange> wired, int range = Range)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(range, 1);
        var ids = apcs.Keys.OrderBy(id => id.Value).ToArray();
        var index = new Dictionary<NodeId, int>(ids.Length);
        for (var i = 0; i < ids.Length; i++)
            index[ids[i]] = i;
        var parts = new UnionFind(ids.Length);
        foreach (var link in wired)
            if (index.TryGetValue(link.A, out var a) && index.TryGetValue(link.B, out var b))
                parts.Join(a, b);

        // The nearest pair of APCs for each pair of parts on a shared level.
        var nearest = new Dictionary<(int, int), (long Distance, int A, int B)>();
        foreach (var level in Enumerable.Range(0, ids.Length).GroupBy(i => apcs[ids[i]].Z))
        {
            var members = level.ToArray();
            for (var x = 0; x < members.Length; x++)
            for (var y = x + 1; y < members.Length; y++)
            {
                int a = members[x], b = members[y];
                int partA = parts.Find(a), partB = parts.Find(b);
                if (partA == partB)
                    continue;
                var key = partA < partB ? (partA, partB) : (partB, partA);
                var distance = DistanceSquared(apcs[ids[a]], apcs[ids[b]]);
                if (!nearest.TryGetValue(key, out var best) || distance < best.Distance ||
                    (distance == best.Distance && (a, b).CompareTo((best.A, best.B)) < 0))
                    nearest[key] = (distance, a, b);
            }
        }

        var components = Enumerable.Range(0, ids.Length).Select(parts.Find).Distinct().Count();
        var groups = new UnionFind(ids.Length);
        var bridges = new List<TopologyChange>();
        var lengths = new List<int>();
        var limit = (long)range * range;
        foreach (var (key, pair) in nearest.OrderBy(entry => entry.Value.Distance)
                     .ThenBy(entry => entry.Value.A).ThenBy(entry => entry.Value.B))
        {
            if (pair.Distance > limit || !groups.Join(key.Item1, key.Item2))
                continue;
            NodeId a = ids[pair.A], b = ids[pair.B];
            bridges.Add(new TopologyChange(TopologyOperation.Connect, a.Value < b.Value ? a : b, a.Value < b.Value ? b : a, Cost));
            lengths.Add((int)Math.Ceiling(Math.Sqrt(pair.Distance)));
        }
        var apartByGroup = new Dictionary<int, long>();
        foreach (var (key, pair) in nearest)
        {
            int groupA = groups.Find(key.Item1), groupB = groups.Find(key.Item2);
            if (groupA == groupB)
                continue;
            foreach (var group in new[] { groupA, groupB })
                if (!apartByGroup.TryGetValue(group, out var known) || pair.Distance < known)
                    apartByGroup[group] = pair.Distance;
        }
        var apart = apartByGroup.Values.Order().Select(gap => (int)Math.Ceiling(Math.Sqrt(gap))).ToArray();
        var groupCount = Enumerable.Range(0, ids.Length).Where(i => parts.Find(i) == i)
            .Select(groups.Find).Distinct().Count();
        return (bridges.ToArray(), new ApcBridgeReport(range, components, groupCount, lengths.ToArray(),
            apart.Length == 0 ? null : apart[0], apart.Take(16).ToArray()));
    }

    private static long DistanceSquared(NodePosition a, NodePosition b)
    {
        var dx = (long)a.X - b.X;
        var dy = (long)a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private sealed class UnionFind
    {
        private readonly int[] _parents;

        public UnionFind(int count)
        {
            _parents = new int[count];
            for (var i = 0; i < count; i++)
                _parents[i] = i;
        }

        public int Find(int index)
        {
            while (_parents[index] != index)
            {
                _parents[index] = _parents[_parents[index]];
                index = _parents[index];
            }
            return index;
        }

        /// <summary>False when both were already joined.</summary>
        public bool Join(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a == b)
                return false;
            if (a > b)
                (a, b) = (b, a);
            _parents[b] = a;
            return true;
        }
    }
}
