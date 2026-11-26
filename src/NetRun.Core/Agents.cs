namespace NetRun.Core;

/// <summary>
/// What a strain does when it lands on a host, and what it costs the network (§12). An agent uses the existing
/// topology: it never creates hosts, links or services of its own.
/// </summary>
public sealed record AgentDefinition(
    string Strain,
    AgentKind Kind,
    long ComputeCu,
    long MemorySlots,
    int SpreadIntervalMilliseconds,
    int MaxGeneration,
    int MaxCopiesPerHost,
    int IntegrityLossPerHop)
{
    public AgentDefinition Validate()
    {
        if (string.IsNullOrWhiteSpace(Strain) || Strain.Length > 32)
            throw new ArgumentException("A strain needs a short name.", nameof(Strain));
        if (!Enum.IsDefined(Kind))
            throw new ArgumentException("Unknown agent kind.", nameof(Kind));
        if (ComputeCu < 0 || MemorySlots < 0 || MaxGeneration < 0 || MaxCopiesPerHost < 1)
            throw new ArgumentException("Agent costs and limits must be non-negative.", nameof(Strain));
        return this;
    }
}

public enum AgentKind : byte
{
    /// <summary>A virus: it copies itself to neighbours as long as its budget allows (§12.1).</summary>
    Virus,
    /// <summary>ICE: it does not spread. It defends the host it was placed on (§9.3, §12.2).</summary>
    Ice
}

public sealed record AgentSnapshot(
    ulong Id,
    string Strain,
    AgentKind Kind,
    HostId Host,
    int Generation,
    int Integrity,
    int SpreadsLeft,
    long NextSpreadAt,
    PrincipalId Owner)
{
    public bool Alive => Integrity > 0;
}

public enum AgentStatus : byte
{
    Ok, UnknownHost, HostUnavailable, InsufficientResources, LimitExceeded, UnknownAgent, InvalidRequest,
    Forbidden
}

public sealed record AgentResult(AgentStatus Status, AgentSnapshot? Agent, string? Reason = null)
{
    public bool Succeeded => Status == AgentStatus.Ok;
}

/// <summary>
/// Neighbours of a host, as the transport sees them. A virus spreads only to hosts that are adjacent on the
/// published graph, so its reach is the network's reach and nothing more.
/// </summary>
public interface IAgentNetwork
{
    HostId[] Neighbours(HostId host);
}

public sealed record AgentOptions(
    int MaxAgents = 4_096,
    int MaxAgentsPerOwner = 256,
    int MaxAgentsPerHost = 8,
    long DefaultBudget = 3);

