using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
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
public static partial class NetworkSpace
{
    public const int DefaultRadius = 3, MaxRadius = 8, MaxScanNodes = 256, ScanEveryMs = 10_000, FreshMs = 60_000;
    public const int MaxOperators = 64, MaxNodes = 512, MaxLinks = 1024, MaxServices = 512, MaxRemovals = 256;
    public const int MaxPageItems = 64, MaxPageChars = 3600;

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
        public Guid Stream;
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
    private const int ChatInboxSize = 8, ChatLifetimeMs = 120_000, ChatSendEveryMs = 1200;
    /// <summary>P13: the shared channel every active operator may listen to and speak on.</summary>
    private const int ChannelSize = 32;
    private static readonly JsonSerializerOptions ChatJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record ChatMessage(string Id, EndpointKey Owner, string Sender, string Text, long SentAt);
    private static readonly Queue<ChatMessage> Channel = [];
    // The network-life runtime owns the shared-channel chat cadence.

    private sealed class ChatVisit
    {
        public readonly string Scope = Guid.NewGuid().ToString("N");
        public readonly Queue<ChatMessage> Inbox = new();
        public long Received;
    }

    private sealed class Presence
    {
        public int Id, Generation = 1;
        public EndpointKey Owner, Provider;
        public string Instance = "", State = "active", Reason = "";
        public NodeId Node;
        public bool Away, Detached;
        /// <summary>P13.5: the operator masks their persona from the other operators.</summary>
        public bool Masked;
        public long AwaySince, EntryLostSince, DetachedSince;
        public bool Ended => State == "ended";
        public ChatVisit? Chat;
        public long NextChatAt;
        /// <summary>P12.3: on its way to another host. It runs nowhere: the source is fenced, the target not yet active.</summary>
        public bool Moving;
        public string ToInstance = "";
        public EndpointKey ToProvider;
        public NodeId ToNode;
        public long ArriveAt;
        public long RouteVersion;
        public Guid RouteStream;
        public IReadOnlyList<(NodeId Node, bool Radio)> Path = [];
        public string VisiblePath = "", VisibleRadioLegs = "";
        public bool LimitedPath;
        public int TravelSeconds;
        public string BreakKind = "", BreakFrom = "", BreakTo = "";
        /// <summary>What the last move ended in, shown as the reason while nothing else is.</summary>
        public string Notice = "";
        /// <summary>P13: the name of an intruder on this presence's node, empty while the node is calm.</summary>
        public string Threat = "";
        /// <summary>P13: an intruder severed this presence; only an explicit enter resumes it.</summary>
        public bool Intruded;
    }

    private static readonly Dictionary<EndpointKey, Presence> Presences = [];
    private static int _nextPresence;
    private static long _nextPresencePass, _actSerial;
    private static Guid _presenceStream;

