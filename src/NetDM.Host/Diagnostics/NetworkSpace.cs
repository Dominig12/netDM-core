using System.Globalization;
using System.Text;
using System.Text.Json;
using NetDM.Core;
using NetDM.Host.Transport;
using NetDM.Services;

namespace NetDM.Host.Diagnostics;

/// <summary>
/// P12.1 · what an operator knows of the network (docs/network-space-design.ru.md, decisions Р2, Р3, Р5).
/// An operator (the asking endpoint) learns the network only by scanning from a device's shell: the relays
/// within a few hops of that device, their cable ports, and the services the operator may discover there.
/// Nothing hidden is ever recorded, so it can never be sent. An item is fresh while scans confirm it; it
/// turns stale 60 s after its last confirmation, and at once when the route to it from the device the
/// operator last scanned from breaks. Knowledge lives in the Host for the round and is read as pages of
/// changes after a revision. All calls come from the one loop that answers Host commands.
/// </summary>
public static class NetworkSpace
{
    public const int DefaultRadius = 3, MaxRadius = 8, MaxScanNodes = 256, ScanEveryMs = 10_000, FreshMs = 60_000;
    public const int MaxOperators = 64, MaxNodes = 512, MaxLinks = 1024, MaxServices = 512, MaxRemovals = 256;
    public const int MaxPageItems = 64, MaxPageChars = 3600, DirectoryPages = 4;

    /// <summary>One thing an operator knows: a node "n|id|role|x|y|z", a link "l|a|b|state" or a service "s|instance|type|node".</summary>
    private sealed class Item
    {
        public string Key = "", Text = "";
        public char Kind;
        public NodeId First, Second;
        public long ConfirmedAt, Revision;
        public bool Broken;
    }

    private sealed class Knowledge
    {
        public readonly Dictionary<string, Item> Items = new(StringComparer.Ordinal);
        public readonly List<(long Revision, string Key)> Removed = [];
        public long RemovedBefore, NextScan, LastUsed, CheckedVersion = -1;
        public object? CheckedWiring;
        public NodeId? Host;
    }

    private static readonly Dictionary<EndpointKey, Knowledge> Operators = [];
    private static long _revision;

    internal static JsonElement Answer(GameNetworkService game, GameNetworkService.Session session, HostCommand command, JsonElement arguments)
    {
        var now = Environment.TickCount64;
        var knowledge = KnowledgeOf(command.InitiatorKey, now);
        return command.Command == "scan"
            ? Scan(game, session, command, knowledge, arguments.TryGetProperty("radius", out var radius)
                ? Math.Clamp(radius.GetInt32(), 1, MaxRadius) : DefaultRadius, now)
            : Read(game, session, knowledge, arguments.TryGetProperty("since", out var since) ? Math.Max(0, since.GetInt64()) : 0,
                arguments.TryGetProperty("limit", out var limit) ? Math.Clamp(limit.GetInt32(), 1, MaxPageItems) : MaxPageItems, now);
    }

    private static Knowledge KnowledgeOf(EndpointKey operatorKey, long now)
    {
        if (!Operators.TryGetValue(operatorKey, out var knowledge))
        {
            if (Operators.Count >= MaxOperators)
                Operators.Remove(Operators.MinBy(pair => pair.Value.LastUsed).Key); // The longest idle operator forgets.
            Operators[operatorKey] = knowledge = new Knowledge();
        }
        knowledge.LastUsed = now;
        return knowledge;
    }

