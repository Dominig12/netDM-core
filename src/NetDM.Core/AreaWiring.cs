namespace NetDM.Core;

/// <summary>An inclusive run of cells belonging to one area on a single row.</summary>
public readonly record struct AreaSpan(ulong Area, int Z, int Y, int StartX, int EndX);

/// <summary>The served cell identifies the segment independently of the APC's physical position.</summary>
public readonly record struct AreaApc(ulong Area, NodeId Node, NodePosition ServedCell);

/// <summary>
/// Wired links derived from area geometry plus a cell lookup of the APC a device there attaches to:
/// the APC of the segment, or for an island of the area without its own APC (a walled-off tank, say),
/// an APC of the same area on the same level, as SS13 powers the whole area. Islands never add links
/// between APCs. A segment of an area without any APC on its level is a passive cable duct (decided
/// 14.11.2026): it joins the APCs whose segments touch it; devices in it have no APC and use radio.
/// Cells outside the exported geometry have no APC.
/// </summary>
public sealed class AreaWiringIndex
{
    private readonly AreaSpan[] _spans;
    private readonly Dictionary<(int Z, int Y), (int Start, int End)> _rows;
    private readonly NodeId[] _servingApc;

    internal AreaWiringIndex(AreaSpan[] spans, Dictionary<(int Z, int Y), (int Start, int End)> rows,
        NodeId[] servingApc, TopologyChange[] links)
    {
        _spans = spans;
        _rows = rows;
        _servingApc = servingApc;
        Links = links;
    }

    public static AreaWiringIndex Empty { get; } = new([], [], [], []);

    public TopologyChange[] Links { get; }

    public bool TryFindApc(NodePosition cell, out NodeId apc)
    {
        apc = default;
        if (!_rows.TryGetValue((cell.Z, cell.Y), out var row))
            return false;
        var span = AreaWiring.FindSpan(_spans, row.Start, row.End, cell.X);
        if (span < 0)
            return false;
        apc = _servingApc[span];
        return apc.Value != 0;
    }
}

public static class AreaWiring
{
    /// <summary>
    /// Joins same-area spans into orthogonally connected segments. A boundary between two parts with
    /// different APCs is a wired link. Several APCs of one segment share it: each span belongs to the
    /// nearest of them (decided 14.11.2026). Segments of areas without an APC on their level are ducts:
    /// touching ducts form one, and the APCs touching a duct are joined by a minimum spanning tree over
    /// their served cells, so a corridor along many rooms adds one link per room, not one per pair.
    /// </summary>
    public static TopologyChange[] BuildLinks(IReadOnlyList<AreaSpan> geometry, IReadOnlyList<AreaApc> apcs) =>
        Build(geometry, apcs).Links;

