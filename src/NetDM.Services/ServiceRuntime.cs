using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetDM.Core;

namespace NetDM.Services;

/// <summary>Identity verified on every request: a reused ID with a new generation is a new receiver.</summary>
public readonly record struct EndpointKey(string Id, ulong Generation);

public enum ServiceStatus : byte
{
    Ok, Duplicate, InvalidRequest, UnknownNode, UnknownEndpoint, StaleGeneration, UnknownService,
    UnknownCommand, Forbidden, Conflict, UnsupportedSchema, InvalidPayload, IdempotencyConflict,
    StaleSequence, LimitExceeded, Busy, Unknown
}

public enum DeliveryMode : byte { LatestValue, Queue }

public enum DeliveryKind : byte { Event, Command, Result }

/// <summary>
/// Accepted → InTransit → TransportDelivered → Applied | RejectedByWorld, or one
/// terminal failure. TransportDelivered only means the simulated packet reached
/// the recipient's node; it never means DM applied anything.
/// </summary>
public enum DeliveryState : byte
{
    Accepted, InTransit, TransportDelivered,
    Applied, RejectedByWorld, NoRoute, Busy, Expired, ReceiverGone, Superseded, Cancelled, Unknown
}

/// <summary>
/// Whether the provider applied a command. NotApplied is certain (the request never
/// reached DM); Unknown means DM may have applied it and is never retried blindly.
/// </summary>
public enum CommandOutcome : byte { Pending, Applied, RejectedByWorld, NotApplied, Unknown }

/// <summary>The separate network leg that carries the result back to the initiator.</summary>
public enum ResultLeg : byte { None, InTransit, Delivered, Failed }

/// <summary>Domain is a world fact of the endpoint (station, faction, training zone).</summary>
public enum AccessScope : byte { None, Domain, Open }

/// <summary>
/// Reachability never grants access. A service the requester may not discover
/// answers like a missing one; a discoverable but forbidden one answers Forbidden.
/// The owner always has access to its own service.
/// </summary>
public sealed record ServicePolicy(AccessScope Discover = AccessScope.Domain,
    AccessScope Subscribe = AccessScope.Domain, AccessScope Invoke = AccessScope.Domain);

public sealed record ServiceRuntimeOptions(
    int MaxEndpoints = 65_536, int MaxServicesPerEndpoint = 8, int MaxServices = 8_192,
    int MaxSubscriptionsPerEndpoint = 16, int MaxSubscriptions = 16_384, int MaxFanout = 32,
    int MaxActiveDeliveries = 8_192, int MaxInboxEntries = 4_096, int MaxInboxBytes = 4 * 1024 * 1024,
    int MaxQueuePending = 16, int DeliveryHistory = 8_192, int EnvelopeBytes = 96,
    int DefaultTimeoutTicks = 50, int MaxTimeoutTicks = 900, int AckTimeoutTicks = 600,
    int MaxOperations = 16_384, int MaxActiveOperationsPerEndpoint = 8, int MaxPendingCommandsPerProvider = 16,
    int MaxDiscoverFanout = 16, int MaxResultAttempts = 3, int ResultTimeoutTicks = 50,
    IReadOnlyList<string>? HostProvidedTypes = null, int MaxHostCommands = 256);

/// <summary>
/// A command that reached the node of a provider whose service the Host runs on its behalf, such as
/// a research server: its state lives in Core, so the command is answered by <see cref="ServiceRuntime.Acknowledge"/>
/// from the Host instead of a DM inbox. The request still travelled the simulated route to that node.
/// </summary>
public sealed record HostCommand(long DeliveryId, string Instance, string ServiceType, string Command,
    string IdempotencyKey, string Initiator, string Provider, byte[] Arguments)
{
    public EndpointKey InitiatorKey { get; init; }
    public EndpointKey ProviderKey { get; init; }
    public NodeId ProviderNode { get; init; }
}

/// <summary>One service of the directory as a query sees it: its name, type and where its provider stands.</summary>
public readonly record struct DirectoryEntry(string Instance, string ServiceType, NodeId Node);

public sealed record ServiceDescriptor(string Instance, string ServiceType, string Schema, int Major, int Minor, string Commands);

/// <summary>
/// A bounded slice of the directory in instance order. <c>Revision</c> changes whenever a service appears
/// or leaves, so a cursor taken at another revision is refused instead of silently skipping entries.
/// <c>More</c> says that entries after the last one were not examined.
/// </summary>
public sealed record DirectorySlice(long Revision, IReadOnlyList<DirectoryEntry> Entries, bool More);

/// <summary>A host-provided service with subscribers; see <see cref="ServiceRuntime.WatchedHostServices"/>.</summary>
public sealed record WatchedService(string Instance, EndpointKey Owner, NodeId Node, long LastSequence);

public sealed record DeliverySubmission(long DeliveryId, NodeId From, NodeId To, int Bytes,
    PacketTrafficClass TrafficClass, int TimeoutTicks)
{
    public string RequestId => ServiceRuntime.RequestIdFor(DeliveryId);
}

public sealed record PublishResult(ServiceStatus Status, long MessageId, int Recipients, long[] Deliveries);

public sealed record SubscribeResult(ServiceStatus Status, long SubscriptionId, long Revision);

public sealed record OperationView(long OperationId, string Instance, string Command, string Initiator,
    ulong InitiatorGeneration, CommandOutcome Outcome, string? OutcomeReason, ResultLeg Result,
    string? ResultReason, int ResultAttempts);

public sealed record InvokeResult(ServiceStatus Status, OperationView? Operation);

public sealed record DiscoverResult(ServiceStatus Status, long[] Operations);

/// <summary>
/// One inbox entry. Kind is event (subscription), command (to a provider) or
/// result (back to an initiator). Payload is the recipient's projection only.
/// </summary>
public sealed record InboxItem(long Sequence, long DeliveryId, string Kind, long MessageId, long OperationId,
    string Recipient, ulong RecipientGeneration, string? SubscriptionKey, string Instance, string? ServiceType,
    string? Command, string Producer, ulong ProducerGeneration, long ProducerSequence, string Schema, int Major,
    int Minor, string? Projection, string? Outcome, string? Reason, JsonElement? Payload, long CreatedTick,
    long DeadlineTick);

public sealed record InboxPage(long NextSequence, int Pending, InboxItem[] Items);

public sealed record DeliveryView(long DeliveryId, long MessageId, DeliveryState State,
    string Recipient, ulong RecipientGeneration, long PacketId, string? Reason, DeliveryKind Kind, long OperationId);

public sealed record ServiceRuntimeCounters(int Endpoints, int Services, int Subscriptions,
    int ActiveDeliveries, int InboxEntries, long InboxBytes, long Published, long DeliveriesCreated,
    long Applied, long RejectedByWorld, long Failed, int Operations, int PendingSubmissions);

/// <summary>
/// Service directory, subscriptions, commands and delivery lifecycle for one game
/// runtime. One lock is the single writer. Physical packets are submitted by the
/// caller from <see cref="TakeSubmissions"/>; outcomes return through
/// <see cref="OnSubmitted"/> and <see cref="OnTransportReceipt"/>. Every collection
/// is bounded by <see cref="ServiceRuntimeOptions"/>.
/// </summary>
public sealed class ServiceRuntime
{
    public const string RequestIdPrefix = "svc:";
    private const int InboxItemOverheadBytes = 512;

    private sealed class EndpointRecord(EndpointKey key, NodeId node, string domain)
    {
        public EndpointKey Key = key;
        public NodeId Node = node;
        public string Domain = domain;
        public int ActiveOperations;
        public int PendingCommands;
        public readonly HashSet<string> Services = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> Subscriptions = new(StringComparer.Ordinal);
        public readonly HashSet<long> Inbound = [];
    }

    private sealed record ServiceRecord(string Instance, string ServiceType, EndpointKey Owner,
        SchemaDefinition Schema, int Minor, ServicePolicy Policy)
    {
        public long LastSequence;
        public long LastMessageId;
        public string? LastDigest;
        public int LastRecipients;
        public readonly HashSet<long> Subscriptions = [];
    }

