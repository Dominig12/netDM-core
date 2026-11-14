using System.Security.Cryptography;
using System.Text;
using NetDM.Core;

namespace NetDM.Host.Transport;

internal sealed class WorldSnapshotStage(string id, int chunks, int facts, long baseSequence, bool apcMap = false)
{
    public const int MaxFacts = 32_768;
    public const int MaxChunks = 512;
    public const int MaxChunkFacts = 128;
    public const int MaxDeltaFacts = 4_096;

    private readonly List<TopologyChange> _topology = [];
    private readonly List<RadioNodeUpdate> _radios = [];
    private readonly List<AreaSpan> _geometry = [];
    private readonly List<AreaApc> _apcs = [];
    private readonly List<(NodeId Node, int Range)> _access = [];
    private readonly List<TopologyChange> _deltas = [];
    private readonly StringBuilder _digests = new();
    private GameNetworkService.Session? _candidate;
    private readonly HashSet<NodeId> _seen = [];

    public string Id => id;
    public int NextChunk { get; private set; }
    public long LastSequence { get; private set; } = baseSequence;
    public bool Committed => _candidate is not null;
    public HashSet<NodeId> SeenNodes => _seen;
    public ApcMap? Map { get; private set; }
    private int FactCount => _topology.Count + _radios.Count + _geometry.Count + _apcs.Count;

