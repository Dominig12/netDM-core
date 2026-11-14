using System.Globalization;
using System.Text;
using System.Text.Json;
using NetDM.Core;
using NetDM.Host.Transport;
using NetDM.Services;
using NetDM.Services.Shell;

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

    // ---- P12.2 · presence (decisions Р1, Р6) ---------------------------------------------------------
    //
    // An operator's presence is its one network entity: it stands on a device's shell (its host) and acts
    // from there with the operator's rights, while the operator controls it over the network. The rules of
    // Р6: an operator who closes the window leaves it suspended and ends it after 5 minutes away; a lost
    // entry endpoint ends it after 60 s; an unpowered host suspends it; a host that leaves ends it. A new
    // runtime gives both ends 60 s to come back. Presences live in this Host process.

    public const int OperatorAwayMs = 300_000, EntryGraceMs = 60_000, PresencePassMs = 500, MaxPresences = 64, MaxPresencesPerHost = 4;
    /// <summary>Р4: a move takes this long per hop of its route, at least one hop's time and at most <see cref="MaxTravelMs"/>.</summary>
    public const int TravelMsPerHop = 1000, MaxTravelMs = 15_000;

    private sealed class Presence
    {
        public int Id, Generation = 1;
        public EndpointKey Owner, Provider;
        public string Instance = "", State = "active", Reason = "";
        public NodeId Node;
        public bool Away, Detached;
        public long AwaySince, EntryLostSince, DetachedSince;
        public bool Ended => State == "ended";
        /// <summary>P12.3: on its way to another host. It runs nowhere: the source is fenced, the target not yet active.</summary>
        public bool Moving;
        public string ToInstance = "";
        public EndpointKey ToProvider;
        public NodeId ToNode;
        public long ArriveAt;
        /// <summary>What the last move ended in, shown as the reason while nothing else is.</summary>
        public string Notice = "";
    }

    private static readonly Dictionary<EndpointKey, Presence> Presences = [];
    private static int _nextPresence;
    private static long _nextPresencePass, _actSerial;
    private static Guid _presenceStream;

    /// <summary>The presence pass: endpoints, power and the operator's absence decide its state (Р6).</summary>
    public static void Tick(GameNetworkService game)
    {
        var now = Environment.TickCount64;
        if (now < _nextPresencePass || Presences.Count == 0)
            return;
        _nextPresencePass = now + PresencePassMs;
        var session = game.CaptureSession();
        if (_presenceStream != session.Packets.ReceiptStreamId)
        {
            _presenceStream = session.Packets.ReceiptStreamId;
            foreach (var presence in Presences.Values.Where(presence => !presence.Ended))
            {
                presence.Detached = true; // A new runtime: both ends get time to come back with the world's facts.
                presence.DetachedSince = now;
            }
        }
        var power = game.CapturePowerState(session);
        foreach (var presence in Presences.Values.Where(presence => !presence.Ended))
            Settle(session, power, presence, now);
    }

    private static void Settle(GameNetworkService.Session session, (SharedMediumSnapshot World, WiringReport Wiring)? power, Presence presence,
        long now)
    {
        if (presence.Moving && !Travel(session, presence, now))
            return;
        if (session.Services.DiscoverableProvider(presence.Provider, presence.Instance) is not { } host || host.Provider != presence.Provider)
        {
            if (presence.Detached && now - presence.DetachedSince < EntryGraceMs)
                Suspend(presence, "host_unreachable");
            else
                End(presence, "host_lost"); // No copy anywhere: a host that is gone ends its presences.
            return;
        }
        presence.Node = host.Node;
        if (!session.Services.IsAttached(presence.Owner))
        {
            if (presence.EntryLostSince == 0)
                presence.EntryLostSince = now;
            if (now - presence.EntryLostSince >= EntryGraceMs)
                End(presence, "entry_lost");
            else
                Suspend(presence, "entry_lost");
            return;
        }
        presence.EntryLostSince = 0;
        presence.Detached = false;
        if (power is { } state && (!state.World.Physical.Nodes.ContainsKey(host.Node) || !state.Wiring.CanExecute(host.Node)))
            Suspend(presence, "host_unpowered");
        else if (presence.Away && now - presence.AwaySince >= OperatorAwayMs)
            End(presence, "operator_gone");
        else if (presence.Away)
            Suspend(presence, "operator_away");
        else
        {
            presence.State = "active";
            presence.Reason = presence.Notice;
        }
    }

    /// <summary>
    /// Р4 in transit: the route from the source to the target must hold all the way. A broken route, or a target
    /// that left, sends the presence back to its source if the source is still there; otherwise it is lost. On
    /// arrival the target becomes its host with the next generation. False while it is still on its way.
    /// </summary>
    private static bool Travel(GameNetworkService.Session session, Presence presence, long now)
    {
        var target = session.Services.DiscoverableProvider(presence.ToProvider, presence.ToInstance);
        var holds = target is { } there && there.Provider == presence.ToProvider && (presence.Node == there.Node ||
            NetworkDiagnostics.Trace(session.Medium.Snapshot, presence.Node, there.Node, QueryHost.ReachBudget).State == NetworkDiagnostics.Reachable);
        if (holds && now < presence.ArriveAt)
        {
            presence.State = "moving";
            presence.Reason = "";
            return false;
        }
        presence.Moving = false;
        if (holds)
        {
            presence.Instance = presence.ToInstance;
            presence.Provider = presence.ToProvider;
            presence.Node = target!.Value.Node;
            presence.Generation++;
            presence.Notice = "";
            return true;
        }
        if (session.Services.DiscoverableProvider(presence.Provider, presence.Instance) is { } source && source.Provider == presence.Provider)
        {
            presence.Notice = "returned: route_lost";
            return true; // Back where it left; the rest of the pass decides whether it may act there.
        }
        End(presence, "lost_in_transit");
        return false;
    }

    /// <summary>
    /// <c>move {to}</c> (Р4): the presence leaves its host for another device's shell that the operator may
    /// see and call, over a route that exists now. It is on its way for one second per hop, at most 15 s.
    /// </summary>
    private static JsonElement Move(GameNetworkService game, GameNetworkService.Session session, HostCommand command, JsonElement arguments,
        long now)
    {
        if (Controlled(game, session, command, now, out var refusal) is not { } presence)
        {
            Presences.TryGetValue(command.InitiatorKey, out var current);
            return JsonSerializer.SerializeToElement(Describe(refusal!, current is { Ended: false } ? current : null));
        }
        var to = arguments.GetProperty("to").GetString() ?? "";
        if (!ServiceNames.IsValid(to) || to == presence.Instance)
            return JsonSerializer.SerializeToElement(Describe("invalid_argument", presence));
        if (session.Services.DiscoverableProvider(presence.Owner, to) is not { ServiceType: ShellHost.ServiceType } target ||
            session.Services.ResidentCommandNode(presence.Owner, target.Provider, to) is null)
            return JsonSerializer.SerializeToElement(Describe("forbidden", presence));
        var route = NetworkDiagnostics.Trace(session.Medium.Snapshot, presence.Node, target.Node, QueryHost.ReachBudget);
        if (route.State != NetworkDiagnostics.Reachable)
            return JsonSerializer.SerializeToElement(Describe("unreachable", presence));
        if (Presences.Values.Count(other => !other.Ended && (other.Moving ? other.ToInstance : other.Instance) == to) >= MaxPresencesPerHost)
            return JsonSerializer.SerializeToElement(Describe("limit_exceeded", presence));
        var travel = Math.Clamp(route.Hops * TravelMsPerHop, TravelMsPerHop, MaxTravelMs);
        presence.Moving = true;
        presence.ToInstance = to;
        presence.ToProvider = target.Provider;
        presence.ToNode = target.Node;
        presence.ArriveAt = now + travel;
        presence.Notice = "";
        presence.State = "moving";
        presence.Reason = "";
        return JsonSerializer.SerializeToElement(Describe("moving", presence));
    }

    private static void Suspend(Presence presence, string reason)
    {
        presence.State = "suspended";
        presence.Reason = reason;
    }

    private static void End(Presence presence, string reason)
    {
        presence.State = "ended";
        presence.Reason = reason;
    }

    private static object Describe(string status, Presence? presence) => presence is null
        ? new { status }
        : presence.Moving
            ? new
            {
                status, presence = presence.Id, host = "", generation = presence.Generation, state = presence.State, reason = presence.Reason,
                to = presence.ToInstance, arriveIn = (int)Math.Max(0, Math.Ceiling((presence.ArriveAt - Environment.TickCount64) / 1000.0))
            }
            : (object)new
            {
                status, presence = presence.Id, host = presence.Instance, generation = presence.Generation, state = presence.State,
                reason = presence.Reason
            };

    private static JsonElement PresenceCommand(GameNetworkService game, GameNetworkService.Session session, HostCommand command, long now)
    {
        Presences.TryGetValue(command.InitiatorKey, out var presence);
        if (presence is not null && presence.Ended && command.Command is "enter")
            presence = null; // An ended presence is reported until the operator enters again.
        switch (command.Command)
        {
            case "presence":
                if (presence is not null)
                    Settle(session, game.CapturePowerState(session), presence, now);
                return JsonSerializer.SerializeToElement(Describe(presence is null ? "none" : presence.Ended ? "ended" : "present", presence));
            case "enter":
                if (presence is not null)
                {
                    // One presence per operator: entering again resumes it where it stands.
                    var resumed = presence.Away;
                    presence.Away = false;
                    Settle(session, game.CapturePowerState(session), presence, now);
                    return JsonSerializer.SerializeToElement(Describe(resumed ? "resumed" : "present", presence));
                }
                if (Presences.Values.Count(other => !other.Ended) >= MaxPresences ||
                    Presences.Values.Count(other => !other.Ended && other.Instance == command.Instance) >= MaxPresencesPerHost)
                    return JsonSerializer.SerializeToElement(Describe("limit_exceeded", null));
                foreach (var ended in Presences.Where(pair => pair.Value.Ended).Select(pair => pair.Key).ToArray())
                    if (Presences.Count >= MaxPresences)
                        Presences.Remove(ended);
                presence = new Presence
                {
                    Id = ++_nextPresence, Owner = command.InitiatorKey, Provider = command.ProviderKey, Instance = command.Instance,
                    Node = command.ProviderNode
                };
                Presences[command.InitiatorKey] = presence;
                Settle(session, game.CapturePowerState(session), presence, now);
                return JsonSerializer.SerializeToElement(Describe("entered", presence));
            case "detach":
                if (presence is null || presence.Ended)
                    return JsonSerializer.SerializeToElement(Describe(presence is null ? "none" : "ended", presence));
                if (!presence.Away)
                {
                    presence.Away = true;
                    presence.AwaySince = now;
                }
                Settle(session, game.CapturePowerState(session), presence, now);
                return JsonSerializer.SerializeToElement(Describe("suspended", presence));
            default: // leave
                if (presence is null || presence.Ended)
                    return JsonSerializer.SerializeToElement(Describe(presence is null ? "none" : "ended", presence));
                End(presence, "left");
                return JsonSerializer.SerializeToElement(Describe("left", presence));
        }
    }

    /// <summary>
    /// The active presence the operator controls from the answering device, or why there is none: it must
    /// be active, and the network must carry the operator's control from that device to the host.
    /// </summary>
    private static Presence? Controlled(GameNetworkService game, GameNetworkService.Session session, HostCommand command, long now,
        out string? refusal)
    {
        refusal = null;
        if (!Presences.TryGetValue(command.InitiatorKey, out var presence) || presence.Ended)
        {
            refusal = "no_presence";
            return null;
        }
        Settle(session, game.CapturePowerState(session), presence, now);
        if (presence.State != "active")
        {
            refusal = presence.Moving ? "moving" : "suspended";
            return null;
        }
        if (command.ProviderNode != presence.Node &&
            NetworkDiagnostics.Trace(session.Medium.Snapshot, command.ProviderNode, presence.Node, QueryHost.ReachBudget).State != NetworkDiagnostics.Reachable)
        {
            refusal = "out_of_reach";
            return null;
        }
        return presence;
    }

    /// <summary><c>act {line}</c>: the line runs at the presence's host, with the operator's rights.</summary>
    private static JsonElement Act(GameNetworkService game, GameNetworkService.Session session, HostCommand command, JsonElement arguments, long now)
    {
        if (Controlled(game, session, command, now, out var refusal) is not { } presence)
            return JsonSerializer.SerializeToElement(new { status = refusal, count = 0, fuel = 0, rows = "", error = refusal!.Replace('_', ' ') });
        var scope = new ShellHost.Scope(presence.Owner, presence.Instance, presence.Node, null,
            $"pr.{_presenceStream:N}.{presence.Id.ToString(CultureInfo.InvariantCulture)}.{(++_actSerial).ToString(CultureInfo.InvariantCulture)}",
            presence.Owner == presence.Provider ? presence.Provider : null);
        var result = NetShell.Run(arguments.GetProperty("line").GetString() ?? "", new ShellHost.Source(game, session, scope),
            ShellHost.ProgramLimits, new ShellHost.Sink(session, scope));
        return ShellHost.Output(result);
    }

    internal static JsonElement Answer(GameNetworkService game, GameNetworkService.Session session, HostCommand command, JsonElement arguments)
    {
        var now = Environment.TickCount64;
        if (command.Command is "enter" or "detach" or "leave" or "presence")
            return PresenceCommand(game, session, command, now);
        if (command.Command == "act")
            return Act(game, session, command, arguments, now);
        if (command.Command == "move")
            return Move(game, session, command, arguments, now);
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
        // With a presence the operator scans from where it stands; without one, from the answering device.
        Presence? presence = null;
        if (Presences.TryGetValue(command.InitiatorKey, out var standing) && !standing.Ended)
        {
            presence = Controlled(game, session, command, now, out var refusal);
            if (presence is null)
                return JsonSerializer.SerializeToElement(new { status = refusal, revision = _revision, nodes = 0, links = 0, services = 0 });
        }
        if (now < knowledge.NextScan)
            return JsonSerializer.SerializeToElement(new
            {
                status = "busy", revision = _revision, nodes = 0, links = 0, services = 0,
                retryAfter = (int)Math.Ceiling((knowledge.NextScan - now) / 1000.0)
            });
        knowledge.NextScan = now + ScanEveryMs;
        var world = session.Medium.Snapshot;
        var wiring = game.Wiring;
        var host = presence?.Node ?? command.ProviderNode;
        knowledge.Host = host;
        var hood = SharedMediumNetwork.Neighborhood(world, host, radius, MaxScanNodes, out var truncated);
        var nodes = world.Physical.Nodes;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // The services the operator may discover whose devices are in reach: read through its own delivery.
        var services = new List<DirectoryEntry>();
        string? after = null;
        for (var page = 0; page < DirectoryPages; page++)
        {
            // From a presence the directory is the operator's own; otherwise it is read through the delivery.
            var slice = presence is not null
                ? session.Services.QueryDirectory(presence.Owner, null, after, 64)
                : session.Services.QueryDirectory(command.DeliveryId, null, after, 64) is { } query && query.Origin == host ? query.Slice : null;
            if (slice is null)
                break;
            services.AddRange(slice.Entries.Where(entry => hood.ContainsKey(entry.Node)));
            if (!slice.More || slice.Entries.Count == 0)
                break;
            after = slice.Entries[^1].Instance;
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