    private sealed class SubscriptionRecord(long id, EndpointKey subscriber, string key)
    {
        public readonly long Id = id;
        public readonly EndpointKey Subscriber = subscriber;
        public readonly string Key = key;
        public string Instance = "";
        public string Projection = "";
        public DeliveryMode Mode;
        public int MaxPending;
        public long Revision;
        public int Pending;
    }

    private sealed class OperationRecord
    {
        public long Id;
        public string Instance = "", ServiceType = "", Digest = "", IdempotencyKey = "";
        public EndpointKey Initiator, Provider;
        public CommandDefinition Definition = null!;
        public byte[]? Result;
        public CommandOutcome Outcome;
        public string? OutcomeReason, ResultReason;
        public ResultLeg ResultState;
        public int ResultAttempts;
        public PacketTrafficClass TrafficClass;
        public bool CountsTowardsInitiator;
    }

    private sealed class DeliveryRecord
    {
        public long Id, MessageId, OperationId, SubscriptionId, SubscriptionRevision, ProducerSequence;
        public DeliveryKind Kind;
        public EndpointKey Recipient, Producer;
        public string? SubscriptionKey, Command, Outcome, OutcomeReason, Projection;
        public string Instance = "";
        public SchemaDefinition Schema = null!;
        public int Minor;
        public byte[]? Payload;
        public long CreatedTick, DeadlineTick, PacketId, InboxSequence;
        public bool Dispatched;
        public DeliveryState State;
        public string? Reason;
        public bool Terminal => State >= DeliveryState.Applied;
        public int InboxBytes => (Payload?.Length ?? 0) + InboxItemOverheadBytes;
    }

    private readonly object _gate = new();
    private readonly SchemaRegistry _schemas;
    private readonly ServiceRuntimeOptions _options;
    private readonly Func<long> _clock;
    private readonly Dictionary<string, EndpointRecord> _endpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _endpointGenerations = new(StringComparer.Ordinal);
    private readonly Dictionary<NodeId, HashSet<string>> _nodeEndpoints = new();
    private readonly Dictionary<string, ServiceRecord> _services = new(StringComparer.Ordinal);
    private readonly Dictionary<(AccessScope Scope, string Principal, string? Type), SortedSet<string>> _directory = new();
    private readonly Dictionary<long, SubscriptionRecord> _subscriptions = new();
    private readonly Dictionary<long, DeliveryRecord> _deliveries = new();
    private readonly Dictionary<long, OperationRecord> _operations = new();
    private readonly Dictionary<(string Instance, string Key), long> _operationKeys = new();
    private readonly Dictionary<(EndpointKey Requester, string Scan), (string ServiceType, long[] Operations)> _scans = new();
    private readonly Queue<long> _history = new();
    private readonly SortedDictionary<long, DeliveryRecord> _inbox = new();
    private readonly Queue<DeliverySubmission> _outbox = new();
    private readonly HashSet<string> _hostTypes = new(StringComparer.Ordinal);
    /// <summary>Commands for host-provided services waiting for the Host, in arrival order.</summary>
    private readonly Queue<long> _hostQueue = new();
    /// <summary>Host commands that hold a provider slot until their ACK or expiry.</summary>
    private readonly HashSet<long> _hostPending = new();
    private long _inboxBytes, _nextMessage, _nextDelivery, _nextSubscription, _nextInbox, _nextOperation;
    private int _activeDeliveries;
    /// <summary>Counts every service that appeared in or left the directory; see <see cref="QueryDirectory"/>.</summary>
    private long _directoryRevision;
    private long _published, _created, _applied, _rejected, _failed;

    public ServiceRuntime(SchemaRegistry schemas, Func<long> clock, ServiceRuntimeOptions? options = null)
    {
        _schemas = schemas ?? throw new ArgumentNullException(nameof(schemas));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new ServiceRuntimeOptions();
        foreach (var type in _options.HostProvidedTypes ?? [])
            if (ServiceNames.IsValid(type))
                _hostTypes.Add(type);
    }

    /// <summary>
    /// Commands for host-provided service types that reached their provider's node. Each is answered
    /// with <see cref="Acknowledge"/>; one whose deadline passed while it waited is finished as expired.
    /// </summary>
    public HostCommand[] TakeHostCommands(int max)
    {
        lock (_gate)
        {
            if (_hostQueue.Count == 0 || max < 1)
                return [];
            var now = _clock();
            var taken = new List<HostCommand>(Math.Min(max, _hostQueue.Count));
            while (taken.Count < max && _hostQueue.TryDequeue(out var id))
            {
                if (!_deliveries.TryGetValue(id, out var delivery) || delivery.Terminal)
                    continue;
                if (now > delivery.DeadlineTick)
                {
                    Finish(delivery, DeliveryState.Expired, "deadline");
                    continue;
                }
                var operation = _operations[delivery.OperationId];
                taken.Add(new HostCommand(delivery.Id, operation.Instance, operation.ServiceType, operation.Definition.Name,
                    operation.IdempotencyKey, operation.Initiator.Id, operation.Provider.Id, delivery.Payload ?? [])
                    { InitiatorKey = operation.Initiator, ProviderKey = operation.Provider, ProviderNode = _endpoints[operation.Provider.Id].Node });
            }
            return taken.ToArray();
        }
    }

    /// <summary>
    /// Services of a host-provided type that have subscribers now, at most <paramref name="max"/>, in
    /// instance order: the Host publishes their state on the owner's behalf with the next sequence.
    /// </summary>
    public WatchedService[] WatchedHostServices(string serviceType, int max)
    {
        lock (_gate)
        {
            if (!_hostTypes.Contains(serviceType) || max < 1)
                return [];
            return _services.Values
                .Where(service => service.ServiceType == serviceType && service.Subscriptions.Count > 0 &&
                    _endpoints.TryGetValue(service.Owner.Id, out var owner) && owner.Key == service.Owner)
                .OrderBy(service => service.Instance, StringComparer.Ordinal).Take(max)
                .Select(service => new WatchedService(service.Instance, service.Owner, _endpoints[service.Owner.Id].Node, service.LastSequence))
                .ToArray();
        }
    }

    /// <summary>
    /// The services the endpoint <paramref name="requester"/> may discover, after <paramref name="after"/>
    /// in ordinal instance order, at most <paramref name="max"/> of them. A service the requester may not
    /// discover is absent, exactly like one that does not exist. Null when the requester is unknown.
    /// </summary>
    /// <summary>Changes whenever a service appears in or leaves the directory: a change index for watchers.</summary>
    public long DirectoryRevision
    {
        get
        {
            lock (_gate)
                return _directoryRevision;
        }
    }

    public DirectorySlice? QueryDirectory(string requester, string? serviceType, string? after, int max)
    {
        lock (_gate)
            return _endpoints.TryGetValue(requester, out var record) && max > 0
                ? ReadDirectory(record, serviceType, after, max) : null;
    }

    public DirectorySlice? QueryDirectory(EndpointKey requester, string? serviceType, string? after, int max)
    {
        lock (_gate)
            return Require(requester, out var record) == ServiceStatus.Ok && max > 0
                ? ReadDirectory(record!, serviceType, after, max) : null;
    }

    public (NodeId Origin, DirectorySlice Slice)? QueryDirectory(long deliveryId, string? serviceType, string? after, int max)
    {
        lock (_gate)
        {
            if (!_hostPending.Contains(deliveryId) || !_deliveries.TryGetValue(deliveryId, out var delivery) || delivery.Terminal ||
                Require(delivery.Producer, out var requester) != ServiceStatus.Ok ||
                Require(delivery.Recipient, out var origin) != ServiceStatus.Ok || AccessFailure(delivery, origin!) is not null || max < 1)
                return null;
            return (origin!.Node, ReadDirectory(requester!, serviceType, after, max));
        }
    }

