using System.Globalization;
using System.Text.Json;
using NetDM.Core;
using NetDM.Host.Transport;
using NetDM.Services;

namespace NetDM.Host.Diagnostics;

/// <summary>
/// Network life (P13): the entities that exist in the network besides the operators who enter it.
///
/// Two kinds live here, both bounded and entirely Host-side:
/// - <b>NetworkPersona</b>: the identity an operator-entity shows to the others (a callsign, a sigil and a
///   colour). It is customization, not authority: rights still belong to the entry device's endpoint.
/// - <b>NetworkBot</b>: an NPC agent with its own goal, host node and movement along real physical links. Bots
///   are the filler life of the network: they travel, watch traffic and appear in the knowledge of an
///   operator standing on a known node. They have no way to reach a device's services: a goal is a
///   description, not a command.
///
/// The population is off unless the Host is started with <c>NetDM__NetLife=1</c> (or
/// <c>NETDM_NET_LIFE=1</c>): the network-space layer is a tool a server opts into, not something a round
/// gets by default. Everything is a ring or a list with an explicit cap; one pass acts on a few bots, so
/// the packet tick and the presence pass keep their budgets.
/// </summary>
public static partial class NetworkSpace
{
    public const int MaxBots = NetworkLifeRuntime.MaxBots, MaxActivity = NetworkLifeRuntime.MaxActivity, MaxActivityPage = 64, MaxPersonaName = 24, MaxSignature = 4, MaxColours = 8, MaxPersonaStatus = 32, MaxPersonaRole = 24;
    private const int BotPassMs = 1000, BotFirstActionMs = 5000, BotMoveMinMs = 5000, BotMoveMaxMs = 11_000;
    private const int BotActionsPerPass = 2, BotFuel = 400, IntrusionDwellMs = 10_000;

    private static readonly NetworkLifeRuntime Life = new();

    private static void EnsureLifeSession(Guid stream)
    {
        if (Life.EnsureSession(stream))
            Channel.Clear(); // Discard previous-round channel messages.
    }

    private static bool? _lifeEnabled;

    /// <summary>The network-space layer is opt-in: an ordinary round does not get bots or personas.</summary>
    private static bool LifeEnabled => _lifeEnabled ??=
        Environment.GetEnvironmentVariable("NETDM_NET_LIFE") is "1" or "true" or "TRUE" ||
        Environment.GetEnvironmentVariable("NetDM__NetLife") is "1" or "true" or "TRUE";

    private static Dictionary<string, object> PersonaOf(EndpointKey owner)
    {
        if (Life.Personas.TryGetValue(owner, out var persona))
            return new Dictionary<string, object>
            {
                ["name"] = persona.Name, ["signature"] = persona.Signature, ["colour"] = persona.Colour, ["statusLine"] = persona.Status, ["role"] = persona.Role, ["revision"] = persona.Revision
            };
        return new Dictionary<string, object> { ["name"] = "", ["signature"] = "", ["colour"] = (int)(owner.Id.Length % MaxColours), ["statusLine"] = "", ["role"] = "", ["revision"] = 0L };
    }