    /// <summary>The presence pass: endpoints, power and the operator's absence decide its state (Р6).</summary>
    public static void Tick(GameNetworkService game)
    {
        var now = Environment.TickCount64;
        // Network life keeps its own cadence and is off unless the server opted in.
        if (LifeEnabled)
            LifePass(game, now);
        if (now < _nextPresencePass || Presences.Count == 0)
            return;
        _nextPresencePass = now + PresencePassMs;
        var session = game.CaptureSession();
        if (_presenceStream != session.Packets.ReceiptStreamId)
        {
            _presenceStream = session.Packets.ReceiptStreamId;
            // P13.5: the breach codes and stolen keys belong to one Host session; a new runtime resets them.
            HackChallenges.Clear();
            StolenKeys.Clear();
            MaskCooldowns.Clear();
            foreach (var presence in Presences.Values.Where(presence => !presence.Ended))
            {
                presence.Chat = null;
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
        // P13: the warning is live state, not a log entry: it clears as soon as the node is calm again.
        if (presence.Threat.Length > 0 && !Life.Bots.Any(bot => bot.Goal == NetworkBotGoal.Intruder && bot.Node == presence.Node))
            presence.Threat = "";
        if (session.Services.DiscoverableProvider(presence.Provider, presence.Instance) is not { } host || host.Provider != presence.Provider)
        {
            if (presence.Detached && now - presence.DetachedSince < EntryGraceMs)
                Suspend(presence, "host_unreachable");
            else
                End(presence, "host_lost"); // No copy anywhere: a host that is gone ends its presences.
            return;
        }
        if (presence.Node != host.Node)
            presence.Chat = null;
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
        else if (presence.Intruded)
            Suspend(presence, "intrusion");
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
        var holds = RouteHolds(session, presence);
        if (holds && now < presence.ArriveAt)
        {
            presence.State = "moving";
            presence.Reason = "";
            return false;
        }
        presence.Moving = false;
        presence.Path = [];
        if (holds)
        {
            presence.Instance = presence.ToInstance;
            presence.Provider = presence.ToProvider;
            presence.Node = presence.ToNode;
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

    private static bool RouteHolds(GameNetworkService.Session session, Presence presence)
    {
        var world = session.Medium.Snapshot;
        if (presence.RouteStream != session.Packets.ReceiptStreamId ||
            session.Services.ResidentCommandNode(presence.Owner, presence.ToProvider, presence.ToInstance) != presence.ToNode ||
            session.Services.DiscoverableProvider(presence.Provider, presence.Instance) is not { } source ||
            source.Provider != presence.Provider || source.Node != presence.Node ||
            !world.Physical.Nodes.ContainsKey(presence.Node) || !world.Physical.Nodes.ContainsKey(presence.ToNode))
        {
            presence.BreakKind = "endpoint";
            return false;
        }
        if (world.Version == presence.RouteVersion)
            return true;
        var path = presence.Path;
        for (var i = 1; i < path.Count; i++)
        {
            var from = path[i - 1].Node;
            var (to, radio) = path[i];
            if (SharedMediumNetwork.IsRouteHopAvailable(world, from, to, radio, i == 1, i == path.Count - 1))
                continue;
            presence.BreakKind = radio ? "radio" : "cable";
            if (Operators.TryGetValue(presence.Owner, out var known) && known.Stream == presence.RouteStream &&
                Knows(known, from) && Knows(known, to))
            {
                presence.BreakFrom = Id(from);
                presence.BreakTo = Id(to);
            }
            return false;
        }
        presence.RouteVersion = world.Version;
        return true;
    }

    private static bool Knows(Knowledge knowledge, NodeId node) => knowledge.Items.ContainsKey($"n|{Id(node)}");

    private static (string Nodes, string RadioLegs, bool Limited) VisibleRoute(Knowledge knowledge,
        IReadOnlyList<(NodeId Node, bool Radio)> path)
    {
        var nodes = new List<string>();
        var radios = new List<int>();
        var truncated = path.Count > NetworkDiagnostics.MaxRouteNodes;
        var length = truncated ? NetworkDiagnostics.MaxRouteNodes - 2 : path.Count;
        for (var i = 0; i < length; i++)
        {
            var (node, radio) = path[i];
            nodes.Add(Knows(knowledge, node) ? Id(node) : "?");
            if (i > 0 && radio && nodes[i] != "?" && nodes[i - 1] != "?")
                radios.Add(i);
        }
        if (truncated)
        {
            nodes.Add("?");
            nodes.Add(Knows(knowledge, path[^1].Node) ? Id(path[^1].Node) : "?");
        }
        return (string.Join('|', nodes), string.Join("|", radios), truncated || nodes.Contains("?"));
    }

    /// <summary>
    /// <c>move {to}</c> (Р4): the presence leaves its host for another device's shell that the operator may
    /// see and call, over a route that exists now. It is on its way for one second per hop, at most 15 s.
    /// </summary>
    private static JsonElement Move(GameNetworkService game, GameNetworkService.Session session, HostCommand command, JsonElement arguments,
        long now, bool preview)
    {
        var to = arguments.GetProperty("to").GetString() ?? "";
        JsonElement Refusal(string status, Presence? current) => preview
            ? JsonSerializer.SerializeToElement(new { status, target = to })
            : JsonSerializer.SerializeToElement(Describe(status, current));
        if (Controlled(game, session, command, now, out var refusal) is not { } presence)
        {
            Presences.TryGetValue(command.InitiatorKey, out var current);
            return Refusal(refusal!, current is { Ended: false } ? current : null);
        }
        if (!ServiceNames.IsValid(to) || to == presence.Instance)
            return Refusal("invalid_argument", presence);
        if (session.Services.DiscoverableProvider(presence.Owner, to) is not { ServiceType: ShellHost.ServiceType } target ||
            session.Services.ResidentCommandNode(presence.Owner, target.Provider, to) is null)
            return Refusal("forbidden", presence);
        var world = session.Medium.Snapshot;
        var route = SharedMediumNetwork.Route(world, presence.Node, target.Node, QueryHost.ReachBudget, out _);
        if (route is null)
            return Refusal("unreachable", presence);
        if (Presences.Values.Count(other => !other.Ended && (other.Moving ? other.ToInstance : other.Instance) == to) >= MaxPresencesPerHost)
            return Refusal("limit_exceeded", presence);
        var stream = session.Packets.ReceiptStreamId;
        var path = VisibleRoute(KnowledgeOf(presence.Owner, stream, now), route);
        var travel = Math.Clamp((route.Count - 1) * TravelMsPerHop, TravelMsPerHop, MaxTravelMs);
        if (preview)
            return JsonSerializer.SerializeToElement(new
            {
                status = "ready", target = to, path = path.Nodes, radioLegs = path.RadioLegs, limited = path.Limited,
                seconds = travel / 1000, radioHops = route.Count(hop => hop.Radio), hops = route.Count - 1
            });
        presence.Chat = null;
        presence.Moving = true;
        presence.ToInstance = to;
        presence.ToProvider = target.Provider;
        presence.ToNode = target.Node;
        presence.ArriveAt = now + travel;
        presence.TravelSeconds = travel / 1000;
        presence.Path = route;
        presence.RouteVersion = world.Version;
        presence.RouteStream = stream;
        (presence.VisiblePath, presence.VisibleRadioLegs, presence.LimitedPath) = path;
        presence.BreakKind = presence.BreakFrom = presence.BreakTo = "";
        presence.Notice = "";
        presence.State = "moving";
        presence.Reason = "";
        return JsonSerializer.SerializeToElement(Describe("moving", presence));
    }

    private static void Suspend(Presence presence, string reason)
    {
        presence.Chat = null;
        presence.State = "suspended";
        presence.Reason = reason;
    }

    private static void End(Presence presence, string reason)
    {
        presence.Chat = null;
        presence.Moving = false;
        presence.Path = [];
        presence.State = "ended";
        presence.Reason = reason;
    }

    private static Dictionary<string, object> Describe(string status, Presence? presence)
    {
        if (presence is null)
            return new Dictionary<string, object> { ["status"] = status };
        var result = new Dictionary<string, object>
        {
            ["status"] = status, ["presence"] = presence.Id, ["host"] = presence.Moving ? "" : presence.Instance,
            ["generation"] = presence.Generation, ["state"] = presence.State, ["reason"] = presence.Reason, ["threat"] = presence.Threat,
            ["node"] = presence.Moving ? "" : presence.Node.Value.ToString()
        };
        // P13: the identity the operator shows to others in the space, as flat schema fields.
        var persona = Life.Personas.GetValueOrDefault(presence.Owner);
        result["personaName"] = persona?.Name ?? "";
        result["personaSignature"] = persona?.Signature ?? "";
        result["personaColour"] = persona?.Colour ?? 0;
        result["personaStatus"] = persona?.Status ?? "";
        result["personaRole"] = persona?.Role ?? "";
        if (presence.Moving)
        {
            result["to"] = presence.ToInstance;
            result["arriveIn"] = (int)Math.Max(0, Math.Ceiling((presence.ArriveAt - Environment.TickCount64) / 1000.0));
            result["travelSeconds"] = presence.TravelSeconds;
            result["route"] = presence.VisiblePath;
            result["radioLegs"] = presence.VisibleRadioLegs;
            result["limited"] = presence.LimitedPath;
        }
        else if (presence.BreakKind.Length > 0 && (presence.Notice == "returned: route_lost" || presence.Reason == "lost_in_transit"))
        {
            result["breakKind"] = presence.BreakKind;
            if (presence.BreakFrom.Length > 0)
            {
                result["breakFrom"] = presence.BreakFrom;
                result["breakTo"] = presence.BreakTo;
            }
        }
        return result;
    }

    private static JsonElement PresenceCommand(GameNetworkService game, GameNetworkService.Session session, HostCommand command,
        JsonElement arguments, long now)
    {
        Presences.TryGetValue(command.InitiatorKey, out var presence);
        if (presence is not null && presence.Ended && command.Command is "enter")
            presence = null; // An ended presence is reported until the operator enters again.
        switch (command.Command)
        {
            case "presence":
                var state = game.CapturePowerState(session);
                if (presence is { Ended: false })
                    Settle(session, state, presence, now);
                var snapshot = Describe(presence is null ? "none" : presence.Ended ? "ended" : "present", presence);
                if (arguments.TryGetProperty("others", out var others) && others.GetBoolean())
                    snapshot["others"] = state is { } powerState ? OtherPresences(session, command, presence, powerState) : "";
                return JsonSerializer.SerializeToElement(snapshot);
            case "enter":
                if (presence is not null)
                {
                    // One presence per operator: entering again resumes it where it stands. A severed link
                    // (an intrusion) is resumed only by this explicit enter, never by a background pass.
                    var resumed = presence.Away;
                    presence.Away = false;
                    presence.Intruded = false;
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
                if (LifeEnabled)
                    Record("operator", presence.Node, OperatorName(presence), "оператор вошёл в сеть");
                return JsonSerializer.SerializeToElement(Describe("entered", presence));
            case "detach":
                if (presence is null || presence.Ended)
                    return JsonSerializer.SerializeToElement(Describe(presence is null ? "none" : "ended", presence));
                if (!presence.Away)
                {
                    if (LifeEnabled)
                        Record("operator", presence.Node, OperatorName(presence), "оператор покинул сеть");
                    presence.Away = true;
                    presence.AwaySince = now;
                }
                Settle(session, game.CapturePowerState(session), presence, now);
                return JsonSerializer.SerializeToElement(Describe("suspended", presence));
            case "abort_move":
                if (presence is null || presence.Ended)
                    return JsonSerializer.SerializeToElement(Describe("no_presence", presence));
                var power = game.CapturePowerState(session);
                Settle(session, power, presence, now);
                if (!presence.Moving)
                    return JsonSerializer.SerializeToElement(Describe("not_moving", presence));
                if (command.ProviderNode != presence.Node && NetworkDiagnostics.Trace(session.Medium.Snapshot,
                    command.ProviderNode, presence.Node, QueryHost.ReachBudget).State != NetworkDiagnostics.Reachable)
                    return JsonSerializer.SerializeToElement(Describe("out_of_reach", presence));
                presence.Moving = false;
                presence.Path = [];
                presence.Notice = "returned: cancelled";
                Settle(session, power, presence, now);
                return JsonSerializer.SerializeToElement(Describe("cancelled", presence));
            default: // leave
                if (presence is null || presence.Ended)
                    return JsonSerializer.SerializeToElement(Describe(presence is null ? "none" : "ended", presence));
                End(presence, "left");
                return JsonSerializer.SerializeToElement(Describe("left", presence));
        }
    }

    /// <summary>Live counts on already known nodes. A private shell never discloses its occupants.</summary>
    private static string OtherPresences(GameNetworkService.Session session, HostCommand command, Presence? own,
        (SharedMediumSnapshot World, WiringReport Wiring) state)
    {
        var stream = session.Packets.ReceiptStreamId;
        if (_presenceStream != stream || !Operators.TryGetValue(command.InitiatorKey, out var knowledge) || knowledge.Stream != stream)
            return "";

        var origin = command.ProviderNode;
        if (own is { Ended: false })
        {
            if (own.State != "active" || !CanControlFrom(state.World, origin, own.Node))
                return "";
            origin = own.Node;
        }
        if (!state.Wiring.CanExecute(origin))
            return "";

        var counts = new Dictionary<NodeId, int>();
        foreach (var other in Presences.Values)
        {
            if (other.Owner == command.InitiatorKey || other.State != "active" || other.Away || other.Detached ||
                !Knows(knowledge, other.Node) || !state.Wiring.CanExecute(other.Node) || !session.Services.IsAttached(other.Owner) ||
                session.Services.DiscoverableProvider(command.InitiatorKey, other.Instance) is not { } host ||
                host.Provider != other.Provider || host.Node != other.Node)
                continue;
            counts[other.Node] = counts.GetValueOrDefault(other.Node) + 1;
        }
        if (counts.Count == 0)
            return "";

        var reached = SharedMediumNetwork.ReachAll(state.World, origin, counts.Keys.ToHashSet(), QueryHost.ReachBudget, out _);
        return string.Join(",", reached.Keys.OrderBy(node => node.Value)
            .Select(node => $"{Id(node)}|{counts[node].ToString(CultureInfo.InvariantCulture)}"));
    }

    private static bool CanControlFrom(SharedMediumSnapshot world, NodeId entry, NodeId host) => entry == host ||
        NetworkDiagnostics.Trace(world, entry, host, QueryHost.ReachBudget).State == NetworkDiagnostics.Reachable;

    /// <summary>A presence can read its own inbox and speak only during its current visit to the node.</summary>
    /// <summary>P13.6: the wire ids of the nodes this operator has learned; used by the shell's `nodes` and completion.</summary>
    internal static IReadOnlyList<string> KnownNodeIds(EndpointKey owner, Guid stream, int max)
    {
        var knowledge = KnowledgeOf(owner, stream, Environment.TickCount64);
        if (knowledge is null || max <= 0)
            return [];
        return knowledge.Items.Keys.Where(key => key.StartsWith("n|", StringComparison.Ordinal))
            .Select(key => key[2..]).Take(max).ToArray();
    }

    internal static JsonElement Chat(GameNetworkService game, GameNetworkService.Session session, HostCommand command,
        JsonElement arguments, out Action? applied)
    {
        applied = null;
        static JsonElement Refuse(string status) => JsonSerializer.SerializeToElement(new { status });

        var now = Environment.TickCount64;
        if (_presenceStream != session.Packets.ReceiptStreamId || game.CapturePowerState(session) is null)
            return Refuse("stale_chat");
        if (Controlled(game, session, command, now, out var refusal) is not { } presence)
            return Refuse(refusal!);

        var node = Id(presence.Node);
        // P13: the shared channel is open to every active operator; node chat stays local to the visit.
        var sharedChannel = arguments.TryGetProperty("channel", out var channelFlag) && channelFlag.ValueKind == JsonValueKind.True;
        if (sharedChannel)
        {
            if (command.Command == "chat_read")
            {
                PruneChannel(now);
                return JsonSerializer.SerializeToElement(new
                {
                    status = "ok", channel = true, received = 0, messages = ChannelSnapshot(presence, now)
                });
            }
            var channelText = arguments.GetProperty("text").GetString()!.Trim();
            if (channelText.Length == 0)
                return Refuse("invalid_text");
            if (now < presence.NextChatAt)
                return Refuse("rate_limited");
            var speaker = OperatorName(presence);
            var channelMessage = new ChatMessage(Guid.NewGuid().ToString("N"), presence.Owner, speaker, channelText, now);
            applied = () => DeliverChannel(game, session, presence, channelMessage);
            return JsonSerializer.SerializeToElement(new { status = "sent", channel = true });
        }
        if (command.Command == "chat_read")
        {
            if (arguments.GetProperty("presence").GetDouble() != presence.Id ||
                arguments.GetProperty("generation").GetDouble() != presence.Generation)
                return Refuse("stale_chat");
            var chat = presence.Chat ??= new ChatVisit();
            return JsonSerializer.SerializeToElement(new
            {
                status = "ok", node, scope = chat.Scope, received = chat.Received, messages = ChatSnapshot(presence, chat, now)
            });
        }

        if (presence.Chat is not { } visit || arguments.GetProperty("scope").GetString() != visit.Scope)
            return Refuse("stale_chat");
        var text = arguments.GetProperty("text").GetString()!.Trim();
        if (text.Length == 0)
            return Refuse("invalid_text");
        if (now < presence.NextChatAt)
            return Refuse("rate_limited");

        var message = new ChatMessage(Guid.NewGuid().ToString("N"), presence.Owner, presence.Masked ? "скрытый оператор" : $"@{visit.Scope[..6]}", text, now);
        applied = () => DeliverChat(game, session, presence, visit, command.ProviderNode, message);
        return JsonSerializer.SerializeToElement(new { status = "sent", node });
    }

    /// <summary>Only the residents at publication time get a copy. Joining a node opens an empty inbox.</summary>
    private static void DeliverChat(GameNetworkService game, GameNetworkService.Session session, Presence sender, ChatVisit visit,
        NodeId entry, ChatMessage message)
    {
        if (game.CapturePowerState(session) is not { } power)
            return;
        var now = Environment.TickCount64;
        Settle(session, power, sender, now);
        if (sender.Chat != visit || sender.State != "active" || !CanControlFrom(power.World, entry, sender.Node))
            return;
        sender.NextChatAt = now + ChatSendEveryMs;

        foreach (var recipient in Presences.Values)
        {
            if (recipient.Ended)
                continue;
            if (recipient != sender)
                Settle(session, power, recipient, now);
            if (recipient.State != "active" || recipient.Node != sender.Node)
                continue;
            var chat = recipient.Chat ??= new ChatVisit();
            if (chat.Inbox.Count == ChatInboxSize)
                chat.Inbox.Dequeue();
            chat.Inbox.Enqueue(message);
            if (recipient != sender)
                chat.Received++;
        }
    }

    private static string ChatSnapshot(Presence presence, ChatVisit visit, long now)
    {
        var rows = new List<string>(visit.Inbox.Count);
        var chars = 2;
        var bytes = 2;
        foreach (var message in visit.Inbox.Reverse())
        {
            if (now - message.SentAt >= ChatLifetimeMs)
                break;
            var row = JsonSerializer.Serialize(new
            {
                id = message.Id, text = message.Text, sender = message.Sender, own = message.Owner == presence.Owner
            }, ChatJson);
            // The enclosing wire field is itself a JSON string. Leave room for its DTO envelope.
            var encodedBytes = JsonEncodedText.Encode(row).EncodedUtf8Bytes.Length;
            if (chars + row.Length + 1 > SchemaRegistry.ChatSnapshotMaxLength ||
                bytes + encodedBytes + 1 > SchemaRegistry.AbsoluteMaxPayloadBytes - 256)
                break;
            rows.Add(row);
            chars += row.Length + 1;
            bytes += encodedBytes + 1;
        }
        rows.Reverse();
        return $"[{string.Join(",", rows)}]";
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
        if (!CanControlFrom(session.Medium.Snapshot, command.ProviderNode, presence.Node))
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
        var scope = new ShellHost.Scope(presence.Owner, presence.Instance, presence.Node, null, ActionKey(presence),
            presence.Owner == presence.Provider ? presence.Provider : null, InvocationHost: presence.Provider);
        var result = NetShell.Run(arguments.GetProperty("line").GetString() ?? "", new ShellHost.Source(game, session, scope),
            ShellHost.ProgramLimits, new ShellHost.Sink(session, scope));
        return ShellHost.Output(result);
    }

    private static string ActionKey(Presence presence) =>
        $"pr.{_presenceStream:N}.{presence.Id.ToString(CultureInfo.InvariantCulture)}.{(++_actSerial).ToString(CultureInfo.InvariantCulture)}";

    private static JsonElement AlarmCommand(GameNetworkService game, GameNetworkService.Session session, HostCommand command, JsonElement arguments, long now)
    {
        if (Controlled(game, session, command, now, out var refusal) is not { } presence)
            return JsonSerializer.SerializeToElement(new { status = refusal, count = 0, fuel = 0, rows = "" });
        var instance = arguments.GetProperty("instance").GetString()!;
        var read = command.Command == "poll_alarm";
        var verb = read ? "read" : "set_alert";
        var payload = read ? JsonSerializer.SerializeToElement(new { reason = "inspect" }) : JsonSerializer.SerializeToElement(new
        {
            ticket = arguments.GetProperty("ticket").GetString(), enabled = arguments.GetProperty("enabled").GetBoolean()
        });
        var invocation = session.Services.Describe(presence.Owner, instance)?.ServiceType == "atmos.alarm"
            ? session.Services.InvokeFrom(presence.Owner, presence.Provider, presence.Instance, presence.Node, instance, verb,
                ActionKey(presence), payload, 50, PacketTrafficClass.Interactive)
            : new InvokeResult(ServiceStatus.UnknownService, null);
        var rows = ShellHost.Format([ShellHost.Record(("instance", instance),
            ("status", invocation.Status == ServiceStatus.Ok ? "sent" : ServiceNames.Code(invocation.Status)),
            ("operation", invocation.Operation?.OperationId.ToString(CultureInfo.InvariantCulture) ?? ""), ("origin", Id(presence.Node)))]);
        return JsonSerializer.SerializeToElement(new { status = "ok", count = 1, fuel = 0, rows });
    }

    internal static JsonElement Answer(GameNetworkService game, GameNetworkService.Session session, HostCommand command, JsonElement arguments)
    {
        EnsureLifeSession(session.Packets.ReceiptStreamId);
        var now = Environment.TickCount64;
        if (command.Command is "enter" or "detach" or "leave" or "presence" or "abort_move")
            return PresenceCommand(game, session, command, arguments, now);
        if (command.Command == "persona")
            return PersonaCommand(command, arguments);
        if (command.Command is "hack_start" or "hack_answer" or "hack_keys" or "hack_use" or "mask")
            return HackCommand(game, session, command, arguments, now);
        if (command.Command == "act")
            return Act(game, session, command, arguments, now);
        if (command.Command is "poll_alarm" or "alarm_action")
            return AlarmCommand(game, session, command, arguments, now);
        if (command.Command is "move" or "preview_move")
            return Move(game, session, command, arguments, now, preview: command.Command == "preview_move");
        var knowledge = KnowledgeOf(command.InitiatorKey, session.Packets.ReceiptStreamId, now);
        if (command.Command == "life")
            return LifeCommand(command, arguments, knowledge, now);
        return command.Command == "scan"
            ? Scan(game, session, command, knowledge, arguments.TryGetProperty("radius", out var radius)
                ? Math.Clamp(radius.GetInt32(), 1, MaxRadius) : DefaultRadius, now)
            : Read(game, session, knowledge, arguments.TryGetProperty("since", out var since) ? Math.Max(0, since.GetInt64()) : 0,
                arguments.TryGetProperty("limit", out var limit) ? Math.Clamp(limit.GetInt32(), 1, MaxPageItems) : MaxPageItems, now);
    }

    private static Knowledge KnowledgeOf(EndpointKey operatorKey, Guid stream, long now)
    {
        if (!Operators.TryGetValue(operatorKey, out var knowledge))
        {
            if (Operators.Count >= MaxOperators)
                Operators.Remove(Operators.MinBy(pair => pair.Value.LastUsed).Key); // The longest idle operator forgets.
            Operators[operatorKey] = knowledge = new Knowledge { Stream = stream };
        }
        else if (knowledge.Stream != stream)
            Operators[operatorKey] = knowledge = new Knowledge { Stream = stream, RemovedBefore = ++_revision };
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

        // Read services on the scanned nodes after transport has authorized this Host command.
        var directory = session.Services.QueryNearbyDirectory(command.InitiatorKey,
            hood.OrderBy(entry => entry.Value).ThenBy(entry => entry.Key.Value).Select(entry => entry.Key), MaxServices);
        var services = directory?.Entries ?? [];

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
            if (!seen.Contains(item.Key) && !item.Broken && (item.Kind != 's' || directory is { More: false }) &&
                hood.ContainsKey(item.First) && (item.Kind != 'l' || hood.ContainsKey(item.Second)))
                Break(item);
        CheckRoutes(game, session, knowledge, force: true);
        Trim(knowledge);
        return JsonSerializer.SerializeToElement(new
        {
            status = truncated || directory?.More == true ? "partial" : "complete", revision = _revision,
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
    /// <summary>P13: a channel message is visible to every active operator; the ring is bounded and pruned.</summary>
    private static void DeliverChannel(GameNetworkService game, GameNetworkService.Session session, Presence sender, ChatMessage message)
    {
        if (game.CapturePowerState(session) is not { } power)
            return;
        var now = Environment.TickCount64;
        Settle(session, power, sender, now);
        if (sender.State != "active")
            return;
        sender.NextChatAt = now + ChatSendEveryMs;
        EnqueueChannel(message);
    }

    private static void EnqueueChannel(ChatMessage message)
    {
        Channel.Enqueue(message);
        while (Channel.Count > ChannelSize)
            Channel.Dequeue();
    }

    private static void PruneChannel(long now)
    {
        while (Channel.Count > 0 && now - Channel.Peek().SentAt >= ChatLifetimeMs)
            Channel.Dequeue();
        while (Channel.Count > ChannelSize)
            Channel.Dequeue();
    }

    private static string ChannelSnapshot(Presence presence, long now)
    {
        var rows = new List<string>(Channel.Count);
        foreach (var message in Channel.Reverse())
        {
            var row = JsonSerializer.Serialize(new
            {
                id = message.Id, text = message.Text, sender = message.Sender, own = message.Owner == presence.Owner
            }, ChatJson);
            rows.Add(row);
            if (rows.Count >= ChannelSize)
                break;
        }
        rows.Reverse();
        return $"[{string.Join(",", rows)}]";
    }
}