    private DirectorySlice ReadDirectory(EndpointRecord requester, string? type, string? after, int max)
    {
        var lower = after is null ? "" : after + '\0';
        using var open = DirectoryRange(AccessScope.Open, "", type, lower);
        using var domain = DirectoryRange(AccessScope.Domain, requester.Domain, type, lower);
        using var owned = DirectoryRange(AccessScope.None, requester.Key.Id, type, lower);
        IEnumerator<string>?[] ranges = [open, domain, owned];
        for (var i = 0; i < ranges.Length; i++)
            if (ranges[i]?.MoveNext() != true)
                ranges[i] = null;
        var entries = new List<DirectoryEntry>(Math.Min(max, 64));
        while (true)
        {
            var next = -1;
            for (var i = 0; i < ranges.Length; i++)
                if (ranges[i] is { } range && (next < 0 || string.CompareOrdinal(range.Current, ranges[next]!.Current) < 0))
                    next = i;
            if (next < 0 || entries.Count == max)
                return new DirectorySlice(_directoryRevision, entries, next >= 0);
            var service = _services[ranges[next]!.Current];
            entries.Add(new DirectoryEntry(service.Instance, service.ServiceType, _endpoints[service.Owner.Id].Node));
            if (!ranges[next]!.MoveNext())
                ranges[next] = null;
        }
    }

    private IEnumerator<string>? DirectoryRange(AccessScope scope, string principal, string? type, string lower) =>
        _directory.TryGetValue((scope, principal, type), out var names) && string.CompareOrdinal(lower, "\uffff") <= 0
            ? names.GetViewBetween(lower, "\uffff").GetEnumerator() : null;

    private void IndexService(ServiceRecord service, EndpointRecord owner, bool add)
    {
        var scope = service.Policy.Discover;
        var principal = scope switch { AccessScope.Open => "", AccessScope.Domain => owner.Domain, _ => owner.Key.Id };
        Update(null);
        Update(service.ServiceType);

        void Update(string? type)
        {
            var key = (scope, principal, type);
            if (add)
            {
                if (!_directory.TryGetValue(key, out var names))
                    _directory.Add(key, names = new SortedSet<string>(StringComparer.Ordinal));
                names.Add(service.Instance);
            }
            else if (_directory.TryGetValue(key, out var names))
            {
                names.Remove(service.Instance);
                if (names.Count == 0)
                    _directory.Remove(key);
            }
        }
    }

    /// <summary>The contract of one service as a living endpoint may discover it; null when hidden, missing or the endpoint is stale.</summary>
    public ServiceDescriptor? Describe(EndpointKey requester, string instance)
    {
        lock (_gate)
            return Require(requester, out var asker) == ServiceStatus.Ok && _services.TryGetValue(instance, out var target) &&
                Require(target.Owner, out var owner) == ServiceStatus.Ok && Allows(target.Policy.Discover, asker!, owner!)
                ? Descriptor(target) : null;
    }

    /// <summary>Resolves the original provider and checks the current invocation policy for a resident command.</summary>
    public NodeId? ResidentCommandNode(EndpointKey initiator, EndpointKey provider, string instance)
    {
        lock (_gate)
            return Require(initiator, out var asker) == ServiceStatus.Ok &&
                _services.TryGetValue(instance, out var service) && service.Owner == provider &&
                Require(provider, out var owner) == ServiceStatus.Ok &&
                Allows(service.Policy.Discover, asker!, owner!) && Allows(service.Policy.Invoke, asker!, owner!)
                ? owner!.Node : null;
    }

    /// <summary>Reads a single service as the original, generation-fenced initiator of a Host command.</summary>
    public (NodeId Origin, NodeId Target, ServiceDescriptor Descriptor, long Sequence)? QueryService(
        long deliveryId, string instance, bool publicationState)
    {
        lock (_gate)
        {
            if (!_hostPending.Contains(deliveryId) || !_deliveries.TryGetValue(deliveryId, out var delivery) || delivery.Terminal ||
                Require(delivery.Producer, out var requester) != ServiceStatus.Ok ||
                Require(delivery.Recipient, out var origin) != ServiceStatus.Ok || AccessFailure(delivery, origin!) is not null ||
                !_services.TryGetValue(instance, out var target) || Require(target.Owner, out var owner) != ServiceStatus.Ok ||
                !Allows(target.Policy.Discover, requester!, owner!) ||
                (publicationState && !Allows(target.Policy.Subscribe, requester!, owner!)))
                return null;
            return (origin!.Node, owner!.Node, Descriptor(target), target.LastSequence);
        }
    }

    public static string RequestIdFor(long deliveryId) =>
        RequestIdPrefix + deliveryId.ToString(CultureInfo.InvariantCulture);

    public static bool IsValidEndpointId(string? id) => ServiceNames.IsValid(id);

    // ---- Endpoints -------------------------------------------------------------------------

    public ServiceStatus AttachEndpoint(EndpointKey key, NodeId node, string domain = "station")
    {
        if (!IsValidEndpointId(key.Id) || key.Generation == 0 || node.Value == 0 || !ServiceNames.IsValid(domain))
            return ServiceStatus.InvalidRequest;
        lock (_gate)
        {
            if (_endpoints.TryGetValue(key.Id, out var existing))
            {
                if (key.Generation < existing.Key.Generation)
                    return ServiceStatus.StaleGeneration;
                if (key.Generation == existing.Key.Generation)
                {
                    if (existing.Node == node && existing.Domain == domain)
                        return ServiceStatus.Duplicate;
                    // Same receiver, new attachment or domain. Policies are rechecked before presentation.
                    UnindexNode(existing);
                    var changedDomain = existing.Domain != domain;
                    if (changedDomain)
                        foreach (var instance in existing.Services)
                            IndexService(_services[instance], existing, false);
                    if (changedDomain || existing.Services.Count > 0)
                        _directoryRevision++;
                    existing.Node = node;
                    existing.Domain = domain;
                    if (changedDomain)
                        foreach (var instance in existing.Services)
                            IndexService(_services[instance], existing, true);
                    IndexNode(existing);
                    return ServiceStatus.Ok;
                }
                RemoveEndpoint(existing, "generation_replaced");
            }
            else if (_endpointGenerations.TryGetValue(key.Id, out var retiredGeneration))
            {
                if (key.Generation <= retiredGeneration)
                    return ServiceStatus.StaleGeneration;
            }
            else if (_endpointGenerations.Count >= _options.MaxEndpoints)
                return ServiceStatus.LimitExceeded;
            var record = new EndpointRecord(key, node, domain);
            _endpointGenerations[key.Id] = key.Generation;
            _endpoints.Add(key.Id, record);
            IndexNode(record);
            return ServiceStatus.Ok;
        }
    }

    /// <summary>The node of the endpoint that provides an instance now, or null.</summary>
    public NodeId? ProviderNode(string instance)
    {
        lock (_gate)
            return _services.TryGetValue(instance, out var service) && _endpoints.TryGetValue(service.Owner.Id, out var owner) &&
                owner.Key == service.Owner ? owner.Node : null;
    }

    public ServiceStatus DetachEndpoint(EndpointKey key)
    {
        lock (_gate)
        {
            var status = Require(key, out var record);
            if (status != ServiceStatus.Ok)
                return status;
            RemoveEndpoint(record!, "detached");
            return ServiceStatus.Ok;
        }
    }

    /// <summary>A removed world node takes every endpoint attached to it.</summary>
    public int OnNodesRemoved(IEnumerable<NodeId> nodes)
    {
        lock (_gate)
        {
            var removed = 0;
            foreach (var node in nodes)
            {
                if (!_nodeEndpoints.TryGetValue(node, out var ids))
                    continue;
                foreach (var id in ids.ToArray())
                {
                    RemoveEndpoint(_endpoints[id], "node_removed");
                    removed++;
                }
            }
            return removed;
        }
    }

    // ---- Directory and subscriptions -------------------------------------------------------

