namespace NetRun.Core;

public sealed record NetRunRuntimeOptions(
    int MaxHosts = 16_384,
    int MaxReservations = 32_768,
    int MaxGrants = 32_768,
    int MaxEffects = 16_384,
    int MaxOperations = 16_384,
    int MaxPrincipals = 65_536,
    bool RegisterMvpEffects = true,
    bool RegisterBuiltinActions = true);

/// <summary>What one tick of the simulation changed, for diagnostics and tests.</summary>
public sealed record NetRunTickResult(
    long NowMilliseconds,
    int ExpiredReservations,
    int ExpiredEffects,
    int ExpiredOperations,
    int PublishedEvents)
{
    public bool ChangedAnything =>
        ExpiredReservations > 0 || ExpiredEffects > 0 || ExpiredOperations > 0 || PublishedEvents > 0;
}

/// <summary>
/// The single logical owner of the NETRUN domain (ARCH-003, ADR-0002). It composes the domain owners,
/// coordinates lifecycle across them, and is the only place where a host appears or disappears together
/// with its resources, grants, effects and pending operations. Callers read through the domain APIs and
/// mutate only through methods here or through the domains they own.
/// </summary>
public sealed class NetRunRuntime
{
    private readonly ISimulationClock _clock;
    private readonly NetRunRuntimeOptions _options;
    private readonly Dictionary<string, int> _publishedTopics = new(StringComparer.Ordinal);
    private ulong _nextPrincipal;
    private ulong _nextIdentity;

    public NetRunRuntime(ISimulationClock clock, NetRunRuntimeOptions? options = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new NetRunRuntimeOptions();
        Hosts = new HostRegistry(clock, new HostRegistryOptions(_options.MaxHosts));
        Resources = new ResourceLedger(clock, new ResourceLedgerOptions(_options.MaxReservations));
        Authority = new CapabilityAuthority(clock, new CapabilityAuthorityOptions(_options.MaxGrants));
        Effects = new EffectRegistry(clock, new EffectRegistryOptions(_options.MaxEffects));
        Actions = new ActionPipeline(clock, Hosts, Resources, Authority,
            new ActionPipelineOptions(MaxOperations: _options.MaxOperations));
        Actions.AttachEffects(Effects);
        if (_options.RegisterMvpEffects)
        {
            foreach (var definition in EffectRegistry.MvpDefaults())
                Effects.Register(definition);
        }
        if (_options.RegisterBuiltinActions)
            BuiltinActions.Register(Actions);
    }

    public HostRegistry Hosts { get; }

    public ResourceLedger Resources { get; }

    public CapabilityAuthority Authority { get; }

    public EffectRegistry Effects { get; }

    public ActionPipeline Actions { get; }

    public long NowMilliseconds => _clock.NowMilliseconds;

    /// <summary>Allocates the identity of an actor that can hold grants and spend resources.</summary>
    public PrincipalId NewPrincipal() => new(NextSlot(ref _nextPrincipal, _options.MaxPrincipals));

    /// <summary>Allocates a persistent digital identity. A principal is its current controller.</summary>
    public IdentityId NewIdentity() => new(NextSlot(ref _nextIdentity, _options.MaxPrincipals));

    /// <summary>
    /// Registers a host and creates its resource pools in one step, so no reader ever sees a host without
    /// the pools its components promise (HOST-001, RES-001).
    /// </summary>
    public HostResult RegisterHost(HostDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var result = Hosts.Register(definition);
        if (result.Status == HostStatus.Ok && result.Host is { } host)
            Resources.Attach(host.Id, definition.Compute, definition.Memory, definition.Storage);
        return result;
    }

    /// <summary>Rebinds a name to a new instance: a new generation, fresh pools, old grants dropped.</summary>
    public HostResult RebindHost(string name, HostDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var previous = Hosts.Get(name);
        var result = Hosts.Rebind(name, definition);
        if (result.Status != HostStatus.Ok || result.Host is not { } host)
            return result;
        if (previous is not null)
        {
            Resources.Detach(previous.Id);
            Authority.RevokeTarget(previous.Id);
            Effects.RemoveTarget(previous.Id);
            Actions.CancelTarget(previous.Id, "rebound");
        }
        Resources.Attach(host.Id, definition.Compute, definition.Memory, definition.Storage);
        return result;
    }

    /// <summary>
    /// Destroys one generation of a host and everything that depended on it: reservations are revoked,
    /// grants dropped, effects removed and undelivered operations cancelled. A stale generation changes
    /// nothing at all, so a late message cannot tear down its successor.
    /// </summary>
    public HostResult DestroyHost(HostId id, HostGeneration generation)
    {
        var result = Hosts.Destroy(id, generation);
        if (result.Status != HostStatus.Ok || result.Host is not { } host)
            return result;
        Resources.Detach(host.Id);
        Authority.RevokeTarget(host.Id);
        Effects.RemoveTarget(host.Id);
        Actions.CancelTarget(host.Id, "target_destroyed");
        return result;
    }

    /// <summary>Lifecycle input from the physical world, already validated for the right generation.</summary>
    public HostResult SetHostLifecycle(HostId id, HostGeneration generation, bool powered, bool operational) =>
        Hosts.SetLifecycle(id, generation, powered, operational);

    /// <summary>
    /// Advances every time-driven domain once. Recovery is credited here and only here, so calling it
    /// twice with the same game time changes nothing and never double-credits a buffer.
    /// </summary>
    public NetRunTickResult Tick()
    {
        var now = _clock.NowMilliseconds;
        var before = Resources.ActiveCount;
        Resources.AdvanceTo(now);
        var expiredReservations = Math.Max(0, before - Resources.ActiveCount);
        var effects = Effects.Tick(now);
        var operations = Actions.ExpirePrepared(now) + Actions.ExpireRemote(now);
        var published = PublishEvents();
        return new NetRunTickResult(now, expiredReservations, effects, operations, published);
    }

    /// <summary>
    /// Moves the events of committed actions to their topics. Events are recorded after the authoritative
    /// change and are never the way a change happens (PRIM-004).
    /// </summary>
    public int PublishEvents()
    {
        var events = Actions.DrainEvents();
        foreach (var item in events)
            _publishedTopics[item.Topic] = _publishedTopics.GetValueOrDefault(item.Topic) + 1;
        return events.Length;
    }

    /// <summary>Bounded counters of published topics, for diagnostics only.</summary>
    public (string Topic, int Count)[] PublishedTopicCounts(int limit)
    {
        var count = Math.Min(Math.Max(limit, 0), _publishedTopics.Count);
        var result = new (string, int)[count];
        var index = 0;
        foreach (var (topic, published) in _publishedTopics.OrderByDescending(pair => pair.Value))
        {
            if (index >= count)
                break;
            result[index++] = (topic, published);
        }
        return result;
    }

    private static ulong NextSlot(ref ulong counter, int limit)
    {
        if (counter >= (ulong)limit)
            return 0;
        return ++counter;
    }
}
