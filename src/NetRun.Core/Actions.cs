using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NetRun.Core;

/// <summary>The universal operation kinds of §6.1.</summary>
public enum ActionKind : byte
{
    Query, Command, Event, Subscription, Transfer
}

/// <summary>The primitive groups of §7.1. The group organizes the UI and the catalogue; it grants nothing.</summary>
public enum ActionCategory : byte
{
    Perception, Connectivity, Data, Programs, Resources, Security, Effects, Execution
}

public enum ParameterType : byte
{
    Integer, Number, Text, Enum, Boolean
}

/// <summary>One declared parameter, used to build forms and documentation from the same source (SVC-001).</summary>
public sealed record ActionParameter(
    string Name,
    ParameterType Type,
    bool Required,
    string Description,
    double Min = -9_007_199_254_740_991,
    double Max = 9_007_199_254_740_991,
    IReadOnlyList<string>? Values = null);

/// <summary>
/// The complete description of one action: identity, version, contract, rights, cost, reach and the
/// documentation a player reads. Context menus, the API reference and the visual builder are generated
/// from this record, so no second copy of the parameters can drift (SVC-001, ADR-0003).
/// </summary>
public sealed record ActionDescriptor(
    string Id,
    int Version,
    ActionKind Kind,
    ActionCategory Category,
    CapabilityCategory RequiredCapability,
    string CooldownGroup,
    int ComputeCost,
    int MemoryCost,
    int CooldownMilliseconds,
    int RangeHops,
    string DisplayName,
    string Documentation,
    IReadOnlyList<ActionParameter>? Parameters = null,
    string? ServiceKey = null)
{
    /// <summary>
    /// Stable description digest; two registrations with the same digest describe the same action. Computed
    /// on read so a copied descriptor never carries a stale digest.
    /// </summary>
    public string Digest => DigestOf(Id, Version, Kind, Category, RequiredCapability, CooldownGroup,
        ComputeCost, MemoryCost, CooldownMilliseconds, RangeHops, ServiceKey);

    private static string DigestOf(string id, int version, ActionKind kind, ActionCategory category,
        CapabilityCategory capability, string cooldownGroup, int computeCost, int memoryCost, int cooldown,
        int range, string? serviceKey)
    {
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{id}|{version}|{(byte)kind}|{(byte)category}|{(byte)capability}|{cooldownGroup}|{computeCost}|{memoryCost}|{cooldown}|{range}|{serviceKey ?? "*"}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

/// <summary>
/// Everything the trusted environment establishes about one attempt. None of these fields can be chosen
/// by a user program: the pipeline fills them from the authenticated session, the process and the host.
/// </summary>
public sealed record ActionContext(
    OperationId OperationId,
    PrincipalId Principal,
    HostId ExecutionHost,
    HostId TargetHost,
    EntityId Entity,
    ProcessId Process,
    CorrelationId Correlation,
    long IssuedAtMilliseconds);

public readonly record struct ActionRequest(
    PrincipalId Principal,
    HostId ExecutionHost,
    HostId TargetHost,
    EntityId Entity,
    ProcessId Process,
    CorrelationId Correlation,
    string IdempotencyKey);

public enum ActionStatus : byte
{
    Ok, Committed, Prepared, Accepted, Cancelled,
    UnknownAction, Forbidden, InvalidRequest, InvalidState,
    TargetDestroyed, HostUnavailable, InsufficientCompute, InsufficientMemory,
    Cooldown, Duplicate, IdempotencyConflict, LimitExceeded, Rejected, UnknownOutcome
}

public sealed record ActionValidation(ActionStatus Status, string? Reason = null)
{
    public static ActionValidation Pass { get; } = new(ActionStatus.Ok);

    public bool Succeeded => Status == ActionStatus.Ok;
}

public sealed record ActionExecution(ActionStatus Status, string? Reason = null, string? Result = null)
{
    public static ActionExecution Ok { get; } = new(ActionStatus.Ok);

    public bool Succeeded => Status == ActionStatus.Ok;
}

/// <summary>Read side of the world that an action may inspect during validation and commit.</summary>
public interface INetRunWorldReader
{
    long NowMilliseconds { get; }

    HostSnapshot? Host(HostId host);

    ResourceSnapshot? Resources(HostId host);

    EffectInstance[] Effects(HostId target, int limit);

    /// <summary>
    /// Hosts this principal may discover, bounded. A host it may not discover never appears, so a
    /// selector can never reveal a hidden object through a count or an error (PRIM-002, AT-12).
    /// </summary>
    HostSnapshot[] VisibleHosts(PrincipalId principal, int limit);

    /// <summary>What this observer actually holds about a host; <see cref="KnowledgeLevel.Unknown"/> otherwise.</summary>
    KnowledgeLevel KnowledgeOf(PrincipalId principal, HostId host);

    /// <summary>Whether the observer holds the level and the record is still fresh enough to act on.</summary>
    bool Knows(PrincipalId principal, HostId host, KnowledgeLevel level);
}

/// <summary>Write side offered to an action at commit time. Everything goes through the registered domains.</summary>
public interface INetRunMutationScope
{
    HostId TargetHost { get; }

    PrincipalId Principal { get; }

    long NowMilliseconds { get; }

    /// <summary>The read model of the committing host, so a result can be built from authoritative state.</summary>
    INetRunWorldReader World { get; }

    /// <summary>Applies a registered effect within its server-side definition bounds.</summary>
    EffectResult ApplyEffect(EffectKind kind, HostId target, int magnitude, int durationMilliseconds, string origin);

    /// <summary>Records a domain event after the authoritative change; the owner publishes it.</summary>
    void Emit(string topic, string payload);

    /// <summary>Stores what the acting principal has just learned, for example from a scan.</summary>
    void Remember(HostId host, KnowledgeLevel level, int confidence);

    /// <summary>
    /// Issues a capability as the outcome of a committed action, for example a breach. The grant is ordinary:
    /// it expires, it can be revoked, and it is rechecked at every later commit.
    /// </summary>
    AuthorizationResult GrantCapability(CapabilityCategory category, string? serviceKey, long lifetimeMilliseconds,
        GrantSource source);
}

/// <summary>
/// One action implementation. Implementations are constructed with their parameters, so a caller never
/// passes an untyped bag of values, and the pipeline handles the invariants that are the same for every
/// action: identity, rights, cost, cooldown, idempotency and the operation state machine.
/// </summary>
public interface IGameAction
{
    ActionDescriptor Descriptor { get; }

    /// <summary>Digest of the bound parameters, used only to tell a retry from a different request.</summary>
    string ContentDigest { get; }

    ActionValidation Validate(in ActionContext context, INetRunWorldReader world);

    ActionExecution Commit(in ActionContext context, INetRunMutationScope scope);
}

/// <summary>How a remote action reports what the transport accepted. Acceptance is not execution.</summary>
public readonly record struct RemoteDispatch(ActionStatus Status, string? Reason = null, string? Result = null)
{
    public bool Accepted => Status is ActionStatus.Accepted or ActionStatus.Committed;

    public static RemoteDispatch Rejected(ActionStatus status, string reason) => new(status, reason);
}

/// <summary>
/// The write side of a remote command: the action states the intent and the adapter carries it over
/// NETDM. The pipeline never talks to the transport itself, which is what keeps the game layer free of
/// transport knowledge (ADR-0001) while still using the single transport for every remote effect.
/// </summary>
public interface INetRunDispatchScope
{
    HostId TargetHost { get; }

    /// <summary>What the domain knows about the target at dispatch time. Null when it was destroyed.</summary>
    HostSnapshot? TargetHostSnapshot { get; }

    PrincipalId Principal { get; }

    long NowMilliseconds { get; }

    /// <summary>
    /// Sends a command to one service instance of the target. Arguments are the canonical JSON object of
    /// the service schema. A refusal here means nothing was sent, so the caller can release its reserves.
    /// </summary>
    RemoteDispatch Send(string serviceType, string instance, string command, string idempotencyKey,
        string argumentsJson);
}

/// <summary>
/// The transport side of remote actions, supplied by the composition root. It is the only place where
/// NETRUN meets NETDM: admission over the service runtime, never a direct call to a game object.
/// </summary>
public interface INetRunTransport
{
    RemoteDispatch Send(in ActionContext context, string serviceType, string instance, string command,
        string idempotencyKey, string argumentsJson);
}

/// <summary>
/// An action whose authoritative change happens at the target rather than in the caller's process. Its
/// commit only dispatches; the operation stays accepted until the target acknowledges, and a lost answer
/// ends as an unknown outcome instead of being repeated (SVC-002, §6.2).
/// </summary>
public interface IRemoteGameAction : IGameAction
{
    RemoteDispatch Dispatch(in ActionContext context, INetRunDispatchScope scope);
}

/// <summary>An operation that passed validation and holds its reserves, waiting for commit or cancellation.</summary>
public sealed record PreparedAction(
    OperationId OperationId,
    ActionContext Context,
    IGameAction Action,
    CapabilityToken Token,
    ReservationId ComputeReservation,
    ReservationId MemoryReservation,
    long PrepareExpiresAtMilliseconds);

/// <summary>The terminal view of one operation, as its initiator may read it.</summary>
public sealed record ActionOutcome(
    ActionStatus Status,
    OperationId OperationId,
    ActionKind Kind,
    ActionCategory Category,
    long CooldownRemainingMilliseconds = 0,
    string? Reason = null,
    string? Result = null);

/// <summary>An event produced by a committed action, waiting for the owner to publish it.</summary>
public sealed record ActionEvent(string Topic, string Payload, OperationId Operation, long AtMilliseconds);

public sealed record ActionPipelineOptions(
    int MaxOperations = 16_384,
    long PrepareLifetimeMilliseconds = 5_000,
    int MaxRetiredIdempotencyKeys = 16_384,
    int MaxPendingEvents = 256,
    long RemoteTimeoutMilliseconds = 30_000);

/// <summary>Result of the prepare step: either a pending operation or the reason it did not start.</summary>
public sealed record PrepareOutcome(ActionStatus Status, OperationId OperationId, PreparedAction? Prepared,
    long CooldownRemainingMilliseconds = 0, string? Reason = null)
{
    public bool Succeeded => Status == ActionStatus.Prepared;
}

/// <summary>
/// The action pipeline of §6.2 and the single registry of §7.1. Player, NPC, terminal and later the VM go
/// through the same instance, so a capability, a cost or a cooldown cannot be bypassed by choosing a
/// different client (PRIM-001). The pipeline owns no game state: it validates, reserves, dispatches and
/// finalizes, while the authoritative change happens in the action at commit.
/// </summary>
public sealed class ActionPipeline(
    ISimulationClock clock,
    HostRegistry hosts,
    ResourceLedger resources,
    CapabilityAuthority authority,
    ActionPipelineOptions? options = null)
{
    private sealed class OperationRecord(OperationId id, ActionDescriptor descriptor, in ActionRequest request,
        string digest, long createdAt)
    {
        public readonly OperationId Id = id;
        public readonly ActionDescriptor Descriptor = descriptor;
        public readonly PrincipalId Principal = request.Principal;
        public readonly HostId ExecutionHost = request.ExecutionHost;
        public readonly HostId TargetHost = request.TargetHost;
        public readonly string IdempotencyKey = request.IdempotencyKey;
        public readonly string Digest = digest;
        public readonly long CreatedAt = createdAt;
        public OperationState State;
        public string? Reason;
        public string? Result;
        public long CompletedAt;
        public long InTransitDeadline;
    }

    private enum OperationState : byte
    {
        Created, Preparing, InTransit, Executing, Committed, Completed, Cancelled, Failed, UnknownOutcome
    }

    private readonly ISimulationClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly HostRegistry _hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
    private readonly ResourceLedger _resources = resources ?? throw new ArgumentNullException(nameof(resources));
    private readonly CapabilityAuthority _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    private readonly ActionPipelineOptions _options = options ?? new ActionPipelineOptions();
    private readonly Dictionary<string, IGameAction> _actions = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, OperationRecord> _operations = [];
    private readonly Dictionary<(ulong Principal, string Key), ulong> _idempotency = [];
    private readonly HashSet<(ulong Principal, string Key)> _retired = [];
    private readonly Dictionary<string, long> _cooldowns = new(StringComparer.Ordinal);
    private readonly SortedSet<(long At, ulong Operation)> _prepared = [];
    private readonly Queue<ActionEvent> _events = new();
    private EffectRegistry? _effects;
    private KnowledgeStore? _knowledge;
    private INetRunTransport? _transport;
    private ulong _nextOperation;

    public int ActionCount => _actions.Count;

    public int OperationCount => _operations.Count;

    public long NowMilliseconds => _clock.NowMilliseconds;

    /// <summary>Connects the effect registry so actions can read and apply effects through the pipeline.</summary>
    public void AttachEffects(EffectRegistry effects) => _effects = effects ?? throw new ArgumentNullException(nameof(effects));

    /// <summary>
    /// Connects the transport that carries remote commands. The composition root supplies it; the domain
    /// never references NETDM directly, it only states the intent and reports what the transport accepted.
    /// </summary>
    public void AttachTransport(INetRunTransport transport) =>
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    /// <summary>Connects the per-observer knowledge store so perception actions can read and record it.</summary>
    public void AttachKnowledge(KnowledgeStore knowledge) =>
        _knowledge = knowledge ?? throw new ArgumentNullException(nameof(knowledge));

    /// <summary>Registers an action. A conflicting duplicate fails at composition time, not at run time.</summary>
    public void Register(IGameAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var descriptor = action.Descriptor;
        if (!IsValidId(descriptor.Id) || descriptor.Version < 1 || !Enum.IsDefined(descriptor.Kind) ||
            !Enum.IsDefined(descriptor.Category) || !Enum.IsDefined(descriptor.RequiredCapability) ||
            descriptor.ComputeCost is < 0 or > 100_000 || descriptor.MemoryCost is < 0 or > 1_000_000 ||
            descriptor.CooldownMilliseconds is < 0 or > 3_600_000 || descriptor.RangeHops is < 0 or > 64 ||
            string.IsNullOrWhiteSpace(descriptor.DisplayName) || string.IsNullOrWhiteSpace(descriptor.CooldownGroup))
            throw new ArgumentException($"Invalid descriptor for action '{descriptor.Id}'.", nameof(action));
        if (_actions.TryGetValue(descriptor.Id, out var existing))
        {
            if (existing.Descriptor.Digest == descriptor.Digest)
                return;
            throw new ArgumentException($"Action '{descriptor.Id}' is already registered with another contract.", nameof(action));
        }
        _actions[descriptor.Id] = action;
    }

    public ActionDescriptor? Describe(string actionId) =>
        _actions.TryGetValue(actionId, out var action) ? action.Descriptor : null;

    /// <summary>The registered instance of an action, or null. Callers execute this exact instance.</summary>
    public IGameAction? GetAction(string actionId) =>
        _actions.TryGetValue(actionId, out var action) ? action : null;

    /// <summary>Bounded catalogue for the UI and the API reference, in registration order.</summary>
    public ActionDescriptor[] Catalogue(int limit)
    {
        var count = Math.Min(Math.Max(limit, 0), _actions.Count);
        var result = new ActionDescriptor[count];
        var index = 0;
        foreach (var action in _actions.Values)
        {
            if (index >= count)
                break;
            result[index++] = action.Descriptor;
        }
        return result;
    }

    /// <summary>Runs an action from validation to commit in one call, for local operations.</summary>
    public ActionOutcome Execute(in ActionRequest request, IGameAction action)
    {
        var prepared = Prepare(request, action);
        return prepared.Prepared is { } pending
            ? Commit(pending)
            : new ActionOutcome(prepared.Status, prepared.OperationId, action.Descriptor.Kind,
                action.Descriptor.Category, prepared.CooldownRemainingMilliseconds, prepared.Reason);
    }

    /// <summary>
    /// Steps 1–3 of the pipeline: verify identity, action, host, rights, cooldown and arguments, then
    /// reserve the budget. Nothing authoritative changes here, so a prepared operation can be cancelled.
    /// </summary>
    public PrepareOutcome Prepare(in ActionRequest request, IGameAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var descriptor = action.Descriptor;
        var now = _clock.NowMilliseconds;

        if (!_actions.TryGetValue(descriptor.Id, out var registered) || !ReferenceEquals(registered, action))
            return new PrepareOutcome(ActionStatus.UnknownAction, OperationId.None, null, Reason: "not_registered");
        if (!IsValidId(request.IdempotencyKey))
            return new PrepareOutcome(ActionStatus.InvalidRequest, OperationId.None, null, Reason: "idempotency_key");
        if (request.Principal.IsNone)
            return new PrepareOutcome(ActionStatus.InvalidRequest, OperationId.None, null, Reason: "principal");

        var executionHost = _hosts.Get(request.ExecutionHost);
        if (executionHost is null)
            return new PrepareOutcome(ActionStatus.TargetDestroyed, OperationId.None, null, Reason: "execution_host");
        if (!executionHost.Available)
            return new PrepareOutcome(ActionStatus.HostUnavailable, OperationId.None, null, Reason: "execution_host_offline");
        // Spending a host's compute is itself a right. A session that was never granted execution there
        // cannot use it as a launchpad, whatever it may do on the target.
        var executionRight = _authority.Authorize(request.Principal, request.ExecutionHost,
            CapabilityCategory.Execution, null, executionHost.Security?.Profile ?? SecurityProfile.Standard);
        if (!executionRight.Allowed)
            return new PrepareOutcome(ActionStatus.Forbidden, OperationId.None, null, Reason: "execution_host_right");

        var digest = DigestOf(request, descriptor, action.ContentDigest);
        if (LookupIdempotency(request.Principal, request.IdempotencyKey) is { } prior)
        {
            return prior.Digest != digest
                ? new PrepareOutcome(ActionStatus.IdempotencyConflict, prior.Id, null, Reason: "content")
                : new PrepareOutcome(ActionStatus.Duplicate, prior.Id, null, Reason: "already_requested");
        }
        if (_retired.Contains((request.Principal.Value, request.IdempotencyKey)))
            return new PrepareOutcome(ActionStatus.UnknownOutcome, OperationId.None, null, Reason: "idempotency_window_expired");

        // A missing target is not refused here yet: the action validates first, so that "you may not see it"
        // and "it does not exist" produce the same answer and an error cannot reveal a hidden object (§6.4).
        var targetHost = _hosts.Get(request.TargetHost);
        var profile = targetHost?.Security?.Profile ?? SecurityProfile.Standard;
        var authorization = _authority.Authorize(request.Principal, request.TargetHost,
            descriptor.RequiredCapability, descriptor.ServiceKey, profile);
        if (!authorization.Allowed)
            return new PrepareOutcome(ActionStatus.Forbidden, OperationId.None, null, Reason: authorization.Status.ToString());

        if (_operations.Count >= _options.MaxOperations)
            EvictOldestOperation();

        var operationId = new OperationId(++_nextOperation);
        var context = new ActionContext(operationId, request.Principal, request.ExecutionHost, request.TargetHost,
            request.Entity, request.Process, request.Correlation, now);

        // Validate before the cooldown: a wrong target or missing service is the real reason an attempt
        // cannot work, and reporting "cooldown" instead would hide it (SVC-001, §6.4).
        var validation = action.Validate(context, new WorldReader(this, now));
        if (!validation.Succeeded)
        {
            Record(operationId, descriptor, request, digest, OperationState.Failed, validation.Reason ?? validation.Status.ToString());
            return new PrepareOutcome(validation.Status, operationId, null, Reason: validation.Reason);
        }

        // An action that passed validation and then finds no host could only be one that does not check its
        // own target; refuse it instead of spending anything.
        if (targetHost is null)
            return new PrepareOutcome(ActionStatus.TargetDestroyed, OperationId.None, null, Reason: "target");

        var cooldownKey = CooldownKey(request.Principal, descriptor);
        if (_cooldowns.TryGetValue(cooldownKey, out var readyAt) && readyAt > now)
            return new PrepareOutcome(ActionStatus.Cooldown, OperationId.None, null, readyAt - now, "cooldown");

        var compute = Reserve(request.ExecutionHost, request.Principal, ResourceKind.Compute, descriptor.ComputeCost);
        if (compute.Status != ReservationStatus.Ok)
            return new PrepareOutcome(Map(compute.Status), operationId, null, Reason: compute.Status.ToString());

        var memory = Reserve(request.ExecutionHost, request.Principal, ResourceKind.Memory, descriptor.MemoryCost);
        if (memory.Status != ReservationStatus.Ok)
        {
            if (!compute.Reservation.IsNone)
                _resources.Release(compute.Reservation, request.Principal);
            return new PrepareOutcome(Map(memory.Status), operationId, null, Reason: memory.Status.ToString());
        }

        var prepared = new PreparedAction(operationId, context, action, authorization.Token,
            compute.Reservation, memory.Reservation,
            checked(now + _options.PrepareLifetimeMilliseconds));
        Record(operationId, descriptor, request, digest, OperationState.Preparing, null);
        _idempotency[(request.Principal.Value, request.IdempotencyKey)] = operationId.Value;
        _prepared.Add((prepared.PrepareExpiresAtMilliseconds, operationId.Value));
        return new PrepareOutcome(ActionStatus.Prepared, operationId, prepared);
    }

    /// <summary>
    /// Steps 4–6: recheck the grant at the commit boundary, run the authoritative change, spend the
    /// reserves and start the cooldown. A revocation ordered before this call stops the commit.
    /// </summary>
    public ActionOutcome Commit(PreparedAction prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var operationId = prepared.OperationId;
        var descriptor = prepared.Action.Descriptor;
        if (!_operations.TryGetValue(operationId.Value, out var record) || record.State != OperationState.Preparing)
            return new ActionOutcome(ActionStatus.InvalidState, operationId, descriptor.Kind, descriptor.Category,
                Reason: "not_prepared");

        var fence = _authority.Recheck(prepared.Token);
        if (fence != AuthorizationStatus.Ok && !prepared.Token.IsNone)
        {
            Cancel(prepared, fence.ToString());
            return new ActionOutcome(ActionStatus.Forbidden, operationId, descriptor.Kind, descriptor.Category,
                Reason: fence.ToString());
        }

        record.State = OperationState.Executing;
        _prepared.Remove((prepared.PrepareExpiresAtMilliseconds, operationId.Value));

        // A remote action only dispatches here. Its authoritative change happens at the target, so the
        // operation stays accepted until the target answers or the deadline passes.
        if (prepared.Action is IRemoteGameAction remote)
        {
            if (_transport is null)
            {
                ReleaseReserves(prepared);
                record.State = OperationState.Failed;
                record.Reason = "no_transport";
                record.CompletedAt = _clock.NowMilliseconds;
                return new ActionOutcome(ActionStatus.InvalidState, operationId, descriptor.Kind,
                    descriptor.Category, Reason: "no_transport");
            }
            var dispatched = remote.Dispatch(prepared.Context,
                new DispatchScope(this, prepared.Context, _transport, _hosts.Get(prepared.Context.TargetHost)));
            if (!dispatched.Accepted)
            {
                ReleaseReserves(prepared);
                record.State = OperationState.Failed;
                record.Reason = dispatched.Reason ?? dispatched.Status.ToString();
                record.CompletedAt = _clock.NowMilliseconds;
                return new ActionOutcome(dispatched.Status, operationId, descriptor.Kind, descriptor.Category,
                    Reason: record.Reason);
            }
            ConsumeReserves(prepared);
            record.State = OperationState.InTransit;
            record.Result = dispatched.Result;
            record.InTransitDeadline = _clock.NowMilliseconds + _options.RemoteTimeoutMilliseconds;
            StartCooldown(prepared.Context.Principal, descriptor);
            return new ActionOutcome(ActionStatus.Accepted, operationId, descriptor.Kind, descriptor.Category,
                Result: dispatched.Result);
        }

        var executed = prepared.Action.Commit(prepared.Context, new MutationScope(this, prepared.Context));
        if (!executed.Succeeded)
        {
            ReleaseReserves(prepared);
            record.State = OperationState.Failed;
            record.Reason = executed.Reason ?? executed.Status.ToString();
            record.CompletedAt = _clock.NowMilliseconds;
            return new ActionOutcome(executed.Status, operationId, descriptor.Kind, descriptor.Category,
                Reason: record.Reason);
        }

        ConsumeReserves(prepared);
        record.State = OperationState.Committed;
        record.Result = executed.Result;
        record.CompletedAt = _clock.NowMilliseconds;
        StartCooldown(prepared.Context.Principal, descriptor);
        return new ActionOutcome(ActionStatus.Committed, operationId, descriptor.Kind, descriptor.Category,
            Result: executed.Result);
    }

    /// <summary>
    /// Finalizes an accepted remote operation with what the target reported. A lost or timed-out answer is
    /// never turned into a success, and the same answer applied twice changes nothing.
    /// </summary>
    public ActionOutcome Complete(OperationId id, ActionStatus status, string? reason = null, string? result = null)
    {
        var kind = ActionKind.Command;
        var category = ActionCategory.Execution;
        if (!_operations.TryGetValue(id.Value, out var record))
            return new ActionOutcome(ActionStatus.InvalidState, id, kind, category, Reason: "unknown_operation");
        kind = record.Descriptor.Kind;
        category = record.Descriptor.Category;
        if (record.State != OperationState.InTransit)
            return new ActionOutcome(ActionStatus.InvalidState, id, kind, category, Reason: "not_in_transit");
        record.State = status switch
        {
            ActionStatus.Committed or ActionStatus.Ok => OperationState.Committed,
            ActionStatus.Cancelled => OperationState.Cancelled,
            _ => OperationState.Failed
        };
        record.Reason = reason;
        record.Result = result ?? record.Result;
        record.CompletedAt = _clock.NowMilliseconds;
        return new ActionOutcome(status, id, kind, category, Reason: reason, Result: record.Result);
    }

    /// <summary>
    /// Ends accepted operations whose answer never arrived as an unknown outcome. They are never repeated:
    /// the caller learns that the effect may or may not have happened.
    /// </summary>
    public int ExpireRemote(long nowMilliseconds)
    {
        var expired = 0;
        foreach (var record in _operations.Values.ToArray())
        {
            if (record.State != OperationState.InTransit || record.InTransitDeadline > nowMilliseconds)
                continue;
            record.State = OperationState.UnknownOutcome;
            record.Reason = "unknown_outcome";
            record.CompletedAt = nowMilliseconds;
            expired++;
        }
        return expired;
    }

    private void StartCooldown(PrincipalId principal, ActionDescriptor descriptor)
    {
        if (descriptor.CooldownMilliseconds > 0)
            _cooldowns[CooldownKey(principal, descriptor)] = _clock.NowMilliseconds + descriptor.CooldownMilliseconds;
    }

    /// <summary>Cancels a prepared operation before commit. Its reserves return and its cooldown never starts.</summary>
    public ActionOutcome Cancel(PreparedAction prepared, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var descriptor = prepared.Action.Descriptor;
        if (_operations.TryGetValue(prepared.OperationId.Value, out var record) &&
            record.State is OperationState.Committed or OperationState.Completed)
            return new ActionOutcome(ActionStatus.InvalidState, prepared.OperationId, descriptor.Kind,
                descriptor.Category, Reason: "already_committed");
        ReleaseReserves(prepared);
        if (record is not null)
        {
            record.State = OperationState.Cancelled;
            record.Reason = reason ?? "cancelled";
            record.CompletedAt = _clock.NowMilliseconds;
        }
        _prepared.Remove((prepared.PrepareExpiresAtMilliseconds, prepared.OperationId.Value));
        return new ActionOutcome(ActionStatus.Cancelled, prepared.OperationId, descriptor.Kind, descriptor.Category,
            Reason: reason ?? "cancelled");
    }

    /// <summary>Cancels every prepared operation whose window elapsed. Called by the owner with game time.</summary>
    public int ExpirePrepared(long nowMilliseconds)
    {
        var expired = 0;
        while (_prepared.Count > 0 && _prepared.Min.At <= nowMilliseconds)
        {
            var at = _prepared.Min;
            _prepared.Remove(at);
            if (!_operations.TryGetValue(at.Operation, out var record) || record.State != OperationState.Preparing)
                continue;
            ReclaimReserves(record);
            record.State = OperationState.Cancelled;
            record.Reason = "prepare_timeout";
            record.CompletedAt = nowMilliseconds;
            expired++;
        }
        return expired;
    }

    /// <summary>Cancels every operation a principal prepared but did not commit, for example when it disconnects.</summary>
    public int CancelPrincipal(PrincipalId principal, string reason)
    {
        var cancelled = 0;
        foreach (var record in _operations.Values.ToArray())
        {
            if (record.Principal != principal || record.State != OperationState.Preparing)
                continue;
            ReclaimReserves(record);
            record.State = OperationState.Cancelled;
            record.Reason = reason;
            record.CompletedAt = _clock.NowMilliseconds;
            cancelled++;
        }
        return cancelled;
    }

    /// <summary>Cancels prepared operations that name a host as their target, for example when it is destroyed.</summary>
    public int CancelTarget(HostId target, string reason)
    {
        var cancelled = 0;
        foreach (var record in _operations.Values.ToArray())
        {
            if (record.State != OperationState.Preparing || record.TargetHost != target)
                continue;
            ReclaimReserves(record);
            record.State = OperationState.Cancelled;
            record.Reason = reason;
            record.CompletedAt = _clock.NowMilliseconds;
            cancelled++;
        }
        return cancelled;
    }

    /// <summary>The principal that started an operation, or none when it is not this runtime's.</summary>
    public PrincipalId OwnerOf(OperationId id) =>
        _operations.TryGetValue(id.Value, out var record) ? record.Principal : PrincipalId.None;

    public ActionOutcome? Operation(OperationId id)
    {
        if (!_operations.TryGetValue(id.Value, out var record))
            return null;
        return new ActionOutcome(MapState(record.State), record.Id, record.Descriptor.Kind,
            record.Descriptor.Category, Reason: record.Reason, Result: record.Result);
    }

    /// <summary>Bounded history, newest first, for the events tab.</summary>
    public ActionOutcome[] Recent(int limit)
    {
        var count = Math.Min(Math.Max(limit, 0), _operations.Count);
        var result = new ActionOutcome[count];
        var index = 0;
        foreach (var record in _operations.Values.OrderByDescending(record => record.Id.Value))
        {
            if (index >= count)
                break;
            result[index++] = new ActionOutcome(MapState(record.State), record.Id, record.Descriptor.Kind,
                record.Descriptor.Category, Reason: record.Reason, Result: record.Result);
        }
        return result;
    }

    /// <summary>
    /// Records an event a program produced after its own committed step. It goes through the same queue and the
    /// same topic counters as the events of actions, so a program cannot invent a system event (PRIM-004).
    /// </summary>
    public void PublishProgramEvent(string topic, string payload) => EmitEvent(topic, payload);

    /// <summary>Drains the events produced by committed actions. The owner publishes them after commit.</summary>
    public ActionEvent[] DrainEvents()
    {
        if (_events.Count == 0)
            return [];
        var drained = new ActionEvent[_events.Count];
        var index = 0;
        while (_events.Count > 0)
            drained[index++] = _events.Dequeue();
        return drained;
    }

    internal EffectInstance[] EffectsReader(HostId target, int limit) => _effects?.On(target, limit) ?? [];

    private static (ReservationStatus Status, ReservationId Reservation) ReserveDefault() =>
        (ReservationStatus.Ok, ReservationId.None);

    private (ReservationStatus Status, ReservationId Reservation) Reserve(HostId host, PrincipalId principal,
        ResourceKind kind, int amount)
    {
        if (amount <= 0)
            return ReserveDefault();
        var result = kind == ResourceKind.Compute
            ? _resources.ReserveCompute(host, principal, amount, _options.PrepareLifetimeMilliseconds)
            : _resources.ReserveMemory(host, principal, amount, _options.PrepareLifetimeMilliseconds);
        return (result.Status, result.Reservation?.Id ?? ReservationId.None);
    }

    private OperationRecord? LookupIdempotency(PrincipalId principal, string key) =>
        _idempotency.TryGetValue((principal.Value, key), out var id) && _operations.TryGetValue(id, out var record)
            ? record
            : null;

    private void ConsumeReserves(PreparedAction prepared)
    {
        if (!prepared.ComputeReservation.IsNone)
            _resources.Consume(prepared.ComputeReservation);
        if (!prepared.MemoryReservation.IsNone)
            _resources.Consume(prepared.MemoryReservation);
    }

    private void ReleaseReserves(PreparedAction prepared)
    {
        if (!prepared.ComputeReservation.IsNone)
            _resources.Release(prepared.ComputeReservation, prepared.Context.Principal);
        if (!prepared.MemoryReservation.IsNone)
            _resources.Release(prepared.MemoryReservation, prepared.Context.Principal);
    }

    private void ReclaimReserves(OperationRecord record)
    {
        foreach (var reservation in _resources.Provider(record.ExecutionHost))
        {
            if (reservation.Owner == record.Principal && reservation.IsActive)
                _resources.Release(reservation.Id, record.Principal);
        }
    }

    private void Record(OperationId id, ActionDescriptor descriptor, in ActionRequest request, string digest,
        OperationState state, string? reason)
    {
        _operations[id.Value] = new OperationRecord(id, descriptor, request, digest, _clock.NowMilliseconds)
        {
            State = state,
            Reason = reason
        };
    }

    private void EvictOldestOperation()
    {
        ulong oldest = 0;
        foreach (var id in _operations.Keys)
            if (oldest == 0 || id < oldest)
                oldest = id;
        if (oldest == 0 || !_operations.Remove(oldest, out var evicted))
            return;
        _idempotency.Remove((evicted.Principal.Value, evicted.IdempotencyKey));
        if (_retired.Count >= _options.MaxRetiredIdempotencyKeys)
            _retired.Clear();
        _retired.Add((evicted.Principal.Value, evicted.IdempotencyKey));
    }

    private static string CooldownKey(PrincipalId principal, ActionDescriptor descriptor) =>
        $"{principal.Value}|{descriptor.CooldownGroup}";

    private static string DigestOf(in ActionRequest request, ActionDescriptor descriptor, string contentDigest)
    {
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{request.Principal.Value}|{descriptor.Id}|{descriptor.Version}|{request.ExecutionHost.Value}|{request.TargetHost.Value}|{contentDigest}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static bool IsValidId(string? value) =>
        value is { Length: > 0 and <= 96 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':');

    private static ActionStatus Map(ReservationStatus status) => status switch
    {
        ReservationStatus.Ok => ActionStatus.Ok,
        ReservationStatus.InsufficientCompute => ActionStatus.InsufficientCompute,
        ReservationStatus.InsufficientMemory => ActionStatus.InsufficientMemory,
        ReservationStatus.UnknownHost => ActionStatus.TargetDestroyed,
        _ => ActionStatus.Rejected
    };

    private static ActionStatus MapState(OperationState state) => state switch
    {
        OperationState.Committed or OperationState.Completed => ActionStatus.Committed,
        OperationState.Cancelled => ActionStatus.Cancelled,
        OperationState.InTransit => ActionStatus.Accepted,
        OperationState.UnknownOutcome => ActionStatus.UnknownOutcome,
        OperationState.Preparing or OperationState.Executing => ActionStatus.Prepared,
        _ => ActionStatus.Rejected
    };

    private sealed class WorldReader(ActionPipeline pipeline, long now) : INetRunWorldReader
    {
        public long NowMilliseconds => now;

        public HostSnapshot? Host(HostId host) => pipeline._hosts.Get(host);

        public ResourceSnapshot? Resources(HostId host) => pipeline._resources.Snapshot(host);

        public EffectInstance[] Effects(HostId target, int limit) => pipeline.EffectsReader(target, limit);

        public KnowledgeLevel KnowledgeOf(PrincipalId principal, HostId host) =>
            pipeline._knowledge?.Level(principal, host) ?? KnowledgeLevel.Unknown;

        public bool Knows(PrincipalId principal, HostId host, KnowledgeLevel level) =>
            pipeline._knowledge?.Holds(principal, host, level) ?? false;

        public HostSnapshot[] VisibleHosts(PrincipalId principal, int limit)
        {
            if (principal.IsNone || limit <= 0)
                return [];
            // The registry is asked for a bounded page; each candidate is then checked against the same
            // authority that guards a direct call, so a filtered list and a refused call always agree.
            var candidates = pipeline._hosts.All(Math.Min(limit * 4, 1_024));
            var visible = new List<HostSnapshot>(Math.Min(limit, candidates.Length));
            foreach (var host in candidates)
            {
                if (visible.Count >= limit)
                    break;
                var profile = host.Security?.Profile ?? SecurityProfile.Standard;
                if (pipeline._authority.Authorize(principal, host.Id, CapabilityCategory.Discovery, null, profile).Allowed)
                    visible.Add(host);
            }
            return visible.ToArray();
        }
    }

    private sealed class DispatchScope(ActionPipeline pipeline, ActionContext context, INetRunTransport transport,
        HostSnapshot? target) : INetRunDispatchScope
    {
        public HostId TargetHost => context.TargetHost;

        public HostSnapshot? TargetHostSnapshot => target;

        public PrincipalId Principal => context.Principal;

        public long NowMilliseconds => pipeline._clock.NowMilliseconds;

        public RemoteDispatch Send(string serviceType, string instance, string command, string idempotencyKey,
            string argumentsJson) =>
            transport.Send(context, serviceType, instance, command, idempotencyKey, argumentsJson);
    }

    private sealed class MutationScope(ActionPipeline pipeline, ActionContext context) : INetRunMutationScope
    {
        public HostId TargetHost => context.TargetHost;

        public PrincipalId Principal => context.Principal;

        public long NowMilliseconds => pipeline._clock.NowMilliseconds;

        public INetRunWorldReader World => new WorldReader(pipeline, pipeline._clock.NowMilliseconds);

        public EffectResult ApplyEffect(EffectKind kind, HostId target, int magnitude, int durationMilliseconds,
            string origin) =>
            pipeline._effects?.Apply(kind, target, context.Principal, magnitude, durationMilliseconds, origin)
            ?? new EffectResult(EffectStatus.UnknownKind, null);

        public void Emit(string topic, string payload) => pipeline.EmitEvent(topic, payload);

        public void Remember(HostId host, KnowledgeLevel level, int confidence) =>
            pipeline._knowledge?.Remember(context.Principal, host, level, confidence);

        public AuthorizationResult GrantCapability(CapabilityCategory category, string? serviceKey,
            long lifetimeMilliseconds, GrantSource source) =>
            pipeline._authority.Grant(context.Principal, context.TargetHost, category, serviceKey,
                lifetimeMilliseconds, source, PrincipalId.None);
    }

    private void EmitEvent(string topic, string payload)
    {
        if (string.IsNullOrEmpty(topic))
            return;
        // An event that cannot be delivered is never dropped in silence: the caller sees the bounded result.
        _events.Enqueue(new ActionEvent(topic, payload, OperationId.None, _clock.NowMilliseconds));
        while (_events.Count > _options.MaxPendingEvents)
            _events.Dequeue();
    }
}
