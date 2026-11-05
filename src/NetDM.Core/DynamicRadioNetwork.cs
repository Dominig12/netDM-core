using System.Collections.Immutable;

namespace NetDM.Core;

/// <summary>
/// Radio profile for a logical node. Channel 0 is reserved for unconfigured devices.
/// A wireless link exists when both powered/enabled endpoints are on the same channel,
/// on the same Z level, and mutually within each other's range.
/// </summary>
public readonly record struct RadioProfile(
    int Range,
    uint Channel,
    uint Cost = 8,
    bool Enabled = true)
{
    public void Validate()
    {
        if (Range is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(Range), "Radio range must be 1..128 world units.");
        if (Channel == 0)
            throw new ArgumentOutOfRangeException(nameof(Channel), "Radio channel 0 is reserved.");
        if (Cost == 0)
            throw new ArgumentOutOfRangeException(nameof(Cost), "Radio link cost must be positive.");
    }
}

/// <summary>
/// Position-only changes keep the current radio profile. ChangeProfile=true means the
/// profile is replaced (null disables radio). Updates must contain distinct existing IDs.
/// </summary>
public readonly record struct RadioNodeUpdate(
    NodeId Id,
    NodePosition? Position = null,
    RadioProfile? Profile = null,
    bool ChangeProfile = false);

public readonly record struct RadioUpdateResult(
    long Version,
    int Moves,
    int AddedLinks,
    int RemovedLinks,
    int ChangedCosts,
    int CandidatePairs);

public readonly record struct RadioStats(
    int ConfiguredNodes,
    int ActiveWirelessLinks,
    int RegisteredCables,
    long TotalPairsExamined,
    long TotalWirelessLinksAdded,
    long TotalWirelessLinksRemoved);

internal readonly record struct LinkPair(NodeId A, NodeId B)
{
    public static LinkPair From(NodeId a, NodeId b) =>
        a.Value < b.Value ? new(a, b) : new(b, a);
}

/// <summary>
/// Single-writer radio topology derived from the existing immutable spatial grid.
/// This class owns all radio edges and registered cable edges on its runtime.
/// Do not independently mutate these endpoints' links in TopologyStore.
/// </summary>
public sealed class DynamicRadioNetwork(NetworkRuntime runtime)
{
    private readonly object _gate = new();
    private readonly Dictionary<NodeId, RadioProfile> _profiles = new();
    private readonly HashSet<LinkPair> _radio = new();
    private readonly Dictionary<NodeId, HashSet<NodeId>> _neighbors = new();
    private readonly Dictionary<LinkPair, uint> _cables = new();
    private long _examined, _added, _removed;

    public RadioStats Stats()
    {
        lock (_gate)
            return new RadioStats(
                _profiles.Count, _radio.Count, _cables.Count,
                _examined, _added, _removed);
    }

    /// <summary>
    /// Register/update many radios and/or move many nodes in one atomic graph update.
    /// Candidate discovery is local to the moved or reconfigured endpoints, not O(V^2).
    /// </summary>
    public RadioUpdateResult Update(IReadOnlyList<RadioNodeUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        cancellationToken.ThrowIfCancellationRequested();
        if (updates.Count == 0)
            return new RadioUpdateResult(runtime.Topology.Snapshot.Version, 0, 0, 0, 0, 0);

        lock (_gate)
        {
            var snapshot = runtime.Topology.Snapshot;
            var staged = new Dictionary<NodeId, RadioNodeUpdate>(updates.Count);
            var changedPositions = new Dictionary<NodeId, NodePosition>();
            var stagedCells = new Dictionary<GridCell, List<NodeId>>();

            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (update.Id.Value == 0 || !snapshot.Nodes.TryGetValue(update.Id, out var node))
                    throw new ArgumentException("Every updated radio node must already exist.");
                if (!staged.TryAdd(update.Id, update))
                    throw new ArgumentException("Duplicate radio update in one batch.");
                if (update.ChangeProfile && update.Profile is { } config)
                    config.Validate();
                if (update.Position is { } point && node.Position != point)
                {
                    changedPositions.Add(update.Id, point);
                    var cell = CellOf(point);
                    if (!stagedCells.TryGetValue(cell, out var list))
                        stagedCells[cell] = list = new List<NodeId>();
                    list.Add(update.Id);
                }
                if (update.Position is null && node.Position is null && update.ChangeProfile
                    && update.Profile is { Enabled: true })
                    throw new ArgumentException("A radio node must have a position.");
            }

            RadioProfile? ProfileOf(NodeId id)
            {
                if (staged.TryGetValue(id, out var update) && update.ChangeProfile)
                    return update.Profile is { Enabled: true } ? update.Profile : null;
                return _profiles.TryGetValue(id, out var old) && old.Enabled ? old : null;
            }

            NodePosition? PositionOf(NodeId id)
            {
                if (changedPositions.TryGetValue(id, out var point)) return point;
                return snapshot.Nodes.TryGetValue(id, out var node) ? node.Position : null;
            }

            const int maxSpatialCandidatesPerNode = 128; // Conservative fail-fast density protection.
            var pairs = new HashSet<LinkPair>();
            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_neighbors.TryGetValue(update.Id, out var neighbors))
                {
                    foreach (var neighbor in neighbors)
                        pairs.Add(LinkPair.From(update.Id, neighbor));
                }

