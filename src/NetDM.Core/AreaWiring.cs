namespace NetDM.Core;

/// <summary>An inclusive run of cells belonging to one area on a single row.</summary>
public readonly record struct AreaSpan(ulong Area, int Z, int Y, int StartX, int EndX);

/// <summary>The served cell identifies the segment independently of the APC's physical position.</summary>
public readonly record struct AreaApc(ulong Area, NodeId Node, NodePosition ServedCell);

/// <summary>
/// Wired links derived from area geometry plus a cell lookup of the APC a device there attaches to:
/// the APC of the segment, or for an island of the area without its own APC (a walled-off tank, say),
/// an APC of the same area on the same level, as SS13 powers the whole area. Islands never add links
/// between APCs. Cells outside the exported geometry, of an area without an APC on that level, have none.
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
    /// Joins same-area spans into orthogonally connected segments. A boundary between two
    /// segments contributes a wired link only when both have an assigned APC.
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

        var assigned = new Dictionary<int, NodeId>();
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
            if (!nodeLevels.Add((apc.Node, cell.Z)) || !assigned.TryAdd(FindRoot(span), apc.Node))
                throw new ArgumentException("APC requires one unique served segment per assigned level.");
        }

        var links = new HashSet<(NodeId A, NodeId B)>();
        foreach (var (left, right) in boundaries)
        {
            if (assigned.TryGetValue(FindRoot(left), out var a) && assigned.TryGetValue(FindRoot(right), out var b))
                links.Add(a.Value < b.Value ? (a, b) : (b, a));
        }
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
        for (var i = 0; i < spans.Length; i++)
        {
            var root = FindRoot(i);
            if (assigned.TryGetValue(root, out var apc) || islands.TryGetValue(root, out apc))
                servingApc[i] = apc;
            else if (levelApcs.TryGetValue((spans[i].Area, spans[i].Z), out var candidates))
                servingApc[i] = islands[root] = Nearest(candidates, spans[i]);
        }
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