    public ServiceStatus RegisterService(EndpointKey owner, string instance, string serviceType,
        string schemaId, int major, int minor, ServicePolicy? policy = null)
    {
        policy ??= new ServicePolicy();
        if (!ServiceNames.IsValid(instance) || !ServiceNames.IsValid(serviceType) || !ServiceNames.IsValid(schemaId) ||
            !Enum.IsDefined(policy.Discover) || !Enum.IsDefined(policy.Subscribe) || !Enum.IsDefined(policy.Invoke))
            return ServiceStatus.InvalidRequest;
        var schema = _schemas.Find(schemaId, major);
        if (schema is null || minor < 0 || minor > schema.Minor)
            return ServiceStatus.UnsupportedSchema;
        lock (_gate)
        {
            var status = Require(owner, out var record);
            if (status != ServiceStatus.Ok)
                return status;
            if (_services.TryGetValue(instance, out var existing))
            {
                if (existing.Owner != owner)
                    return ServiceStatus.Forbidden;
                return existing.ServiceType == serviceType && existing.Schema == schema &&
                    existing.Minor == minor && existing.Policy == policy
                    ? ServiceStatus.Duplicate : ServiceStatus.Conflict;
            }
            if (record!.Services.Count >= _options.MaxServicesPerEndpoint || _services.Count >= _options.MaxServices)
                return ServiceStatus.LimitExceeded;
            var service = new ServiceRecord(instance, serviceType, owner, schema, minor, policy);
            _services.Add(instance, service);
            IndexService(service, record, true);
            _directoryRevision++;
            record.Services.Add(instance);
            return ServiceStatus.Ok;
        }
    }

    /// <summary>
    /// Creates or updates the subscriber's subscription named <paramref name="key"/>.
    /// It is confirmed only for the provider's schema and major with a known projection.
    /// </summary>
    public SubscribeResult Subscribe(EndpointKey subscriber, string key, string instance, string schemaId,
        int major, string projection, DeliveryMode mode, int maxPending)
    {
        if (!ServiceNames.IsValid(key) || !ServiceNames.IsValid(instance) || !ServiceNames.IsValid(projection) ||
            !Enum.IsDefined(mode) || (mode == DeliveryMode.Queue && (maxPending < 1 || maxPending > _options.MaxQueuePending)))
            return new SubscribeResult(ServiceStatus.InvalidRequest, 0, 0);
        lock (_gate)
        {
            var status = Require(subscriber, out var record);
            if (status != ServiceStatus.Ok)
                return new SubscribeResult(status, 0, 0);
            status = Authorize(record!, instance, service => service.Policy.Subscribe, out var service);
            if (status != ServiceStatus.Ok)
                return new SubscribeResult(status, 0, 0);
            if (service!.Schema.Id != schemaId || service.Schema.Major != major ||
                !service.Schema.Projections.ContainsKey(projection))
                return new SubscribeResult(ServiceStatus.UnsupportedSchema, 0, 0);
            var pending = mode == DeliveryMode.LatestValue ? 1 : maxPending;
            if (record!.Subscriptions.TryGetValue(key, out var existingId))
            {
                var existing = _subscriptions[existingId];
                if (existing.Instance == instance && existing.Projection == projection &&
                    existing.Mode == mode && existing.MaxPending == pending)
                    return new SubscribeResult(ServiceStatus.Duplicate, existing.Id, existing.Revision);
                // Not-yet-presented deliveries of the old revision are cancelled.
                CancelSubscriptionDeliveries(record, existing, "subscription_changed");
                _services.GetValueOrDefault(existing.Instance)?.Subscriptions.Remove(existing.Id);
                Configure(existing, instance, projection, mode, pending);
                service.Subscriptions.Add(existing.Id);
                return new SubscribeResult(ServiceStatus.Ok, existing.Id, existing.Revision);
            }
            if (record.Subscriptions.Count >= _options.MaxSubscriptionsPerEndpoint ||
                _subscriptions.Count >= _options.MaxSubscriptions)
                return new SubscribeResult(ServiceStatus.LimitExceeded, 0, 0);
            var subscription = new SubscriptionRecord(++_nextSubscription, subscriber, key);
            Configure(subscription, instance, projection, mode, pending);
            _subscriptions.Add(subscription.Id, subscription);
            record.Subscriptions.Add(key, subscription.Id);
            service.Subscriptions.Add(subscription.Id);
            return new SubscribeResult(ServiceStatus.Ok, subscription.Id, subscription.Revision);
        }
    }

    public ServiceStatus Unsubscribe(EndpointKey subscriber, string key)
    {
        lock (_gate)
        {
            var status = Require(subscriber, out var record);
            if (status != ServiceStatus.Ok)
                return status;
            if (!record!.Subscriptions.TryGetValue(key, out var id))
                return ServiceStatus.UnknownService;
            RemoveSubscription(record, _subscriptions[id], "unsubscribed");
            return ServiceStatus.Ok;
        }
    }

    // ---- Publish/subscribe -----------------------------------------------------------------

    /// <summary>
    /// Accepts one publication. Repeating the same producer sequence with the same
    /// canonical payload returns the original message and creates no deliveries.
    /// </summary>
    public PublishResult Publish(EndpointKey producer, string instance, long producerSequence,
        JsonElement payload, int? timeoutTicks, PacketTrafficClass trafficClass)
    {
        var timeout = timeoutTicks ?? _options.DefaultTimeoutTicks;
        if (!ServiceNames.IsValid(instance) || producerSequence < 1 || timeout < 1 ||
            timeout > _options.MaxTimeoutTicks || !Enum.IsDefined(trafficClass))
            return Rejected(ServiceStatus.InvalidRequest);
        lock (_gate)
        {
            var status = Require(producer, out var record);
            if (status != ServiceStatus.Ok)
                return Rejected(status);
            if (!_services.TryGetValue(instance, out var service) || !Visible(service, record!))
                return Rejected(ServiceStatus.UnknownService);
            if (service.Owner != producer)
                return Rejected(ServiceStatus.Forbidden);
            if (producerSequence < service.LastSequence)
                return Rejected(ServiceStatus.StaleSequence);
            status = _schemas.Validate(service.Schema, service.Minor, payload, out var canonical);
            if (status != ServiceStatus.Ok)
                return Rejected(status);
            if (producerSequence == service.LastSequence)
                return service.LastDigest == canonical!.Digest
                    ? new PublishResult(ServiceStatus.Duplicate, service.LastMessageId, service.LastRecipients, [])
                    : Rejected(ServiceStatus.IdempotencyConflict);

            var recipients = service.Subscriptions.Select(id => _subscriptions[id])
                .Where(subscription => CanSubscribe(service, _endpoints[subscription.Subscriber.Id], record!))
                .OrderBy(s => s.Id).ToArray();
            if (recipients.Length > _options.MaxFanout)
                return Rejected(ServiceStatus.LimitExceeded);
            if (!CanQueue(recipients.Length))
                return Rejected(ServiceStatus.Busy);

            var now = _clock();
            var messageId = ++_nextMessage;
            var projections = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var deliveries = new long[recipients.Length];
            for (var i = 0; i < recipients.Length; i++)
            {
                var subscription = recipients[i];
                if (!projections.TryGetValue(subscription.Projection, out var bytes))
                    projections[subscription.Projection] = bytes = SchemaRegistry.Project(canonical!, subscription.Projection);
                var target = _endpoints[subscription.Subscriber.Id];
                var delivery = Create(DeliveryKind.Event, target, record!, instance, service.Schema, service.Minor,
                    bytes, now + timeout);
                delivery.MessageId = messageId;
                delivery.SubscriptionId = subscription.Id;
                delivery.SubscriptionRevision = subscription.Revision;
                delivery.SubscriptionKey = subscription.Key;
                delivery.Projection = subscription.Projection;
                delivery.ProducerSequence = producerSequence;
                Submit(delivery, record!.Node, target.Node, trafficClass, timeout);
                deliveries[i] = delivery.Id;
            }
            service.LastSequence = producerSequence;
            service.LastMessageId = messageId;
            service.LastDigest = canonical!.Digest;
            service.LastRecipients = recipients.Length;
            _published++;
            return new PublishResult(ServiceStatus.Ok, messageId, recipients.Length, deliveries);
        }
    }

    // ---- Command/result --------------------------------------------------------------------

