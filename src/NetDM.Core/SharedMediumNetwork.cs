using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;

namespace NetDM.Core;

/// <summary>A shared airtime budget is scoped to one channel and one Z-level.</summary>
public readonly record struct RadioDomainKey(uint Channel, int Z);

/// <summary>Grid cell carrying radio devices on one channel / Z-level.</summary>
public readonly record struct RadioMediumCell(RadioDomainKey Domain, int X, int Y);

/// <summary>
/// A coherent view of the physical graph and spatial radio membership.
/// It is published as a single immutable object, so route readers never mix
/// pre-move wireless membership with post-move physical positions.
/// </summary>
public sealed record SharedMediumSnapshot(
    long Version,
    TopologySnapshot Physical,
    ImmutableDictionary<NodeId, RadioProfile> Devices,
    ImmutableDictionary<RadioMediumCell, ImmutableHashSet<NodeId>> Cells);

public readonly record struct SharedMediumUpdateResult(
    long Version, long PhysicalVersion, int Moves, int ChangedRadios, int IndexedCells);

public readonly record struct SharedMediumStatistics(
    long Version, long PhysicalVersion, int ConfiguredDevices,
    int Domains, int OccupiedCells, long PairwiseRadioEdges,
    long AcceptedTransfers, long RejectedTransfers,
    long AcceptedBytes, long RejectedBytes);

public readonly record struct RadioMediumCellView(
    uint Channel, int Z, int X, int Y, int Count);

/// <summary>
/// On-demand radio connectivity, without writing O(n²) physical graph edges.
/// This layer owns device positions/profiles and cable changes while it is active.
/// The cell grid is persistent immutable data; route queries are lock-free.
/// </summary>
public sealed class SharedMediumNetwork
{
    private readonly NetworkRuntime _runtime;
    private readonly object _writer = new();
    private readonly object _airtimeLock = new();
    private readonly RouteCache _cache = new(8_192);
    private readonly Dictionary<RadioDomainKey, (long Tick, long Used)> _airtime = new();
    private readonly long _bytesPerTick;
    private SharedMediumSnapshot _current;
    private long _accepted, _rejected, _acceptedBytes, _rejectedBytes;

    public SharedMediumNetwork(NetworkRuntime runtime, long bytesPerTick = 8_192)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (bytesPerTick is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(bytesPerTick));
        _runtime = runtime;
        _bytesPerTick = bytesPerTick;
        _current = new SharedMediumSnapshot(0, runtime.Topology.Snapshot,
            ImmutableDictionary<NodeId, RadioProfile>.Empty,
            ImmutableDictionary<RadioMediumCell, ImmutableHashSet<NodeId>>.Empty);
    }

    public long BytesPerTick => _bytesPerTick;
    public SharedMediumSnapshot Snapshot => Volatile.Read(ref _current);

    public SharedMediumStatistics Stats()
    {
        var snapshot = Snapshot;
        var domains = snapshot.Devices
            .Select(pair => DomainOf(pair.Value, snapshot.Physical.Nodes[pair.Key].Position!.Value))
            .ToHashSet();
        return new SharedMediumStatistics(snapshot.Version, snapshot.Physical.Version,
            snapshot.Devices.Count, domains.Count, snapshot.Cells.Count, 0,
            Interlocked.Read(ref _accepted), Interlocked.Read(ref _rejected),
            Interlocked.Read(ref _acceptedBytes), Interlocked.Read(ref _rejectedBytes));
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
                    candidate.Validate();

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
            var next = new SharedMediumSnapshot(checked(before.Version + 1), physical,
                devices.ToImmutable(), cells.ToImmutable());
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
            PublishPhysical(before);
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
            var next = new SharedMediumSnapshot(checked(before.Version + 1),
                _runtime.Topology.Snapshot, devices.ToImmutable(), cells.ToImmutable());
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
        var snapshot = Snapshot;
        RouteResult cachedResult = null!;
        var eligible = maxExplored == 100_000;
        var cached = eligible &&
            _cache.TryGet(snapshot.Version, source, destination, out cachedResult);
        var result = cached ? cachedResult :
            RouteCore(snapshot, source, destination, maxExplored, cancellationToken);
        if (!cached && eligible && result.Explored <= maxExplored)
            _cache.Store(snapshot.Version, result, source, destination);
        _runtime.RecordExternalRoute(result, cached, Stopwatch.GetTimestamp() - started);
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
    /// Reserve shared bytes/tick on every radio hop atomically. A cable hop
    /// costs zero wireless airtime. Each domain shares one quota across devices.
    /// Tick is a nondecreasing caller-defined simulation counter.
    /// </summary>
    public bool TryTransmit(RouteResult route, int payloadBytes, long tick)
    {
        if (payloadBytes < 1 || payloadBytes > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(payloadBytes));
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        var snapshot = Snapshot;
        var needed = new Dictionary<RadioDomainKey, long>();
        if (!route.Found || route.Version != snapshot.Version)
        {
            RegisterRejection(payloadBytes);
            return false;
        }

        for (var i = 1; i < route.Path.Count; i++)
        {
            var a = route.Path[i - 1]; var b = route.Path[i];
            var wired = snapshot.Physical.Nodes[a].Links.TryGetValue(b, out var edge)
                ? (ulong)edge.Cost : ulong.MaxValue;
            var wireless = RadioCost(snapshot, a, b, out var domain);
            if (wireless is null && wired == ulong.MaxValue)
            {
                RegisterRejection(payloadBytes);
                return false;
            }
            if (wireless is { } w && (ulong)w < wired)
            {
                needed.TryGetValue(domain, out var total);
                needed[domain] = checked(total + payloadBytes);
            }
        }

        lock (_airtimeLock)
        {
            // Never partially debit a multi-hop transfer.
            foreach (var (domain, bytes) in needed)
            {
                var slot = _airtime.TryGetValue(domain, out var existing)
                    ? existing : (Tick: -1L, Used: 0L);
                if (slot.Tick > tick || bytes > _bytesPerTick ||
                    (slot.Tick == tick && slot.Used + bytes > _bytesPerTick))
                {
                    RegisterRejection(payloadBytes);
                    return false;
                }
            }
            foreach (var (domain, bytes) in needed)
            {
                var slot = _airtime.TryGetValue(domain, out var existing)
                    ? existing : (Tick: -1L, Used: 0L);
                var already = slot.Tick == tick ? slot.Used : 0L;
                _airtime[domain] = (tick, already + bytes);
            }
            Interlocked.Increment(ref _accepted);
            Interlocked.Add(ref _acceptedBytes, payloadBytes);
            return true;
        }
    }

    public IReadOnlyList<RadioMediumCellView> ViewCells(
        int minX, int minY, int maxX, int maxY, int z = 0, int limit = 1_000)
    {
        if (minX > maxX || minY > maxY) throw new ArgumentException("Invalid viewport.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var snapshot = Snapshot;
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

    private void PublishPhysical(SharedMediumSnapshot before)
    {
        var next = before with
        {
            Version = checked(before.Version + 1),
            Physical = _runtime.Topology.Snapshot
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

    private void RegisterRejection(int bytes)
    {
        Interlocked.Increment(ref _rejected);
        Interlocked.Add(ref _rejectedBytes, bytes);
    }
}
