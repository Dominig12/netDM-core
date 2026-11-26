namespace NetRun.Core;

public sealed record NetRunRuntimeOptions(
    int MaxHosts = 16_384,
    int MaxReservations = 32_768,
    int MaxGrants = 32_768,
    int MaxEffects = 16_384,
    int MaxOperations = 16_384,
    int MaxPrincipals = 65_536,
    int MaxKnowledgePerObserver = 1_024,
    int MaxPrograms = 4_096,
    bool RegisterMvpEffects = true,
    bool RegisterBuiltinActions = true);

/// <summary>What one tick of the simulation changed, for diagnostics and tests.</summary>
public sealed record NetRunTickResult(
    long NowMilliseconds,
    int ExpiredReservations,
    int ExpiredEffects,
    int ExpiredOperations,
    int PublishedEvents,
    int ProgramInstructions)
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
public sealed class NetRunRuntime : IEffectTargets
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
        Knowledge = new KnowledgeStore(clock, new KnowledgeOptions(MaxPerObserver: _options.MaxKnowledgePerObserver));
        Actions = new ActionPipeline(clock, Hosts, Resources, Authority,
            new ActionPipelineOptions(MaxOperations: _options.MaxOperations));
        Actions.AttachEffects(Effects);
        Actions.AttachKnowledge(Knowledge);
        // Programs run through the same pipeline, so they need it to exist first.
        Programs = new ProgramScheduler(clock, Actions, new ProgramOptions(MaxPrograms: _options.MaxPrograms));
        // Effects change the pools and the lives the runtime owns; without this seam they would only be recorded.
        Effects.AttachTargets(new EffectTargets(this));
        Lives = new DigitalLifeRegistry(clock, Hosts, Resources);
        Presences = new PresenceRegistry(clock, Hosts, Lives);
        Agents = new AgentRegistry(clock, Hosts, Resources);
        Actions.AttachAgents(Agents);
        Alerts = new AlertRegistry(clock);
        Actions.AttachAlerts(Alerts);
        Actions.AttachLives(Lives);
        // A closed host notices a stranger arriving at it: the alarm and the trace are policy, so they live
        // here rather than inside the registry that only records where presences stand (§9.3, §9.4).
        Presences.AttachArrivals(new ArrivalPolicy(this));
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

    public KnowledgeStore Knowledge { get; }

    /// <summary>The bounded executor of user programs (§17.3). It runs through the same action pipeline.</summary>
    public ProgramScheduler Programs { get; }

    /// <summary>Digital lives and their cores (§13.4). One identity has exactly one active controlling core.</summary>
    public DigitalLifeRegistry Lives { get; }

    /// <summary>Where digital presences stand and how they travel (§8.1).</summary>
    public PresenceRegistry Presences { get; }

    /// <summary>Viruses and ICE that live on the network (§12).</summary>
    public AgentRegistry Agents { get; }

    /// <summary>Per-host alert levels (§9.3): raised by what a host notices, lowered by quiet time.</summary>
    public AlertRegistry Alerts { get; }

    /// <summary>Connects the transport that plans travel routes.</summary>
    public void AttachRoutePlanner(IRoutePlanner planner) => Presences.AttachPlanner(planner);

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
            // A new generation is a new watch: the alarm of the object that is gone does not describe this one.
            Alerts.Forget(previous.Id);
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
        Knowledge.Forget(host.Id);
        // A projection of a host that no longer exists must not keep describing it as alarmed.
        Alerts.Forget(host.Id);
        Actions.CancelTarget(host.Id, "target_destroyed");
        Programs.CancelHost(host.Id);
        Lives.OnHostDestroyed(host.Id);
        Presences.OnHostDestroyed(host.Id);
        Agents.OnHostDestroyed(host.Id);
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
        Knowledge.Prune();
        var operations = Actions.ExpirePrepared(now) + Actions.ExpireRemote(now);
        Programs.Reap();
        Lives.Tick();
        Presences.Tick();
        Agents.Tick();
        // An alarm that nothing renews steps back down on its own, whether or not somebody is watching.
        Alerts.Sweep();
        var instructions = Programs.Advance(now);
        var published = PublishEvents();
        return new NetRunTickResult(now, expiredReservations, effects, operations, published, instructions);
    }

    /// <summary>
    /// Moves the events of committed actions to their topics. Events are recorded after the authoritative
    /// change and are never the way a change happens (PRIM-004).
    /// </summary>
    public int PublishEvents()
    {
        var events = Actions.DrainEvents();
        foreach (var item in events)
        {
            _publishedTopics[item.Topic] = _publishedTopics.GetValueOrDefault(item.Topic) + 1;
            // §9.3: an alarm reaches the neighbours over the links that exist, one hop and no further.
            // The host that noticed raised its own level; its neighbours only become suspicious.
            if (item.Topic == "security.breach.detected" && Hosts.Get(item.Payload) is { } alarmed)
                foreach (var neighbour in Agents.NeighboursOf(alarmed.Id))
                    if (!neighbour.IsNone && neighbour != alarmed.Id)
                        Alerts.Raise(neighbour, 1, "neighbour_alerted");
        }
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

    // ---- IEffectTargets: what an effect changes when it lands -------------------------------------------------

    long IEffectTargets.DrainCompute(HostId host, long milliCu) => Resources.Drain(host, milliCu);

    int IEffectTargets.DamageLife(HostId host, int amount) => Lives.DamageCore(host, amount);

    int IEffectTargets.RepairLife(HostId host, int amount) => Lives.RepairCore(host, amount);

    /// <summary>
    /// Damage the host currently resists. A live shield is the only source today; the rule lives here so a
    /// future resistance effect does not need a new call site.
    /// </summary>
    int IEffectTargets.Resistance(HostId host)
    {
        var best = 0;
        foreach (var effect in Effects.On(host, 16))
            if (effect.Kind == EffectKind.Shield && effect.Magnitude > best)
                best = effect.Magnitude;
        return best;
    }

    private sealed class EffectTargets(NetRunRuntime runtime) : IEffectTargets
    {
        public long DrainCompute(HostId host, long milliCu) => ((IEffectTargets)runtime).DrainCompute(host, milliCu);

        public int DamageLife(HostId host, int amount) => ((IEffectTargets)runtime).DamageLife(host, amount);

        public int RepairLife(HostId host, int amount) => ((IEffectTargets)runtime).RepairLife(host, amount);

        public int Resistance(HostId host) => ((IEffectTargets)runtime).Resistance(host);
    }

    /// <summary>
    /// What a host makes of a presence landing on it (§9.3, §9.4). An ordinary host does not check papers: the
    /// arrival changes nothing, because a presence is not authority (§8.1). A secured or quarantined host
    /// notices the stranger, raises its own alarm and lets its guards learn the way the arrival came. The
    /// arrival itself is never refused here — the defence is a reaction, not a wall (§9.3, SEC-002).
    /// </summary>
    private sealed class ArrivalPolicy(NetRunRuntime runtime) : IPresenceArrivalSink
    {
        /// <summary>How many guards are told about one arrival; the same bound the rest of the domain uses.</summary>
        private const int GuardSlots = 8;

        public void Arrived(PrincipalId controller, HostId host, HostId source)
        {
            if (controller.IsNone || host.IsNone)
                return;
            if (runtime.Hosts.Get(host) is not { } snapshot)
                return;
            var security = snapshot.Security;
            if (security is null || security.Profile < SecurityProfile.Secure)
                return;
            // A host that already lets this principal look around has nothing to be alarmed about.
            if (runtime.Authority.Authorize(controller, host, CapabilityCategory.Discovery, null, security.Profile).Allowed)
                return;
            runtime.Alerts.Raise(host, 1, "presence_intrusion");
            if (source.IsNone || source == host)
                return;
            foreach (var guard in runtime.Agents.On(host, GuardSlots))
            {
                if (guard.Kind != AgentKind.Ice || guard.Owner.IsNone || guard.Owner == controller)
                    continue;
                runtime.Knowledge.Remember(guard.Owner, source, KnowledgeLevel.Observed, 55);
            }
        }
    }

    private static ulong NextSlot(ref ulong counter, int limit)
    {
        if (counter >= (ulong)limit)
            return 0;
        return ++counter;
    }
}
