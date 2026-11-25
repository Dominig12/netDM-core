using NetRun.Core;

namespace NetDM.Host.Transport;

/// <summary>
/// The owner of the NETRUN domain inside the Host process (ADR-0002). All access is serialized through
/// one gate, exactly like <c>ServiceRuntime</c>: the domain is not thread-safe by design, because a
/// single logical writer is what keeps its composed invariants true. The gate is never held across an
/// await or IPC I/O.
/// </summary>
internal sealed class NetRunHostService : BackgroundService
{
    /// <summary>NETRUN reads the same monotonic quantum clock as the transport, in milliseconds.</summary>
    private sealed class QuantumClock(GameNetworkService game) : ISimulationClock
    {
        public long NowMilliseconds => game.CurrentTick * 100;
    }

    private readonly NetRunRuntime _runtime;
    private readonly ISimulationClock _clock;
    private readonly Dictionary<string, PrincipalId> _principals = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _ticks;

    public NetRunHostService(GameNetworkService game)
    {
        ArgumentNullException.ThrowIfNull(game);
        _clock = new QuantumClock(game);
        _runtime = new NetRunRuntime(_clock);
    }

    public long TickCount => Volatile.Read(ref _ticks);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    _runtime.Tick();
                    Interlocked.Increment(ref _ticks);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Maps a stable name from the game world to one principal for the round.</summary>
    public PrincipalId Principal(string name)
    {
        lock (_gate)
        {
            if (_principals.TryGetValue(name, out var existing))
                return existing;
            if (_principals.Count >= 65_536)
                return PrincipalId.None;
            var created = _runtime.NewPrincipal();
            if (created.IsNone)
                return PrincipalId.None;
            _principals[name] = created;
            return created;
        }
    }

    public HostResult RegisterHost(HostDefinition definition)
    {
        lock (_gate)
            return _runtime.RegisterHost(definition);
    }

    public HostResult DestroyHost(HostId id, HostGeneration generation)
    {
        lock (_gate)
            return _runtime.DestroyHost(id, generation);
    }

    public HostResult SetLifecycle(HostId id, HostGeneration generation, bool powered, bool operational)
    {
        lock (_gate)
            return _runtime.SetHostLifecycle(id, generation, powered, operational);
    }

    public AuthorizationResult Grant(PrincipalId subject, HostId target, CapabilityCategory category,
        string? serviceKey, long lifetimeMilliseconds)
    {
        lock (_gate)
            return _runtime.Authority.Grant(subject, target, category, serviceKey, lifetimeMilliseconds,
                GrantSource.Granted, PrincipalId.None);
    }

    public AuthorizationStatus Revoke(GrantId grant)
    {
        lock (_gate)
            return _runtime.Authority.Revoke(grant);
    }

    public ActionDescriptor[] Actions(int limit)
    {
        lock (_gate)
            return _runtime.Actions.Catalogue(limit);
    }

    /// <summary>Runs one registered action through the pipeline for a named principal.</summary>
    public ActionOutcome Execute(string principalName, string actionId, HostId executionHost, HostId targetHost,
        string idempotencyKey)
    {
        lock (_gate)
        {
            var principal = PrincipalLocked(principalName);
            if (principal.IsNone)
                return new ActionOutcome(ActionStatus.InvalidRequest, OperationId.None, ActionKind.Query,
                    ActionCategory.Perception, Reason: "principal");
            var action = _runtime.Actions.GetAction(actionId);
            if (action is null)
                return new ActionOutcome(ActionStatus.UnknownAction, OperationId.None, ActionKind.Query,
                    ActionCategory.Perception, Reason: "unknown_action");
            var request = new ActionRequest(principal, executionHost, targetHost, EntityId.None, ProcessId.None,
                CorrelationId.None, idempotencyKey);
            return _runtime.Actions.Execute(request, action);
        }
    }

    /// <summary>A bounded projection of the domain for diagnostics and the UI.</summary>
    public NetRunStateView State(int hostLimit)
    {
        lock (_gate)
        {
            var hosts = _runtime.Hosts.All(hostLimit);
            var views = new HostStateView[hosts.Length];
            for (var i = 0; i < hosts.Length; i++)
            {
                var host = hosts[i];
                var resources = _runtime.Resources.Snapshot(host.Id);
                views[i] = new HostStateView(host.Id.Value, host.Generation.Value, host.Name, host.Class.ToString(),
                    host.Powered, host.Operational, host.Compute?.CapacityCu ?? 0, host.Compute?.RateCuPerSecond ?? 0,
                    resources?.BufferCu ?? 0, resources?.MemoryHeld ?? 0, host.Memory?.Slots ?? 0,
                    _runtime.Effects.On(host.Id, 8).Length);
            }
            return new NetRunStateView(_runtime.NowMilliseconds, TickCount, views,
                _runtime.Actions.ActionCount, _runtime.Actions.OperationCount);
        }
    }

    /// <summary>Reads the current effect list of a host, for projections.</summary>
    public EffectInstance[] Effects(HostId host, int limit)
    {
        lock (_gate)
            return _runtime.Effects.On(host, limit);
    }

    /// <summary>Reads the live resources of one host.</summary>
    public ResourceSnapshot? Resources(HostId host)
    {
        lock (_gate)
            return _runtime.Resources.Snapshot(host);
    }

    /// <summary>Reads one host.</summary>
    public HostSnapshot? Host(HostId host)
    {
        lock (_gate)
            return _runtime.Hosts.Get(host);
    }

    /// <summary>Recent operations, newest first.</summary>
    public ActionOutcome[] RecentOperations(int limit)
    {
        lock (_gate)
            return _runtime.Actions.Recent(limit);
    }

    private PrincipalId PrincipalLocked(string name)
    {
        if (_principals.TryGetValue(name, out var existing))
            return existing;
        if (_principals.Count >= 65_536)
            return PrincipalId.None;
        var created = _runtime.NewPrincipal();
        if (created.IsNone)
            return PrincipalId.None;
        _principals[name] = created;
        return created;
    }
}

internal sealed record HostStateView(
    ulong Host, ulong Generation, string Name, string HostClass, bool Powered, bool Operational,
    int ComputeCapacity, int ComputeRate, double BufferCu, int MemoryHeld, int MemorySlots, int Effects);

internal sealed record NetRunStateView(
    long NowMilliseconds, long Ticks, HostStateView[] Hosts, int Actions, int Operations);