                if (ProfileOf(update.Id) is not { } profile || PositionOf(update.Id) is not { } origin)
                    continue;

                var inspected = 0;
                foreach (var candidate in Candidates(snapshot, origin, profile.Range, stagedCells))
                {
                    if ((inspected & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
                    if (candidate == update.Id) continue;
                    if (++inspected > maxSpatialCandidatesPerNode)
                        throw new InvalidOperationException(
                            $"Radio density near node {update.Id} exceeds {maxSpatialCandidatesPerNode} candidate endpoints. " +
                            "Use smaller radio range or a shared-medium radio domain.");
                    pairs.Add(LinkPair.From(update.Id, candidate));
                }
            }

            var graphChanges = new List<TopologyChange>(changedPositions.Count + pairs.Count);
            foreach (var (id, position) in changedPositions)
                graphChanges.Add(new TopologyChange(TopologyOperation.MoveNode, id, Position: position));

            var newlyLinked = new List<LinkPair>();
            var disconnected = new List<LinkPair>();
            var changedCosts = 0;

            foreach (var pair in pairs.OrderBy(p => p.A.Value).ThenBy(p => p.B.Value))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var formerRadio = _radio.Contains(pair);
                var profileA = ProfileOf(pair.A);
                var profileB = ProfileOf(pair.B);
                var posA = PositionOf(pair.A);
                var posB = PositionOf(pair.B);
                var desiredRadio =
                    profileA is { Enabled: true } a &&
                    profileB is { Enabled: true } b &&
                    a.Channel == b.Channel &&
                    posA is { } pa && posB is { } pb &&
                    pa.Z == pb.Z &&
                    InRange(pa, pb, Math.Min(a.Range, b.Range));

                if (desiredRadio != formerRadio)
                {
                    if (desiredRadio) newlyLinked.Add(pair);
                    else disconnected.Add(pair);
                }

                uint? cost = _cables.TryGetValue(pair, out var cableCost) ? cableCost : null;
                if (desiredRadio)
                {
                    var wirelessCost = Math.Max(profileA!.Value.Cost, profileB!.Value.Cost);
                    cost = cost is { } wired ? Math.Min(wired, wirelessCost) : wirelessCost;
                }

                var currentlyLinked = snapshot.Nodes[pair.A].Links.TryGetValue(pair.B, out var oldEdge);
                if (cost is { } desired)
                {
                    if (!currentlyLinked || oldEdge.Cost != desired)
                    {
                        graphChanges.Add(new TopologyChange(
                            TopologyOperation.Connect, pair.A, pair.B, desired));
                        if (currentlyLinked && desiredRadio == formerRadio) changedCosts++;
                    }
                }
                else if (currentlyLinked)
                    graphChanges.Add(new TopologyChange(TopologyOperation.Disconnect, pair.A, pair.B));
            }

            cancellationToken.ThrowIfCancellationRequested();
            // The graph publication and ledger update are a single non-cancellable commit phase.
            var result = runtime.Apply(graphChanges);
            foreach (var update in updates)
            {
                if (!update.ChangeProfile) continue;
                if (update.Profile is { Enabled: true } profile)
                    _profiles[update.Id] = profile;
                else
                    _profiles.Remove(update.Id);
            }