    // Digest checks detect dropped or altered chunks. Owner authentication is independent.
    public static string Digest(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text)));

    public void Append(int? index, string digest, IReadOnlyList<TopologyChange> topology,
        IReadOnlyList<RadioNodeUpdate> radios, IReadOnlyList<AreaSpan> geometry, IReadOnlyList<AreaApc> apcs,
        IReadOnlyList<(NodeId Node, int Range)> access)
    {
        var count = topology.Count + radios.Count + geometry.Count + apcs.Count;
        if (Committed || index != NextChunk || NextChunk >= chunks || count is < 1 or > MaxChunkFacts ||
            FactCount + count > facts)
            throw new ArgumentException("Unexpected snapshot chunk or fact count.");
        _topology.AddRange(topology);
        _radios.AddRange(radios);
        _geometry.AddRange(geometry);
        _apcs.AddRange(apcs);
        _access.AddRange(access);
        _digests.Append(digest);
        NextChunk++;
    }

    public void Commit(string? digest)
    {
        if (apcMap || _geometry.Count != 0 || _apcs.Count != 0)
        {
            var map = BuildMap(digest);
            _candidate = GameNetworkService.PrepareSnapshot([.. map.Nodes.Values.Select(map.AddNode), .. map.Links],
                [.. map.Access.Select(access => ApcMap.AccessUpdate(access.Key, access.Value))]);
            Map = map;
        }
        else
        {
            if (_access.Count != 0)
                throw new ArgumentException("An access range is a fact of the APC map.");
            ValidateManifest(digest);
            var links = new HashSet<(NodeId, NodeId)>();
            foreach (var change in _topology)
                if (change.Operation == TopologyOperation.Connect && !links.Add(change.A.Value < change.B.Value
                        ? (change.A, change.B) : (change.B, change.A)))
                    throw new ArgumentException("Snapshot has duplicate links.");
            _candidate = GameNetworkService.PrepareSnapshot(
                _topology.OrderBy(change => change.Operation).ToArray(), _radios);
        }
        _seen.UnionWith(_candidate.Medium.Snapshot.Physical.Nodes.Keys);
    }

    public ApcMap BuildMap(string? digest)
    {
        ValidateManifest(digest);
        if (_radios.Count != 0 || _topology.Any(change => change.Operation != TopologyOperation.AddNode))
            throw new ArgumentException("The APC map requires derived wired links.");
        if (_topology.Any(change => change.A.Value == 0))
            throw new ArgumentException("APC map requires positive node IDs.");
        var nodes = _topology.ToDictionary(change => change.A);
        var index = AreaWiring.Build(_geometry, _apcs);
        var links = index.Links.ToHashSet();
        var access = new Dictionary<NodeId, int>();
        foreach (var (node, range) in _access)
            if (!nodes.ContainsKey(node) || range is < 1 or > DeviceAttachmentTable.MaxRadioRange || !access.TryAdd(node, range))
                throw new ArgumentException("APC access range must belong to one map node and stay within 1..128.");
        var levels = _apcs.GroupBy(apc => apc.Node)
            .ToDictionary(group => group.Key, group => group.Select(apc => apc.ServedCell.Z).ToHashSet());
        foreach (var (node, servedLevels) in levels)
            if (!nodes.TryGetValue(node, out var fact) || !servedLevels.Contains(fact.Position!.Value.Z))
                throw new ArgumentException("APC must have a served segment on its physical level.");
        // Parts of the wired network that space or APC-less zones keep apart are joined by APC radio bridges.
        var (bridges, report) = ApcBridges.Build(levels.Keys.ToDictionary(id => id, id => nodes[id].Position!.Value), index.Links);
        links.UnionWith(bridges);
        return new ApcMap(nodes, links, levels, access, index) { Bridges = report };
    }

    private void ValidateManifest(string? digest)
    {
        if (Committed || NextChunk != chunks || FactCount != facts ||
            !string.Equals(digest, Digest(_digests.ToString()), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Snapshot manifest is incomplete or invalid.");
    }

    public void AppendDelta(long sequence, TopologyChange[] changes, IEnumerable<NodeId> added)
    {
        if (!Committed || LastSequence == long.MaxValue || sequence != LastSequence + 1 ||
            _deltas.Count + changes.Length > MaxDeltaFacts)
            throw new ArgumentException("Snapshot delta sequence or size is invalid.");
        ValidateApcMoves(changes, Map?.Levels);
        _deltas.AddRange(changes);
        _seen.UnionWith(added);
        LastSequence = sequence;
    }

    public GameNetworkService.Session Complete()
    {
        var candidate = _candidate ?? throw new InvalidOperationException("Snapshot must be committed.");
        if (_deltas.Count > 0)
        {
            ValidateDelta(candidate.Medium.Snapshot.Physical, _deltas);
            candidate.Medium.ApplyExternalTopology(_deltas);
        }
        return candidate;
    }

    // Validate the whole ordered delta before mutating any published topology.
    public static void ValidateDelta(TopologySnapshot snapshot, IReadOnlyList<TopologyChange> changes)
    {
        var membership = new Dictionary<NodeId, bool>();
        foreach (var change in changes)
        {
            var present = membership.TryGetValue(change.A, out var updated)
                ? updated : snapshot.Nodes.ContainsKey(change.A);
            if (change.A.Value == 0 || change.B.Value != 0)
                throw new ArgumentException("World delta requires one positive node ID per change.");
            switch (change.Operation)
            {
                case TopologyOperation.AddNode when !present && change.Position.HasValue:
                    membership[change.A] = true;
                    break;
                case TopologyOperation.MoveNode when present && change.Position.HasValue:
                    break;
                case TopologyOperation.SetForwarding when present && change.CanForward.HasValue:
                    break;
                case TopologyOperation.RemoveNode when present:
                    membership[change.A] = false;
                    break;
                default:
                    throw new ArgumentException("World delta contains an invalid node lifecycle change.");
            }
        }
    }

    public static void ValidateApcMoves(IReadOnlyList<TopologyChange> changes,
        IReadOnlyDictionary<NodeId, HashSet<int>>? levels)
    {
        if (levels is null)
            return;
        foreach (var change in changes)
            if (change.Operation == TopologyOperation.MoveNode && levels.TryGetValue(change.A, out var servedLevels) &&
                (change.Position is not { } position || !servedLevels.Contains(position.Z)))
                throw new ArgumentException("APC cannot move to a level outside its served area.");
    }
}

internal sealed record ApcMap(Dictionary<NodeId, TopologyChange> Nodes, HashSet<TopologyChange> Links,
    Dictionary<NodeId, HashSet<int>> Levels, Dictionary<NodeId, int> Access, AreaWiringIndex Index)
{
    /// <summary>The radio bridges among <see cref="Links"/> and what stayed apart.</summary>
    public ApcBridgeReport Bridges { get; init; } = ApcBridgeReport.Empty;

    /// <summary>The designed cables and bridges of the map with the nodes reported inactive, for network.diagnostics.</summary>
    public WiringReport Wiring(NodeStateTable states, DeviceAttachmentTable devices) =>
        WiringReport.Create(Links.Select(link => (link.A, link.B)), Nodes.Keys, states.Inactive.Concat(devices.InactiveStates))
        with { ExecutionPaused = devices.ExecutionPaused.ToHashSet() };

    public TopologyChange AddNode(TopologyChange fact) =>
        fact with { CanForward = fact.CanForward ?? Levels.ContainsKey(fact.A) };

    /// <summary>An APC radio access point; two access points never pair with each other.</summary>
    public static RadioNodeUpdate AccessUpdate(NodeId node, int? range) => new(node,
        Profile: range is { } value ? new RadioProfile(value, DeviceAttachmentTable.NetworkChannel, AccessPoint: true) : null,
        ChangeProfile: true);

    /// <summary>
    /// Access point profiles that differ from the active medium, for nodes of this map or the
    /// given subset of them. An inactive APC has no access point.
    /// </summary>
    public List<RadioNodeUpdate> RadioChangesFrom(SharedMediumSnapshot active, Func<NodeId, bool> isActive,
        IEnumerable<NodeId>? nodes = null)
    {
        var updates = new List<RadioNodeUpdate>();
        foreach (var id in nodes ?? Nodes.Keys)
        {
            if (!Nodes.ContainsKey(id))
                continue;
            var desired = AccessUpdate(id, Access.TryGetValue(id, out var range) && isActive(id) ? range : null);
            RadioProfile? actual = active.Devices.TryGetValue(id, out var profile) ? profile : null;
            if (actual != desired.Profile)
                updates.Add(desired);
        }
        return updates;
    }

    /// <summary>
    /// Wired map links to add or remove after <paramref name="nodes"/> changed activity. A link
    /// carries traffic only between two active APCs; node membership and roles stay as they are.
    /// </summary>
    public List<TopologyChange> LinkChanges(TopologySnapshot active, IReadOnlySet<NodeId> nodes,
        Func<NodeId, bool> isActive)
    {
        var changes = new List<TopologyChange>();
        foreach (var edge in Links)
        {
            if ((!nodes.Contains(edge.A) && !nodes.Contains(edge.B)) ||
                !active.Nodes.TryGetValue(edge.A, out var node) || !active.Nodes.ContainsKey(edge.B))
                continue;
            var wanted = isActive(edge.A) && isActive(edge.B);
            if (wanted != node.Links.ContainsKey(edge.B))
                changes.Add(wanted ? edge : edge with { Operation = TopologyOperation.Disconnect });
        }
        return changes;
    }

    public List<TopologyChange> ChangesFrom(ApcMap? previous, TopologySnapshot active, Func<NodeId, bool> isActive)
    {
        var changes = new List<TopologyChange>();
        // An edge of the map carries traffic only while both of its APCs are active.
        bool Wanted(TopologyChange edge) => Links.Contains(edge) && isActive(edge.A) && isActive(edge.B);
        foreach (var edge in previous is null ? Links : previous.Links.Union(Links))
            if (!Wanted(edge) && Nodes.ContainsKey(edge.A) && Nodes.ContainsKey(edge.B) && HasLink(edge))
                changes.Add(edge with { Operation = TopologyOperation.Disconnect });
        if (previous is not null)
        {
            foreach (var id in previous.Nodes.Keys)
                if (!Nodes.ContainsKey(id) && active.Nodes.ContainsKey(id))
                    changes.Add(new TopologyChange(TopologyOperation.RemoveNode, id));
        }
        foreach (var (id, fact) in Nodes)
        {
            if (!active.Nodes.TryGetValue(id, out var node))
            {
                changes.Add(AddNode(fact));
                continue;
            }
            if (previous is null || !previous.Nodes.ContainsKey(id))
                throw new ArgumentException("APC map cannot take ownership of an existing world node.");
            if (node.Position != fact.Position)
                changes.Add(fact with { Operation = TopologyOperation.MoveNode, CanForward = null });
            // Preserve explicit live role changes when the map has no role fact to replace them.
            var forwarding = fact.CanForward ?? (Levels.ContainsKey(id) != previous.Levels.ContainsKey(id)
                ? Levels.ContainsKey(id) : node.CanForward);
            if (node.CanForward != forwarding)
                changes.Add(new TopologyChange(TopologyOperation.SetForwarding, id) { CanForward = forwarding });
        }
        foreach (var edge in Links)
            if (Wanted(edge) && !HasLink(edge))
                changes.Add(edge);
        return changes;

        bool HasLink(TopologyChange edge) =>
            active.Nodes.TryGetValue(edge.A, out var node) && node.Links.ContainsKey(edge.B);
    }
}
