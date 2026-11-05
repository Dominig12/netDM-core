using System.Collections.Immutable;

namespace NetDM.Core;

/// <summary>
/// Immutable, snapshot-consistent radio/node view addressed by dense integers.
/// The ID mapping is shared between movement-only snapshots; 64-node blocks are
/// copied on write so one move does not clone all 12k node states.
/// </summary>
internal sealed class DenseRadioIndex
{
    private const int BlockShift = 6;
    private const int BlockSize = 1 << BlockShift;
    private const int BlockMask = BlockSize - 1;

    internal readonly record struct State(
        NodePosition? Position,
        RadioProfile? Radio,
        ImmutableDictionary<NodeId, Edge> Cables);

    private readonly NodeId[] _ids;
    private readonly Dictionary<NodeId, int> _lookup;
    private readonly State[][] _blocks;

    private DenseRadioIndex(NodeId[] ids, Dictionary<NodeId, int> lookup, State[][] blocks)
    {
        _ids = ids;
        _lookup = lookup;
        _blocks = blocks;
    }

    public int Count => _ids.Length;
    public bool TryIndex(NodeId id, out int index) => _lookup.TryGetValue(id, out index);
    public NodeId IdAt(int index) => _ids[index];
    public State At(int index) => _blocks[index >> BlockShift][index & BlockMask];

    public static DenseRadioIndex Build(TopologySnapshot physical,
        ImmutableDictionary<NodeId, RadioProfile> devices)
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
                node.Links);
        }
        return new DenseRadioIndex(ids, lookup, blocks);
    }

    /// <summary>
    /// Creates a new immutable view. Unchanged blocks and the read-only ID
    /// mapping remain shared with older, still-active route queries.
    /// </summary>
    public DenseRadioIndex WithChanges(TopologySnapshot physical,
        ImmutableDictionary<NodeId, RadioProfile> devices, IEnumerable<NodeId> changedIds)
    {
        var blocks = (State[][])_blocks.Clone();
        var copied = new HashSet<int>();
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
                node.Links);
            if (current.Equals(next)) continue;
            var block = index >> BlockShift;
            if (copied.Add(block))
                blocks[block] = (State[])blocks[block].Clone();
            blocks[block][index & BlockMask] = next;
            changed = true;
        }
        return changed ? new DenseRadioIndex(_ids, _lookup, blocks) : this;
    }
}
