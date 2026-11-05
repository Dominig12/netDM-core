using System.Collections.Immutable;

namespace NetDM.Core;

/// <summary>
/// Immutable, snapshot-consistent dense radio view. The ID map is shared
/// across movements; only modified 64-node state blocks and affected radio
/// cell membership arrays are rebuilt. Readers see one published epoch.
/// </summary>
internal sealed class DenseRadioIndex
{
    private const int BlockShift = 6;
    private const int BlockSize = 1 << BlockShift;
    private const int BlockMask = BlockSize - 1;

    internal readonly record struct State(
        NodePosition? Position,
        RadioProfile? Radio,
        ImmutableDictionary<NodeId, Edge> LinkVersion,
        Edge[] Cables);

    private readonly NodeId[] _ids;
    private readonly Dictionary<NodeId, int> _lookup;
    private readonly State[][] _blocks;
    private readonly ImmutableDictionary<RadioMediumCell, int[]> _cellMembers;

    private DenseRadioIndex(NodeId[] ids, Dictionary<NodeId, int> lookup,
        State[][] blocks, ImmutableDictionary<RadioMediumCell, int[]> cellMembers)
    {
        _ids = ids;
        _lookup = lookup;
        _blocks = blocks;
        _cellMembers = cellMembers;
    }

    public int Count => _ids.Length;
    public bool TryIndex(NodeId id, out int index) => _lookup.TryGetValue(id, out index);
    public NodeId IdAt(int index) => _ids[index];
    public State At(int index) => _blocks[index >> BlockShift][index & BlockMask];
    public bool TryCell(RadioMediumCell key, out int[] members) =>
        _cellMembers.TryGetValue(key, out members!);

    public static DenseRadioIndex Build(TopologySnapshot physical,
        ImmutableDictionary<NodeId, RadioProfile> devices,
        ImmutableDictionary<RadioMediumCell, ImmutableHashSet<NodeId>> cells)
    {
        var ids = physical.Nodes.Keys.ToArray();
        var lookup = new Dictionary<NodeId, int>(ids.Length);
        var blocks = new State[(ids.Length + BlockMask) >> BlockShift][];
        for (int i = 0; i < blocks.Length; i++)
            blocks[i] = new State[Math.Min(BlockSize, ids.Length - (i << BlockShift))];
        for (int i = 0; i < ids.Length; i++)
        {
            var id = ids[i];
            lookup.Add(id, i);
            var node = physical.Nodes[id];
            blocks[i >> BlockShift][i & BlockMask] = new State(
                node.Position, devices.TryGetValue(id, out var profile) ? profile : null,
                node.Links, GetCables(node.Links));
        }
        var index = new DenseRadioIndex(ids, lookup, blocks,
            ImmutableDictionary<RadioMediumCell, int[]>.Empty);
        var members = ImmutableDictionary.CreateBuilder<RadioMediumCell, int[]>();
        foreach (var entry in cells)
            members[entry.Key] = index.ConvertMembers(entry.Value);
        return new DenseRadioIndex(ids, lookup, blocks, members.ToImmutable());
    }

    public DenseRadioIndex WithChanges(TopologySnapshot physical,
        ImmutableDictionary<NodeId, RadioProfile> devices,
        ImmutableDictionary<RadioMediumCell, ImmutableHashSet<NodeId>> cells,
        IEnumerable<NodeId> changedIds)
    {
        var blocks = (State[][])_blocks.Clone();
        var copied = new HashSet<int>();
        var affectedCells = new HashSet<RadioMediumCell>();
        bool changed = false;
        foreach (var id in changedIds)
        {
            if (!_lookup.TryGetValue(id, out var index) ||
                !physical.Nodes.TryGetValue(id, out var node))
                throw new InvalidOperationException(
                    "Dense radio membership changed without rebuilding its ID index.");
            var current = At(index);
            var next = new State(node.Position,
                devices.TryGetValue(id, out var profile) ? profile : null,
                node.Links, ReferenceEquals(node.Links, current.LinkVersion)
                    ? current.Cables : GetCables(node.Links));
            if (current.Equals(next)) continue;

            var oldCell = CellOf(current);
            var nextCell = CellOf(next);
            if (oldCell != nextCell)
            {
                if (oldCell is { } previous) affectedCells.Add(previous);
                if (nextCell is { } upcoming) affectedCells.Add(upcoming);
            }

            int block = index >> BlockShift;
            if (copied.Add(block))
                blocks[block] = (State[])blocks[block].Clone();
            blocks[block][index & BlockMask] = next;
            changed = true;
        }
        if (!changed) return this;

        var updatedCells = _cellMembers;
        if (affectedCells.Count != 0)
        {
            var memberBuilder = _cellMembers.ToBuilder();
            foreach (var cell in affectedCells)
            {
                if (cells.TryGetValue(cell, out var devicesInCell))
                    memberBuilder[cell] = ConvertMembers(devicesInCell);
                else
                    memberBuilder.Remove(cell);
            }
            updatedCells = memberBuilder.ToImmutable();
        }
        return new DenseRadioIndex(_ids, _lookup, blocks, updatedCells);
    }

    private int[] ConvertMembers(ImmutableHashSet<NodeId> ids)
    {
        var members = new int[ids.Count];
        int count = 0;
        foreach (var id in ids)
        {
            if (!_lookup.TryGetValue(id, out var index))
                throw new InvalidOperationException("Radio cell contains a removed node.");
            members[count++] = index;
        }
        return members;
    }

    private static Edge[] GetCables(ImmutableDictionary<NodeId, Edge> links) =>
        links.Count == 0 ? Array.Empty<Edge>() : links.Values.ToArray();

    private static RadioMediumCell? CellOf(State node)
    {
        if (node.Radio is not { } radio || node.Position is not { } pos)
            return null;
        return new RadioMediumCell(new RadioDomainKey(radio.Channel, pos.Z),
            FloorCell(pos.X), FloorCell(pos.Y));
    }

    private static int FloorCell(int value) =>
        value >= 0
            ? value / TopologyStore.CellSize
            : checked((int)(((long)value + 1) / TopologyStore.CellSize - 1));
}
