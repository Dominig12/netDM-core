using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace NetDM.Core;

/// <summary>A shared airtime budget is scoped to one channel and one Z-level.</summary>
public readonly record struct RadioDomainKey(uint Channel, int Z);

/// <summary>Grid cell carrying radio devices on one channel / Z-level.</summary>
public readonly record struct RadioMediumCell(RadioDomainKey Domain, int X, int Y);

/// <summary>
/// ChannelWide preserves legacy one-budget-per-channel behaviour. SpatialCells
/// reserves all cells touched by an interference region around a wireless hop.
/// Independent distant hops may transmit at the same tick, while overlapping
/// regions compete for airtime. The model is deliberately conservative.
/// </summary>
public enum RadioAirtimeScope { ChannelWide = 0, SpatialCells = 1 }

/// <summary>
/// A coherent view of the physical graph and spatial radio membership.
/// It is published as a single immutable object, so route readers never mix
/// pre-move wireless membership with post-move physical positions.
/// </summary>
public sealed record SharedMediumSnapshot(
    long Version,
    TopologySnapshot Physical,
    ImmutableDictionary<NodeId, RadioProfile> Devices,
    ImmutableDictionary<RadioMediumCell, ImmutableHashSet<NodeId>> Cells)
{
    /// <summary>Disables geometric heuristics when arbitrary long-range cables exist.</summary>
    public bool HasPhysicalLinks { get; init; }
    internal DenseRadioIndex? Dense { get; init; }
}

public enum SharedRadioSearchMode
{
    Dijkstra = 0,
    AStar = 1,
    AStarPooled = 2,
    AStarDense = 3
}

public readonly record struct SharedMediumUpdateResult(
    long Version, long PhysicalVersion, int Moves, int ChangedRadios, int IndexedCells);

public readonly record struct SharedMediumStatistics(
    long Version, long PhysicalVersion, int ConfiguredDevices,
    int Domains, int OccupiedCells, long PairwiseRadioEdges,
    long AcceptedTransfers, long RejectedTransfers,
    long AcceptedBytes, long RejectedBytes,
    long RejectedStaleRoute, long RejectedInvalidPath,
    long RejectedTransferTooLarge, long RejectedCapacity,
    long RejectedOldTick, long AttemptedWirelessHops,
    long AcceptedRevalidatedTransfers,
    long SpatialSuccessfulHops, long SpatialBusyHops,
    long SpatialPayloadBytes, long SpatialCellReservations,
    int SpatialActiveCells, long SpatialLastTick,
    long InteractiveHopTransmissions, long BackgroundHopTransmissions,
    long InteractiveHopBytes, long BackgroundHopBytes,
    long ProtectedBackgroundDeferrals);

public readonly record struct RadioMediumCellView(
    uint Channel, int Z, int X, int Y, int Count);

/// <summary>
/// On-demand radio connectivity, without writing O(n²) physical graph edges.
/// This layer owns device positions/profiles and cable changes while it is active.
/// The cell grid is persistent immutable data; route queries are lock-free.
/// </summary>
public sealed partial class SharedMediumNetwork
{
    private readonly NetworkRuntime _runtime;
    private readonly object _writer = new();
    private readonly object _airtimeLock = new();
    private readonly RouteCache _cache = new(8_192);

    // A search owns its workspace exclusively for the duration of the call.
    // The bounded pool amortizes dictionaries and priority-queue backing
    // arrays across unrelated requests without sharing mutable state.
    private sealed class PooledAStarWorkspace
    {
        public readonly Dictionary<NodeId, ulong> Costs = new();
        public readonly Dictionary<NodeId, NodeId> Parents = new();
        public readonly PriorityQueue<(NodeId Id, ulong Distance), double> Frontier = new();
        public int PeakQueueCount;
    }

    private static readonly ConcurrentBag<PooledAStarWorkspace> AStarWorkspaces = new();
    private static int _pooledWorkspaceCount;
    private const int MaxPooledWorkspaces = 8;
    private const int MaxRetainedSearchNodes = 65_536;
    private const int MaxRetainedFrontier = 131_072;

    private static PooledAStarWorkspace RentAStarWorkspace()
    {
        if (AStarWorkspaces.TryTake(out var workspace))
        {
            Interlocked.Decrement(ref _pooledWorkspaceCount);
            return workspace;
        }
        return new PooledAStarWorkspace();
    }

    private static void ReturnAStarWorkspace(PooledAStarWorkspace workspace)
    {
        // Never leak one request's graph membership or parents to another.
        // Keep only bounded working buffers. Discard abnormal huge searches.
        bool keep = workspace.Costs.Count <= MaxRetainedSearchNodes &&
                    workspace.Frontier.Count <= MaxRetainedFrontier &&
                    workspace.PeakQueueCount <= MaxRetainedFrontier;
        workspace.Costs.Clear();
        workspace.Parents.Clear();
        workspace.Frontier.Clear();
        workspace.PeakQueueCount = 0;
        if (!keep) return;
        if (Interlocked.Increment(ref _pooledWorkspaceCount) > MaxPooledWorkspaces)
        {
            Interlocked.Decrement(ref _pooledWorkspaceCount);
            return;
        }
        AStarWorkspaces.Add(workspace);
    }
    // Background bytes are tracked alongside total usage in the SAME ledger:
    // interactive reservations must never exceed the physical channel budget.
    private readonly Dictionary<RadioDomainKey, (long Tick, long Used, long Background)> _airtime = new();
    // Cells are scoped to one logical tick and cleared atomically on advance:
    // allocation size is bounded by occupied airtime regions, not elapsed time.
    private readonly Dictionary<RadioMediumCell, (long Used, long Background)> _spatialAirtime = new();
    private long _spatialTick = -1;
    private long _spatialHops, _spatialBusy, _spatialBytes, _spatialCellReservations;
    private long _interactiveHopTransmissions, _backgroundHopTransmissions;
    private long _interactiveHopBytes, _backgroundHopBytes, _protectedBackgroundDeferrals;
    private readonly long _bytesPerTick;
    private readonly SharedRadioSearchMode _searchMode;
    private readonly RadioAirtimeScope _airtimeScope;
    private readonly int _maximumRadioRange;
    private readonly uint _minimumRadioCost;
    private SharedMediumSnapshot _current;
    private long _accepted, _rejected, _acceptedBytes, _rejectedBytes;
    private long _rejectedStaleRoute, _rejectedInvalidPath;
    private long _rejectedTooLarge, _rejectedCapacity, _rejectedOldTick;
    private long _attemptedWirelessHops;
    private long _acceptedRevalidated;

