using System.Text.Json;
using NetDM.Core;
using NetDM.Services;
using NetRun.Core;

namespace NetDM.Host.Transport;

/// <summary>
/// The owner of the NETRUN domain inside the Host process (ADR-0002). All access is serialized through
/// one gate, exactly like <c>ServiceRuntime</c>: the domain is not thread-safe by design, because a
/// single logical writer is what keeps its composed invariants true. The gate is never held across an
/// await or IPC I/O.
/// </summary>
internal sealed class NetRunHostService : BackgroundService, INetRunTransport
{
    /// <summary>NETRUN reads the same monotonic quantum clock as the transport, in milliseconds.</summary>
    private sealed class QuantumClock(GameNetworkService game) : ISimulationClock
    {
        public long NowMilliseconds => game.CurrentTick * 100;
    }

    private readonly NetRunRuntime _runtime;
    private readonly ISimulationClock _clock;
    private readonly GameNetworkService _game;
    private readonly Dictionary<string, PrincipalId> _principals = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, EndpointKey> _endpoints = [];
    private readonly object _gate = new();
    private long _ticks;

    // Lock order: this gate, then the transport session gate, then the service runtime gate. Nothing in
    // the transport knows about NETRUN, so the order cannot be reversed.
    public NetRunHostService(GameNetworkService game)
    {
        ArgumentNullException.ThrowIfNull(game);
        _game = game;
        _clock = new QuantumClock(game);
        _runtime = new NetRunRuntime(_clock);
        _runtime.Actions.AttachTransport(this);
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

    /// <summary>
    /// Binds a principal to the network endpoint that speaks for it. A remote action travels as that
    /// endpoint, so the transport authorizes the same identity the domain authorized. The endpoint itself
    /// is attached to a node by the world, not here.
    /// </summary>
    public ServiceStatus BindEndpoint(PrincipalId principal, EndpointKey endpoint)
    {
        if (principal.IsNone || string.IsNullOrEmpty(endpoint.Id))
            return ServiceStatus.InvalidRequest;
        lock (_gate)
            _endpoints[principal.Value] = endpoint;
        return ServiceStatus.Ok;
    }

    /// <summary>
    /// Carries a remote action over NETDM. Admission here means the command was accepted by the service
    /// runtime for physical delivery; it does not mean the door moved (G-001, SVC-002).
    /// </summary>
    public RemoteDispatch Send(in ActionContext context, string serviceType, string instance, string command,
        string idempotencyKey, string argumentsJson)
    {
        EndpointKey initiator;
        lock (_gate)
        {
            if (!_endpoints.TryGetValue(context.Principal.Value, out initiator))
                return new RemoteDispatch(ActionStatus.Forbidden, "no_endpoint");
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(argumentsJson);
        }
        catch (JsonException)
        {
            return new RemoteDispatch(ActionStatus.InvalidRequest, "arguments");
        }
        using (document)
        {
            var result = _game.Invoke(initiator, instance, command, idempotencyKey, document.RootElement,
                null, PacketTrafficClass.Interactive);
            return result.Status switch
            {
                ServiceStatus.Ok => new RemoteDispatch(ActionStatus.Accepted),
                ServiceStatus.Duplicate => new RemoteDispatch(ActionStatus.Accepted, "duplicate"),
                ServiceStatus.Forbidden => new RemoteDispatch(ActionStatus.Forbidden, "forbidden"),
                ServiceStatus.UnknownEndpoint => new RemoteDispatch(ActionStatus.Forbidden, "unknown_endpoint"),
                ServiceStatus.UnknownNode => new RemoteDispatch(ActionStatus.TargetDestroyed, "no_route"),
                ServiceStatus.UnknownService => new RemoteDispatch(ActionStatus.TargetDestroyed, "unknown_service"),
                ServiceStatus.UnknownCommand => new RemoteDispatch(ActionStatus.InvalidState, "unknown_command"),
                ServiceStatus.IdempotencyConflict => new RemoteDispatch(ActionStatus.IdempotencyConflict, "idempotency"),
                ServiceStatus.Busy => new RemoteDispatch(ActionStatus.Rejected, "busy"),
                ServiceStatus.LimitExceeded => new RemoteDispatch(ActionStatus.LimitExceeded, "limit"),
                _ => new RemoteDispatch(ActionStatus.Rejected, result.Status.ToString())
            };
        }
    }

    /// <summary>
    /// Imports program text and starts it as the named principal. The text carries no identity, no rights and
    /// no host from another round: the owner and the execution host come from here, and the pipeline checks
    /// every invoked action again (SCRIPT-003, AT-27).
    /// </summary>
    public (ProgramStatus Status, string? Reason, ProcessId Process, string? Name) StartProgram(
        string principalName, string text, HostId executionHost, string programName)
    {
        lock (_gate)
        {
            var principal = PrincipalLocked(principalName);
            if (principal.IsNone)
                return (ProgramStatus.InvalidDefinition, "principal", ProcessId.None, null);
            var host = _runtime.Hosts.Get(executionHost);
            if (host is not { Available: true })
                return (ProgramStatus.InvalidDefinition, "execution_host", ProcessId.None, null);
            // Starting a program spends the host's capacity, so it is a use of that host in its own right;
            // every action the program then invokes is authorized again at commit (SEC-001).
            var profile = host.Security?.Profile ?? SecurityProfile.Standard;
            if (!_runtime.Authority.Authorize(principal, executionHost, CapabilityCategory.Execution, null, profile).Allowed)
                return (ProgramStatus.Forbidden, "execution_host_right", ProcessId.None, null);
            var parsed = ProgramSource.TryParse(text, out var source, out var parseError);
            if (parsed != ProgramStatus.Ok || source is null)
                return (parsed == ProgramStatus.Ok ? ProgramStatus.InvalidDefinition : parsed,
                    parseError ?? "source", ProcessId.None, null);
            var name = string.IsNullOrEmpty(programName) ? source.Name : programName;
            if (string.IsNullOrEmpty(name) || name.Length > 64)
                return (ProgramStatus.InvalidDefinition, "name", ProcessId.None, null);
            var definition = new ProgramDefinition(name, executionHost, source.Steps, source.InstructionBudget);
            var started = _runtime.Programs.Start(principal, definition);
            return (started.Status, started.Reason, started.Process, started.Status == ProgramStatus.Ok ? name : null);
        }
    }

    /// <summary>Stops a program the caller owns. A program of somebody else is not even visible here.</summary>
    public ProgramStatus StopProgram(string principalName, ProcessId process)
    {
        lock (_gate)
        {
            var snapshot = _runtime.Programs.Peek(process);
            if (snapshot is null)
                return ProgramStatus.UnknownProgram;
            var principal = PrincipalLocked(principalName);
            if (snapshot.Owner != principal)
                return ProgramStatus.NotRunning;
            return _runtime.Programs.Cancel(process).Status;
        }
    }

    /// <summary>Finalizes an accepted remote operation with what the world reported.</summary>
    public ActionOutcome Complete(OperationId operation, ActionStatus status, string? reason, string? result)
    {
        lock (_gate)
            return _runtime.Actions.Complete(operation, status, reason, result);
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

    /// <summary>What one principal knows, freshest first. Age and staleness are computed by the caller.</summary>
    public KnowledgeRecord[] Knowledge(string principalName, int limit)
    {
        lock (_gate)
            return _principals.TryGetValue(principalName, out var principal)
                ? _runtime.Knowledge.Known(principal, limit)
                : [];
    }

    /// <summary>Whether a record is older than the freshness window, using the domain clock.</summary>
    public bool IsStale(KnowledgeRecord record) => _runtime.Knowledge.IsStale(record);

    /// <summary>
    /// What one observer may see of the digital world: only hosts it holds knowledge of, never the name of a
    /// host it has merely scanned, and never a hidden host at all. This is the projection the client draws
    /// (UI-003, AT-12); a developer can still read the unfiltered state separately.
    /// </summary>
    public ObservationView[] View(string principalName, int limit)
    {
        lock (_gate)
        {
            if (!_principals.TryGetValue(principalName, out var principal))
                return [];
            var records = _runtime.Knowledge.Known(principal, limit);
            var views = new List<ObservationView>(records.Length);
            var now = _runtime.NowMilliseconds;
            foreach (var record in records)
            {
                var host = _runtime.Hosts.Get(record.Host);
                if (host is null)
                    continue;
                var resources = _runtime.Resources.Snapshot(host.Id);
                var identified = record.Level >= KnowledgeLevel.Identified;
                views.Add(new ObservationView(
                    host.Id.Value,
                    identified ? host.Name : string.Empty,
                    host.Network?.Node?.Value ?? 0,
                    host.Class.ToString(),
                    record.Level.ToString().ToLowerInvariant(),
                    record.Confidence,
                    record.AgeMilliseconds(now),
                    _runtime.Knowledge.IsStale(record),
                    host.Powered && host.Operational,
                    resources?.BufferCu ?? 0,
                    _runtime.Effects.On(host.Id, 8).Length,
                    identified));
            }
            return views.ToArray();
        }
    }

    /// <summary>
    /// One bounded payload for a client refresh: what the observer may see, the actions it may attempt and
    /// its own recent operations. Filtering happens here, before serialization, so a hidden host never
    /// reaches the client even if the client would not draw it (UI-003, §15.3).
    /// </summary>
    public object Panel(string principalName, int hostLimit, int actionLimit, int operationLimit)
    {
        lock (_gate)
        {
            var views = View(principalName, hostLimit);
            var operations = _runtime.Actions.Recent(operationLimit);
            var principal = _principals.TryGetValue(principalName, out var known) ? known : PrincipalId.None;
            var records = principal.IsNone ? [] : _runtime.Knowledge.Known(principal, hostLimit);
            var stale = 0;
            foreach (var record in records)
                if (_runtime.Knowledge.IsStale(record))
                    stale++;
            return new
            {
                now = _runtime.NowMilliseconds,
                ticks = TickCount,
                principal = principal.Value,
                hosts = views,
                links = Links(views),
                actions = _runtime.Actions.Catalogue(actionLimit).Select(action => new
                {
                    id = action.Id,
                    kind = action.Kind.ToString().ToLowerInvariant(),
                    category = action.Category.ToString().ToLowerInvariant(),
                    capability = action.RequiredCapability.ToString().ToLowerInvariant(),
                    computeCost = action.ComputeCost,
                    memoryCost = action.MemoryCost,
                    cooldownMs = action.CooldownMilliseconds,
                    rangeHops = action.RangeHops,
                    displayName = action.DisplayName,
                    documentation = action.Documentation
                }).ToArray(),
                operations = operations.Select(operation => new
                {
                    operation = operation.OperationId.Value,
                    status = operation.Status.ToString().ToLowerInvariant(),
                    kind = operation.Kind.ToString().ToLowerInvariant(),
                    category = operation.Category.ToString().ToLowerInvariant(),
                    reason = operation.Reason,
                    result = operation.Result
                }).ToArray(),
                programs = _runtime.Programs.All(operationLimit).Select(program => new
                {
                    process = program.Process.Value,
                    definition = program.DefinitionId,
                    state = program.State.ToString().ToLowerInvariant(),
                    instructions = program.InstructionCount,
                    runs = program.CompletedRuns,
                    fault = program.Fault,
                    lastAction = program.LastAction,
                    lastOutcome = program.LastOutcome
                }).ToArray(),
                lives = _runtime.Lives.All(operationLimit).Select(core => new
                {
                    identity = core.Identity.Value,
                    host = core.Host.Value,
                    generation = core.HostGeneration.Value,
                    control = core.ControlGeneration,
                    integrity = core.Integrity,
                    state = core.State.ToString().ToLowerInvariant(),
                    migrating = core.Migrating,
                    destination = core.MigrationDestination.Value
                }).ToArray(),
                presets = ProgramPresets.All.Select(preset => new
                {
                    name = preset.Name,
                    text = preset.ToText()
                }).ToArray(),
                knowledge = new { known = views.Length, stale, total = records.Length }
            };
        }
    }

    /// <summary>
    /// Wired adjacencies between hosts the observer may see. Only a connection whose both ends are visible is
    /// reported, so an edge never discloses that a hidden host exists (UI-003).
    /// </summary>
    private (ulong A, ulong B, uint Cost)[] Links(ObservationView[] views)
    {
        if (views.Length < 2)
            return [];
        var topology = _game.Snapshot.Physical;
        var byNode = new Dictionary<ulong, ulong>(views.Length);
        foreach (var view in views)
            if (view.Node != 0)
                byNode[view.Node] = view.Host;
        var links = new List<(ulong, ulong, uint)>();
        foreach (var view in views)
        {
            if (view.Node == 0 || !topology.Nodes.TryGetValue(new NetDM.Core.NodeId(view.Node), out var node))
                continue;
            foreach (var (to, edge) in node.Links)
            {
                if (!byNode.TryGetValue(to.Value, out var other) || other <= view.Host)
                    continue;
                links.Add((view.Host, other, edge.Cost));
            }
        }
        return links.ToArray();
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

/// <summary>One host as an observer may see it. A name appears only when the identity was established.</summary>
internal sealed record ObservationView(
    ulong Host, string Name, ulong Node, string HostClass, string Level, int Confidence, long AgeMs, bool Stale,
    bool Available, double BufferCu, int Effects, bool Identified);

internal sealed record HostStateView(
    ulong Host, ulong Generation, string Name, string HostClass, bool Powered, bool Operational,
    int ComputeCapacity, int ComputeRate, double BufferCu, int MemoryHeld, int MemorySlots, int Effects);

internal sealed record NetRunStateView(
    long NowMilliseconds, long Ticks, HostStateView[] Hosts, int Actions, int Operations);