    /// <summary>
    /// Scan (Р5): from the device whose shell answers, the relays within <paramref name="radius"/> hops, their
    /// cable ports, the links of service devices to them, and the services the operator may discover there.
    /// At most once in <see cref="ScanEveryMs"/> per operator.
    /// </summary>
    private static JsonElement Scan(GameNetworkService game, GameNetworkService.Session session, HostCommand command, Knowledge knowledge,
        int radius, long now)
    {
        if (now < knowledge.NextScan)
            return JsonSerializer.SerializeToElement(new
            {
                status = "busy", revision = _revision, nodes = 0, links = 0, services = 0,
                retryAfter = (int)Math.Ceiling((knowledge.NextScan - now) / 1000.0)
            });
        knowledge.NextScan = now + ScanEveryMs;
        var world = session.Medium.Snapshot;
        var wiring = game.Wiring;
        var host = command.ProviderNode;
        knowledge.Host = host;
        var hood = SharedMediumNetwork.Neighborhood(world, host, radius, MaxScanNodes, out var truncated);
        var nodes = world.Physical.Nodes;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // The services the operator may discover whose devices are in reach: read through its own delivery.
        var services = new List<DirectoryEntry>();
        string? after = null;
        for (var page = 0; page < DirectoryPages; page++)
        {
            if (session.Services.QueryDirectory(command.DeliveryId, null, after, 64) is not { } query || query.Origin != host)
                break;
            services.AddRange(query.Slice.Entries.Where(entry => hood.ContainsKey(entry.Node)));
            if (!query.Slice.More || query.Slice.Entries.Count == 0)
                break;
            after = query.Slice.Entries[^1].Instance;
        }

        void Node(NodeId id)
        {
            if (!nodes.TryGetValue(id, out var node) || node.Position is not { } at)
                return;
            var role = node.CanForward ? "relay" : "device";
            Upsert(knowledge, seen, $"n|{Id(id)}", $"n|{Id(id)}|{role}|{at.X}|{at.Y}|{at.Z}", 'n', id, id, now);
        }
        void Link(NodeId a, NodeId b, string state)
        {
            var (low, high) = a.Value < b.Value ? (a, b) : (b, a);
            Upsert(knowledge, seen, $"l|{Id(low)}|{Id(high)}", $"l|{Id(low)}|{Id(high)}|{state}", 'l', low, high, now);
        }

        Node(host);
        foreach (var (id, _) in hood)
            if (nodes[id].CanForward)
                Node(id);
        foreach (var (id, _) in hood)
        {
            if (!nodes[id].CanForward || NetworkDiagnostics.LinkState(world, wiring, id) is not { } ports)
                continue;
            // A cable port to a relay out of reach is known as a port, not as the node behind it.
            foreach (var neighbor in ports.Neighbors)
                if (hood.ContainsKey(neighbor.Node) || knowledge.Items.ContainsKey($"n|{Id(neighbor.Node)}"))
                    Link(id, neighbor.Node, neighbor.State);
        }
        foreach (var entry in services)
        {
            Node(entry.Node);
            if (!nodes[entry.Node].CanForward)
                foreach (var link in nodes[entry.Node].Links.Values.Where(link => hood.ContainsKey(link.To)))
                {
                    Node(link.To);
                    Link(entry.Node, link.To, "up");
                }
            Upsert(knowledge, seen, $"s|{entry.Instance}", $"s|{entry.Instance}|{entry.ServiceType}|{Id(entry.Node)}", 's', entry.Node,
                entry.Node, now);
        }

        // What was known in reach and is not there now went away; what is out of reach now is stale at once.
        foreach (var item in knowledge.Items.Values)
            if (!seen.Contains(item.Key) && !item.Broken && hood.ContainsKey(item.First) && (item.Kind != 'l' || hood.ContainsKey(item.Second)))
                Break(item);
        CheckRoutes(game, session, knowledge, force: true);
        Trim(knowledge);
        return JsonSerializer.SerializeToElement(new
        {
            status = truncated ? "partial" : "complete", revision = _revision,
            nodes = seen.Count(key => key[0] == 'n'), links = seen.Count(key => key[0] == 'l'), services = seen.Count(key => key[0] == 's')
        });
    }

    /// <summary>The operator's knowledge changed after <paramref name="since"/>, oldest change first.</summary>
    private static JsonElement Read(GameNetworkService game, GameNetworkService.Session session, Knowledge knowledge, long since, int limit,
        long now)
    {
        CheckRoutes(game, session, knowledge, force: false);
        if (since > 0 && since < knowledge.RemovedBefore)
            return JsonSerializer.SerializeToElement(new { status = "reset", revision = _revision, count = 0, items = "" });
        var changes = knowledge.Items.Values.Where(item => item.Revision > since).Select(item => (item.Revision, Line: Line(item, now)))
            .Concat(knowledge.Removed.Where(removal => removal.Revision > since).Select(removal => (removal.Revision, Line: "r|" + removal.Key)))
            .OrderBy(change => change.Revision).ToArray();
        var text = new StringBuilder();
        var count = 0;
        var upto = since;
        foreach (var (revision, line) in changes)
        {
            if (count == limit || text.Length + line.Length + 1 > MaxPageChars)
                break;
            if (text.Length > 0)
                text.Append(',');
            text.Append(line);
            count++;
            upto = revision;
        }
        var more = count < changes.Length;
        return JsonSerializer.SerializeToElement(new
        {
            status = more ? "partial" : "complete", revision = more ? upto : Math.Max(upto, changes.Length == 0 ? since : changes[^1].Revision),
            count, items = text.ToString()
        });
    }