    /// <summary>
    /// The operator's own appearance in the network. Bounded strings, no markup: the window and other
    /// operators only ever render what is stored here, so a persona is never a channel for commands.
    /// </summary>
    private static JsonElement PersonaCommand(HostCommand command, JsonElement arguments)
    {
        Life.Personas.TryGetValue(command.InitiatorKey, out var persona);
        if (arguments.TryGetProperty("name", out var name) || arguments.TryGetProperty("signature", out _) ||
            arguments.TryGetProperty("colour", out _) || arguments.TryGetProperty("statusLine", out _) ||
            arguments.TryGetProperty("role", out _))
        {
            var wantedName = name.ValueKind == JsonValueKind.String ? name.GetString()?.Trim() : persona?.Name;
            var wantedSignature = arguments.TryGetProperty("signature", out var signature) && signature.ValueKind == JsonValueKind.String
                ? signature.GetString()?.Trim() : persona?.Signature ?? "";
            var wantedColour = arguments.TryGetProperty("colour", out var colour) && colour.TryGetInt32(out var parsedColour)
                ? parsedColour : persona?.Colour ?? 0;
            var wantedStatus = arguments.TryGetProperty("statusLine", out var statusLine) && statusLine.ValueKind == JsonValueKind.String
                ? statusLine.GetString()?.Trim() : persona?.Status ?? "";
            var wantedRole = arguments.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String
                ? role.GetString()?.Trim() : persona?.Role ?? "";
            if (wantedName is null || wantedName.Length is 0 or > MaxPersonaName || !Printable(wantedName))
                return JsonSerializer.SerializeToElement(new { status = "invalid_argument", reason = "name" });
            if (wantedSignature is null || wantedSignature.Length > MaxSignature || !Printable(wantedSignature))
                return JsonSerializer.SerializeToElement(new { status = "invalid_argument", reason = "signature" });
            if (wantedColour is < 0 or >= MaxColours)
                return JsonSerializer.SerializeToElement(new { status = "invalid_argument", reason = "colour" });
            if (wantedStatus is null || wantedStatus.Length > MaxPersonaStatus || !Printable(wantedStatus))
                return JsonSerializer.SerializeToElement(new { status = "invalid_argument", reason = "statusLine" });
            if (wantedRole is null || wantedRole.Length > MaxPersonaRole || !Printable(wantedRole))
                return JsonSerializer.SerializeToElement(new { status = "invalid_argument", reason = "role" });
            persona = new NetworkPersona
            {
                Name = wantedName, Signature = wantedSignature, Colour = wantedColour, Status = wantedStatus, Role = wantedRole,
                Revision = (persona?.Revision ?? 0) + 1
            };
            Life.SetPersona(command.InitiatorKey, persona);
            Record("persona", command.ProviderNode, wantedName, $"{wantedName} changed their signature");
            return JsonSerializer.SerializeToElement(new { status = "ok", personaName = persona.Name, personaSignature = persona.Signature, personaColour = persona.Colour, personaStatus = persona.Status, personaRole = persona.Role });
        }
        return JsonSerializer.SerializeToElement(new
        {
            status = "ok", personaName = persona?.Name ?? "", personaSignature = persona?.Signature ?? "",
            personaColour = persona?.Colour ?? 0, personaStatus = persona?.Status ?? "", personaRole = persona?.Role ?? ""
        });
    }

    private static bool Printable(string text) => text.All(character => !char.IsControl(character));

    /// <summary>
    /// Entities an operator may see: only bots standing on nodes in their own knowledge. A node the
    /// operator never saw discloses no occupants, exactly like the live presence counts. Lists travel as
    /// text, the way every other shell result carries them: bots are
    /// <c>id|name|signature|colour|goal|node|state|moves</c> and activity is
    /// <c>seq|kind|node|name|text</c>, joined by semicolons.
    /// </summary>
    private static JsonElement LifeCommand(HostCommand command, JsonElement arguments, Knowledge knowledge, long now)
    {
        if (knowledge.Items.Count == 0)
            return JsonSerializer.SerializeToElement(new { status = "no_knowledge" });
        var known = knowledge.Items.Keys.Where(key => key.StartsWith("n|", StringComparison.Ordinal))
            .Select(key => key[2..]).ToHashSet(StringComparer.Ordinal);
        var since = arguments.TryGetProperty("since", out var sinceValue) && sinceValue.TryGetInt64(out var parsedSince)
            ? Math.Max(0, parsedSince) : 0;
        var limit = arguments.TryGetProperty("limit", out var limitValue) && limitValue.TryGetInt32(out var parsedLimit)
            ? Math.Clamp(parsedLimit, 1, MaxActivityPage) : MaxActivityPage;
        var bots = string.Join(';', Life.Bots.Where(bot => known.Contains(bot.Node.Value.ToString(CultureInfo.InvariantCulture)))
            .Select(bot => string.Join('|', bot.Id.ToString(CultureInfo.InvariantCulture), Clean(bot.Name), Clean(bot.Signature),
                bot.Colour.ToString(CultureInfo.InvariantCulture), bot.BehaviorId,
                bot.Node.Value.ToString(CultureInfo.InvariantCulture), bot.State, bot.Moves.ToString(CultureInfo.InvariantCulture))));
        var activity = Life.Activities.Where(entry => entry.Seq > since && known.Contains(entry.Node)).Take(limit).ToArray();
        var text = string.Join(';', activity.Select(entry => string.Join('|', entry.Seq.ToString(CultureInfo.InvariantCulture),
            entry.Kind, entry.Node, Clean(entry.Name), Clean(entry.Text))));
        var last = activity.Length > 0 ? activity[^1].Seq : since;
        // Other operators are part of the life of the network: their personas, nodes and states, but
        // only on nodes this operator already knows, exactly like the bots.
        var presences = string.Join(';', Presences.Values
            .Where(entry => !entry.Ended && known.Contains(entry.Node.Value.ToString(CultureInfo.InvariantCulture)))
            .Select(entry =>
            {
                var identity = PersonaOf(entry.Owner);
                var masked = entry.Masked;
                return string.Join('|', entry.Node.Value.ToString(CultureInfo.InvariantCulture),
                    masked ? "" : Clean((string)identity["name"]), masked ? "" : Clean((string)identity["signature"]),
                    masked ? "7" : ((int)identity["colour"]).ToString(CultureInfo.InvariantCulture),
                    masked ? "" : Clean((string)identity["statusLine"]),
                    masked ? "" : Clean((string)identity["role"]), Clean(entry.State), Clean(entry.Threat ?? ""));
            }));
        var persona = Life.Personas.GetValueOrDefault(command.InitiatorKey);
        return JsonSerializer.SerializeToElement(new
        {
            status = "ok", bots, activity = text, presences, since = last, enabled = true,
            personaName = persona?.Name ?? "", personaSignature = persona?.Signature ?? "", personaColour = persona?.Colour ?? 0, personaStatus = persona?.Status ?? "", personaRole = persona?.Role ?? ""
        });
    }

