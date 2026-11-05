using System.Collections.Immutable;
using System.Threading;

namespace NetDM.Core;

public readonly record struct NodeId(ulong Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct Edge(NodeId To, uint Cost);
public readonly record struct NodePosition(int X, int Y, int Z = 0);
public readonly record struct GridCell(int X, int Y, int Z);

public sealed record Node(NodeId Id, ImmutableDictionary<NodeId, Edge> Links, NodePosition? Position)
{
    public static Node Create(NodeId id, NodePosition? position = null) =>
        new(id, ImmutableDictionary<NodeId, Edge>.Empty, position);
}

public sealed record TopologySnapshot(
    long Version,
    ImmutableDictionary<NodeId, Node> Nodes,
    ImmutableDictionary<GridCell, ImmutableHashSet<NodeId>> SpatialCells)
{
    public static TopologySnapshot Empty { get; } = new(
        0, ImmutableDictionary<NodeId, Node>.Empty,
        ImmutableDictionary<GridCell, ImmutableHashSet<NodeId>>.Empty);
}

public enum TopologyOperation { AddNode, RemoveNode, Connect, Disconnect, MoveNode }

public readonly record struct TopologyChange(
    TopologyOperation Operation,
    NodeId A,
    NodeId B = default,
    uint Cost = 1,
    NodePosition? Position = null);

public readonly record struct BatchResult(long Version, int Applied, int Skipped);

/// <summary>
/// Mutations are serialized; readers pin a consistent immutable snapshot without locks.
/// Batch operations publish exactly one new version, rather than one per node or edge.
/// </summary>
public sealed class TopologyStore
{
    public const int CellSize = 32;
    private readonly object _writerGate = new();
    private TopologySnapshot _current = TopologySnapshot.Empty;
    public TopologySnapshot Snapshot => Volatile.Read(ref _current);

    public bool AddNode(NodeId id) => ApplyBatch(
        [new TopologyChange(TopologyOperation.AddNode, id)]).Applied != 0;
    public bool RemoveNode(NodeId id) => ApplyBatch(
        [new TopologyChange(TopologyOperation.RemoveNode, id)]).Applied != 0;
    public bool Connect(NodeId left, NodeId right, uint cost = 1) => ApplyBatch(
        [new TopologyChange(TopologyOperation.Connect, left, right, cost)]).Applied != 0;
    public bool Disconnect(NodeId left, NodeId right) => ApplyBatch(
        [new TopologyChange(TopologyOperation.Disconnect, left, right)]).Applied != 0;
    public bool MoveNode(NodeId id, NodePosition position) => ApplyBatch(
        [new TopologyChange(TopologyOperation.MoveNode, id, Position: position)]).Applied != 0;

    /// <summary>
    /// Commands are applied in order. Missing/duplicate entities are no-ops.
    /// Invalid IDs, costs and self-edges reject the whole batch before it is applied.
    /// </summary>
    public BatchResult ApplyBatch(IReadOnlyList<TopologyChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        foreach (var change in changes)
        {
            if (change.A.Value == 0)
                throw new ArgumentOutOfRangeException(nameof(changes), "Node IDs must be positive.");
            if (change.Operation == TopologyOperation.Connect &&
                (change.B.Value == 0 || change.A == change.B || change.Cost == 0))
                throw new ArgumentException("A link needs two distinct IDs and a positive cost.", nameof(changes));
            if (change.Operation == TopologyOperation.Disconnect && change.B.Value == 0)
                throw new ArgumentException("A link needs two positive IDs.", nameof(changes));
            if (change.Operation == TopologyOperation.MoveNode && change.Position is null)
                throw new ArgumentException("A move needs a position.", nameof(changes));
        }

        lock (_writerGate)
        {
            var current = Snapshot;
            var nodes = current.Nodes.ToBuilder();
            var cells = current.SpatialCells.ToBuilder();
            int applied = 0;

            foreach (var change in changes)
            {
                switch (change.Operation)
                {
                    case TopologyOperation.AddNode:
                        if (nodes.ContainsKey(change.A)) break;
                        nodes.Add(change.A, Node.Create(change.A, change.Position));
                        if (change.Position is { } newPosition)
                            AddToCell(cells, change.A, newPosition);
                        applied++;
                        break;

                    case TopologyOperation.RemoveNode:
                        if (!nodes.TryGetValue(change.A, out var removed)) break;
                        foreach (var neighbor in removed.Links.Keys)
                        {
                            if (nodes.TryGetValue(neighbor, out var other))
                                nodes[neighbor] = other with { Links = other.Links.Remove(change.A) };
                        }
                        if (removed.Position is { } oldPosition)
                            RemoveFromCell(cells, change.A, oldPosition);
                        nodes.Remove(change.A);
                        applied++;
                        break;

                    case TopologyOperation.Connect:
                        if (!nodes.TryGetValue(change.A, out var left) ||
                            !nodes.TryGetValue(change.B, out var right)) break;
                        if (left.Links.TryGetValue(change.B, out var oldEdge) &&
                            oldEdge.Cost == change.Cost) break;
                        nodes[change.A] = left with
                        {
                            Links = left.Links.SetItem(change.B, new Edge(change.B, change.Cost))
                        };
                        nodes[change.B] = right with
                        {
                            Links = right.Links.SetItem(change.A, new Edge(change.A, change.Cost))
                        };
                        applied++;
                        break;

                    case TopologyOperation.Disconnect:
                        if (!nodes.TryGetValue(change.A, out var from) ||
                            !nodes.TryGetValue(change.B, out var to) ||
                            !from.Links.ContainsKey(change.B)) break;
                        nodes[change.A] = from with { Links = from.Links.Remove(change.B) };
                        nodes[change.B] = to with { Links = to.Links.Remove(change.A) };
                        applied++;
                        break;

                    case TopologyOperation.MoveNode:
                        if (!nodes.TryGetValue(change.A, out var moving) ||
                            moving.Position == change.Position) break;
                        if (moving.Position is { } previous)
                            RemoveFromCell(cells, change.A, previous);
                        AddToCell(cells, change.A, change.Position!.Value);
                        nodes[change.A] = moving with { Position = change.Position };
                        applied++;
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(changes), "Unknown topology operation.");
                }
            }

            if (applied == 0) return new BatchResult(current.Version, 0, changes.Count);
            var next = new TopologySnapshot(
                checked(current.Version + 1), nodes.ToImmutable(), cells.ToImmutable());
            Volatile.Write(ref _current, next);
            return new BatchResult(next.Version, applied, changes.Count - applied);
        }
    }

    /// <summary>Empty the graph while preserving monotonic topology versions.</summary>
    public long Clear()
    {
        lock (_writerGate)
        {
            var current = Snapshot;
            var next = TopologySnapshot.Empty with { Version = checked(current.Version + 1) };
            Volatile.Write(ref _current, next);
            return next.Version;
        }
    }

    /// <summary>O(number of occupied spatial cells in viewport) for bounded viewports.</summary>
    public static IReadOnlyList<Node> QueryViewport(TopologySnapshot snapshot,
        int minX, int minY, int maxX, int maxY, int z = 0, int limit = 4000)
    {
        if (minX > maxX || minY > maxY) throw new ArgumentException("Invalid viewport.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        limit = Math.Min(limit, 20_000);
        var found = new List<Node>(Math.Min(limit, 1024));
        long minCellX = FloorCell(minX), minCellY = FloorCell(minY);
        long maxCellX = FloorCell(maxX), maxCellY = FloorCell(maxY);
        var cellCount = (maxCellX - minCellX + 1) * (maxCellY - minCellY + 1);
        if (cellCount > 16_384)
        {
            // Large zoom-out: scan nodes once, rather than millions of empty cells.
            foreach (var node in snapshot.Nodes.Values)
            {
                if (Visible(node)) { found.Add(node); if (found.Count == limit) break; }
            }
            return found;
        }

        for (long x = minCellX; x <= maxCellX; x++)
        for (long y = minCellY; y <= maxCellY; y++)
        {
            if (!snapshot.SpatialCells.TryGetValue(new GridCell((int)x, (int)y, z), out var ids))
                continue;
            foreach (var id in ids)
            {
                var node = snapshot.Nodes[id];
                if (Visible(node)) { found.Add(node); if (found.Count == limit) return found; }
            }
        }
        return found;

        bool Visible(Node n) =>
            n.Position is { } p && p.Z == z &&
            p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY;
    }

    private static int FloorCell(int value) =>
        value >= 0 ? value / CellSize : (value + 1) / CellSize - 1;

    private static void AddToCell(
        ImmutableDictionary<GridCell, ImmutableHashSet<NodeId>>.Builder cells,
        NodeId id, NodePosition p)
    {
        var cell = new GridCell(FloorCell(p.X), FloorCell(p.Y), p.Z);
        cells[cell] = cells.TryGetValue(cell, out var ids)
            ? ids.Add(id) : ImmutableHashSet.Create(id);
    }

    private static void RemoveFromCell(
        ImmutableDictionary<GridCell, ImmutableHashSet<NodeId>>.Builder cells,
        NodeId id, NodePosition p)
    {
        var cell = new GridCell(FloorCell(p.X), FloorCell(p.Y), p.Z);
        if (!cells.TryGetValue(cell, out var ids)) return;
        var updated = ids.Remove(id);
        if (updated.IsEmpty) cells.Remove(cell);
        else cells[cell] = updated;
    }
}