    /// <summary>"…|age|fresh" or "…|age|stale": the age in seconds since the item was last confirmed.</summary>
    private static string Line(Item item, long now)
    {
        var age = Math.Max(0, (now - item.ConfirmedAt) / 1000);
        var fresh = !item.Broken && now - item.ConfirmedAt < FreshMs;
        return $"{item.Text}|{age.ToString(CultureInfo.InvariantCulture)}|{(fresh ? "fresh" : "stale")}";
    }

    private static void Upsert(Knowledge knowledge, HashSet<string> seen, string key, string text, char kind, NodeId first, NodeId second,
        long now)
    {
        if (!seen.Add(key))
            return;
        if (!knowledge.Items.TryGetValue(key, out var item))
            knowledge.Items[key] = item = new Item { Key = key, Kind = kind, First = first, Second = second };
        if (item.Text != text || item.Broken || item.Revision == 0 || now - item.ConfirmedAt >= FreshMs)
            item.Revision = ++_revision; // Changed, or fresh again: a reader learns it.
        item.Text = text;
        item.Broken = false;
        item.ConfirmedAt = now;
    }

    private static void Break(Item item)
    {
        item.Broken = true;
        item.Revision = ++_revision;
    }

    /// <summary>
    /// Р3: what the operator holds as fresh and the device it last scanned from no longer reaches is stale at
    /// once. One bounded search, only when the world or its wiring changed since the last check.
    /// </summary>
    private static void CheckRoutes(GameNetworkService game, GameNetworkService.Session session, Knowledge knowledge, bool force)
    {
        var world = session.Medium.Snapshot;
        var wiring = game.Wiring;
        if (knowledge.Host is not { } host || (!force && world.Version == knowledge.CheckedVersion && ReferenceEquals(wiring, knowledge.CheckedWiring)))
            return;
        knowledge.CheckedVersion = world.Version;
        knowledge.CheckedWiring = wiring;
        var now = Environment.TickCount64;
        var live = knowledge.Items.Values.Where(item => !item.Broken && now - item.ConfirmedAt < FreshMs).ToArray();
        if (live.Length == 0)
            return;
        var targets = live.SelectMany(item => new[] { item.First, item.Second }).Where(node => node != host).ToHashSet();
        var reached = SharedMediumNetwork.ReachAll(world, host, targets, QueryHost.ReachBudget, out var exhausted);
        if (exhausted)
            return; // A search that ran out proves nothing about what it did not reach.
        bool Reached(NodeId node) => node == host || reached.ContainsKey(node);
        foreach (var item in live)
            if (!Reached(item.First) || !Reached(item.Second))
                Break(item);
    }

    /// <summary>Over its bounds, an operator forgets the oldest stale items first; a reader is told what went.</summary>
    private static void Trim(Knowledge knowledge)
    {
        foreach (var (kind, max) in new[] { ('n', MaxNodes), ('l', MaxLinks), ('s', MaxServices) })
        {
            var items = knowledge.Items.Values.Where(item => item.Kind == kind).ToArray();
            if (items.Length <= max)
                continue;
            foreach (var item in items.OrderBy(item => item.Broken ? 0 : 1).ThenBy(item => item.ConfirmedAt).Take(items.Length - max))
            {
                knowledge.Items.Remove(item.Key);
                knowledge.Removed.Add((++_revision, item.Key));
            }
        }
        if (knowledge.Removed.Count > MaxRemovals)
        {
            var dropped = knowledge.Removed.Count - MaxRemovals;
            knowledge.RemovedBefore = knowledge.Removed[dropped - 1].Revision + 1; // Older readers start over ("reset").
            knowledge.Removed.RemoveRange(0, dropped);
        }
    }

    private static string Id(NodeId node) => node.Value.ToString(CultureInfo.InvariantCulture);
}