/// <summary>
/// Viruses and ICE (§12). An agent is a process with an owner, a strain, an integrity and a finite budget of
/// copies; it lives on a host, costs that host compute and memory, and dies when its host does. Nothing here
/// creates network structure: an agent only uses links that already exist (AGENT-001).
/// </summary>
public sealed class AgentRegistry(
    ISimulationClock clock,
    HostRegistry hosts,
    ResourceLedger ledger,
    AgentOptions? options = null)
{
    private readonly ISimulationClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly HostRegistry _hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
    private readonly ResourceLedger _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    private readonly AgentOptions _options = options ?? new AgentOptions();
    private readonly Dictionary<ulong, AgentSnapshot> _agents = [];
    private readonly Dictionary<ulong, AgentDefinition> _definitions = [];
    private IAgentNetwork? _network;
    private ulong _nextId;

    public int Count => _agents.Count;

    /// <summary>Connects the topology an agent may spread over.</summary>
    public void AttachNetwork(IAgentNetwork network)
    {
        if (_network is not null)
            throw new InvalidOperationException("The agent network is already attached.");
        _network = network ?? throw new ArgumentNullException(nameof(network));
    }

    /// <summary>
    /// Places a strain on a host. The host must exist and be usable, and it must be able to pay the strain's
    /// compute and memory: an agent that cannot run is refused instead of being remembered as present.
    /// </summary>
    public AgentResult Deploy(PrincipalId owner, HostId host, AgentDefinition definition, long budget, int integrity)
    {
        definition = definition.Validate();
        if (owner.IsNone || host.IsNone)
            return new AgentResult(AgentStatus.InvalidRequest, null, "arguments");
        if (_agents.Count >= _options.MaxAgents)
            return new AgentResult(AgentStatus.LimitExceeded, null, "agents");
        if (CountFor(owner) >= _options.MaxAgentsPerOwner)
            return new AgentResult(AgentStatus.LimitExceeded, null, "owner");
        if (CountOn(host) >= Math.Min(_options.MaxAgentsPerHost, definition.MaxCopiesPerHost))
            return new AgentResult(AgentStatus.LimitExceeded, null, "host");
        if (_hosts.Get(host) is not { Available: true } snapshot)
            return new AgentResult(AgentStatus.HostUnavailable, null, "host");
        if (snapshot.Compute is null || snapshot.Compute.CapacityCu < definition.ComputeCu)
            return new AgentResult(AgentStatus.InsufficientResources, null, "compute");
        if (snapshot.Memory is null || snapshot.Memory.Slots < definition.MemorySlots)
            return new AgentResult(AgentStatus.InsufficientResources, null, "memory");
        // The strain's demand is reserved against the host's buffer, so a virus competes for compute like any
        // other program instead of being free (RES-001).
        var take = definition.ComputeCu * 1_000L;
        if (take > 0 && _ledger.Drain(host, take) <= 0)
            return new AgentResult(AgentStatus.InsufficientResources, null, "buffer");
        var agent = new AgentSnapshot(++_nextId, definition.Strain, definition.Kind, host, 1,
            Math.Clamp(integrity, 1, 100), (int)Math.Clamp(budget, 0, 64),
            _clock.NowMilliseconds + definition.SpreadIntervalMilliseconds, owner);
        _agents[agent.Id] = agent;
        _definitions[agent.Id] = definition;
        return new AgentResult(AgentStatus.Ok, agent);
    }

    private int CountFor(PrincipalId owner)
    {
        var count = 0;
        foreach (var agent in _agents.Values)
            if (agent.Owner == owner)
                count++;
        return count;
    }

    private int CountOn(HostId host)
    {
        var count = 0;
        foreach (var agent in _agents.Values)
            if (agent.Host == host)
                count++;
        return count;
    }

    public AgentSnapshot? Get(ulong id) => _agents.GetValueOrDefault(id);

    public AgentSnapshot[] On(HostId host, int limit)
    {
        var result = new List<AgentSnapshot>();
        foreach (var agent in _agents.Values.OrderBy(agent => agent.Id))
        {
            if (result.Count >= limit)
                break;
            if (agent.Host == host)
                result.Add(agent);
        }
        return result.ToArray();
    }

    public AgentSnapshot[] OwnedBy(PrincipalId owner, int limit)
    {
        if (owner.IsNone || limit <= 0)
            return [];
        var result = new List<AgentSnapshot>();
        foreach (var agent in _agents.Values.OrderBy(agent => agent.Id))
        {
            if (result.Count >= limit)
                break;
            if (agent.Owner == owner)
                result.Add(agent);
        }
        return result.ToArray();
    }

    /// <summary>
    /// One tick of the infection: a virus with copies left and its interval elapsed sends a copy to an adjacent
    /// host that can pay for it. A copy is weaker than its parent and never outlives the strain's generation cap;
    /// a host that cannot pay is skipped, not infected for free.
    /// </summary>
    public int Tick()
    {
        var now = _clock.NowMilliseconds;
        var spread = 0;
        foreach (var agent in _agents.Values.ToArray())
        {
            var definition = _definitions[agent.Id];
            if (agent.Kind != AgentKind.Virus || agent.SpreadsLeft <= 0 || agent.NextSpreadAt > now)
                continue;
            if (agent.Generation >= definition.MaxGeneration)
                continue;
            if (_network is null)
                continue;
            var moved = agent with
            {
                SpreadsLeft = agent.SpreadsLeft - 1,
                NextSpreadAt = now + definition.SpreadIntervalMilliseconds
            };
            var infected = false;
            foreach (var neighbour in _network.Neighbours(agent.Host))
            {
                if (neighbour == agent.Host || CountOn(neighbour) >= definition.MaxCopiesPerHost)
                    continue;
                var child = Deploy(agent.Owner, neighbour, definition, moved.SpreadsLeft,
                    Math.Max(1, agent.Integrity - definition.IntegrityLossPerHop));
                if (!child.Succeeded)
                    continue;
                var grown = child.Agent! with { Generation = agent.Generation + 1 };
                _agents[child.Agent!.Id] = grown;
                infected = true;
                spread++;
                break;
            }
            _agents[agent.Id] = moved;
            if (!infected)
                continue;
        }
        return spread;
    }

    /// <summary>Removes one agent by id. A cleaned agent frees whatever its host reserved implicitly.</summary>
    public AgentResult Clean(PrincipalId actor, ulong id)
    {
        if (!_agents.TryGetValue(id, out var agent))
            return new AgentResult(AgentStatus.UnknownAgent, null);
        if (!agent.Owner.Equals(actor))
            return new AgentResult(AgentStatus.Forbidden, agent, "owner");
        _agents.Remove(id);
        _definitions.Remove(id);
        return new AgentResult(AgentStatus.Ok, agent);
    }

    /// <summary>Purges every agent on one host. Purging is a repair of the host, so it needs no ownership.</summary>
    public int Purge(HostId host)
    {
        var removed = 0;
        foreach (var agent in _agents.Values.ToArray())
        {
            if (agent.Host != host)
                continue;
            _agents.Remove(agent.Id);
            _definitions.Remove(agent.Id);
            removed++;
        }
        return removed;
    }

    /// <summary>Agents die with their host: they are processes, not records.</summary>
    public int OnHostDestroyed(HostId host) => Purge(host);

    /// <summary>A damaged agent loses integrity; at zero it is gone. Effects reach agents this way.</summary>
    public int Damage(ulong id, int amount)
    {
        if (amount <= 0 || !_agents.TryGetValue(id, out var agent))
            return 0;
        var before = agent.Integrity;
        var after = agent.Integrity - amount;
        if (after <= 0)
        {
            _agents.Remove(id);
            _definitions.Remove(id);
            return before;
        }
        _agents[id] = agent with { Integrity = after };
        return before - after;
    }
}