    /// <summary>Validates geometry and APC assignment once and keeps the segment lookup.</summary>
    public static AreaWiringIndex Build(IReadOnlyList<AreaSpan> geometry, IReadOnlyList<AreaApc> apcs)
    {
        var spans = geometry.OrderBy(s => s.Z).ThenBy(s => s.Y).ThenBy(s => s.StartX).ToArray();
        var rows = new Dictionary<(int Z, int Y), (int Start, int End)>();
        var parents = new int[spans.Length];
        var sizes = new int[spans.Length];
        var boundaries = new List<(int Left, int Right)>();
        var inner = new List<(int Left, int Right)>();
        for (var start = 0; start < spans.Length;)
        {
            var row = (spans[start].Z, spans[start].Y);
            var end = start;
            while (end < spans.Length && (spans[end].Z, spans[end].Y) == row)
            {
                var span = spans[end];
                if (span.Area == 0 || span.Z < 1 || span.Y < 1 || span.StartX < 1 || span.EndX < span.StartX)
                    throw new ArgumentException("Invalid area span.");
                parents[end] = end;
                sizes[end] = 1;
                if (end > start)
                {
                    var previous = spans[end - 1];
                    if (previous.EndX >= span.StartX)
                        throw new ArgumentException("Area spans on a row must not overlap.");
                    if (previous.EndX == span.StartX - 1)
                        JoinOrRecord(end - 1, end);
                }
                end++;
            }

            if (rows.TryGetValue((row.Z, row.Y - 1), out var above))
            {
                var left = above.Start;
                var right = start;
                // Both rows are sorted and disjoint, so finding every overlap is linear in their sizes.
                while (left < above.End && right < end)
                {
                    var upper = spans[left];
                    var lower = spans[right];
                    if (upper.StartX <= lower.EndX && lower.StartX <= upper.EndX)
                        JoinOrRecord(left, right);
                    if (upper.EndX <= lower.EndX)
                        left++;
                    if (lower.EndX <= upper.EndX)
                        right++;
                }
            }
            rows.Add(row, (start, end));
            start = end;
        }

        var assigned = new Dictionary<int, List<AreaApc>>();
        var nodeAreas = new Dictionary<NodeId, ulong>();
        var nodeLevels = new HashSet<(NodeId Node, int Z)>();
        foreach (var apc in apcs)
        {
            var cell = apc.ServedCell;
            if (apc.Node.Value == 0 || !rows.TryGetValue((cell.Z, cell.Y), out var row))
                throw new ArgumentException("APC must identify a served area cell.");
            var span = FindSpan(spans, row.Start, row.End, cell.X);
            if (span < 0 || spans[span].Area != apc.Area)
                throw new ArgumentException("APC served cell is outside its declared area.");
            if (nodeAreas.TryGetValue(apc.Node, out var area) && area != apc.Area)
                throw new ArgumentException("An APC can serve segments of only one area.");
            nodeAreas[apc.Node] = apc.Area;
            if (!nodeLevels.Add((apc.Node, cell.Z)))
                throw new ArgumentException("APC requires one unique served segment per assigned level.");
            var root = FindRoot(span);
            if (!assigned.TryGetValue(root, out var sharing))
                assigned[root] = sharing = [];
            sharing.Add(apc);
        }
        // The APC of each span of an assigned segment: the only one, or the nearest of those sharing it.
        var spanApc = new NodeId[spans.Length];
        var spanServed = new NodePosition[spans.Length];
        for (var i = 0; i < spans.Length; i++)
            if (assigned.TryGetValue(FindRoot(i), out var sharing))
            {
                var owner = sharing.Count == 1 ? sharing[0] : NearestApc(sharing, spans[i]);
                spanApc[i] = owner.Node;
                spanServed[i] = owner.ServedCell;
            }

        var links = new HashSet<(NodeId A, NodeId B)>();
        void Link(NodeId a, NodeId b)
        {
            if (a.Value != 0 && b.Value != 0 && a != b)
                links.Add(a.Value < b.Value ? (a, b) : (b, a));
        }
        foreach (var (left, right) in boundaries)
            Link(spanApc[left], spanApc[right]);
        foreach (var (left, right) in inner)
            Link(spanApc[left], spanApc[right]); // Parts of one segment served by different APCs.
        // Devices in an island of an area attach to an APC of that area on the same level; with several,
        // to the one whose served cell is nearest to the island's first row. Links above stay per segment.
        var levelApcs = new Dictionary<(ulong Area, int Z), List<AreaApc>>();
        foreach (var apc in apcs)
        {
            var key = (apc.Area, apc.ServedCell.Z);
            if (!levelApcs.TryGetValue(key, out var list))
                levelApcs[key] = list = [];
            list.Add(apc);
        }
        var islands = new Dictionary<int, NodeId>();
        var servingApc = new NodeId[spans.Length];
        bool IsDuct(int i) => spanApc[i].Value == 0 && !levelApcs.ContainsKey((spans[i].Area, spans[i].Z));
        for (var i = 0; i < spans.Length; i++)
        {
            var root = FindRoot(i);
            if (spanApc[i].Value != 0)
                servingApc[i] = spanApc[i];
            else if (islands.TryGetValue(root, out var island))
                servingApc[i] = island;
            else if (levelApcs.TryGetValue((spans[i].Area, spans[i].Z), out var candidates))
                servingApc[i] = islands[root] = Nearest(candidates, spans[i]);
        }

        // Ducts: segments of areas without an APC on their level. Touching ducts are one duct; each duct
        // joins the APCs touching it with a minimum spanning tree over their served cells.
        var ductParent = new Dictionary<int, int>();
        int DuctRoot(int segment)
        {
            while (ductParent.TryGetValue(segment, out var parent) && parent != segment)
                segment = parent;
            return segment;
        }
        foreach (var (left, right) in boundaries)
            if (IsDuct(left) && IsDuct(right))
            {
                var a = DuctRoot(FindRoot(left));
                var b = DuctRoot(FindRoot(right));
                if (a != b)
                    ductParent[b] = a;
            }
        var touching = new Dictionary<int, Dictionary<NodeId, NodePosition>>();
        foreach (var (left, right) in boundaries)
        {
            var (duct, other) = IsDuct(left) ? (left, right) : IsDuct(right) ? (right, left) : (-1, -1);
            if (duct < 0 || spanApc[other].Value == 0)
                continue;
            var key = DuctRoot(FindRoot(duct));
            if (!touching.TryGetValue(key, out var members))
                touching[key] = members = [];
            members.TryAdd(spanApc[other], spanServed[other]);
        }
        foreach (var members in touching.Values)
            foreach (var (a, b) in SpanningTree(members))
                Link(a, b);
        // A duct carries APC links only: a device in it has no APC of its own and uses radio, as does a
        // device of an area whose APC was destroyed.
        return new AreaWiringIndex(spans, rows, servingApc,
            links.OrderBy(link => link.A.Value).ThenBy(link => link.B.Value)
                .Select(link => new TopologyChange(TopologyOperation.Connect, link.A, link.B)).ToArray());

        int FindRoot(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }
            return index;
        }

