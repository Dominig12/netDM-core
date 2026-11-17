using NetDM.Core;

namespace NetDM.Services;

/// <summary>
/// Read-only, bounded observation offered to an NPC behavior. No ServiceRuntime, packet send,
/// arbitrary topology lookup or DM callback is exposed to a behavior.
/// </summary>
public readonly record struct NetworkBotState(int Id, NodeId Node, int Moves, long Fuel, string State);

/// <summary>The host fills this with genuine next hops and a routed pursuit step.</summary>
public sealed record NetworkBotObservation(
    IReadOnlyList<NodeId> Adjacent,
    NodeId? PursuitStep,
    bool TargetHere,
    long Serial);

/// <summary>Intent, not an executed action: the Host validates the next hop and any intrusion.</summary>
public readonly record struct NetworkBotDecision(
    NodeId? Next,
    bool Engage = false,
    string? ActivityKind = null,
    string? ActivityText = null);

/// <summary>Enforces a single physical hop and prevents non-hostile engagement.</summary>
public static class NetworkBotDecisionPolicy
{
    public static bool Allows(NodeId current, bool hostile,
        NetworkBotObservation observation, NetworkBotDecision decision) =>
        decision.Next is { } next &&
        (next == current || observation.Adjacent.Contains(next)) &&
        (!decision.Engage || (hostile && observation.TargetHere && next == current));
}

/// <summary>
/// Pluggable, stateless network NPC strategy. Add implementations through DI as
/// INetworkBotBehavior without changing NetworkSpace or its control-plane switch.
/// </summary>
public interface INetworkBotBehavior
{
    string Id { get; }
    string Name { get; }
    string Signature { get; }
    bool Hostile { get; }
    NetworkBotDecision Decide(NetworkBotState bot, NetworkBotObservation observation);
    string Describe(NodeId node);
}

/// <summary>Generic deterministic patrol; differences in role and narration are data.</summary>
public sealed class PatrolBotBehavior(
    string id, string name, string signature, Func<NodeId, string> description,
    string activityKind = "task") : INetworkBotBehavior
{
    public string Id => id;
    public string Name => name;
    public string Signature => signature;
    public bool Hostile => false;
    public string Describe(NodeId node) => description(node);

    public NetworkBotDecision Decide(NetworkBotState bot, NetworkBotObservation observation)
    {
        if (observation.Adjacent.Count == 0)
            return default;
        var hop = observation.Adjacent[(int)(observation.Serial % observation.Adjacent.Count)];
        var report = (bot.Moves + 1) % 3 == 0;
        return new NetworkBotDecision(hop, ActivityKind: report ? activityKind : null,
            ActivityText: report ? Describe(hop) : null);
    }
}

/// <summary>Chases presences by a Host-computed physical route, without authority to reach services.</summary>
public sealed class IntruderBotBehavior : INetworkBotBehavior
{
    public string Id => "intruder";
    public string Name => "rogue";
    public string Signature => "R";
    public bool Hostile => true;

    public NetworkBotDecision Decide(NetworkBotState bot, NetworkBotObservation observation) =>
        observation.PursuitStep is { } hop
            ? new NetworkBotDecision(hop, Engage: observation.TargetHere)
            : default;

    public string Describe(NodeId node) => $"rogue scanned node {node.Value}, looking for operators";
}

/// <summary>
/// Immutable catalog. Duplicate or malformed IDs fail startup; existing behaviors cannot
/// be silently shadowed by an extension. All entries are safe for the shell's text framing.
/// </summary>
public sealed class NetworkBotBehaviorRegistry
{
    private readonly Dictionary<string, INetworkBotBehavior> _byId = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<INetworkBotBehavior> All { get; }

    public NetworkBotBehaviorRegistry(IEnumerable<INetworkBotBehavior>? additional = null)
    {
        INetworkBotBehavior[] defaults =
        [
            new PatrolBotBehavior("sentinel", "sentinel", "S",
                node => $"sentinel completed a patrol lap at {node.Value}"),
            new PatrolBotBehavior("watcher", "watcher", "W",
                node => $"watcher is watching traffic at {node.Value}", "watch"),
            new PatrolBotBehavior("archivist", "archivist", "A",
                node => $"archivist indexed traffic at {node.Value}"),
            new PatrolBotBehavior("courier", "courier", "C",
                node => $"courier delivered a packet to {node.Value}"),
            new IntruderBotBehavior(),
            new PatrolBotBehavior("saboteur", "saboteur", "X",
                node => $"saboteur looked for a breaker at {node.Value}"),
            new PatrolBotBehavior("engineer", "engineer", "E",
                node => $"engineer checked the power feed at {node.Value}")
        ];
        foreach (var behavior in defaults.Concat(additional ?? []))
        {
            ArgumentNullException.ThrowIfNull(behavior);
            if (!ValidId(behavior.Id) || !ValidDisplay(behavior.Name, 24) ||
                !ValidDisplay(behavior.Signature, 4) || !_byId.TryAdd(behavior.Id, behavior))
                throw new ArgumentException($"Duplicate or invalid network bot behavior '{behavior.Id}'.");
        }
        All = _byId.Values.ToArray();
    }

    public bool TryGet(string id, out INetworkBotBehavior behavior) =>
        _byId.TryGetValue(id, out behavior!);

    private static bool ValidId(string? id) => id is { Length: > 0 and <= 32 } &&
        id.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) ||
            character is '.' or '-' or '_');

    private static bool ValidDisplay(string? value, int max) => value is { Length: > 0 } &&
        value.Length <= max && value.All(character =>
            !char.IsControl(character) && character is not ('|' or ';'));
}