    /// <summary>
    /// Sends a command to one specific service instance. <paramref name="idempotencyKey"/>
    /// names the business operation within the instance: the same key and content
    /// never cause a second effect, other content under the key is a conflict.
    /// </summary>
    public InvokeResult Invoke(EndpointKey initiator, string instance, string command, string idempotencyKey,
        JsonElement arguments, int? timeoutTicks, PacketTrafficClass trafficClass)
    {
        var timeout = timeoutTicks ?? _options.DefaultTimeoutTicks;
        if (!ServiceNames.IsValid(instance) || !ServiceNames.IsValid(command) || !ServiceNames.IsValid(idempotencyKey) ||
            command == SchemaRegistry.DescribeCommand || timeout < 1 || timeout > _options.MaxTimeoutTicks ||
            !Enum.IsDefined(trafficClass))
            return new InvokeResult(ServiceStatus.InvalidRequest, null);
        lock (_gate)
        {
            var status = Require(initiator, out var record);
            if (status != ServiceStatus.Ok)
                return new InvokeResult(status, null);
            status = Authorize(record!, instance, service => service.Policy.Invoke, out var service);
            if (status != ServiceStatus.Ok)
                return new InvokeResult(status, null);
            var definition = _schemas.FindCommand(service!.ServiceType, command);
            if (definition is null)
                return new InvokeResult(ServiceStatus.UnknownCommand, null);
            status = _schemas.Validate(definition.Arguments, definition.Arguments.Minor, arguments, out var canonical);
            if (status != ServiceStatus.Ok)
                return new InvokeResult(status, null);
            var digest = Hash($"{initiator.Id}\n{initiator.Generation}\n{command}\n{canonical!.Digest}");
            if (_operationKeys.TryGetValue((instance, idempotencyKey), out var existingId))
            {
                var existing = _operations[existingId];
                if (existing.Digest != digest)
                    return new InvokeResult(ServiceStatus.IdempotencyConflict, null);
                RetryResult(existing);
                return new InvokeResult(ServiceStatus.Duplicate, View(existing));
            }
            if (record!.ActiveOperations >= _options.MaxActiveOperationsPerEndpoint ||
                !CanQueue(1))
                return new InvokeResult(ServiceStatus.Busy, null);
            if (_operations.Count >= _options.MaxOperations)
                return new InvokeResult(ServiceStatus.LimitExceeded, null);
            var operation = StartOperation(record, service, definition, canonical, digest, timeout, trafficClass, true);
            operation.IdempotencyKey = idempotencyKey;
            _operationKeys.Add((instance, idempotencyKey), operation.Id);
            return new InvokeResult(ServiceStatus.Ok, View(operation));
        }
    }