            foreach (var pair in disconnected)
            {
                _radio.Remove(pair);
                RemoveNeighbor(pair.A, pair.B);
                RemoveNeighbor(pair.B, pair.A);
            }
            foreach (var pair in newlyLinked)
            {
                _radio.Add(pair);
                AddNeighbor(pair.A, pair.B);
                AddNeighbor(pair.B, pair.A);
            }
            _examined += pairs.Count;
            _added += newlyLinked.Count;
            _removed += disconnected.Count;
            return new RadioUpdateResult(result.Version, changedPositions.Count,
                newlyLinked.Count, disconnected.Count, changedCosts, pairs.Count);
        }
    }

    /// <summary>
    /// Registered cables are persistent across radio moves and outages.
    /// Overlapping physical+radio links use the lower of the two costs.
    /// </summary>
    public bool SetCable(NodeId a, NodeId b, uint? cost)
    {
        if (a.Value == 0 || b.Value == 0 || a == b || cost == 0)
            throw new ArgumentException("Invalid cable endpoints or weight.");
        lock (_gate)
        {
            var snapshot = runtime.Topology.Snapshot;
            if (!snapshot.Nodes.ContainsKey(a) || !snapshot.Nodes.ContainsKey(b))
                throw new ArgumentException("Both cable endpoints must exist.");
            var pair = LinkPair.From(a, b);
            var previous = _cables.TryGetValue(pair, out var old) ? old : (uint?)null;
            if (previous == cost) return false;

            uint? desired = cost;
            if (_radio.Contains(pair))
            {
                var radioCost = Math.Max(_profiles[pair.A].Cost, _profiles[pair.B].Cost);
                desired = desired is { } cable ? Math.Min(cable, radioCost) : radioCost;
            }
            TopologyChange change = desired is { } weight
                ? new TopologyChange(TopologyOperation.Connect, pair.A, pair.B, weight)
                : new TopologyChange(TopologyOperation.Disconnect, pair.A, pair.B);
            runtime.Apply([change]);
            if (cost is { } newCost) _cables[pair] = newCost;
            else _cables.Remove(pair);
            return true;
        }
    }

    public int RemoveNodes(IReadOnlyCollection<NodeId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return 0;
        lock (_gate)
        {
            var snapshot = runtime.Topology.Snapshot;
            var unique = ids.ToHashSet();
            if (unique.Contains(default) || unique.Any(id => !snapshot.Nodes.ContainsKey(id)))
                throw new ArgumentException("All removed nodes must exist.");

            var removedLinks = _radio.Where(pair =>
                unique.Contains(pair.A) || unique.Contains(pair.B)).ToArray();
            var removedCables = _cables.Keys.Where(pair =>
                unique.Contains(pair.A) || unique.Contains(pair.B)).ToArray();

            var commands = unique.Select(id =>
                new TopologyChange(TopologyOperation.RemoveNode, id)).ToArray();
            runtime.Apply(commands);
            foreach (var id in unique) _profiles.Remove(id);
            foreach (var pair in removedLinks)
            {
                _radio.Remove(pair);
                RemoveNeighbor(pair.A, pair.B);
                RemoveNeighbor(pair.B, pair.A);
            }
            foreach (var pair in removedCables) _cables.Remove(pair);
            _removed += removedLinks.Length;
            return unique.Count;
        }
    }

    private static bool InRange(NodePosition a, NodePosition b, int range)
    {
        var dx = (long)a.X - b.X;
        var dy = (long)a.Y - b.Y;
        // Check signed magnitudes before squaring: extreme teleports may otherwise
        // overflow Int64 and falsely resurrect an out-of-range link.
        if (Math.Abs(dx) > range || Math.Abs(dy) > range) return false;
        return dx * dx + dy * dy <= (long)range * range;
    }

    private static GridCell CellOf(NodePosition p) =>
        new(FloorCell(p.X), FloorCell(p.Y), p.Z);

    private static int FloorCell(int x) =>
        (int)Math.DivRem((long)x, TopologyStore.CellSize, out var remainder)
        - (remainder < 0 ? 1 : 0);

    private static IEnumerable<NodeId> Candidates(
        TopologySnapshot snapshot,
        NodePosition origin,
        int radius,
        Dictionary<GridCell, List<NodeId>> staged)
    {
        var x0 = FloorCell((int)Math.Clamp((long)origin.X - radius, int.MinValue, int.MaxValue));
        var x1 = FloorCell((int)Math.Clamp((long)origin.X + radius, int.MinValue, int.MaxValue));
        var y0 = FloorCell((int)Math.Clamp((long)origin.Y - radius, int.MinValue, int.MaxValue));
        var y1 = FloorCell((int)Math.Clamp((long)origin.Y + radius, int.MinValue, int.MaxValue));
        var seen = new HashSet<NodeId>();
        for (long x = x0; x <= x1; x++)
        for (long y = y0; y <= y1; y++)
        {
            var cell = new GridCell((int)x, (int)y, origin.Z);
            if (snapshot.SpatialCells.TryGetValue(cell, out var oldIds))
            {
                foreach (var id in oldIds)
                    if (seen.Add(id)) yield return id;
            }
            if (staged.TryGetValue(cell, out var added))
            {
                foreach (var id in added)
                    if (seen.Add(id)) yield return id;
            }
        }
    }

    private void AddNeighbor(NodeId a, NodeId b)
    {
        if (!_neighbors.TryGetValue(a, out var set))
            _neighbors[a] = set = new HashSet<NodeId>();
        set.Add(b);
    }

    private void RemoveNeighbor(NodeId a, NodeId b)
    {
        if (!_neighbors.TryGetValue(a, out var set)) return;
        set.Remove(b);
        if (set.Count == 0) _neighbors.Remove(a);
    }
}