        void JoinOrRecord(int left, int right)
        {
            if (spans[left].Area != spans[right].Area)
            {
                boundaries.Add((left, right));
                return;
            }
            inner.Add((left, right));
            var a = FindRoot(left);
            var b = FindRoot(right);
            if (a == b)
                return;
            if (sizes[a] < sizes[b])
                (a, b) = (b, a);
            parents[b] = a;
            sizes[a] += sizes[b];
        }
    }

    private static long Distance(NodePosition cell, AreaSpan span)
    {
        long dx = cell.X < span.StartX ? span.StartX - cell.X : cell.X > span.EndX ? cell.X - span.EndX : 0;
        return dx + Math.Abs((long)cell.Y - span.Y);
    }

    private static AreaApc NearestApc(List<AreaApc> candidates, AreaSpan span)
    {
        var best = candidates[0];
        var bestDistance = long.MaxValue;
        foreach (var candidate in candidates)
        {
            var distance = Distance(candidate.ServedCell, span);
            if (distance < bestDistance || (distance == bestDistance && candidate.Node.Value < best.Node.Value))
                (best, bestDistance) = (candidate, distance);
        }
        return best;
    }

    /// <summary>Prim's tree over manhattan distances of served cells; ties go to the smaller node IDs.</summary>
    private static IEnumerable<(NodeId A, NodeId B)> SpanningTree(Dictionary<NodeId, NodePosition> members)
    {
        var nodes = members.Keys.OrderBy(node => node.Value).ToArray();
        if (nodes.Length < 2)
            yield break;
        var inTree = new bool[nodes.Length];
        var best = new long[nodes.Length];
        var from = new int[nodes.Length];
        Array.Fill(best, long.MaxValue);
        best[0] = 0;
        for (var step = 0; step < nodes.Length; step++)
        {
            var next = -1;
            for (var i = 0; i < nodes.Length; i++)
                if (!inTree[i] && (next < 0 || best[i] < best[next]))
                    next = i;
            inTree[next] = true;
            if (step > 0)
                yield return (nodes[from[next]], nodes[next]);
            var cell = members[nodes[next]];
            for (var i = 0; i < nodes.Length; i++)
            {
                if (inTree[i])
                    continue;
                var other = members[nodes[i]];
                var distance = Math.Abs((long)cell.X - other.X) + Math.Abs((long)cell.Y - other.Y);
                if (distance < best[i])
                {
                    best[i] = distance;
                    from[i] = next;
                }
            }
        }
    }

    private static NodeId Nearest(List<AreaApc> candidates, AreaSpan span)
    {
        var best = candidates[0];
        var bestDistance = long.MaxValue;
        foreach (var candidate in candidates)
        {
            var cell = candidate.ServedCell;
            long dx = cell.X < span.StartX ? span.StartX - cell.X : cell.X > span.EndX ? cell.X - span.EndX : 0;
            var distance = dx + Math.Abs((long)cell.Y - span.Y);
            if (distance < bestDistance || (distance == bestDistance && candidate.Node.Value < best.Node.Value))
                (best, bestDistance) = (candidate, distance);
        }
        return best.Node;
    }

    internal static int FindSpan(AreaSpan[] spans, int start, int end, int x)
    {
        while (start < end)
        {
            var middle = start + (end - start) / 2;
            var span = spans[middle];
            if (x < span.StartX)
                end = middle;
            else if (x > span.EndX)
                start = middle + 1;
            else
                return middle;
        }
        return -1;
    }
}