    /// <summary>
    /// Network discovery: a probe travels to each policy-visible provider of the type
    /// and its descriptor travels back. Unreachable providers never appear.
    /// </summary>
    public DiscoverResult Discover(EndpointKey requester, string serviceType, string scanKey,
        PacketTrafficClass trafficClass)
    {
        if (!ServiceNames.IsValid(serviceType) || !ServiceNames.IsValid(scanKey) || !Enum.IsDefined(trafficClass))
            return new DiscoverResult(ServiceStatus.InvalidRequest, []);
        lock (_gate)
        {
            var status = Require(requester, out var record);
            if (status != ServiceStatus.Ok)
                return new DiscoverResult(status, []);
            if (_scans.TryGetValue((requester, scanKey), out var previous))
                return previous.ServiceType == serviceType
                    ? new DiscoverResult(ServiceStatus.Duplicate, previous.Operations)
                    : new DiscoverResult(ServiceStatus.IdempotencyConflict, []);
            var candidates = _services.Values
                .Where(s => s.ServiceType == serviceType && s.Owner != requester && Visible(s, record!))
                .OrderBy(s => s.Instance, StringComparer.Ordinal).Take(_options.MaxDiscoverFanout).ToArray();
            if (_operations.Count + candidates.Length > _options.MaxOperations || _scans.Count >= _options.MaxOperations)
                return new DiscoverResult(ServiceStatus.LimitExceeded, []);
            if (!CanQueue(candidates.Length))
                return new DiscoverResult(ServiceStatus.Busy, []);
            using var probe = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new { scan = scanKey }));
            var operations = new long[candidates.Length];
            for (var i = 0; i < candidates.Length; i++)
            {
                var definition = _schemas.FindCommand(candidates[i].ServiceType, SchemaRegistry.DescribeCommand)!;
                _schemas.Validate(definition.Arguments, 0, probe.RootElement, out var canonical);
                operations[i] = StartOperation(record!, candidates[i], definition, canonical!, "", _options.DefaultTimeoutTicks,
                    trafficClass, false).Id;
            }
            _scans.Add((requester, scanKey), (serviceType, operations));
            return new DiscoverResult(ServiceStatus.Ok, operations);
        }
    }

    /// <summary>
    /// The initiator's own view of its operation: acceptance and outcome codes only.
    /// The result payload reaches the initiator exclusively by network delivery.
    /// </summary>
    public (ServiceStatus Status, OperationView? Operation) Operation(EndpointKey initiator, long operationId)
    {
        lock (_gate)
        {
            var status = Require(initiator, out _);
            if (status != ServiceStatus.Ok)
                return (status, null);
            return _operations.TryGetValue(operationId, out var operation) && operation.Initiator == initiator
                ? (ServiceStatus.Ok, View(operation)) : (ServiceStatus.Unknown, null);
        }
    }

    // ---- Transport integration -------------------------------------------------------------

    /// <summary>Physical packets waiting to be routed, in creation order.</summary>
    public DeliverySubmission[] TakeSubmissions(int max)
    {
        lock (_gate)
        {
            var count = Math.Min(Math.Max(0, max), _outbox.Count);
            if (count == 0)
                return [];
            var taken = new DeliverySubmission[count];
            var written = 0;
            while (written < count && _outbox.TryDequeue(out var submission))
                if (_deliveries.TryGetValue(submission.DeliveryId, out var delivery) &&
                    delivery.State == DeliveryState.Accepted)
                    taken[written++] = submission;
            return written == count ? taken : taken[..written];
        }
    }

    /// <summary>Records the transport admission of a delivery's packet.</summary>
    public void OnSubmitted(long deliveryId, PacketSubmissionResult result)
    {
        lock (_gate)
        {
            if (!_deliveries.TryGetValue(deliveryId, out var delivery))
                return;
            if (result.Accepted)
                delivery.PacketId = result.PacketId;
            if (delivery.State != DeliveryState.Accepted)
                return; // The terminal receipt raced ahead of this callback.
            if (result.Accepted)
                delivery.State = DeliveryState.InTransit;
            else
                Finish(delivery, result.Rejection switch
                {
                    PacketSubmissionRejection.InvalidRoute => DeliveryState.NoRoute,
                    PacketSubmissionRejection.SessionChanged => DeliveryState.Cancelled,
                    _ => DeliveryState.Busy
                }, ServiceNames.Code(result.Rejection));
        }
    }

    /// <summary>Marks a delivery that could not even be scheduled for routing.</summary>
    public void OnSubmissionFailed(long deliveryId, DeliveryState state, string reason)
    {
        lock (_gate)
        {
            if (_deliveries.TryGetValue(deliveryId, out var delivery) && delivery.State == DeliveryState.Accepted)
                Finish(delivery, state, reason);
        }
    }

    /// <summary>Terminal transport observer. Runs under the transport lock and only takes this lock.</summary>
    public void OnTransportReceipt(PacketDeliveryReceipt receipt)
    {
        if (!receipt.RequestId.StartsWith(RequestIdPrefix, StringComparison.Ordinal) ||
            !long.TryParse(receipt.RequestId.AsSpan(RequestIdPrefix.Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out var deliveryId))
            return;
        lock (_gate)
        {
            if (!_deliveries.TryGetValue(deliveryId, out var delivery) || delivery.Terminal ||
                delivery.State == DeliveryState.TransportDelivered)
                return;
            delivery.PacketId = receipt.PacketId;
            switch (receipt.Status)
            {
                case PacketCompletionStatus.Delivered:
                    Present(delivery);
                    break;
                case PacketCompletionStatus.Dropped:
                    Finish(delivery, DeliveryState.NoRoute, ServiceNames.Code(receipt.Reason));
                    break;
                case PacketCompletionStatus.Expired:
                    Finish(delivery, DeliveryState.Expired, "transport_ttl");
                    break;
                default:
                    Finish(delivery, DeliveryState.Cancelled, ServiceNames.Code(receipt.Reason));
                    break;
            }
        }
    }

    // ---- Inbox and application ACK ---------------------------------------------------------

    /// <summary>
    /// Non-destructive page of inbox entries after <paramref name="afterSequence"/>.
    /// Entries stay until acknowledged, so a lost reply is recovered by polling again.
    /// </summary>
    public InboxPage Poll(long afterSequence, int limit, int maxBytes)
    {
        limit = Math.Clamp(limit, 1, 64);
        lock (_gate)
        {
            ExpireInbox();
            var items = new List<InboxItem>(limit);
            List<(DeliveryRecord Delivery, string Reason)>? revoked = null;
            var bytes = 0;
            var next = afterSequence;
            foreach (var (sequence, delivery) in _inbox)
            {
                if (sequence <= afterSequence)
                    continue;
                if (items.Count == limit)
                    break;
                if (AccessFailure(delivery, _endpoints[delivery.Recipient.Id]) is { } reason)
                {
                    // Suppress replay after revocation. If it was already issued, DM may still confirm the effect.
                    if (!delivery.Dispatched)
                        (revoked ??= []).Add((delivery, reason));
                    continue;
                }
                var size = delivery.InboxBytes;
                if (items.Count > 0 && bytes + size > maxBytes)
                    break;
                bytes += size;
                delivery.Dispatched = true;
                items.Add(Item(sequence, delivery));
                next = sequence;
            }
            if (revoked is not null)
                foreach (var (delivery, reason) in revoked)
                    Finish(delivery, DeliveryState.Cancelled, reason);
            return new InboxPage(next, _inbox.Count, items.ToArray());
        }
    }

    /// <summary>
    /// Application ACK from DM. For a command, <paramref name="result"/> is the
    /// provider's typed result; it starts the separate result leg. A repeated ACK
    /// returns the recorded outcome; an ACK for a delivery DM never received is
    /// rejected; an unknown ID (for example from an older runtime) changes nothing.
    /// </summary>
    public (ServiceStatus Status, DeliveryView? Delivery) Acknowledge(long deliveryId, bool applied, string? reason,
        JsonElement? result = null)
    {
        if (reason is not null && !ServiceNames.IsValid(reason))
            return (ServiceStatus.InvalidRequest, null);
        lock (_gate)
        {
            if (!_deliveries.TryGetValue(deliveryId, out var delivery))
                return (ServiceStatus.Unknown, null);
            if (delivery.Terminal)
                return (ServiceStatus.Duplicate, View(delivery));
            if (delivery.State != DeliveryState.TransportDelivered || !delivery.Dispatched)
                return (ServiceStatus.InvalidRequest, View(delivery));
            if (PastInboxDeadline(delivery, _clock()))
            {
                Finish(delivery, DeliveryState.Unknown, "ack_timeout");
                return (ServiceStatus.Duplicate, View(delivery));
            }
            if (applied)
                _applied++;
            else
                _rejected++;
            if (delivery.Kind != DeliveryKind.Command)
            {
                Finish(delivery, applied ? DeliveryState.Applied : DeliveryState.RejectedByWorld, reason);
                return (ServiceStatus.Ok, View(delivery));
            }

            var operation = _operations[delivery.OperationId];
            string? resultError = null;
            if (applied)
            {
                if (result is not { ValueKind: JsonValueKind.Object } typed)
                    resultError = "missing_result";
                else if (_schemas.Validate(operation.Definition.Result, operation.Definition.Result.Minor, typed,
                             out var canonical) != ServiceStatus.Ok)
                    resultError = "invalid_result";
                else
                    operation.Result = SchemaRegistry.Project(canonical!, "full");
            }
            SetOutcome(operation, applied ? CommandOutcome.Applied : CommandOutcome.RejectedByWorld, reason);
            Finish(delivery, applied ? DeliveryState.Applied : DeliveryState.RejectedByWorld, reason);
            if (resultError is null)
                StartResult(operation);
            else
            {
                // The effect happened; only its report is unusable. It is never re-applied.
                operation.ResultState = ResultLeg.Failed;
                operation.ResultReason = resultError;
            }
            return (ServiceStatus.Ok, View(delivery));
        }
    }

    public DeliveryView? Delivery(long deliveryId)
    {
        lock (_gate)
            return _deliveries.TryGetValue(deliveryId, out var delivery) ? View(delivery) : null;
    }

    public DeliveryView[] MessageDeliveries(long messageId)
    {
        lock (_gate)
            return _deliveries.Values.Where(d => d.Kind == DeliveryKind.Event && d.MessageId == messageId)
                .OrderBy(d => d.Id).Select(View).ToArray();
    }

    public ServiceRuntimeCounters Capture()
    {
        lock (_gate)
            return new ServiceRuntimeCounters(_endpoints.Count, _services.Count, _subscriptions.Count,
                _activeDeliveries, _inbox.Count, _inboxBytes, _published, _created, _applied, _rejected, _failed,
                _operations.Count, _outbox.Count);
    }

    // ---- Internals -------------------------------------------------------------------------

    private static PublishResult Rejected(ServiceStatus status) => new(status, 0, 0, []);

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private ServiceStatus Require(EndpointKey key, out EndpointRecord? record)
    {
        record = null;
        if (!IsValidEndpointId(key.Id) || key.Generation == 0)
            return ServiceStatus.InvalidRequest;
        if (!_endpoints.TryGetValue(key.Id, out var found))
            return ServiceStatus.UnknownEndpoint;
        if (found.Key.Generation != key.Generation)
            return ServiceStatus.StaleGeneration;
        record = found;
        return ServiceStatus.Ok;
    }

    private static bool Allows(AccessScope scope, EndpointRecord requester, EndpointRecord owner) =>
        requester.Key == owner.Key || scope == AccessScope.Open ||
        (scope == AccessScope.Domain && requester.Domain == owner.Domain);

    private static bool CanSubscribe(ServiceRecord service, EndpointRecord subscriber, EndpointRecord owner) =>
        Allows(service.Policy.Discover, subscriber, owner) && Allows(service.Policy.Subscribe, subscriber, owner);

    private bool Visible(ServiceRecord service, EndpointRecord requester) =>
        _endpoints.TryGetValue(service.Owner.Id, out var owner) && Allows(service.Policy.Discover, requester, owner);

    /// <summary>A hidden service is indistinguishable from a missing one.</summary>
    private ServiceStatus Authorize(EndpointRecord requester, string instance, Func<ServiceRecord, AccessScope> scope,
        out ServiceRecord? service)
    {
        if (!_services.TryGetValue(instance, out service) || !Visible(service, requester))
        {
            service = null;
            return ServiceStatus.UnknownService;
        }
        return Allows(scope(service), requester, _endpoints[service.Owner.Id]) ? ServiceStatus.Ok : ServiceStatus.Forbidden;
    }

    private static void Configure(SubscriptionRecord subscription, string instance, string projection,
        DeliveryMode mode, int pending)
    {
        subscription.Instance = instance;
        subscription.Projection = projection;
        subscription.Mode = mode;
        subscription.MaxPending = pending;
        subscription.Revision++;
    }

    private void IndexNode(EndpointRecord record)
    {
        if (!_nodeEndpoints.TryGetValue(record.Node, out var ids))
            _nodeEndpoints[record.Node] = ids = new HashSet<string>(StringComparer.Ordinal);
        ids.Add(record.Key.Id);
    }

    private void UnindexNode(EndpointRecord record)
    {
        if (_nodeEndpoints.TryGetValue(record.Node, out var ids) && ids.Remove(record.Key.Id) && ids.Count == 0)
            _nodeEndpoints.Remove(record.Node);
    }

    private DeliveryRecord Create(DeliveryKind kind, EndpointRecord recipient, EndpointRecord producer, string instance,
        SchemaDefinition schema, int minor, byte[]? payload, long deadline)
    {
        var delivery = new DeliveryRecord
        {
            Id = ++_nextDelivery, Kind = kind, Recipient = recipient.Key, Producer = producer.Key,
            Instance = instance, Schema = schema, Minor = minor, Payload = payload, CreatedTick = _clock(),
            DeadlineTick = deadline, State = DeliveryState.Accepted
        };
        _deliveries.Add(delivery.Id, delivery);
        recipient.Inbound.Add(delivery.Id);
        _activeDeliveries++;
        _created++;
        return delivery;
    }

    private void Submit(DeliveryRecord delivery, NodeId from, NodeId to, PacketTrafficClass trafficClass, int timeout) =>
        _outbox.Enqueue(new DeliverySubmission(delivery.Id, from, to, (delivery.Payload?.Length ?? 0) + _options.EnvelopeBytes,
            trafficClass, timeout));

    private bool CanQueue(int count) => _activeDeliveries + count <= _options.MaxActiveDeliveries &&
        _outbox.Count + count <= _options.MaxActiveDeliveries;

    private OperationRecord StartOperation(EndpointRecord initiator, ServiceRecord service, CommandDefinition definition,
        CanonicalPayload arguments, string digest, int timeout, PacketTrafficClass trafficClass, bool countsTowardsInitiator)
    {
        var provider = _endpoints[service.Owner.Id];
        var operation = new OperationRecord
        {
            Id = ++_nextOperation, Instance = service.Instance, ServiceType = service.ServiceType, Digest = digest,
            Initiator = initiator.Key, Provider = provider.Key, Definition = definition, TrafficClass = trafficClass,
            CountsTowardsInitiator = countsTowardsInitiator
        };
        _operations.Add(operation.Id, operation);
        if (countsTowardsInitiator)
            initiator.ActiveOperations++;
        var request = Create(DeliveryKind.Command, provider, initiator, service.Instance, definition.Arguments,
            definition.Arguments.Minor, SchemaRegistry.Project(arguments, "full"), _clock() + timeout);
        request.OperationId = operation.Id;
        request.Command = definition.Name;
        request.Projection = "full";
        Submit(request, initiator.Node, provider.Node, trafficClass, timeout);
        return operation;
    }

    private void SetOutcome(OperationRecord operation, CommandOutcome outcome, string? reason)
    {
        if (operation.Outcome != CommandOutcome.Pending)
            return;
        operation.Outcome = outcome;
        operation.OutcomeReason = reason;
        if (operation.CountsTowardsInitiator && _endpoints.TryGetValue(operation.Initiator.Id, out var initiator) &&
            initiator.Key == operation.Initiator)
            initiator.ActiveOperations--;
    }

    /// <summary>Sends the recorded outcome from the provider's node back to the initiator.</summary>
    private void StartResult(OperationRecord operation)
    {
        if (!_endpoints.TryGetValue(operation.Provider.Id, out var provider) || provider.Key != operation.Provider)
        {
            operation.ResultState = ResultLeg.Failed;
            operation.ResultReason = "provider_gone";
            return;
        }
        if (!_endpoints.TryGetValue(operation.Initiator.Id, out var initiator) || initiator.Key != operation.Initiator)
        {
            operation.ResultState = ResultLeg.Failed;
            operation.ResultReason = "initiator_gone";
            return;
        }
        if (!CanQueue(1))
        {
            operation.ResultState = ResultLeg.Failed;
            operation.ResultReason = "busy";
            return;
        }
        var result = Create(DeliveryKind.Result, initiator, provider, operation.Instance, operation.Definition.Result,
            operation.Definition.Result.Minor, operation.Outcome == CommandOutcome.Applied ? operation.Result : null,
            _clock() + _options.ResultTimeoutTicks);
        result.OperationId = operation.Id;
        result.Command = operation.Definition.Name;
        result.Outcome = ServiceNames.Code(operation.Outcome);
        result.OutcomeReason = operation.OutcomeReason;
        operation.ResultAttempts++;
        operation.ResultState = ResultLeg.InTransit;
        operation.ResultReason = null;
        Submit(result, provider.Node, initiator.Node, operation.TrafficClass, _options.ResultTimeoutTicks);
    }

    /// <summary>A repeated request may resend a known outcome; it never re-applies the command.</summary>
    private void RetryResult(OperationRecord operation)
    {
        if (operation.Outcome is CommandOutcome.Applied or CommandOutcome.RejectedByWorld &&
            operation.ResultState == ResultLeg.Failed && operation.ResultAttempts < _options.MaxResultAttempts &&
            (operation.Outcome == CommandOutcome.RejectedByWorld || operation.Result is not null))
            StartResult(operation);
    }

    private byte[]? Describe(OperationRecord operation)
    {
        if (!_services.TryGetValue(operation.Instance, out var service) || service.Owner != operation.Provider)
            return null;
        var descriptor = JsonSerializer.SerializeToElement(Descriptor(service), JsonSerializerOptions.Web);
        return _schemas.Validate(operation.Definition.Result, 0, descriptor, out var canonical) == ServiceStatus.Ok
            ? SchemaRegistry.Project(canonical!, "full") : null;
    }

    private ServiceDescriptor Descriptor(ServiceRecord service) => new(service.Instance, service.ServiceType,
        service.Schema.Id, service.Schema.Major, service.Minor, string.Join(',', _schemas.CommandNames(service.ServiceType)));

    private void RemoveEndpoint(EndpointRecord record, string reason)
    {
        // The receiver outcome is decided first, so it is ReceiverGone rather than Cancelled.
        foreach (var deliveryId in record.Inbound.ToArray())
            Finish(_deliveries[deliveryId], DeliveryState.ReceiverGone, reason);
        foreach (var instance in record.Services)
        {
            var service = _services[instance];
            // Subscribers never silently follow an instance ID to another provider.
            foreach (var subscriptionId in service.Subscriptions.ToArray())
            {
                var subscription = _subscriptions[subscriptionId];
                RemoveSubscription(_endpoints[subscription.Subscriber.Id], subscription, "provider_gone");
            }
            _services.Remove(instance);
            IndexService(service, record, false);
            _directoryRevision++;
        }
        foreach (var subscriptionId in record.Subscriptions.Values.ToArray())
            RemoveSubscription(record, _subscriptions[subscriptionId], reason);
        UnindexNode(record);
        _endpoints.Remove(record.Key.Id);
    }

    private void RemoveSubscription(EndpointRecord record, SubscriptionRecord subscription, string reason)
    {
        CancelSubscriptionDeliveries(record, subscription, reason);
        _services.GetValueOrDefault(subscription.Instance)?.Subscriptions.Remove(subscription.Id);
        record.Subscriptions.Remove(subscription.Key);
        _subscriptions.Remove(subscription.Id);
    }

    private void CancelSubscriptionDeliveries(EndpointRecord record, SubscriptionRecord subscription, string reason)
    {
        foreach (var deliveryId in record.Inbound.ToArray())
        {
            var delivery = _deliveries[deliveryId];
            if (delivery.Kind == DeliveryKind.Event && delivery.SubscriptionId == subscription.Id && !delivery.Dispatched)
                Finish(delivery, DeliveryState.Cancelled, reason);
        }
    }

    private void Present(DeliveryRecord delivery)
    {
        // Lifecycle, deadline and policy are checked again before presentation.
        if (!_endpoints.TryGetValue(delivery.Recipient.Id, out var recipient) || recipient.Key != delivery.Recipient)
        {
            Finish(delivery, DeliveryState.ReceiverGone, "receiver_gone");
            return;
        }
        if (_clock() > delivery.DeadlineTick)
        {
            Finish(delivery, DeliveryState.Expired, "deadline");
            return;
        }
        if (AccessFailure(delivery, recipient) is { } reason)
        {
            Finish(delivery, DeliveryState.Cancelled, reason);
            return;
        }
        switch (delivery.Kind)
        {
            case DeliveryKind.Event:
                PresentEvent(delivery, recipient);
                return;
            case DeliveryKind.Command:
                var operation = _operations[delivery.OperationId];
                if (operation.Definition.HostHandled)
                {
                    // Answered by the reached provider without DM: no world effect exists.
                    operation.Result = Describe(operation);
                    if (operation.Result is null)
                    {
                        Finish(delivery, DeliveryState.ReceiverGone, "service_gone");
                        return;
                    }
                    SetOutcome(operation, CommandOutcome.Applied, null);
                    Finish(delivery, DeliveryState.Applied, "host_handled");
                    StartResult(operation);
                    return;
                }
                if (recipient.PendingCommands >= _options.MaxPendingCommandsPerProvider)
                {
                    Finish(delivery, DeliveryState.Busy, "provider_busy");
                    return;
                }
                if (_hostTypes.Contains(operation.ServiceType))
                {
                    // The provider's state lives in the Host: it takes the command instead of a DM inbox.
                    if (_hostQueue.Count >= _options.MaxHostCommands)
                    {
                        Finish(delivery, DeliveryState.Busy, "provider_busy");
                        return;
                    }
                    delivery.State = DeliveryState.TransportDelivered;
                    delivery.Dispatched = true;
                    _hostQueue.Enqueue(delivery.Id);
                    _hostPending.Add(delivery.Id);
                    recipient.PendingCommands++;
                    return;
                }
                if (Enqueue(delivery))
                    recipient.PendingCommands++;
                return;
            default:
                Enqueue(delivery);
                return;
        }
    }

    private void PresentEvent(DeliveryRecord delivery, EndpointRecord recipient)
    {
        var subscription = _subscriptions[delivery.SubscriptionId];
        if (subscription.Mode == DeliveryMode.LatestValue)
        {
            foreach (var deliveryId in recipient.Inbound.ToArray())
            {
                var older = _deliveries[deliveryId];
                if (older.Kind == DeliveryKind.Event && older.SubscriptionId == subscription.Id &&
                    older.State == DeliveryState.TransportDelivered && !older.Dispatched)
                    Finish(older, DeliveryState.Superseded, "newer_value");
            }
        }
        // Latest-value keeps at most one undelivered value; an older one may still be in DM.
        if (subscription.Mode == DeliveryMode.Queue && subscription.Pending >= subscription.MaxPending)
        {
            Finish(delivery, DeliveryState.Busy, "recipient_inbox_full");
            return;
        }
        if (Enqueue(delivery))
            subscription.Pending++;
    }

    private string? AccessFailure(DeliveryRecord delivery, EndpointRecord recipient)
    {
        if (delivery.Kind == DeliveryKind.Event &&
            (!_subscriptions.TryGetValue(delivery.SubscriptionId, out var subscription) ||
             subscription.Revision != delivery.SubscriptionRevision))
            return "subscription_changed";

        var provider = delivery.Kind == DeliveryKind.Command ? delivery.Recipient : delivery.Producer;
        if (!_services.TryGetValue(delivery.Instance, out var service) || service.Owner != provider)
            return "service_gone";
        var requester = recipient;
        if (delivery.Kind == DeliveryKind.Command &&
            (!_endpoints.TryGetValue(delivery.Producer.Id, out requester) || requester.Key != delivery.Producer))
            return "initiator_gone";
        var owner = _endpoints[provider.Id];
        if (delivery.Kind == DeliveryKind.Event)
            return CanSubscribe(service, requester, owner) ? null : "access_revoked";
        return Allows(service.Policy.Discover, requester, owner) &&
            (_operations[delivery.OperationId].Definition.HostHandled || Allows(service.Policy.Invoke, requester, owner))
                ? null : "access_revoked";
    }

    private bool Enqueue(DeliveryRecord delivery)
    {
        var size = delivery.InboxBytes;
        if (_inbox.Count >= _options.MaxInboxEntries || _inboxBytes + size > _options.MaxInboxBytes)
        {
            Finish(delivery, DeliveryState.Busy, "inbox_full");
            return false;
        }
        delivery.State = DeliveryState.TransportDelivered;
        delivery.InboxSequence = ++_nextInbox;
        _inbox.Add(delivery.InboxSequence, delivery);
        _inboxBytes += size;
        return true;
    }

    private void ExpireInbox()
    {
        var now = _clock();
        List<DeliveryRecord>? expired = null;
        foreach (var delivery in _inbox.Values)
            if (PastInboxDeadline(delivery, now))
                (expired ??= []).Add(delivery);
        if (expired is null)
            return;
        // A dispatched entry without ACK may or may not have been applied: Unknown, never retried.
        foreach (var delivery in expired)
            Finish(delivery, delivery.Dispatched ? DeliveryState.Unknown : DeliveryState.Expired,
                delivery.Dispatched ? "ack_timeout" : "deadline");
    }

    private bool PastInboxDeadline(DeliveryRecord delivery, long now) =>
        now > delivery.DeadlineTick && now - delivery.DeadlineTick > (delivery.Dispatched ? _options.AckTimeoutTicks : 0);

    private void Finish(DeliveryRecord delivery, DeliveryState state, string? reason)
    {
        if (delivery.Terminal)
            return;
        if (delivery.State == DeliveryState.TransportDelivered && _hostPending.Remove(delivery.Id) &&
            _endpoints.TryGetValue(delivery.Recipient.Id, out var hostProvider) && hostProvider.Key == delivery.Recipient)
            hostProvider.PendingCommands--;
        if (delivery.State == DeliveryState.TransportDelivered && _inbox.Remove(delivery.InboxSequence))
        {
            _inboxBytes -= delivery.InboxBytes;
            if (delivery.Kind == DeliveryKind.Event && _subscriptions.TryGetValue(delivery.SubscriptionId, out var subscription))
                subscription.Pending--;
            if (delivery.Kind == DeliveryKind.Command && _endpoints.TryGetValue(delivery.Recipient.Id, out var provider) &&
                provider.Key == delivery.Recipient)
                provider.PendingCommands--;
        }
        if (state is not (DeliveryState.Applied or DeliveryState.RejectedByWorld))
            _failed++;
        var dispatched = delivery.Dispatched;
        delivery.State = state;
        delivery.Reason = reason;
        delivery.Payload = null;
        if (_endpoints.TryGetValue(delivery.Recipient.Id, out var recipient) && recipient.Key == delivery.Recipient)
            recipient.Inbound.Remove(delivery.Id);
        _activeDeliveries--;
        _history.Enqueue(delivery.Id);
        while (_history.Count > _options.DeliveryHistory)
            _deliveries.Remove(_history.Dequeue());
        if (delivery.Kind != DeliveryKind.Event && _operations.TryGetValue(delivery.OperationId, out var operation))
            OnOperationLeg(operation, delivery.Kind, state, reason, dispatched);
    }

    private void OnOperationLeg(OperationRecord operation, DeliveryKind kind, DeliveryState state, string? reason,
        bool dispatched)
    {
        if (kind == DeliveryKind.Command)
        {
            // Applied and RejectedByWorld outcomes are recorded by the caller first.
            if (state is not (DeliveryState.Applied or DeliveryState.RejectedByWorld))
                SetOutcome(operation, dispatched ? CommandOutcome.Unknown : CommandOutcome.NotApplied, reason);
            return;
        }
        if (state is DeliveryState.Applied or DeliveryState.RejectedByWorld)
        {
            operation.ResultState = ResultLeg.Delivered;
            operation.ResultReason = state == DeliveryState.RejectedByWorld ? reason : null;
        }
        else
        {
            operation.ResultState = ResultLeg.Failed;
            operation.ResultReason = reason;
        }
    }

    private InboxItem Item(long sequence, DeliveryRecord delivery)
    {
        JsonElement? payload = null;
        if (delivery.Payload is { } bytes)
        {
            using var document = JsonDocument.Parse(bytes);
            payload = document.RootElement.Clone();
        }
        var serviceType = delivery.Kind == DeliveryKind.Event ? null
            : _operations.TryGetValue(delivery.OperationId, out var operation) ? operation.ServiceType : null;
        return new InboxItem(sequence, delivery.Id, ServiceNames.Code(delivery.Kind), delivery.MessageId,
            delivery.OperationId, delivery.Recipient.Id, delivery.Recipient.Generation, delivery.SubscriptionKey,
            delivery.Instance, serviceType, delivery.Command, delivery.Producer.Id, delivery.Producer.Generation,
            delivery.ProducerSequence, delivery.Schema.Id, delivery.Schema.Major, delivery.Minor, delivery.Projection,
            delivery.Outcome, delivery.OutcomeReason, payload, delivery.CreatedTick, delivery.DeadlineTick);
    }

    private static DeliveryView View(DeliveryRecord delivery) => new(delivery.Id, delivery.MessageId, delivery.State,
        delivery.Recipient.Id, delivery.Recipient.Generation, delivery.PacketId, delivery.Reason, delivery.Kind,
        delivery.OperationId);

    private static OperationView View(OperationRecord operation) => new(operation.Id, operation.Instance,
        operation.Definition.Name, operation.Initiator.Id, operation.Initiator.Generation, operation.Outcome,
        operation.OutcomeReason, operation.ResultState, operation.ResultReason, operation.ResultAttempts);
}