    public SharedMediumNetwork(NetworkRuntime runtime, long bytesPerTick = 8_192,
        SharedRadioSearchMode searchMode = SharedRadioSearchMode.Dijkstra,
        int maximumRadioRange = 128, uint minimumRadioCost = 1,
        RadioAirtimeScope airtimeScope = RadioAirtimeScope.ChannelWide)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (bytesPerTick is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(bytesPerTick));
        if (maximumRadioRange is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(maximumRadioRange));
        if (minimumRadioCost == 0)
            throw new ArgumentOutOfRangeException(nameof(minimumRadioCost));
        if (!Enum.IsDefined(searchMode))
            throw new ArgumentOutOfRangeException(nameof(searchMode));
        if (!Enum.IsDefined(airtimeScope))
            throw new ArgumentOutOfRangeException(nameof(airtimeScope));
        _runtime = runtime;
        _bytesPerTick = bytesPerTick;
        _searchMode = searchMode;
        _airtimeScope = airtimeScope;
        _maximumRadioRange = maximumRadioRange;
        _minimumRadioCost = minimumRadioCost;
        var physical = runtime.Topology.Snapshot;
        _current = new SharedMediumSnapshot(0, physical,
            ImmutableDictionary<NodeId, RadioProfile>.Empty,
            ImmutableDictionary<RadioMediumCell, ImmutableHashSet<NodeId>>.Empty)
        {
            HasPhysicalLinks = physical.Nodes.Values.Any(n => n.Links.Count != 0),
            Dense = searchMode == SharedRadioSearchMode.AStarDense
                ? DenseRadioIndex.Build(physical,
                    ImmutableDictionary<NodeId, RadioProfile>.Empty,
                    ImmutableDictionary<RadioMediumCell, ImmutableHashSet<NodeId>>.Empty)
                : null
        };
    }

    public long BytesPerTick => _bytesPerTick;
    public SharedRadioSearchMode SearchMode => _searchMode;
    public RadioAirtimeScope AirtimeScope => _airtimeScope;
    public SharedMediumSnapshot Snapshot => Volatile.Read(ref _current);

    public SharedMediumStatistics Stats()
    {
        var snapshot = Snapshot;
        var domains = snapshot.Devices
            .Select(pair => DomainOf(pair.Value, snapshot.Physical.Nodes[pair.Key].Position!.Value))
            .ToHashSet();
        int spatialActive;
        long spatialLastTick;
        lock (_airtimeLock)
        {
            spatialActive = _spatialAirtime.Count;
            spatialLastTick = _spatialTick;
        }
        return new SharedMediumStatistics(snapshot.Version, snapshot.Physical.Version,
            snapshot.Devices.Count, domains.Count, snapshot.Cells.Count, 0,
            Interlocked.Read(ref _accepted), Interlocked.Read(ref _rejected),
            Interlocked.Read(ref _acceptedBytes), Interlocked.Read(ref _rejectedBytes),
            Interlocked.Read(ref _rejectedStaleRoute),
            Interlocked.Read(ref _rejectedInvalidPath),
            Interlocked.Read(ref _rejectedTooLarge),
            Interlocked.Read(ref _rejectedCapacity),
            Interlocked.Read(ref _rejectedOldTick),
            Interlocked.Read(ref _attemptedWirelessHops),
            Interlocked.Read(ref _acceptedRevalidated),
            Interlocked.Read(ref _spatialHops),
            Interlocked.Read(ref _spatialBusy),
            Interlocked.Read(ref _spatialBytes),
            Interlocked.Read(ref _spatialCellReservations),
            spatialActive, spatialLastTick,
            Interlocked.Read(ref _interactiveHopTransmissions),
            Interlocked.Read(ref _backgroundHopTransmissions),
            Interlocked.Read(ref _interactiveHopBytes),
            Interlocked.Read(ref _backgroundHopBytes),
            Interlocked.Read(ref _protectedBackgroundDeferrals));
    }

    /// <summary>
    /// Update many radio devices/movements and publish one combined snapshot.
    /// No radio clique is constructed, even when thousands of devices overlap.
    /// </summary>
    public SharedMediumUpdateResult Update(IReadOnlyList<RadioNodeUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writer)
        {
            var before = Snapshot;
            VerifyOwnership(before);
            if (updates.Count == 0)
                return new SharedMediumUpdateResult(before.Version,
                    before.Physical.Version, 0, 0, before.Cells.Count);

            var devices = before.Devices.ToBuilder();
            var cells = before.Cells.ToBuilder();
            var moves = new List<TopologyChange>(updates.Count);
            var seen = new HashSet<NodeId>();
            int changedRadios = 0;
            foreach (var change in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (change.Id.Value == 0 || !seen.Add(change.Id) ||
                    !before.Physical.Nodes.TryGetValue(change.Id, out var node))
                    throw new ArgumentException("Every updated node must exist and be unique.");

                if (change.ChangeProfile && change.Profile is { } candidate)
                {
                    candidate.Validate();
                    if (candidate.Range > _maximumRadioRange ||
                        candidate.Cost < _minimumRadioCost)
                        throw new ArgumentException(
                            "Radio profile violates the configured A* range/cost bounds.");
                }

                var oldPosition = node.Position;
                var nextPosition = change.Position ?? oldPosition;
                var oldProfile = devices.TryGetValue(change.Id, out var previous)
                    ? (RadioProfile?)previous : null;
                var nextProfile = change.ChangeProfile
                    ? change.Profile is { Enabled: true } ? change.Profile : null
                    : oldProfile;

                if (nextProfile.HasValue && nextPosition is null)
                    throw new ArgumentException("Active radio devices require a position.");

                if (oldProfile == nextProfile && oldPosition == nextPosition)
                    continue;

                if (oldProfile is { } oldRadio && oldPosition is { } oldPos)
                    RemoveFromCell(cells, change.Id, CellOf(oldRadio, oldPos));
                if (nextProfile is { } newRadio && nextPosition is { } newPos)
                    AddToCell(cells, change.Id, CellOf(newRadio, newPos));

                if (nextProfile is { } configured)
                    devices[change.Id] = configured;
                else
                    devices.Remove(change.Id);

                if (oldProfile != nextProfile) changedRadios++;
                if (oldPosition != nextPosition)
                    moves.Add(new TopologyChange(TopologyOperation.MoveNode,
                        change.Id, Position: nextPosition));
            }

            if (moves.Count == 0 && changedRadios == 0)
                return new SharedMediumUpdateResult(before.Version,
                    before.Physical.Version, 0, 0, before.Cells.Count);

            cancellationToken.ThrowIfCancellationRequested();
            if (moves.Count > 0) _runtime.Apply(moves);
            var physical = _runtime.Topology.Snapshot;
            var nextDevices = devices.ToImmutable();
            var nextCells = cells.ToImmutable();
            var next = new SharedMediumSnapshot(checked(before.Version + 1), physical,
                nextDevices, nextCells)
            {
                HasPhysicalLinks = before.HasPhysicalLinks,
                Dense = before.Dense?.WithChanges(physical, nextDevices, nextCells,
                    updates.Select(u => u.Id))
            };
            _cache.Clear();
            Volatile.Write(ref _current, next);
            return new SharedMediumUpdateResult(next.Version, physical.Version,
                moves.Count, changedRadios, next.Cells.Count);
        }
    }

    /// <summary>Update a permanent cable link; overlapping radio remains available.</summary>
    public bool SetCable(NodeId a, NodeId b, uint? cost)
    {
        if (a.Value == 0 || b.Value == 0 || a == b || cost == 0)
            throw new ArgumentException("A cable requires distinct nonzero nodes and positive weight.");
        lock (_writer)
        {
            var before = Snapshot;
            VerifyOwnership(before);
            if (!before.Physical.Nodes.TryGetValue(a, out var left) ||
                !before.Physical.Nodes.ContainsKey(b))
                throw new ArgumentException("Both endpoints must exist.");
            var exists = left.Links.TryGetValue(b, out var existing);
            if (cost is { } weight && exists && existing.Cost == weight) return false;
            if (cost is null && !exists) return false;
            var op = cost is null ? TopologyOperation.Disconnect : TopologyOperation.Connect;
            var result = _runtime.Apply([new TopologyChange(op, a, b, cost ?? 1)]);
            if (result.Applied == 0) return false;
            PublishPhysical(before, a, b);
            return true;
        }
    }

    public int RemoveNodes(IReadOnlyCollection<NodeId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_writer)
        {
            var before = Snapshot;
            VerifyOwnership(before);
            if (ids.Count == 0) return 0;
            var unique = ids.ToHashSet();
            if (unique.Contains(default) ||
                unique.Any(id => !before.Physical.Nodes.ContainsKey(id)))
                throw new ArgumentException("All removed IDs must be existing and nonzero.");
            var devices = before.Devices.ToBuilder();
            var cells = before.Cells.ToBuilder();
            foreach (var id in unique)
            {
                if (!devices.TryGetValue(id, out var profile)) continue;
                var position = before.Physical.Nodes[id].Position!.Value;
                RemoveFromCell(cells, id, CellOf(profile, position));
                devices.Remove(id);
            }
            var changes = unique.Select(id =>
                new TopologyChange(TopologyOperation.RemoveNode, id)).ToArray();
            _runtime.Apply(changes);
            var physical = _runtime.Topology.Snapshot;
            var nextDevices = devices.ToImmutable();
            var nextCells = cells.ToImmutable();
            var next = new SharedMediumSnapshot(checked(before.Version + 1),
                physical, nextDevices, nextCells)
            {
                HasPhysicalLinks = physical.Nodes.Values.Any(n => n.Links.Count != 0),
                Dense = before.Dense is not null
                    ? DenseRadioIndex.Build(physical, nextDevices, nextCells)
                    : null
            };
            _cache.Clear();
            Volatile.Write(ref _current, next);
            return unique.Count;
        }
    }

    /// <summary>
    /// Calculates shortest routes across physical cables AND lazy radio neighbors.
    /// Every returned path is bound to one version of the combined medium snapshot.
    /// </summary>
    public RouteResult Route(NodeId source, NodeId destination, int maxExplored = 100_000,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var snapshot = Snapshot;
        RouteResult cachedResult = null!;
        var eligible = maxExplored == 100_000;
        var cached = eligible &&
            _cache.TryGet(snapshot.Version, source, destination, out cachedResult);
        var result = cached ? cachedResult : _searchMode switch
        {
            SharedRadioSearchMode.AStar => RouteAStarCore(snapshot, source, destination,
                _maximumRadioRange, _minimumRadioCost, maxExplored, cancellationToken),
            SharedRadioSearchMode.AStarPooled => RouteAStarPooledCore(snapshot, source,
                destination, _maximumRadioRange, _minimumRadioCost,
                maxExplored, cancellationToken),
            SharedRadioSearchMode.AStarDense => RouteAStarDenseCore(snapshot, source,
                destination, _maximumRadioRange, _minimumRadioCost,
                maxExplored, cancellationToken),
            _ => RouteCore(snapshot, source, destination, maxExplored, cancellationToken)
        };
        if (!cached && eligible && result.Explored <= maxExplored)
            _cache.Store(snapshot.Version, result, source, destination);
        _runtime.RecordExternalRoute(result, cached, Stopwatch.GetTimestamp() - started,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        return result;
    }

    public static RouteResult RouteCore(SharedMediumSnapshot snapshot,
        NodeId source, NodeId target, int maxExplored = 100_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExplored);
        if (!snapshot.Physical.Nodes.ContainsKey(source) ||
            !snapshot.Physical.Nodes.ContainsKey(target))
            return RouteResult.Missing(snapshot.Version);
        if (source == target)
            return new RouteResult(snapshot.Version, true, 0, [source], 1);

        var costs = new Dictionary<NodeId, ulong> { [source] = 0 };
        var parent = new Dictionary<NodeId, NodeId>();
        var q = new PriorityQueue<NodeId, ulong>();
        q.Enqueue(source, 0);
        var explored = 0;

        while (q.TryDequeue(out var node, out var distance))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!costs.TryGetValue(node, out var known) || known != distance) continue;
            // With strictly positive weights, once the smallest unsettled
            // distance reaches the best known target distance, no remaining
            // path can improve it. This avoids N squared expansion of dense
            // same-cost radio cliques after the target has been discovered.
            if (node != target && costs.TryGetValue(target, out var upperBound) &&
                distance >= upperBound)
            {
                var shortest = new List<NodeId> { target };
                for (var cursor = target; cursor != source;)
                {
                    cursor = parent[cursor];
                    shortest.Add(cursor);
                }
                shortest.Reverse();
                return new RouteResult(snapshot.Version, true, upperBound, shortest, explored);
            }
            if (++explored > maxExplored)
                return RouteResult.Missing(snapshot.Version, explored);
            if (node == target)
            {
                var path = new List<NodeId> { target };
                for (var cursor = target; cursor != source;)
                {
                    cursor = parent[cursor];
                    path.Add(cursor);
                }
                path.Reverse();
                return new RouteResult(snapshot.Version, true, distance, path, explored);
            }
            void Relax(NodeId candidate, uint weight)
            {
                var next = checked(distance + weight);
                if (costs.TryGetValue(candidate, out var old) && old <= next) return;
                costs[candidate] = next;
                parent[candidate] = node;
                q.Enqueue(candidate, next);
            }

            foreach (var wired in snapshot.Physical.Nodes[node].Links.Values)
                Relax(wired.To, wired.Cost);
            foreach (var wireless in RadioNeighbors(snapshot, node))
                Relax(wireless.To, wireless.Cost);
        }
        return RouteResult.Missing(snapshot.Version, explored);
    }

    /// <summary>
    /// Exact A* on the immutable radio graph. An optimistic geometric heuristic
    /// is only admissible without arbitrary-length physical cable shortcuts.
    /// Bounds are enforced at radio registration. Distant channels and Z
    /// layers have no radio path when there are no cables.
    /// </summary>
    public static RouteResult RouteAStarCore(SharedMediumSnapshot snapshot,
        NodeId source, NodeId target, int maximumRadioRange = 128,
        uint minimumRadioCost = 1, int maxExplored = 100_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExplored);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRadioRange);
        ArgumentOutOfRangeException.ThrowIfZero(minimumRadioCost);
        if (!snapshot.Physical.Nodes.ContainsKey(source) ||
            !snapshot.Physical.Nodes.ContainsKey(target))
            return RouteResult.Missing(snapshot.Version);
        if (source == target)
            return new RouteResult(snapshot.Version, true, 0, [source], 1);

        if (!snapshot.HasPhysicalLinks)
        {
            if (!snapshot.Devices.TryGetValue(source, out var sourceRadio) ||
                !snapshot.Devices.TryGetValue(target, out var targetRadio) ||
                sourceRadio.Channel != targetRadio.Channel ||
                snapshot.Physical.Nodes[source].Position!.Value.Z !=
                snapshot.Physical.Nodes[target].Position!.Value.Z)
                return RouteResult.Missing(snapshot.Version);
        }

        var targetPosition = snapshot.Physical.Nodes[target].Position;
        double Heuristic(NodeId node)
        {
            if (snapshot.HasPhysicalLinks || targetPosition is null)
                return 0;
            var position = snapshot.Physical.Nodes[node].Position;
            if (position is null) return 0;
            double dx = (double)position.Value.X - targetPosition.Value.X;
            double dy = (double)position.Value.Y - targetPosition.Value.Y;
            // Every radio hop moves at most maximumRadioRange units and costs
            // at least minimumRadioCost. floor() rounds toward a *lower* bound.
            return Math.Floor(Math.Sqrt(dx * dx + dy * dy) / maximumRadioRange) *
                minimumRadioCost;
        }

        var distances = new Dictionary<NodeId, ulong> { [source] = 0 };
        var parents = new Dictionary<NodeId, NodeId>();
        var frontier = new PriorityQueue<(NodeId Id, ulong Distance), double>();
        frontier.Enqueue((source, 0), Heuristic(source));
        int explored = 0;

        while (frontier.TryDequeue(out var item, out _))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = item.Id;
            var cost = item.Distance;
            if (!distances.TryGetValue(node, out var latest) || latest != cost)
                continue;
            if (++explored > maxExplored)
                return RouteResult.Missing(snapshot.Version, explored);
            if (node == target)
            {
                var path = new List<NodeId> { node };
                for (var cursor = target; cursor != source;)
                {
                    cursor = parents[cursor];
                    path.Add(cursor);
                }
                path.Reverse();
                return new RouteResult(snapshot.Version, true, cost, path, explored);
            }

            void Relax(NodeId candidate, uint weight)
            {
                var next = checked(cost + weight);
                if (distances.TryGetValue(candidate, out var prior) && next >= prior)
                    return;
                distances[candidate] = next;
                parents[candidate] = node;
                frontier.Enqueue((candidate, next), next + Heuristic(candidate));
            }
            foreach (var cable in snapshot.Physical.Nodes[node].Links.Values)
                Relax(cable.To, cable.Cost);
            foreach (var radio in RadioNeighbors(snapshot, node))
                Relax(radio.To, radio.Cost);
        }
        return RouteResult.Missing(snapshot.Version, explored);
    }

    /// <summary>
    /// Exact A* with an exclusive pooled workspace and zero-allocation
    /// radio-neighbor traversal. Geometric heuristic is byte-for-byte the
    /// same formula as RouteAStarCore, including cable fallback.
    /// Every request pins one immutable SharedMediumSnapshot.
    /// </summary>
    public static RouteResult RouteAStarPooledCore(SharedMediumSnapshot snapshot,
        NodeId source, NodeId target, int maximumRadioRange = 128,
        uint minimumRadioCost = 1, int maxExplored = 100_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExplored);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRadioRange);
        ArgumentOutOfRangeException.ThrowIfZero(minimumRadioCost);
        if (!snapshot.Physical.Nodes.ContainsKey(source) ||
            !snapshot.Physical.Nodes.ContainsKey(target))
            return RouteResult.Missing(snapshot.Version);
        if (source == target)
            return new RouteResult(snapshot.Version, true, 0, [source], 1);

        if (!snapshot.HasPhysicalLinks)
        {
            if (!snapshot.Devices.TryGetValue(source, out var sourceRadio) ||
                !snapshot.Devices.TryGetValue(target, out var targetRadio) ||
                sourceRadio.Channel != targetRadio.Channel ||
                snapshot.Physical.Nodes[source].Position!.Value.Z !=
                snapshot.Physical.Nodes[target].Position!.Value.Z)
                return RouteResult.Missing(snapshot.Version);
        }

        var targetPosition = snapshot.Physical.Nodes[target].Position;
        double Heuristic(NodeId id)
        {
            if (snapshot.HasPhysicalLinks || targetPosition is null)
                return 0;
            var position = snapshot.Physical.Nodes[id].Position;
            if (position is null) return 0;
            double dx = (double)position.Value.X - targetPosition.Value.X;
            double dy = (double)position.Value.Y - targetPosition.Value.Y;
            return Math.Floor(Math.Sqrt(dx * dx + dy * dy) / maximumRadioRange) *
                minimumRadioCost;
        }

        var workspace = RentAStarWorkspace();
        var costs = workspace.Costs;
        var parents = workspace.Parents;
        var frontier = workspace.Frontier;
        try
        {
            costs[source] = 0;
            frontier.Enqueue((source, 0), Heuristic(source));
            int explored = 0;

            while (frontier.TryDequeue(out var item, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var node = item.Id;
                var cost = item.Distance;
                if (!costs.TryGetValue(node, out var latest) || latest != cost)
                    continue;
                if (++explored > maxExplored)
                    return RouteResult.Missing(snapshot.Version, explored);

                if (node == target)
                {
                    var path = new List<NodeId> { node };
                    for (var cursor = target; cursor != source;)
                    {
                        cursor = parents[cursor];
                        path.Add(cursor);
                    }
                    path.Reverse();
                    return new RouteResult(snapshot.Version, true, cost, path, explored);
                }

                void Relax(NodeId nextNode, uint weight)
                {
                    var candidateCost = checked(cost + weight);
                    if (costs.TryGetValue(nextNode, out var previous) &&
                        candidateCost >= previous)
                        return;
                    costs[nextNode] = candidateCost;
                    parents[nextNode] = node;
                    frontier.Enqueue((nextNode, candidateCost),
                        candidateCost + Heuristic(nextNode));
                    if (frontier.Count > workspace.PeakQueueCount)
                        workspace.PeakQueueCount = frontier.Count;
                }

                foreach (var cable in snapshot.Physical.Nodes[node].Links.Values)
                    Relax(cable.To, cable.Cost);

                // Inline the lazy radio-neighbor iterator. Iterator states
                // and temporary Edge values previously dominated per-query
                // allocations in radio-dense connected networks.
                if (!snapshot.Devices.TryGetValue(node, out var profile))
                    continue;
                var origin = snapshot.Physical.Nodes[node].Position!.Value;
                var domain = DomainOf(profile, origin);
                int fromX = FloorCell((int)Math.Clamp((long)origin.X - profile.Range,
                    int.MinValue, int.MaxValue));
                int toX = FloorCell((int)Math.Clamp((long)origin.X + profile.Range,
                    int.MinValue, int.MaxValue));
                int fromY = FloorCell((int)Math.Clamp((long)origin.Y - profile.Range,
                    int.MinValue, int.MaxValue));
                int toY = FloorCell((int)Math.Clamp((long)origin.Y + profile.Range,
                    int.MinValue, int.MaxValue));

                for (long x = fromX; x <= toX; x++)
                for (long y = fromY; y <= toY; y++)
                {
                    var cell = new RadioMediumCell(domain, (int)x, (int)y);
                    if (!snapshot.Cells.TryGetValue(cell, out var members))
                        continue;
                    foreach (var neighbor in members)
                    {
                        if (neighbor == node ||
                            !snapshot.Devices.TryGetValue(neighbor, out var other))
                            continue;
                        var destination = snapshot.Physical.Nodes[neighbor].Position!.Value;
                        if (!Within(origin, destination,
                            Math.Min(profile.Range, other.Range)))
                            continue;
                        Relax(neighbor, Math.Max(profile.Cost, other.Cost));
                    }
                }
            }
            return RouteResult.Missing(snapshot.Version, explored);
        }
        finally
        {
            // Cancellation and any exception must also return a cleared
            // workspace. A concurrent query can never see partial results.
            ReturnAStarWorkspace(workspace);
        }
    }

    /// <summary>
    /// Transmit exactly one hop against current topology and the shared
    /// per-channel airtime ledger. Cable links need no channel allocation.
    /// This is intentionally distinct from legacy whole-route TryTransmit.
    /// </summary>
    /// <summary>
    /// Pure, lock-free next-hop reachability check on one pinned snapshot.
    /// It does not reserve channel airtime and does not guarantee that a later
    /// transmission will succeed. Used to wake a waiting packet when its
    /// original wireless/cable hop has physically recovered.
    /// </summary>
    public bool HasUsableHop(NodeId from, NodeId to)
    {
        var snapshot = Snapshot;
        if (from == to ||
            !snapshot.Physical.Nodes.TryGetValue(from, out var node) ||
            !snapshot.Physical.Nodes.ContainsKey(to))
            return false;
        return node.Links.ContainsKey(to) ||
            RadioCost(snapshot, from, to, out _) is not null;
    }

    /// <summary>
    /// Optional demand-aware airtime reservation. When a packet scheduler
    /// has unvisited interactive entries from the tick-start ready snapshot,
    /// background wireless hops may consume only
    /// (100 - interactiveReservePercent)% of each channel/cell budget.
    /// The scheduler passes zero after draining that snapshot, allowing
    /// background to borrow otherwise idle airtime within the same tick.
    /// Interactive hops may use any free bytes.
    /// This is admission control, not preemption or an additional airtime
    /// pool. With reservePercent=0 the old shared-medium behavior is preserved.
    /// Cable hops never use radio airtime.
    /// </summary>
    public RadioHopOutcome TryTransmitHop(NodeId from, NodeId to, int bytes, long tick,
        PacketTrafficClass trafficClass = PacketTrafficClass.Background,
        int interactiveReservePercent = 0)
    {
        if (bytes is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        if (trafficClass is not (PacketTrafficClass.Background or PacketTrafficClass.Interactive))
            throw new ArgumentOutOfRangeException(nameof(trafficClass));
        if (interactiveReservePercent is < 0 or > 90)
            throw new ArgumentOutOfRangeException(nameof(interactiveReservePercent));
        var snapshot = Snapshot;
        if (from == to ||
            !snapshot.Physical.Nodes.TryGetValue(from, out var node) ||
            !snapshot.Physical.Nodes.ContainsKey(to))
            return RadioHopOutcome.LinkUnavailable;

        var wire = node.Links.TryGetValue(to, out var edge)
            ? (ulong)edge.Cost : ulong.MaxValue;
        var radio = RadioCost(snapshot, from, to, out var domain);
        if (wire == ulong.MaxValue && radio is null)
            return RadioHopOutcome.LinkUnavailable;
        if (wire <= (ulong)(radio ?? uint.MaxValue))
            return RadioHopOutcome.TransmittedCable;

        lock (_airtimeLock)
        {
            long backgroundLimit = Math.Max(1,
                _bytesPerTick * (100 - interactiveReservePercent) / 100);
            if (_airtimeScope == RadioAirtimeScope.ChannelWide)
            {
                var slot = _airtime.TryGetValue(domain, out var existing)
                    ? existing : (Tick: -1L, Used: 0L, Background: 0L);
                if (slot.Tick > tick) return RadioHopOutcome.OldTick;
                if (bytes > _bytesPerTick) return RadioHopOutcome.PacketTooLarge;
                var used = slot.Tick == tick ? slot.Used : 0L;
                var background = slot.Tick == tick ? slot.Background : 0L;
                if (used + bytes > _bytesPerTick)
                    return RadioHopOutcome.ChannelBusy;
                if (trafficClass == PacketTrafficClass.Background &&
                    interactiveReservePercent != 0 &&
                    background + bytes > backgroundLimit)
                {
                    Interlocked.Increment(ref _protectedBackgroundDeferrals);
                    return RadioHopOutcome.ChannelBusy;
                }
                _airtime[domain] = (tick, used + bytes,
                    background + (trafficClass == PacketTrafficClass.Background ? bytes : 0));
                CountClassRadioHop(trafficClass, bytes);
                return RadioHopOutcome.TransmittedRadio;
            }

            if (tick < _spatialTick) return RadioHopOutcome.OldTick;
            if (bytes > _bytesPerTick) return RadioHopOutcome.PacketTooLarge;
            if (tick > _spatialTick)
            {
                _spatialTick = tick;
                _spatialAirtime.Clear();
            }

            // A wireless hop reserves the same payload in EVERY intersecting
            // coarse interference cell: two overlapping transmissions are
            // serialized even if their transmitter tiles differ. The swept
            // bounding rectangle is conservative and includes both endpoints'
            // configured coverage radii. No partial reservations are allowed.
            var fromPos = snapshot.Physical.Nodes[from].Position!.Value;
            var toPos = snapshot.Physical.Nodes[to].Position!.Value;
            int radius = Math.Max(snapshot.Devices[from].Range, snapshot.Devices[to].Range);
            long left = Math.Max(int.MinValue, Math.Min((long)fromPos.X, toPos.X) - radius);
            long right = Math.Min(int.MaxValue, Math.Max((long)fromPos.X, toPos.X) + radius);
            long top = Math.Max(int.MinValue, Math.Min((long)fromPos.Y, toPos.Y) - radius);
            long bottom = Math.Min(int.MaxValue, Math.Max((long)fromPos.Y, toPos.Y) + radius);
            int x0 = FloorCell((int)left), x1 = FloorCell((int)right);
            int y0 = FloorCell((int)top), y1 = FloorCell((int)bottom);

            for (long x = x0; x <= x1; x++)
            for (long y = y0; y <= y1; y++)
            {
                var cell = new RadioMediumCell(domain, (int)x, (int)y);
                _spatialAirtime.TryGetValue(cell, out var occupied);
                if (occupied.Used + bytes > _bytesPerTick)
                {
                    Interlocked.Increment(ref _spatialBusy);
                    return RadioHopOutcome.ChannelBusy;
                }
                if (trafficClass == PacketTrafficClass.Background &&
                    interactiveReservePercent != 0 &&
                    occupied.Background + bytes > backgroundLimit)
                {
                    Interlocked.Increment(ref _protectedBackgroundDeferrals);
                    Interlocked.Increment(ref _spatialBusy);
                    return RadioHopOutcome.ChannelBusy;
                }
            }
            long reservedCells = 0;
            for (long x = x0; x <= x1; x++)
            for (long y = y0; y <= y1; y++)
            {
                var cell = new RadioMediumCell(domain, (int)x, (int)y);
                _spatialAirtime.TryGetValue(cell, out var occupied);
                _spatialAirtime[cell] = (occupied.Used + bytes,
                    occupied.Background +
                        (trafficClass == PacketTrafficClass.Background ? bytes : 0));
                reservedCells++;
            }
            Interlocked.Increment(ref _spatialHops);
            Interlocked.Add(ref _spatialBytes, bytes);
            Interlocked.Add(ref _spatialCellReservations, reservedCells);
            CountClassRadioHop(trafficClass, bytes);
            return RadioHopOutcome.TransmittedRadio;
        }
    }

    private void CountClassRadioHop(PacketTrafficClass trafficClass, int bytes)
    {
        if (trafficClass == PacketTrafficClass.Interactive)
        {
            Interlocked.Increment(ref _interactiveHopTransmissions);
            Interlocked.Add(ref _interactiveHopBytes, bytes);
        }
        else
        {
            Interlocked.Increment(ref _backgroundHopTransmissions);
            Interlocked.Add(ref _backgroundHopBytes, bytes);
        }
    }

    /// <summary>
    /// Reserve shared bytes/tick on every radio hop atomically. A cable hop
    /// costs zero wireless airtime. Each domain shares one quota across devices.
    /// A route computed on an older snapshot can still be used if EVERY hop is
    /// valid under the current immutable snapshot. It may no longer be shortest;
    /// physical delivery requires reachability, not a new global route version.
    /// Tick is a nondecreasing caller-defined simulation counter.
    /// </summary>
    public bool TryTransmit(RouteResult route, int payloadBytes, long tick)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (payloadBytes < 1 || payloadBytes > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(payloadBytes));
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        var snapshot = Snapshot;
        var needed = new Dictionary<RadioDomainKey, long>();
        var needsRevalidation = route.Version != snapshot.Version;
        if (route.Version > snapshot.Version)
        {
            RegisterRejection(payloadBytes, ref _rejectedStaleRoute);
            return false;
        }
        if (!route.Found || route.Path is null || route.Path.Count == 0 ||
            route.Path.Count > snapshot.Physical.Nodes.Count)
        {
            RegisterRejection(payloadBytes, ref _rejectedInvalidPath);
            return false;
        }

        // Even a same-version route supplied to the public API must contain
        // existing endpoints. Avoid KeyNotFoundException when nodes are removed.
        foreach (var id in route.Path)
        {
            if (snapshot.Physical.Nodes.ContainsKey(id)) continue;
            if (needsRevalidation)
                RegisterRejection(payloadBytes, ref _rejectedStaleRoute);
            else
                RegisterRejection(payloadBytes, ref _rejectedInvalidPath);
            return false;
        }

        long radioHops = 0;
        for (var i = 1; i < route.Path.Count; i++)
        {
            var a = route.Path[i - 1]; var b = route.Path[i];
            if (a == b)
            {
                if (needsRevalidation)
                    RegisterRejection(payloadBytes, ref _rejectedStaleRoute);
                else
                    RegisterRejection(payloadBytes, ref _rejectedInvalidPath);
                return false;
            }
            var wired = snapshot.Physical.Nodes[a].Links.TryGetValue(b, out var edge)
                ? (ulong)edge.Cost : ulong.MaxValue;
            var wireless = RadioCost(snapshot, a, b, out var domain);
            if (wireless is null && wired == ulong.MaxValue)
            {
                if (needsRevalidation)
                    RegisterRejection(payloadBytes, ref _rejectedStaleRoute);
                else
                    RegisterRejection(payloadBytes, ref _rejectedInvalidPath);
                return false;
            }
            if (wireless is { } w && (ulong)w < wired)
            {
                radioHops++;
                needed.TryGetValue(domain, out var total);
                needed[domain] = checked(total + payloadBytes);
            }
        }

        Interlocked.Add(ref _attemptedWirelessHops, radioHops);
        lock (_airtimeLock)
        {
            // Never partially debit a multi-hop transfer.
            foreach (var (domain, bytes) in needed)
            {
                var slot = _airtime.TryGetValue(domain, out var existing)
                    ? existing : (Tick: -1L, Used: 0L, Background: 0L);
                if (slot.Tick > tick)
                {
                    RegisterRejection(payloadBytes, ref _rejectedOldTick);
                    return false;
                }
                // A single transfer consumes one airtime allocation PER radio hop.
                // Distinguish packets that can never fit from temporary saturation.
                if (bytes > _bytesPerTick)
                {
                    RegisterRejection(payloadBytes, ref _rejectedTooLarge);
                    return false;
                }
                if (slot.Tick == tick && slot.Used + bytes > _bytesPerTick)
                {
                    RegisterRejection(payloadBytes, ref _rejectedCapacity);
                    return false;
                }
            }
            foreach (var (domain, bytes) in needed)
            {
                var slot = _airtime.TryGetValue(domain, out var existing)
                    ? existing : (Tick: -1L, Used: 0L, Background: 0L);
                var already = slot.Tick == tick ? slot.Used : 0L;
                _airtime[domain] = (tick, already + bytes,
                    (slot.Tick == tick ? slot.Background : 0L) + bytes);
            }
            Interlocked.Increment(ref _accepted);
            Interlocked.Add(ref _acceptedBytes, payloadBytes);
            if (needsRevalidation)
                Interlocked.Increment(ref _acceptedRevalidated);
            return true;
        }
    }

    public IReadOnlyList<RadioMediumCellView> ViewCells(
        int minX, int minY, int maxX, int maxY, int z = 0, int limit = 1_000,
        SharedMediumSnapshot? pinnedSnapshot = null)
    {
        if (minX > maxX || minY > maxY) throw new ArgumentException("Invalid viewport.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var snapshot = pinnedSnapshot ?? Snapshot;
        var result = new List<RadioMediumCellView>();
        var clampedLimit = Math.Min(limit, 4_000);
        foreach (var (cell, occupants) in snapshot.Cells)
        {
            if (cell.Domain.Z != z) continue;
            long left = (long)cell.X * TopologyStore.CellSize;
            long top = (long)cell.Y * TopologyStore.CellSize;
            if (left > maxX || top > maxY ||
                left + TopologyStore.CellSize < minX ||
                top + TopologyStore.CellSize < minY) continue;
            result.Add(new RadioMediumCellView(cell.Domain.Channel, cell.Domain.Z,
                cell.X, cell.Y, occupants.Count));
            if (result.Count == clampedLimit) break;
        }
        return result;
    }

    private static IEnumerable<Edge> RadioNeighbors(SharedMediumSnapshot snapshot, NodeId id)
    {
        if (!snapshot.Devices.TryGetValue(id, out var profile)) yield break;
        var origin = snapshot.Physical.Nodes[id].Position!.Value;
        var domain = DomainOf(profile, origin);
        var fromX = FloorCell((int)Math.Clamp((long)origin.X - profile.Range,
            int.MinValue, int.MaxValue));
        var toX = FloorCell((int)Math.Clamp((long)origin.X + profile.Range,
            int.MinValue, int.MaxValue));
        var fromY = FloorCell((int)Math.Clamp((long)origin.Y - profile.Range,
            int.MinValue, int.MaxValue));
        var toY = FloorCell((int)Math.Clamp((long)origin.Y + profile.Range,
            int.MinValue, int.MaxValue));
        for (long x = fromX; x <= toX; x++)
        for (long y = fromY; y <= toY; y++)
        {
            var key = new RadioMediumCell(domain, (int)x, (int)y);
            if (!snapshot.Cells.TryGetValue(key, out var members)) continue;
            foreach (var candidate in members)
            {
                if (candidate == id ||
                    !snapshot.Devices.TryGetValue(candidate, out var other)) continue;
                var destination = snapshot.Physical.Nodes[candidate].Position!.Value;
                if (!Within(origin, destination, Math.Min(profile.Range, other.Range)))
                    continue;
                yield return new Edge(candidate, Math.Max(profile.Cost, other.Cost));
            }
        }
    }

    private static uint? RadioCost(SharedMediumSnapshot snapshot,
        NodeId a, NodeId b, out RadioDomainKey domain)
    {
        domain = default;
        if (!snapshot.Devices.TryGetValue(a, out var left) ||
            !snapshot.Devices.TryGetValue(b, out var right) ||
            left.Channel != right.Channel) return null;
        var p = snapshot.Physical.Nodes[a].Position!.Value;
        var q = snapshot.Physical.Nodes[b].Position!.Value;
        if (p.Z != q.Z ||
            !Within(p, q, Math.Min(left.Range, right.Range))) return null;
        domain = DomainOf(left, p);
        return Math.Max(left.Cost, right.Cost);
    }

    private static bool Within(NodePosition a, NodePosition b, int radius)
    {
        var dx = (long)a.X - b.X;
        var dy = (long)a.Y - b.Y;
        return Math.Abs(dx) <= radius && Math.Abs(dy) <= radius
            && dx * dx + dy * dy <= (long)radius * radius;
    }

    private void VerifyOwnership(SharedMediumSnapshot snapshot)
    {
        if (!ReferenceEquals(snapshot.Physical, _runtime.Topology.Snapshot))
            throw new InvalidOperationException(
                "Physical topology was changed outside SharedMediumNetwork. " +
                "Use this service to mutate its nodes and cables.");
    }

    private void PublishPhysical(SharedMediumSnapshot before, NodeId a, NodeId b)
    {
        var physical = _runtime.Topology.Snapshot;
        var next = before with
        {
            Version = checked(before.Version + 1),
            Physical = physical,
            HasPhysicalLinks = physical.Nodes.Values.Any(n => n.Links.Count != 0),
            Dense = before.Dense?.WithChanges(physical, before.Devices, before.Cells, [a, b])
        };
        _cache.Clear();
        Volatile.Write(ref _current, next);
    }

    private static RadioDomainKey DomainOf(RadioProfile profile, NodePosition pos) =>
        new(profile.Channel, pos.Z);

    private static RadioMediumCell CellOf(RadioProfile profile, NodePosition pos) =>
        new(DomainOf(profile, pos), FloorCell(pos.X), FloorCell(pos.Y));

    private static int FloorCell(int coordinate)
    {
        var quotient = Math.DivRem((long)coordinate, TopologyStore.CellSize, out var remainder);
        return checked((int)(quotient - (remainder < 0 ? 1 : 0)));
    }

    private static void AddToCell(
        ImmutableDictionary<RadioMediumCell, ImmutableHashSet<NodeId>>.Builder cells,
        NodeId id, RadioMediumCell cell)
    {
        cells[cell] = cells.TryGetValue(cell, out var members)
            ? members.Add(id) : ImmutableHashSet.Create(id);
    }

    private static void RemoveFromCell(
        ImmutableDictionary<RadioMediumCell, ImmutableHashSet<NodeId>>.Builder cells,
        NodeId id, RadioMediumCell cell)
    {
        if (!cells.TryGetValue(cell, out var members)) return;
        var remaining = members.Remove(id);
        if (remaining.IsEmpty) cells.Remove(cell);
        else cells[cell] = remaining;
    }

    private void RegisterRejection(int bytes, ref long reasonCounter)
    {
        Interlocked.Increment(ref reasonCounter);
        Interlocked.Increment(ref _rejected);
        Interlocked.Add(ref _rejectedBytes, bytes);
    }
}
