using System.Collections.Immutable;
using System.Threading;

namespace NetDM.Core;

public readonly record struct NodeId(ulong Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct Edge(NodeId To, uint Cost);

public sealed record Node(NodeId Id, ImmutableDictionary<NodeId, Edge> Links)
{
    public static Node Create(NodeId id) => new(id, ImmutableDictionary<NodeId, Edge>.Empty);
}

public sealed record TopologySnapshot(long Version, ImmutableDictionary<NodeId, Node> Nodes)
{
    public static TopologySnapshot Empty { get; } =
        new(0, ImmutableDictionary<NodeId, Node>.Empty);
}

/// <summary>
/// Single-writer, multi-reader graph. Mutations publish structurally shared, immutable snapshots.
/// Reads do not hold the writer lock and never observe half-applied changes.
/// </summary>
public sealed class TopologyStore
{
    private readonly object _writerGate = new();
    private TopologySnapshot _current = TopologySnapshot.Empty;

    public TopologySnapshot Snapshot => Volatile.Read(ref _current);

    public bool AddNode(NodeId id)
    {
        if (id.Value == 0) throw new ArgumentOutOfRangeException(nameof(id), "Node IDs must be positive.");
        lock (_writerGate)
        {
            var current = Snapshot;
            if (current.Nodes.ContainsKey(id)) return false;
            Publish(current, current.Nodes.Add(id, Node.Create(id)));
            return true;
        }
    }

    public bool RemoveNode(NodeId id)
    {
        lock (_writerGate)
        {
            var current = Snapshot;
            if (!current.Nodes.TryGetValue(id, out var node)) return false;

            var nodes = current.Nodes.Remove(id);
            foreach (var neighbor in node.Links.Keys)
            {
                if (nodes.TryGetValue(neighbor, out var linked))
                    nodes = nodes.SetItem(neighbor, linked with { Links = linked.Links.Remove(id) });
            }
            Publish(current, nodes);
            return true;
        }
    }

    public bool Connect(NodeId left, NodeId right, uint cost = 1)
    {
        if (left == right) throw new ArgumentException("Self-links are not supported.");
        if (cost == 0) throw new ArgumentOutOfRangeException(nameof(cost), "Cost must be positive.");
        lock (_writerGate)
        {
            var current = Snapshot;
            if (!current.Nodes.TryGetValue(left, out var a) ||
                !current.Nodes.TryGetValue(right, out var b))
                return false;

            if (a.Links.TryGetValue(right, out var existing) && existing.Cost == cost)
                return false;

            var nodes = current.Nodes
                .SetItem(left, a with { Links = a.Links.SetItem(right, new Edge(right, cost)) })
                .SetItem(right, b with { Links = b.Links.SetItem(left, new Edge(left, cost)) });
            Publish(current, nodes);
            return true;
        }
    }

    public bool Disconnect(NodeId left, NodeId right)
    {
        lock (_writerGate)
        {
            var current = Snapshot;
            if (!current.Nodes.TryGetValue(left, out var a) ||
                !current.Nodes.TryGetValue(right, out var b) ||
                !a.Links.ContainsKey(right))
                return false;

            var nodes = current.Nodes
                .SetItem(left, a with { Links = a.Links.Remove(right) })
                .SetItem(right, b with { Links = b.Links.Remove(left) });
            Publish(current, nodes);
            return true;
        }
    }

    private void Publish(TopologySnapshot old, ImmutableDictionary<NodeId, Node> nodes) =>
        Volatile.Write(ref _current, new TopologySnapshot(checked(old.Version + 1), nodes));
}