    /// <summary>Free text inside a text list: separators and control characters would break the framing.</summary>
    private static string Clean(string text) =>
        new(text.Select(character => character is '|' or ';' or '\n' or '\r' ? ' ' : character).ToArray());

    public const int MaxSpawnPerRequest = 8;

    /// <summary>
    /// Host control plane for the population: a round event, an admin action or a test can seed agents
    /// and retire them. Bounded and opt-in; without the layer every call is refused.
    /// </summary>
    public static (bool Ok, string? Error, object? Result) Control(GameNetworkService game, JsonElement payload)
    {
        if (!LifeEnabled)
            return (false, "life_disabled", null);
        if (payload.ValueKind != JsonValueKind.Object)
            return (false, "invalid_request", null);
        var action = payload.TryGetProperty("action", out var actionValue) && actionValue.ValueKind == JsonValueKind.String
            ? actionValue.GetString() : null;
        var session = game.CaptureSession();
        EnsureLifeSession(session.Packets.ReceiptStreamId);
        var world = session.Medium.Snapshot;
        switch (action)
        {
            case "inspect":
                // The admin/status view of the population: counts and the nodes it occupies.
                return (true, null, new
                {
                    total = Life.Bots.Count,
                    nodes = Life.Bots.Select(bot => bot.Node.Value.ToString(CultureInfo.InvariantCulture)).ToArray(),
                    goals = Life.Bots.Select(bot => bot.BehaviorId).ToArray()
                });
            case "spawn":
                var count = payload.TryGetProperty("count", out var countValue) && countValue.TryGetInt32(out var parsedCount)
                    ? Math.Clamp(parsedCount, 1, MaxSpawnPerRequest) : 1;
                var goalId = payload.TryGetProperty("goal", out var goalValue) && goalValue.ValueKind == JsonValueKind.String
                    ? goalValue.GetString() ?? "" : "sentinel";
                if (!game.BotBehaviors.TryGet(goalId, out var behavior))
                    return (false, "unknown_goal", null);
                NodeId? only = null;
                if (payload.TryGetProperty("node", out var nodeValue) &&
                    nodeValue.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                {
                    var text = nodeValue.ValueKind == JsonValueKind.String ? nodeValue.GetString() : nodeValue.GetRawText();
                    if (!ulong.TryParse(text, out var id) || !world.Physical.Nodes.ContainsKey(new NodeId(id)))
                        return (false, "unknown_node", null);
                    only = new NodeId(id);
                }
                // An explicit spawn is a request, not a suggestion: make room by retiring the oldest
                // background agents instead of silently spawning nothing at the cap.
                var candidates = SpawnNodes(world, only).Take(count).ToList();
                Life.MakeRoom(candidates.Count);
                var spawned = 0;
                foreach (var candidate in candidates)
                {
                    NewBot(candidate, behavior, Environment.TickCount64);
                    spawned++;
                }
                return (true, null, new { spawned, total = Life.Bots.Count, goal = behavior.Id,
                    nodes = candidates.Select(node => node.Value.ToString()).ToArray() });
            case "banish":
                // Counterplay: an operator on the node can push a hostile agent out of the network.
                NodeId? against = null;
                if (payload.TryGetProperty("node", out var banishNode) &&
                    (banishNode.ValueKind == JsonValueKind.String || banishNode.ValueKind == JsonValueKind.Number))
                {
                    var banishText = banishNode.ValueKind == JsonValueKind.String ? banishNode.GetString() : banishNode.GetRawText();
                    if (!ulong.TryParse(banishText, out var banishId))
                        return (false, "unknown_node", null);
                    against = new NodeId(banishId);
                }
                var banished = Life.Banish(against);
                return (true, null, new { banished, total = Life.Bots.Count });
            case "retire":
                var retireCount = payload.TryGetProperty("count", out var retireValue) && retireValue.TryGetInt32(out var parsedRetire)
                    ? Math.Clamp(parsedRetire, 1, MaxBots) : Life.Bots.Count;
                var retired = Life.Retire(retireCount);
                return (true, null, new { retired, total = Life.Bots.Count });
            default:
                return (false, "invalid_request", null);
        }
    }

    private static IEnumerable<NodeId> SpawnNodes(SharedMediumSnapshot world, NodeId? only) =>
        only is { } node
            ? [node]
            : world.Physical.Nodes.Values.Where(node => node.CanForward && node.Links.Count > 0)
                .Select(node => node.Id).OrderBy(id => id.Value);

    private static NetworkBot NewBot(NodeId node, INetworkBotBehavior behavior, long now) =>
        Life.Spawn(node, behavior, now, BotFirstActionMs);

    /// <summary>One bounded pass: a few bots move one physical link and leave an activity record.</summary>
    private static void LifePass(GameNetworkService game, long now)
    {
        var session = game.CaptureSession();
        EnsureLifeSession(session.Packets.ReceiptStreamId);
        if (now < Life.NextBotPass)
            return;
        Life.NextBotPass = now + BotPassMs;
        var world = session.Medium.Snapshot;
        if (!Life.PopulationStarted)
            Populate(world, now, game.BotBehaviors);
        var actions = 0;
        var bots = Life.Bots.Count;
        for (var step = 0; step < bots && actions < BotActionsPerPass; step++)
        {
            var index = (int)(Life.BotCursor++ % (uint)Math.Max(1, bots));
            var bot = Life.Bots[index];
            if (now < bot.NextAt)
                continue;
            actions++;
            bot.NextAt = now + BotMoveMinMs + (long)(Life.BotSerial % (BotMoveMaxMs - BotMoveMinMs + 1));
            Life.BotSerial++;
            if (bot.Fuel <= 0)
            {
                bot.State = "idle";
                bot.Fuel = BotFuel;
                bot.NextAt = now + 30_000;
                continue;
            }
            bot.Fuel--;
            if (!game.BotBehaviors.TryGet(bot.BehaviorId, out var behavior) ||
                !world.Physical.Nodes.TryGetValue(bot.Node, out var node))
            {
                bot.State = "idle";
                bot.ThreatSince = 0;
                continue;
            }

            // Behaviors get only a bounded observation. The Host computes pursuit over actual physical
            // routing and validates every proposed hop; a plugin cannot teleport or bypass the network.
            var adjacent = node.Links.Keys.OrderBy(id => id.Value).ToArray();
            var pursuit = behavior.Hostile ? Hunt(world, bot) : null;
            var observation = new NetworkBotObservation(adjacent, pursuit?.Next,
                pursuit?.Reached is not null, Life.BotSerial);
            var snapshot = new NetworkBotState(bot.Id, bot.Node, bot.Moves, bot.Fuel, bot.State);
            var decision = behavior.Decide(snapshot, observation);
            if (!NetworkBotDecisionPolicy.Allows(bot.Node, behavior.Hostile, observation, decision))
            {
                bot.State = "idle";
                bot.ThreatSince = 0;
                continue;
            }
            var next = decision.Next!.Value;

            var moved = next != bot.Node;
            if (moved)
            {
                bot.Node = next;
                bot.Moves++;
                Record("move", next, bot.Name, $"{bot.Name} moved to {next.Value}");
            }
            bot.State = "active";

            if (decision.Engage && pursuit?.Reached is { } victim && victim.State == "active")
            {
                victim.Threat = bot.Name;
                if (bot.ThreatSince == 0)
                    bot.ThreatSince = now;
                Record("intrusion", bot.Node, bot.Name,
                    $"{bot.Name} reached an operator presence at {bot.Node.Value}");
                if (now - bot.ThreatSince >= IntrusionDwellMs)
                {
                    victim.Intruded = true;
                    Suspend(victim, "intrusion");
                    Record("intrusion", bot.Node, bot.Name,
                        $"{bot.Name} severed the operator's link at {bot.Node.Value}");
                }
            }
            else
                bot.ThreatSince = 0;

            if (moved && decision.ActivityKind is { Length: > 0 } kind &&
                decision.ActivityText is { Length: > 0 } activity)
                Record(kind, bot.Node, bot.Name, activity);
        }

        // P13: the population murmurs on the shared channel now and then, so the network feels alive.
        if (now >= Life.NextChannelChatter)
        {
            Life.NextChannelChatter = now + Random.Shared.Next(20_000, 40_000);
            if (Life.Bots.Count > 0)
            {
                var chatter = Life.Bots[Random.Shared.Next(Life.Bots.Count)];
                if (game.BotBehaviors.TryGet(chatter.BehaviorId, out var behavior))
                    EnqueueChannel(new ChatMessage(Guid.NewGuid().ToString("N"), default, chatter.Name,
                        behavior.Describe(chatter.Node), now));
            }
        }
    }

    /// <summary>
    /// The initial population: a few entities on real forwarding nodes, spread deterministically. They
    /// are the background of the network, not a threat: nothing is spawned on a node the map does not
    /// forward through.
    /// </summary>
    private static void Populate(SharedMediumSnapshot world, long now, NetworkBotBehaviorRegistry behaviors)
    {
        var entries = behaviors.All;
        if (entries.Count == 0)
            return;
        foreach (var node in SpawnNodes(world, null).Take(MaxBots))
            NewBot(node, entries[(Life.NextBotId + 1) % entries.Count], now);
    }

    /// <summary>
    /// One step of an intruder towards the nearest live presence: the real cable route decides the next
    /// hop, so an agent never crosses a link the network does not have. Null means no presence is reachable.
    /// </summary>
    private static (NodeId Next, Presence? Reached)? Hunt(SharedMediumSnapshot world, NetworkBot bot)
    {
        Presence? target = null;
        var best = int.MaxValue;
        foreach (var presence in Presences.Values.Where(presence => !presence.Ended && !presence.Moving))
        {
            if (presence.Node == bot.Node)
                return (bot.Node, presence);
            var route = NetworkDiagnostics.Route(world, bot.Node, presence.Node, QueryHost.ReachBudget);
            if (route.Nodes.Count >= 2 && route.Nodes.Count < best)
            {
                best = route.Nodes.Count;
                target = presence;
            }
        }
        if (target is null)
            return null;
        var hop = NetworkDiagnostics.Route(world, bot.Node, target.Node, QueryHost.ReachBudget);
        return hop.Nodes.Count >= 2 ? (hop.Nodes[1].Node, null) : null;
    }

    private static void Record(string kind, NodeId node, string name, string text) =>
        Life.Record(kind, node, name, text);

    /// <summary>P13: the persona name an operator shows in the activity feed; a default while it is unset.</summary>
    private static string OperatorName(Presence presence)
    {
        if (presence.Masked)
            return "скрытый оператор";
        var identity = PersonaOf(presence.Owner);
        var name = (string)identity["name"];
        return name.Length == 0 ? "оператор" : name;
    }
}
