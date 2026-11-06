using System.Diagnostics;

namespace NetDM.Core;

public enum RadioHopOutcome
{
    TransmittedCable = 0,
    TransmittedRadio = 1,
    LinkUnavailable = 2,
    PacketTooLarge = 3,
    ChannelBusy = 4,
    OldTick = 5
}

/// <summary>Service class independent of player/NPC identity.</summary>
public enum PacketTrafficClass : byte { Background = 0, Interactive = 1 }

/// <summary>
/// Submission is NOT delivery. PacketId is scoped to the transport's StreamId.
/// RequestId is an opaque caller tag, not a deduplication or authentication key.
/// </summary>
public enum PacketSubmissionRejection : byte
{
    None = 0, InvalidRoute = 1, QueueFull = 2, BackgroundCongestion = 3
}
public readonly record struct PacketSubmissionResult(
    bool Accepted, Guid StreamId, long PacketId, string RequestId,
    PacketSubmissionRejection Rejection);

public enum PacketCompletionStatus : byte
{
    Delivered = 0, Dropped = 1, Expired = 2, Cancelled = 3
}
public enum PacketCompletionReason : byte
{
    Delivered = 0, LinkFailure = 1, PacketTooLarge = 2,
    TimeToLive = 3, CancelledByCaller = 4
}

/// <summary>One terminal event per accepted, tracked packet.</summary>
public sealed record PacketDeliveryReceipt(
    long Sequence, long PacketId, string RequestId, PacketTrafficClass TrafficClass,
    PacketCompletionStatus Status, PacketCompletionReason Reason,
    long CreatedTick, long CompletedTick, int Bytes, int SuccessfulHops);

/// <summary>
/// Poll by passing back NextSequence. Gap means the consumer must resynchronize:
/// at least one event was overwritten or the cursor belongs to a newer stream.
/// </summary>
public sealed record PacketReceiptPage(
    Guid StreamId, long OldestSequence, long LatestSequence, long NextSequence,
    bool Gap, PacketDeliveryReceipt[] Receipts);

public sealed record PacketTransportOptions(
    int Capacity = 2048,
    int MaxAgeTicks = 900,
    int MaxReroutes = 8,
    int MaxAdvancesPerTick = 1024,
    int NoRouteRetryDelayTicks = 3,
    int MaxPendingReroutes = 64,
    int MaxConcurrentReroutes = 2,
    int MaxTickWorkMilliseconds = 25,
    int MaxNoRouteRetryDelayTicks = 96,
    int RerouteQueueRetryDelayTicks = 8,
    bool AdaptivePacketVisits = false,
    bool EventDrivenPacketWaiting = false,
    bool EventDrivenRerouteCapacity = false,
    bool PrioritizeInteractiveTraffic = false,
    int InteractiveReservedSlots = 0,
    int InteractiveVisitWeight = 3,
    bool PrioritizeInteractiveReroutes = false,
    int InteractiveRerouteReservedSlots = 0,
    int InteractiveRerouteWeight = 3,
    bool PrioritizeInteractiveAirtime = false,
    int InteractiveAirtimeReservePercent = 25,
    int BackgroundCongestionHighPercent = 0,
    int BackgroundCongestionLowPercent = 60,
    int InteractiveLatencyObjectiveTicks = 10,
    int ReceiptHistoryCapacity = 1024)
{
    public void Validate()
    {
        if (Capacity is < 1 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(Capacity));
        if (MaxAgeTicks is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(MaxAgeTicks));
        if (MaxReroutes is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(MaxReroutes));
        if (MaxAdvancesPerTick is < 1 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(MaxAdvancesPerTick));
        if (NoRouteRetryDelayTicks is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(NoRouteRetryDelayTicks));
        if (MaxPendingReroutes is < 1 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(MaxPendingReroutes));
        if (MaxConcurrentReroutes is < 1 or > 32 || MaxConcurrentReroutes > MaxPendingReroutes)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentReroutes));
        if (MaxTickWorkMilliseconds is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(MaxTickWorkMilliseconds));
        if (MaxNoRouteRetryDelayTicks < NoRouteRetryDelayTicks ||
            MaxNoRouteRetryDelayTicks > 10_000)
            throw new ArgumentOutOfRangeException(nameof(MaxNoRouteRetryDelayTicks));
        if (RerouteQueueRetryDelayTicks is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(RerouteQueueRetryDelayTicks));
        if (EventDrivenRerouteCapacity && !EventDrivenPacketWaiting)
            throw new ArgumentException("Capacity wakeups require event-driven packet waiting.");
        if (PrioritizeInteractiveTraffic && !EventDrivenPacketWaiting)
            throw new ArgumentException("Packet priorities require event-driven waiting.");
        if (InteractiveReservedSlots < 0 || InteractiveReservedSlots >= Capacity)
            throw new ArgumentOutOfRangeException(nameof(InteractiveReservedSlots));
        if (InteractiveVisitWeight is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(InteractiveVisitWeight));
        if (PrioritizeInteractiveReroutes &&
            (!PrioritizeInteractiveTraffic || !EventDrivenRerouteCapacity))
            throw new ArgumentException("Interactive reroute QoS requires packet priorities and capacity events.");
        if (InteractiveRerouteReservedSlots < 0 ||
            InteractiveRerouteReservedSlots >= MaxPendingReroutes ||
            (InteractiveRerouteReservedSlots > 0 && !PrioritizeInteractiveReroutes))
            throw new ArgumentOutOfRangeException(nameof(InteractiveRerouteReservedSlots));
        if (InteractiveRerouteWeight is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(InteractiveRerouteWeight));
        if (PrioritizeInteractiveAirtime && !PrioritizeInteractiveTraffic)
            throw new ArgumentException("Radio airtime QoS requires interactive packet priorities.");
        if (InteractiveAirtimeReservePercent is < 1 or > 90)
            throw new ArgumentOutOfRangeException(nameof(InteractiveAirtimeReservePercent));
        if (BackgroundCongestionHighPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(BackgroundCongestionHighPercent));
        if (BackgroundCongestionLowPercent is < 0 or > 99 ||
            (BackgroundCongestionHighPercent > 0 &&
             BackgroundCongestionLowPercent >= BackgroundCongestionHighPercent))
            throw new ArgumentOutOfRangeException(nameof(BackgroundCongestionLowPercent));
        if (BackgroundCongestionHighPercent > 0 && !PrioritizeInteractiveTraffic)
            throw new ArgumentException("Congestion guard requires interactive packet priorities.");
        if (InteractiveLatencyObjectiveTicks is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(InteractiveLatencyObjectiveTicks));
        if (ReceiptHistoryCapacity is < 0 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(ReceiptHistoryCapacity));
    }
}

public readonly record struct PacketRerouteRequest(
    long Id, long PacketId, NodeId Source, NodeId Destination, long RequestedTick,
    PacketTrafficClass TrafficClass = PacketTrafficClass.Background);

public sealed record PacketTransportCounters(
    long Enqueued, long Rejected, long Delivered, long Dropped,
    long Expired, long Reroutes, long BusyDeferrals, long WirelessHops,
    long CableHops, long DeliveredBytes, int Queued, int PeakQueued,
    long LastTick,
    long NoRouteDeferrals, long LinkFailureDrops, long OversizeDrops,
    long QueueFullRejections, long InvalidRejections,
    double DeliveryAgeP50Ticks, double DeliveryAgeP95Ticks,
    double MeanDeliveredHops, long TotalDeliveryAgeTicks,
    int RerouteQueued, int RerouteInFlight, long RerouteCompleted,
    long RerouteCancelled, long RerouteQueueDeferrals,
    long RerouteSchedulerRejections, double RerouteWaitP95Ticks,
    long SkippedTicks, long TickAdmissionFailures,
    double ForwardingP95Milliseconds,
    double FirstHopAgeP50Ticks, double FirstHopAgeP95Ticks,
    long FirstHopSamples, long TickBudgetStops,
    long NoRouteBackoffs, long NoRouteBackoffTicks,
    long CooldownSkips, long LocalChangeWakeups,
    long RecoveredOriginalHops, long StaleNegativeResults,
    int TrackedChangedNodes,
    long ForwardingVisits, long VisitBudgetStops,
    int CurrentVisitLimit, long AdaptiveLimitIncreases,
    long AdaptiveLimitDecreases, long PreRouteBackpressureRejections,
    int ReservedAdmissions, long AdmissionReservations,
    int ReadyPackets, int DelayedPackets, int AwaitingReroutePackets,
    long TimerWakeups, long TopologyWakeups, long ExpiredWhileParked,
    int CapacityBlockedPackets, long CapacityWakeups,
    double MaxAdvanceMilliseconds, double MaxLockWaitMilliseconds,
    long LockWaitOver100Milliseconds,
    long InteractiveEnqueued, long InteractiveDelivered, long BackgroundDelivered,
    int InteractiveQueued, int BackgroundQueued,
    long InteractivePreRouteRejections, long BackgroundPreRouteRejections,
    double InteractiveDeliveryAgeP95Ticks, double BackgroundDeliveryAgeP95Ticks,
    int InteractiveRerouteQueued, int BackgroundRerouteQueued,
    int InteractiveRerouteInFlight, int BackgroundRerouteInFlight,
    int InteractiveCapacityBlocked, int BackgroundCapacityBlocked,
    long InteractiveReroutesStarted, long BackgroundReroutesStarted,
    long InteractiveCapacityWakeups,
    double InteractiveRerouteWaitP95Ticks, double BackgroundRerouteWaitP95Ticks,
    long BackgroundCongestionRejections, bool BackgroundCongestionThrottled,
    double InteractiveDeliveryAgeP99Ticks, double BackgroundDeliveryAgeP99Ticks,
    double InteractiveFirstHopAgeP95Ticks, double InteractiveFirstHopAgeP99Ticks,
    double BackgroundFirstHopAgeP95Ticks, double BackgroundFirstHopAgeP99Ticks,
    long InteractiveFirstHopSamples, long BackgroundFirstHopSamples,
    long InteractiveDropped, long BackgroundDropped,
    long InteractiveExpired, long BackgroundExpired,
    long InteractiveCompletedBeyondObjective,
    int InteractiveOutstandingBeyondObjective,
    long InteractiveOldestOutstandingAgeTicks,
    long BackgroundOldestOutstandingAgeTicks);

/// <summary>
/// Bounded round-robin store-and-forward. Advance never calculates a route:
/// invalid hops only emit bounded reroute requests. The host schedules these
/// requests independently at background priority, and applies results after
/// validating the packet identity and its actual current position.
/// </summary>
/// <summary>One temporary admission slot, held while the originating route is computed.
/// Must be disposed even if the route never runs or is cancelled.</summary>
public sealed class PacketAdmissionLease : IDisposable
{
    private RadioPacketTransport? _owner;

    internal PacketAdmissionLease(RadioPacketTransport owner, PacketTrafficClass trafficClass)
    {
        _owner = owner;
        TrafficClass = trafficClass;
    }
    internal PacketTrafficClass TrafficClass { get; }

    internal bool Consume(RadioPacketTransport owner) =>
        ReferenceEquals(Interlocked.CompareExchange(ref _owner, null, owner), owner);

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseReservedAdmission(TrafficClass);
}

public sealed class RadioPacketTransport
{
    private const int SampleWindow = 1024;
    private const int MaxRememberedChangedNodes = 65_536;

    private sealed class PendingPacket(RouteResult route, int size, long tick, long id, PacketTrafficClass trafficClass)
    {
        public long Id = id;
        public readonly PacketTrafficClass TrafficClass = trafficClass;
        public IReadOnlyList<NodeId> Path = route.Path;
        public readonly NodeId Destination = route.Path[^1];
        public int Index;
        public readonly int Bytes = size;
        public readonly long CreatedTick = tick;
        public string? RequestId;
        public long ExpiresAtTick;
        public int Reroutes;
        public long NextRetryTick;
        public int SuccessfulHops;
        public bool Active = true;
        public long PendingRerouteId;
        public int NoRouteStreak;
        public long LastRouteAttemptTick = -1;
        public long SeenLocalRevision;
        public PacketWaitState WaitState;
        public int WaitVersion;
    }

    private readonly SharedMediumNetwork _network;
    private readonly PacketTransportOptions _settings;
    private readonly object _gate = new();
    private readonly Guid _receiptStreamId = Guid.NewGuid();
    private readonly Queue<PacketDeliveryReceipt> _receipts = new();
    private long _receiptSequence;
    // The legacy FIFO is preserved for controlled G/H comparisons.
    private enum PacketWaitState : byte { None, Ready, Delayed, Rerouting, CapacityBlocked }
    private readonly Queue<PendingPacket> _queue = new();
    private readonly Queue<(PendingPacket Packet, int Version)> _ready = new();
    private readonly Queue<(PendingPacket Packet, int Version)> _readyInteractive = new();
    private long _priorityCursor;
    private readonly PriorityQueue<(PendingPacket Packet, int Version), (long Tick, long Sequence)> _delayed = new();
    private readonly PriorityQueue<PendingPacket, long> _expirations = new();
    private readonly Dictionary<NodeId, HashSet<long>> _waitingByNode = new();
    private readonly Queue<(PendingPacket Packet, int Version)> _capacityBlocked = new();
    private readonly Queue<(PendingPacket Packet, int Version)> _capacityBlockedInteractive = new();
    private long _capacityPriorityCursor;
    private long _timerSequence, _timerWakeups, _topologyWakeups, _expiredWhileParked;
    private int _delayedCount, _reroutingCount, _capacityBlockedCount, _retirementsSinceExpiryCompaction;
    private long _capacityWakeups;
    private readonly Dictionary<long, PendingPacket> _active = new();
    private readonly Queue<PacketRerouteRequest> _rerouteQueue = new();
    private readonly Queue<PacketRerouteRequest> _rerouteInteractiveQueue = new();
    private long _reroutePriorityCursor;
    private int _backgroundReroutesInFlight, _capacityBlockedInteractiveCount;
    private long _interactiveReroutesStarted, _backgroundReroutesStarted;
    private long _interactiveCapacityWakeups;
    private readonly Dictionary<long, PendingPacket> _inflight = new();
    // Local node-change revisions are bounded by the size of the network.
    // They let an affected waiting packet retry early without waking every
    // packet for each unrelated topology update elsewhere in the graph.
    private readonly Dictionary<NodeId, long> _localRevisions = new();
    private long _topologyChangeRevision;
    private readonly long[] _completedAges = new long[SampleWindow];
    private readonly long[] _interactiveAges = new long[SampleWindow];
    private readonly long[] _backgroundAges = new long[SampleWindow];
    private readonly long[] _firstHopAges = new long[SampleWindow];
    private readonly long[] _interactiveFirstHopAges = new long[SampleWindow];
    private readonly long[] _backgroundFirstHopAges = new long[SampleWindow];
    private long _interactiveFirstHopSamples, _backgroundFirstHopSamples;
    private readonly long[] _rerouteWaitAges = new long[SampleWindow];
    private readonly long[] _interactiveRerouteWaitAges = new long[SampleWindow];
    private readonly long[] _backgroundRerouteWaitAges = new long[SampleWindow];
    private long _interactiveRerouteWaitSamples, _backgroundRerouteWaitSamples;
    private readonly long[] _forwardUs = new long[SampleWindow];
    private long _maxAdvanceUs, _maxLockWaitUs, _lockWaitOver100Ms;
    private long _enqueued, _rejected, _delivered, _dropped, _expired;
    private long _reroutes, _busy, _radioHops, _cableHops, _bytesDelivered;
    private long _noRouteDeferrals, _linkFailureDrops, _oversizeDrops;
    private long _fullRejects, _invalidRejects, _totalAge, _totalDeliveredHops;
    private long _rerouteCompleted, _rerouteCancelled, _rerouteQueueDeferrals;
    private long _rerouteSchedulerRejections, _skippedTicks, _tickAdmissionFailures;
    private long _rerouteSampleCount, _forwardSampleCount, _firstHopSampleCount;
    private long _tickBudgetStops;
    private long _noRouteBackoffs, _noRouteBackoffTicks, _cooldownSkips;
    private long _localWakes, _recoveredHops, _staleNegativeResults;
    private long _lastTick = -1, _nextPacketId, _nextRequestId;
    private long _forwardingVisits, _visitBudgetStops, _adaptiveIncreases, _adaptiveDecreases;
    private long _preRouteRejects, _admissionReservations;
    private int _peak, _reservedAdmissions, _currentVisitLimit;
    private int _reservedBackgroundAdmissions, _queuedInteractive, _queuedBackground;
    private long _interactiveEnqueued, _interactiveDelivered, _backgroundDelivered;
    private long _interactivePreRouteRejects, _backgroundPreRouteRejects;
    private bool _backgroundCongestionThrottled;
    private long _backgroundCongestionRejects;
    private long _interactiveDropped, _backgroundDropped, _interactiveExpired, _backgroundExpired;
    private long _interactiveDeliveredBeyondObjective;

    public RadioPacketTransport(SharedMediumNetwork network, PacketTransportOptions? settings = null)
    {
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _settings = settings ?? new PacketTransportOptions();
        _settings.Validate();
        _currentVisitLimit = _settings.AdaptivePacketVisits
            ? Math.Min(1024, _settings.MaxAdvancesPerTick)
            : _settings.MaxAdvancesPerTick;
    }

    public PacketTransportOptions Options => _settings;
    public Guid ReceiptStreamId => _receiptStreamId;

    /// <summary>
    /// Bounded, non-destructive receipt polling. Events are a best-effort
    /// replay window, not an unbounded durable message broker. Consumers MUST
    /// check both StreamId and Gap. A missing receipt cannot imply success.
    /// </summary>
    public PacketReceiptPage CaptureReceipts(long afterSequence = 0, int limit = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        if (limit is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_gate)
        {
            long oldest = _receipts.Count == 0
                ? _receiptSequence + 1 : _receipts.Peek().Sequence;
            bool gap = afterSequence < oldest - 1 || afterSequence > _receiptSequence;
            long cursor = gap ? oldest - 1 : afterSequence;
            var events = _receipts.Where(item => item.Sequence > cursor)
                .Take(limit).ToArray();
            long next = events.Length > 0 ? events[^1].Sequence : Math.Min(cursor, _receiptSequence);
            return new PacketReceiptPage(_receiptStreamId, oldest, _receiptSequence,
                next, gap, events);
        }
    }

    private static void ValidateRequestId(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128)
            throw new ArgumentException("Request ID must be 1..128 non-whitespace characters.",
                nameof(requestId));
    }

    private void RecordReceipt(PendingPacket packet, PacketCompletionStatus status,
        PacketCompletionReason reason, long tick)
    {
        if (packet.RequestId is null) return;
        long sequence = ++_receiptSequence;
        if (_settings.ReceiptHistoryCapacity == 0) return;
        if (_receipts.Count == _settings.ReceiptHistoryCapacity) _receipts.Dequeue();
        _receipts.Enqueue(new PacketDeliveryReceipt(sequence, packet.Id,
            packet.RequestId, packet.TrafficClass, status, reason,
            packet.CreatedTick, Math.Max(packet.CreatedTick, tick),
            packet.Bytes, packet.SuccessfulHops));
    }
    private int QueuedCount => _settings.EventDrivenPacketWaiting ? _active.Count : _queue.Count;
    private int ReadyQueueCount => _ready.Count + _readyInteractive.Count;
    private int RerouteQueueCount => _rerouteQueue.Count + _rerouteInteractiveQueue.Count;
    private bool CanQueueReroute(PacketTrafficClass trafficClass) =>
        RerouteQueueCount + _inflight.Count < _settings.MaxPendingReroutes &&
        (!_settings.PrioritizeInteractiveReroutes ||
         trafficClass == PacketTrafficClass.Interactive ||
         _rerouteQueue.Count + _backgroundReroutesInFlight <
             _settings.MaxPendingReroutes - _settings.InteractiveRerouteReservedSlots);
    private static void ValidateTrafficClass(PacketTrafficClass trafficClass)
    {
        if (trafficClass is not (PacketTrafficClass.Background or PacketTrafficClass.Interactive))
            throw new ArgumentOutOfRangeException(nameof(trafficClass));
    }
    private bool IsAdmissionFull(PacketTrafficClass trafficClass) =>
        QueuedCount + _reservedAdmissions >= _settings.Capacity ||
        (_settings.PrioritizeInteractiveTraffic && trafficClass == PacketTrafficClass.Background &&
         _queuedBackground + _reservedBackgroundAdmissions >=
             _settings.Capacity - _settings.InteractiveReservedSlots);

    // Admission-only hysteresis. It never evicts a previously admitted packet
    // or revokes an outstanding pre-route lease. Avoid buffering background
    // traffic into latency-sensitive occupancy when players are active.
    private bool BackgroundCongested()
    {
        if (_settings.BackgroundCongestionHighPercent == 0)
            return false;
        bool interactiveDemand = _queuedInteractive +
            _reservedAdmissions - _reservedBackgroundAdmissions > 0;
        if (!interactiveDemand)
        {
            _backgroundCongestionThrottled = false;
            return false;
        }
        int occupied = QueuedCount + _reservedAdmissions;
        int high = (int)Math.Ceiling(
            _settings.Capacity * _settings.BackgroundCongestionHighPercent / 100.0);
        int low = (int)Math.Floor(
            _settings.Capacity * _settings.BackgroundCongestionLowPercent / 100.0);
        if (_backgroundCongestionThrottled)
        {
            if (occupied <= low) _backgroundCongestionThrottled = false;
        }
        else if (occupied >= high) _backgroundCongestionThrottled = true;
        return _backgroundCongestionThrottled;
    }

    // All scheduler transitions are protected by _gate.
    private void IndexWaiting(PendingPacket packet, bool add)
    {
        if (packet.Index >= packet.Path.Count - 1) return;
        NodeId from = packet.Path[packet.Index], to = packet.Path[packet.Index + 1];
        Change(from);
        if (to != from) Change(to);
        void Change(NodeId id)
        {
            if (add)
            {
                if (!_waitingByNode.TryGetValue(id, out var ids))
                    _waitingByNode[id] = ids = new HashSet<long>();
                ids.Add(packet.Id);
            }
            else if (_waitingByNode.TryGetValue(id, out var ids))
            {
                ids.Remove(packet.Id);
                if (ids.Count == 0) _waitingByNode.Remove(id);
            }
        }
    }

    private void LeaveWait(PendingPacket packet)
    {
        if (packet.WaitState is PacketWaitState.Delayed or PacketWaitState.Rerouting or PacketWaitState.CapacityBlocked)
        {
            IndexWaiting(packet, false);
            if (packet.WaitState == PacketWaitState.Delayed) _delayedCount--;
            else if (packet.WaitState == PacketWaitState.Rerouting) _reroutingCount--;
            else
            {
                _capacityBlockedCount--;
                if (packet.TrafficClass == PacketTrafficClass.Interactive)
                    _capacityBlockedInteractiveCount--;
            }
        }
        packet.WaitVersion++;
        packet.WaitState = PacketWaitState.None;
    }

    private void Ready(PendingPacket packet)
    {
        if (!_settings.EventDrivenPacketWaiting) { _queue.Enqueue(packet); return; }
        if (!packet.Active || packet.WaitState == PacketWaitState.Ready) return;
        LeaveWait(packet);
        packet.WaitState = PacketWaitState.Ready;
        if (_settings.PrioritizeInteractiveTraffic &&
            packet.TrafficClass == PacketTrafficClass.Interactive)
            _readyInteractive.Enqueue((packet, packet.WaitVersion));
        else
            _ready.Enqueue((packet, packet.WaitVersion));
        CompactReadyQueue();
    }

    private void CompactReadyQueue()
    {
        if (ReadyQueueCount <= Math.Max(512, _active.Count * 4)) return;
        CompactReadyLane(_ready);
        CompactReadyLane(_readyInteractive);
    }

    private static void CompactReadyLane(Queue<(PendingPacket Packet, int Version)> queue)
    {
        int count = queue.Count;
        for (int i = 0; i < count; i++)
        {
            var entry = queue.Dequeue();
            if (entry.Packet.Active && entry.Packet.WaitState == PacketWaitState.Ready &&
                entry.Packet.WaitVersion == entry.Version)
                queue.Enqueue(entry);
        }
    }

    private void WaitUntil(PendingPacket packet, long tick)
    {
        if (!_settings.EventDrivenPacketWaiting) { _queue.Enqueue(packet); return; }
        LeaveWait(packet);
        packet.WaitState = PacketWaitState.Delayed;
        packet.NextRetryTick = tick;
        _delayedCount++;
        IndexWaiting(packet, true);
        _delayed.Enqueue((packet, packet.WaitVersion), (tick, ++_timerSequence));
        CompactDelayedQueue();
    }

    private void AwaitReroute(PendingPacket packet)
    {
        if (!_settings.EventDrivenPacketWaiting) { _queue.Enqueue(packet); return; }
        LeaveWait(packet);
        packet.WaitState = PacketWaitState.Rerouting;
        _reroutingCount++;
        IndexWaiting(packet, true);
        // A periodic probe is a safety net for physical repairs that do not
        // produce NotifyRadioUpdates (e.g. cable link changes).
        _delayed.Enqueue((packet, packet.WaitVersion), (_lastTick + 8, ++_timerSequence));
        CompactDelayedQueue();
    }

    private void WaitForRerouteCapacity(PendingPacket packet, long tick)
    {
        if (!_settings.EventDrivenRerouteCapacity)
        {
            // Legacy FIFO requires this field before WaitUntil (which only
            // enqueues in legacy mode). Preserve the original cooldown.
            packet.NextRetryTick = tick + _settings.RerouteQueueRetryDelayTicks;
            WaitUntil(packet, packet.NextRetryTick);
            return;
        }

        LeaveWait(packet);
        packet.WaitState = PacketWaitState.CapacityBlocked;
        _capacityBlockedCount++;
        IndexWaiting(packet, true);
        if (packet.TrafficClass == PacketTrafficClass.Interactive)
            _capacityBlockedInteractiveCount++;
        if (_settings.PrioritizeInteractiveReroutes &&
            packet.TrafficClass == PacketTrafficClass.Interactive)
            _capacityBlockedInteractive.Enqueue((packet, packet.WaitVersion));
        else
            _capacityBlocked.Enqueue((packet, packet.WaitVersion));
        // Safety probe: unannounced cable repair can heal without a reroute slot.
        // Capacity-release and topology events can resume the packet earlier.
        long probe = tick + Math.Max(8, _settings.RerouteQueueRetryDelayTicks * 4L);
        packet.NextRetryTick = probe;
        _delayed.Enqueue((packet, packet.WaitVersion), (probe, ++_timerSequence));
        CompactDelayedQueue();
        CompactCapacityQueue();
    }

    private void CompactCapacityQueue()
    {
        if (_capacityBlocked.Count + _capacityBlockedInteractive.Count <=
            Math.Max(512, _active.Count * 4)) return;
        CompactCapacityLane(_capacityBlocked);
        CompactCapacityLane(_capacityBlockedInteractive);
    }

    private static void CompactCapacityLane(Queue<(PendingPacket Packet, int Version)> queue)
    {
        int count = queue.Count;
        for (int i = 0; i < count; i++)
        {
            var entry = queue.Dequeue();
            if (entry.Packet.Active && entry.Packet.WaitState == PacketWaitState.CapacityBlocked &&
                entry.Packet.WaitVersion == entry.Version)
                queue.Enqueue(entry);
        }
    }

    private void WakeAvailableRerouteCapacity()
    {
        if (!_settings.EventDrivenRerouteCapacity) return;
        int available = _settings.MaxPendingReroutes - RerouteQueueCount - _inflight.Count;
        if (!_settings.PrioritizeInteractiveReroutes)
        {
            // Original H/I/K wake behavior is unchanged unless QoS is enabled.
            while (available > 0 && _capacityBlocked.TryDequeue(out var oldEntry))
            {
                var packet = oldEntry.Packet;
                if (!packet.Active || packet.WaitState != PacketWaitState.CapacityBlocked ||
                    packet.WaitVersion != oldEntry.Version) continue;
                packet.NextRetryTick = Math.Max(0, _lastTick + 1);
                Ready(packet);
                _capacityWakeups++;
                available--;
            }
            return;
        }

        int backgroundAvailable = Math.Max(0,
            _settings.MaxPendingReroutes - _settings.InteractiveRerouteReservedSlots -
            _rerouteQueue.Count - _backgroundReroutesInFlight);
        while (available > 0)
        {
            bool hasInteractive = _capacityBlockedInteractive.Count > 0;
            bool hasBackground = _capacityBlocked.Count > 0 && backgroundAvailable > 0;
            if (!hasInteractive && !hasBackground) break;
            bool pickInteractive = hasInteractive &&
                (!hasBackground || _capacityPriorityCursor %
                    (_settings.InteractiveRerouteWeight + 1) <
                    _settings.InteractiveRerouteWeight);
            var queue = pickInteractive ? _capacityBlockedInteractive : _capacityBlocked;
            var entry = queue.Dequeue();
            var packet = entry.Packet;
            if (!packet.Active || packet.WaitState != PacketWaitState.CapacityBlocked ||
                packet.WaitVersion != entry.Version) continue;
            packet.NextRetryTick = Math.Max(0, _lastTick + 1);
            Ready(packet);
            _capacityWakeups++;
            if (pickInteractive) _interactiveCapacityWakeups++;
            else backgroundAvailable--;
            available--;
            _capacityPriorityCursor++;
        }
    }

    private void CompactDelayedQueue()
    {
        // Event-driven wakeups invalidate heap entries. Bound stale storage.
        if (_delayed.Count <= Math.Max(512, _active.Count * 4)) return;
        var old = _delayed.UnorderedItems.ToArray();
        _delayed.Clear();
        foreach (var entry in old)
        {
            var (packet, version) = entry.Element;
            if (packet.Active && packet.WaitState is PacketWaitState.Delayed or PacketWaitState.Rerouting or PacketWaitState.CapacityBlocked &&
                packet.WaitVersion == version)
                _delayed.Enqueue(entry.Element, entry.Priority);
        }
    }

    private void WakeTimers(long tick)
    {
        while (_expirations.TryPeek(out var packet, out var expiry) && expiry <= tick)
        {
            _expirations.Dequeue();
            if (!packet.Active) continue;
            if (packet.WaitState is PacketWaitState.Delayed or PacketWaitState.Rerouting or PacketWaitState.CapacityBlocked)
                _expiredWhileParked++;
            Expire(packet, tick);
        }
        while (_delayed.TryPeek(out _, out var due) && due.Tick <= tick)
        {
            var (packet, version) = _delayed.Dequeue();
            if (!packet.Active || packet.WaitState is not (PacketWaitState.Delayed or PacketWaitState.Rerouting or PacketWaitState.CapacityBlocked) ||
                packet.WaitVersion != version) continue;
            if (packet.WaitState == PacketWaitState.CapacityBlocked)
                packet.NextRetryTick = tick;
            _timerWakeups++;
            Ready(packet);
        }
    }

    private void CompactExpirations()
    {
        // Amortized rebuild: per-packet heap rebuilds cause rare multi-tick pauses.
        if (++_retirementsSinceExpiryCompaction < 512) return;
        _retirementsSinceExpiryCompaction = 0;
        if (_expirations.Count <= Math.Max(4096, _active.Count * 4)) return;
        _expirations.Clear();
        foreach (var packet in _active.Values)
            _expirations.Enqueue(packet, packet.ExpiresAtTick);
    }

    public PacketTransportCounters Capture()
    {
        lock (_gate)
        {
            int deliveredLength = (int)Math.Min(_delivered, SampleWindow);
            var ages = new long[deliveredLength];
            Array.Copy(_completedAges, ages, deliveredLength);
            Array.Sort(ages);
            int firstHopLength = (int)Math.Min(_firstHopSampleCount, SampleWindow);
            var firstHopAges = new long[firstHopLength];
            Array.Copy(_firstHopAges, firstHopAges, firstHopLength);
            Array.Sort(firstHopAges);
            int rerouteLength = (int)Math.Min(_rerouteSampleCount, SampleWindow);
            var rerouteAges = new long[rerouteLength];
            Array.Copy(_rerouteWaitAges, rerouteAges, rerouteLength);
            Array.Sort(rerouteAges);
            var interactiveRerouteAges = new long[(int)Math.Min(_interactiveRerouteWaitSamples, SampleWindow)];
            var backgroundRerouteAges = new long[(int)Math.Min(_backgroundRerouteWaitSamples, SampleWindow)];
            Array.Copy(_interactiveRerouteWaitAges, interactiveRerouteAges, interactiveRerouteAges.Length);
            Array.Copy(_backgroundRerouteWaitAges, backgroundRerouteAges, backgroundRerouteAges.Length);
            Array.Sort(interactiveRerouteAges);
            Array.Sort(backgroundRerouteAges);
            int forwardLength = (int)Math.Min(_forwardSampleCount, SampleWindow);
            var forwardUs = new long[forwardLength];
            Array.Copy(_forwardUs, forwardUs, forwardLength);
            Array.Sort(forwardUs);
            static double Percentile(long[] sorted, double factor) =>
                sorted.Length == 0 ? 0 :
                sorted[(int)Math.Ceiling((sorted.Length - 1) * factor)];
            int interactiveLength = (int)Math.Min(_interactiveDelivered, SampleWindow);
            int backgroundLength = (int)Math.Min(_backgroundDelivered, SampleWindow);
            var interactiveAges = new long[interactiveLength];
            var backgroundAges = new long[backgroundLength];
            Array.Copy(_interactiveAges, interactiveAges, interactiveLength);
            Array.Copy(_backgroundAges, backgroundAges, backgroundLength);
            Array.Sort(interactiveAges);
            Array.Sort(backgroundAges);

            int iFirstLen = (int)Math.Min(_interactiveFirstHopSamples, SampleWindow);
            int bFirstLen = (int)Math.Min(_backgroundFirstHopSamples, SampleWindow);
            var interactiveFirst = new long[iFirstLen];
            var backgroundFirst = new long[bFirstLen];
            Array.Copy(_interactiveFirstHopAges, interactiveFirst, iFirstLen);
            Array.Copy(_backgroundFirstHopAges, backgroundFirst, bFirstLen);
            Array.Sort(interactiveFirst);
            Array.Sort(backgroundFirst);

            long oldestInteractive = 0, oldestBackground = 0;
            int interactivePendingBeyond = 0;
            foreach (var packet in _active.Values)
            {
                long age = Math.Max(0, _lastTick - packet.CreatedTick);
                if (packet.TrafficClass == PacketTrafficClass.Interactive)
                {
                    oldestInteractive = Math.Max(oldestInteractive, age);
                    if (age > _settings.InteractiveLatencyObjectiveTicks)
                        interactivePendingBeyond++;
                }
                else oldestBackground = Math.Max(oldestBackground, age);
            }
            BackgroundCongested(); // Keep hysteresis state up to date for telemetry.
            return new PacketTransportCounters(
                _enqueued, _rejected, _delivered, _dropped, _expired, _reroutes,
                _busy, _radioHops, _cableHops, _bytesDelivered, QueuedCount,
                _peak, _lastTick, _noRouteDeferrals, _linkFailureDrops, _oversizeDrops,
                _fullRejects, _invalidRejects, Percentile(ages, .5),
                Percentile(ages, .95),
                _delivered == 0 ? 0 : (double)_totalDeliveredHops / _delivered,
                _totalAge, RerouteQueueCount, _inflight.Count,
                _rerouteCompleted, _rerouteCancelled, _rerouteQueueDeferrals,
                _rerouteSchedulerRejections, Percentile(rerouteAges, .95),
                _skippedTicks, _tickAdmissionFailures, Percentile(forwardUs, .95) / 1000.0,
                Percentile(firstHopAges, .5), Percentile(firstHopAges, .95),
                _firstHopSampleCount, _tickBudgetStops,
                _noRouteBackoffs, _noRouteBackoffTicks,
                _cooldownSkips, _localWakes, _recoveredHops,
                _staleNegativeResults, _localRevisions.Count,
                _forwardingVisits, _visitBudgetStops, _currentVisitLimit,
                _adaptiveIncreases, _adaptiveDecreases, _preRouteRejects,
                _reservedAdmissions, _admissionReservations,
                _settings.EventDrivenPacketWaiting ? _active.Count - _delayedCount - _reroutingCount - _capacityBlockedCount : _queue.Count,
                _delayedCount, _reroutingCount, _timerWakeups, _topologyWakeups,
                _expiredWhileParked, _capacityBlockedCount, _capacityWakeups,
                _maxAdvanceUs / 1000.0, _maxLockWaitUs / 1000.0,
                _lockWaitOver100Ms,
                _interactiveEnqueued, _interactiveDelivered, _backgroundDelivered,
                _queuedInteractive, _queuedBackground,
                _interactivePreRouteRejects, _backgroundPreRouteRejects,
                Percentile(interactiveAges, .95), Percentile(backgroundAges, .95),
                _rerouteInteractiveQueue.Count, _rerouteQueue.Count,
                _inflight.Count - _backgroundReroutesInFlight, _backgroundReroutesInFlight,
                _capacityBlockedInteractiveCount,
                _capacityBlockedCount - _capacityBlockedInteractiveCount,
                _interactiveReroutesStarted, _backgroundReroutesStarted,
                _interactiveCapacityWakeups,
                Percentile(interactiveRerouteAges, .95),
                Percentile(backgroundRerouteAges, .95),
                _backgroundCongestionRejects, _backgroundCongestionThrottled,
                Percentile(interactiveAges, .99), Percentile(backgroundAges, .99),
                Percentile(interactiveFirst, .95), Percentile(interactiveFirst, .99),
                Percentile(backgroundFirst, .95), Percentile(backgroundFirst, .99),
                _interactiveFirstHopSamples, _backgroundFirstHopSamples,
                _interactiveDropped, _backgroundDropped, _interactiveExpired, _backgroundExpired,
                _interactiveDeliveredBeyondObjective,
                interactivePendingBeyond, oldestInteractive, oldestBackground);
        }
    }

    /// <summary>
    /// Reserve a *real* queue slot before scheduling an expensive route.
    /// Includes all pending reservations when checking capacity. Without the
    /// lease, simply checking queue depth races between concurrent producers.
    /// </summary>
    public PacketAdmissionLease? TryReserveAdmission(
        PacketTrafficClass trafficClass = PacketTrafficClass.Background) =>
        TryReserveAdmission(trafficClass, out _);

    /// <summary>
    /// Distinguishes physical saturation from congestion policy before any
    /// expensive route search. Null means rejected, not lost or delivered.
    /// </summary>
    public PacketAdmissionLease? TryReserveAdmission(PacketTrafficClass trafficClass,
        out PacketSubmissionRejection rejection)
    {
        ValidateTrafficClass(trafficClass);
        lock (_gate)
        {
            if (IsAdmissionFull(trafficClass))
            {
                _preRouteRejects++;
                if (trafficClass == PacketTrafficClass.Interactive) _interactivePreRouteRejects++;
                else _backgroundPreRouteRejects++;
                rejection = PacketSubmissionRejection.QueueFull;
                return null;
            }
            if (trafficClass == PacketTrafficClass.Background && BackgroundCongested())
            {
                _preRouteRejects++;
                _backgroundPreRouteRejects++;
                _backgroundCongestionRejects++;
                rejection = PacketSubmissionRejection.BackgroundCongestion;
                return null;
            }
            _reservedAdmissions++;
            if (trafficClass == PacketTrafficClass.Background) _reservedBackgroundAdmissions++;
            _admissionReservations++;
            rejection = PacketSubmissionRejection.None;
            return new PacketAdmissionLease(this, trafficClass);
        }
    }

    internal void ReleaseReservedAdmission(PacketTrafficClass trafficClass)
    {
        lock (_gate)
        {
            if (_reservedAdmissions <= 0 ||
                (trafficClass == PacketTrafficClass.Background && _reservedBackgroundAdmissions <= 0))
                throw new InvalidOperationException("Packet admission reservation underflow.");
            _reservedAdmissions--;
            if (trafficClass == PacketTrafficClass.Background) _reservedBackgroundAdmissions--;
        }
    }

    public bool Enqueue(RouteResult route, int bytes, long tick,
        PacketTrafficClass trafficClass = PacketTrafficClass.Background)
    {
        ValidateEnqueue(route, bytes, tick);
        ValidateTrafficClass(trafficClass);
        lock (_gate) return EnqueueCore(route, bytes, tick, trafficClass).Accepted;
    }

    /// <summary>Consumes the admission lease, successful route or not.
    /// The unreserved entry path can never steal its slot.</summary>
    public bool EnqueueReserved(RouteResult route, int bytes, long tick,
        PacketAdmissionLease lease)
    {
        ValidateEnqueue(route, bytes, tick);
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            if (!lease.Consume(this))
                throw new InvalidOperationException("Packet admission lease is already consumed or invalid.");
            _reservedAdmissions--;
            if (lease.TrafficClass == PacketTrafficClass.Background) _reservedBackgroundAdmissions--;
            // The lease was granted before congestion changed: honor it.
            return EnqueueCore(route, bytes, tick, lease.TrafficClass, hasLease: true).Accepted;
        }
    }

    /// <summary>
    /// Enqueue one tracked operation. Acceptance is not completion; poll
    /// CaptureReceipts for a terminal event using the returned packet ID.
    /// Rejected submissions receive an immediate reason and emit no receipt.
    /// </summary>
    public PacketSubmissionResult Submit(string requestId, RouteResult route, int bytes,
        long tick, PacketTrafficClass trafficClass = PacketTrafficClass.Background,
        int? timeoutTicks = null)
    {
        ValidateRequestId(requestId);
        ValidateEnqueue(route, bytes, tick);
        ValidateTrafficClass(trafficClass);
        ValidateTimeout(timeoutTicks);
        lock (_gate)
            return EnqueueCore(route, bytes, tick, trafficClass,
                requestId: requestId, timeoutTicks: timeoutTicks);
    }

    /// <summary>
    /// Consumption of an already granted lease is guaranteed to bypass
    /// subsequently activated congestion admission, but not an invalid route.
    /// </summary>
    public PacketSubmissionResult SubmitReserved(string requestId, RouteResult route,
        int bytes, long tick, PacketAdmissionLease lease, int? timeoutTicks = null)
    {
        ValidateRequestId(requestId);
        ValidateEnqueue(route, bytes, tick);
        ValidateTimeout(timeoutTicks);
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            if (!lease.Consume(this))
                throw new InvalidOperationException("Packet admission lease is already consumed or invalid.");
            _reservedAdmissions--;
            if (lease.TrafficClass == PacketTrafficClass.Background) _reservedBackgroundAdmissions--;
            return EnqueueCore(route, bytes, tick, lease.TrafficClass,
                hasLease: true, requestId: requestId, timeoutTicks: timeoutTicks);
        }
    }

    /// <summary>Explicitly cancel a live tracked or untracked packet.</summary>
    /// <summary>
    /// Epoch-aware cancellation for clients holding a transport-scoped token.
    /// A packet ID from a restarted transport must never cancel a new packet.
    /// </summary>
    public bool TryCancel(Guid streamId, long packetId) =>
        streamId == _receiptStreamId && TryCancel(packetId);

    public bool TryCancel(long packetId)
    {
        if (packetId < 1) return false;
        lock (_gate)
        {
            if (!_active.TryGetValue(packetId, out var packet)) return false;
            if (!_settings.EventDrivenPacketWaiting)
            {
                // Legacy FIFO uses queue size as occupied capacity. Remove
                // the cancelled entry now, not lazily at the next tick.
                int count = _queue.Count;
                for (int i = 0; i < count; i++)
                {
                    var queued = _queue.Dequeue();
                    if (!ReferenceEquals(queued, packet)) _queue.Enqueue(queued);
                }
            }
            RecordReceipt(packet, PacketCompletionStatus.Cancelled,
                PacketCompletionReason.CancelledByCaller,
                _lastTick >= 0 ? _lastTick : packet.CreatedTick);
            Retire(packet);
            return true;
        }
    }

    private void ValidateTimeout(int? timeoutTicks)
    {
        if (timeoutTicks.HasValue &&
            (timeoutTicks.Value < 1 || timeoutTicks.Value > _settings.MaxAgeTicks))
            throw new ArgumentOutOfRangeException(nameof(timeoutTicks));
    }

    private static void ValidateEnqueue(RouteResult route, int bytes, long tick)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (bytes is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
    }

    private PacketSubmissionResult EnqueueCore(RouteResult route, int bytes, long tick,
        PacketTrafficClass trafficClass, bool hasLease = false, string? requestId = null,
        int? timeoutTicks = null)
    {
        if (!route.Found || route.Path is null || route.Path.Count == 0 ||
            (_lastTick >= 0 && tick < _lastTick))
        {
            _rejected++;
            _invalidRejects++;
            return new PacketSubmissionResult(false, _receiptStreamId, 0, requestId ?? "",
                PacketSubmissionRejection.InvalidRoute);
        }
        if (IsAdmissionFull(trafficClass))
        {
            _rejected++;
            _fullRejects++;
            return new PacketSubmissionResult(false, _receiptStreamId, 0, requestId ?? "",
                PacketSubmissionRejection.QueueFull);
        }
        if (!hasLease && trafficClass == PacketTrafficClass.Background && BackgroundCongested())
        {
            _rejected++;
            _backgroundCongestionRejects++;
            return new PacketSubmissionResult(false, _receiptStreamId, 0, requestId ?? "",
                PacketSubmissionRejection.BackgroundCongestion);
        }
        var detached = route with { Path = route.Path.ToArray() };
        var packet = new PendingPacket(detached, bytes, tick, ++_nextPacketId, trafficClass)
        {
            RequestId = requestId,
            ExpiresAtTick = checked(tick + (timeoutTicks ?? _settings.MaxAgeTicks))
        };
        if (trafficClass == PacketTrafficClass.Interactive)
        {
            _interactiveEnqueued++;
            _queuedInteractive++;
        }
        else _queuedBackground++;
        _active.Add(packet.Id, packet);
        Ready(packet);
        if (_settings.EventDrivenPacketWaiting)
            _expirations.Enqueue(packet, packet.ExpiresAtTick);
        _enqueued++;
        _peak = Math.Max(_peak, QueuedCount);
        return new PacketSubmissionResult(true, _receiptStreamId, packet.Id, requestId ?? "",
            PacketSubmissionRejection.None);
    }

    /// <summary>
    /// Reports a batch of locally changed radio devices. The packet loop checks
    /// only the endpoints of its failed next hop, not the global topology epoch.
    /// This avoids retry storms when unrelated nodes move elsewhere.
    /// Alternative paths can emerge without either endpoint changing; bounded
    /// periodic retry still handles those changes.
    /// </summary>
    public void NotifyRadioUpdates(IReadOnlyList<RadioNodeUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0) return;
        lock (_gate)
        {
            var revision = ++_topologyChangeRevision;
            HashSet<long>? affected = _settings.EventDrivenPacketWaiting ? new() : null;
            foreach (var update in updates)
            {
                if (affected is not null && _waitingByNode.TryGetValue(update.Id, out var ids))
                    affected.UnionWith(ids);
                if (_localRevisions.Count >= MaxRememberedChangedNodes &&
                    !_localRevisions.ContainsKey(update.Id))
                {
                    // No correctness relies on this hint cache. Clearing it
                    // bounds memory if node IDs churn over a long-lived host.
                    // Affected packets still retry on the periodic timeout.
                    _localRevisions.Clear();
                }
                _localRevisions[update.Id] = revision;
            }
            if (affected is not null)
                foreach (var id in affected)
                    if (_active.TryGetValue(id, out var packet) &&
                        packet.WaitState is PacketWaitState.Delayed or PacketWaitState.Rerouting or PacketWaitState.CapacityBlocked)
                    {
                        Ready(packet);
                        _topologyWakeups++;
                    }
        }
    }

    private long LocalRevision(PendingPacket packet)
    {
        var from = packet.Path[packet.Index];
        var to = packet.Path[packet.Index + 1];
        _localRevisions.TryGetValue(from, out var a);
        _localRevisions.TryGetValue(to, out var b);
        return Math.Max(a, b);
    }

    private long RetryDelay(int noRouteFailures)
    {
        // Bounded exponential backoff: 3, 6, 12, 24, ... up to the cap.
        var shift = Math.Min(noRouteFailures - 1, 12);
        var delay = (long)_settings.NoRouteRetryDelayTicks << shift;
        return Math.Min(delay, _settings.MaxNoRouteRetryDelayTicks);
    }

    /// <summary>
    /// Only bounded hop forwarding runs under the packet lock. No A* or Dijkstra
    /// can run here; broken hops enqueue a ticket for the separate reroute worker.
    /// </summary>
    public int Advance(long tick, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        // Soft per-tick CPU/wall execution limit. We never preempt a single
        // hop, but after at least one visit we stop rotating the queue when
        // the budget has elapsed. Unvisited packets retain their FIFO order.
        var deadline = started + Math.Max(1L,
            (long)_settings.MaxTickWorkMilliseconds * Stopwatch.Frequency / 1_000);
        lock (_gate)
        {
            // Diagnostic: distinguish monitor contention from work inside
            // the forwarding pass. One Stopwatch read, zero allocations.
            long lockWaitUs = (long)Math.Max(0,
                (Stopwatch.GetTimestamp() - started) * 1_000_000.0 / Stopwatch.Frequency);
            _maxLockWaitUs = Math.Max(_maxLockWaitUs, lockWaitUs);
            if (lockWaitUs >= 100_000) _lockWaitOver100Ms++;
            if (tick <= _lastTick) return 0;
            if (_lastTick >= 0 && tick - _lastTick > 1)
                _skippedTicks += tick - _lastTick - 1;
            _lastTick = tick;
            int visitLimit = _settings.AdaptivePacketVisits
                ? _currentVisitLimit : _settings.MaxAdvancesPerTick;
            if (_settings.EventDrivenPacketWaiting) WakeTimers(tick);
            // Only the entries present at tick start are eligible. During
            // processing a hop/ChannelBusy can Ready() the same packet again;
            // do not consume those new entries until the NEXT simulation tick.
            // The weighted two-lane scheduler must never advance one packet
            // twice while leaving other original entries unvisited.
            int backgroundReadyAtStart = _settings.EventDrivenPacketWaiting ? _ready.Count : 0;
            int interactiveReadyAtStart = _settings.EventDrivenPacketWaiting ? _readyInteractive.Count : 0;
            // Protect bytes only while tick-start interactive-ready entries
            // remain. After the last interactive attempt, background packets
            // can borrow the unused channel capacity within this SAME tick.
            int count = Math.Min(_settings.EventDrivenPacketWaiting ? ReadyQueueCount : _queue.Count, visitLimit);
            int forwarded = 0;
            int visited = 0;
            for (var i = 0; i < count; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (i > 0 && Stopwatch.GetTimestamp() >= deadline)
                {
                    _tickBudgetStops++;
                    break;
                }
                visited++;
                PendingPacket packet;
                if (_settings.EventDrivenPacketWaiting)
                {
                    // Weighted 3-to-1 service with guaranteed background
                    // visits as long as both lanes remain ready.
                    bool pickInteractive = _settings.PrioritizeInteractiveTraffic &&
                        interactiveReadyAtStart > 0 &&
                        (backgroundReadyAtStart == 0 ||
                         _priorityCursor % (_settings.InteractiveVisitWeight + 1) <
                             _settings.InteractiveVisitWeight);
                    if (pickInteractive) interactiveReadyAtStart--;
                    else backgroundReadyAtStart--;
                    if (_settings.PrioritizeInteractiveTraffic) _priorityCursor++;
                    var entry = pickInteractive
                        ? _readyInteractive.Dequeue() : _ready.Dequeue();
                    packet = entry.Packet;
                    if (!packet.Active || packet.WaitState != PacketWaitState.Ready ||
                        packet.WaitVersion != entry.Version) continue;
                    packet.WaitState = PacketWaitState.None;
                }
                else packet = _queue.Dequeue();
                if (tick >= packet.ExpiresAtTick)
                {
                    Expire(packet, tick);
                    continue;
                }
                if (packet.Index >= packet.Path.Count - 1)
                {
                    Delivered(packet, tick);
                    continue;
                }
                if (packet.PendingRerouteId == 0 && tick < packet.NextRetryTick)
                {
                    // An endpoint may have moved or changed its radio profile.
                    // Wake only that packet, with a minimum retry interval,
                    // rather than invalidating every disconnected route after
                    // any unrelated global snapshot update.
                    var revision = LocalRevision(packet);
                    if (revision > packet.SeenLocalRevision)
                    {
                        packet.SeenLocalRevision = revision;
                        var probeFrom = packet.Path[packet.Index];
                        var probeTo = packet.Path[packet.Index + 1];
                        // Local recovery does not need another full A* search
                        // and must not wait for an exponential backoff deadline.
                        if (_network.HasUsableHop(probeFrom, probeTo) ||
                            (packet.LastRouteAttemptTick >= 0 &&
                             tick - packet.LastRouteAttemptTick >=
                                 _settings.NoRouteRetryDelayTicks))
                        {
                            packet.NextRetryTick = tick;
                            _localWakes++;
                        }
                    }
                    if (tick < packet.NextRetryTick)
                    {
                        _cooldownSkips++;
                        WaitUntil(packet, packet.NextRetryTick);
                        continue;
                    }
                }
                // A temporarily broken hop may heal while its reroute is
                // queued or running. Recheck the cheap single-hop path so
                // it can recover without waiting for an expensive search.
                // A successful hop invalidates the ticket generation; any
                // late reroute result is ignored.

                var from = packet.Path[packet.Index];
                var to = packet.Path[packet.Index + 1];
                var airtimeReserve = _settings.PrioritizeInteractiveAirtime &&
                    interactiveReadyAtStart > 0 ? _settings.InteractiveAirtimeReservePercent : 0;
                var result = _network.TryTransmitHop(from, to, packet.Bytes, tick,
                    packet.TrafficClass, airtimeReserve);
                switch (result)
                {
                    case RadioHopOutcome.TransmittedRadio:
                    case RadioHopOutcome.TransmittedCable:
                        if (packet.NoRouteStreak > 0) _recoveredHops++;
                        packet.NoRouteStreak = 0;
                        packet.NextRetryTick = 0;
                        packet.PendingRerouteId = 0; // Cancel logical ticket on healing.
                        if (packet.SuccessfulHops == 0)
                        {
                            long firstAge = Math.Max(0, tick - packet.CreatedTick);
                            _firstHopAges[(int)(_firstHopSampleCount++ % SampleWindow)] = firstAge;
                            if (packet.TrafficClass == PacketTrafficClass.Interactive)
                                _interactiveFirstHopAges[
                                    (int)(_interactiveFirstHopSamples++ % SampleWindow)] = firstAge;
                            else
                                _backgroundFirstHopAges[
                                    (int)(_backgroundFirstHopSamples++ % SampleWindow)] = firstAge;
                        }
                        packet.Index++;
                        packet.SuccessfulHops++;
                        forwarded++;
                        if (result == RadioHopOutcome.TransmittedRadio) _radioHops++;
                        else _cableHops++;
                        if (packet.Index == packet.Path.Count - 1)
                            Delivered(packet, tick);
                        else Ready(packet);
                        break;

                    case RadioHopOutcome.ChannelBusy:
                    case RadioHopOutcome.OldTick:
                        _busy++;
                        Ready(packet);
                        break;

                    case RadioHopOutcome.LinkUnavailable:
                        if (packet.PendingRerouteId != 0)
                        {
                            AwaitReroute(packet);
                            break;
                        }
                        if (packet.Reroutes >= _settings.MaxReroutes)
                        {
                            Drop(packet, PacketCompletionReason.LinkFailure, tick);
                            _linkFailureDrops++;
                            break;
                        }
                        if (!CanQueueReroute(packet.TrafficClass))
                        {
                            _rerouteQueueDeferrals++;
                            WaitForRerouteCapacity(packet, tick);
                            break;
                        }
                        var ticket = new PacketRerouteRequest(++_nextRequestId,
                            packet.Id, from, packet.Destination, tick,
                            packet.TrafficClass);
                        packet.PendingRerouteId = ticket.Id;
                        if (_settings.PrioritizeInteractiveReroutes &&
                            ticket.TrafficClass == PacketTrafficClass.Interactive)
                            _rerouteInteractiveQueue.Enqueue(ticket);
                        else
                            _rerouteQueue.Enqueue(ticket);
                        AwaitReroute(packet);
                        break;

                    case RadioHopOutcome.PacketTooLarge:
                    default:
                        Drop(packet, PacketCompletionReason.PacketTooLarge, tick);
                        _oversizeDrops++;
                        break;
                }
            }
            var elapsedUs = (long)((Stopwatch.GetTimestamp() - started) *
                1_000_000.0 / Stopwatch.Frequency);
            _forwardingVisits += visited;
            if (visited == visitLimit && (_settings.EventDrivenPacketWaiting ? ReadyQueueCount : _queue.Count) != 0)
                _visitBudgetStops++;
            if (_settings.AdaptivePacketVisits && visited > 0)
            {
                // AIMD-like visit ceiling. CPU/wall-time deadline still takes
                // precedence and prevents increased visit counts from
                // monopolizing the dedicated forwarding tick thread.
                long budgetUs = (long)_settings.MaxTickWorkMilliseconds * 1000;
                if (elapsedUs >= budgetUs * 8 / 10)
                {
                    int lower = Math.Max(1, Math.Min(1024, _settings.MaxAdvancesPerTick));
                    int reduced = Math.Max(lower, visitLimit - Math.Max(64, visitLimit / 4));
                    if (reduced < visitLimit)
                    {
                        _currentVisitLimit = reduced;
                        _adaptiveDecreases++;
                    }
                }
                else if (visited == visitLimit && (_settings.EventDrivenPacketWaiting ? ReadyQueueCount : _queue.Count) > 0 &&
                    elapsedUs <= budgetUs * 6 / 10)
                {
                    int increased = Math.Min(_settings.MaxAdvancesPerTick,
                        visitLimit + Math.Max(64, visitLimit / 4));
                    if (increased > visitLimit)
                    {
                        _currentVisitLimit = increased;
                        _adaptiveIncreases++;
                    }
                }
            }
            _forwardUs[(int)(_forwardSampleCount++ % SampleWindow)] = elapsedUs;
            _maxAdvanceUs = Math.Max(_maxAdvanceUs, elapsedUs);
            return forwarded;
        }
    }

    /// <summary>
    /// Removes one pending ticket without running its search. The host keeps a
    /// separate, small limit on concurrently scheduled background reroutes.
    /// </summary>
    public bool TryTakeReroute(out PacketRerouteRequest ticket)
    {
        lock (_gate)
        {
            if (_inflight.Count >= _settings.MaxConcurrentReroutes)
            {
                ticket = default;
                return false;
            }
            bool removedStale = false;
            while (RerouteQueueCount > 0)
            {
                // 3 interactive reroutes per background ticket while both
                // lanes are ready, preserving bounded background progress.
                bool interactive = _settings.PrioritizeInteractiveReroutes &&
                    _rerouteInteractiveQueue.Count > 0 &&
                    (_rerouteQueue.Count == 0 ||
                     _reroutePriorityCursor %
                       (_settings.InteractiveRerouteWeight + 1) <
                         _settings.InteractiveRerouteWeight);
                var source = interactive ? _rerouteInteractiveQueue : _rerouteQueue;
                ticket = source.Dequeue();
                if (!_active.TryGetValue(ticket.PacketId, out var packet) ||
                    packet.PendingRerouteId != ticket.Id)
                {
                    removedStale = true;
                    continue;
                }
                _inflight.Add(ticket.Id, packet);
                if (ticket.TrafficClass == PacketTrafficClass.Background)
                {
                    _backgroundReroutesInFlight++;
                    _backgroundReroutesStarted++;
                }
                else _interactiveReroutesStarted++;
                _reroutePriorityCursor++;
                packet.Reroutes++;
                packet.LastRouteAttemptTick = _lastTick;
                packet.SeenLocalRevision = LocalRevision(packet);
                _reroutes++;
                if (removedStale) WakeAvailableRerouteCapacity();
                return true;
            }
            WakeAvailableRerouteCapacity(); // Discarding stale tickets can free a slot.
            ticket = default;
            return false;
        }
    }

    /// <summary>
    /// A completed search is applied ONLY if its originating ticket is still
    /// active, at its original source node, and targets the original destination.
    /// This prevents stale results from resurrecting expired or dropped packets.
    /// </summary>
    public bool CompleteReroute(PacketRerouteRequest ticket, RouteResult route)
    {
        ArgumentNullException.ThrowIfNull(route);
        lock (_gate)
        {
            if (!_inflight.Remove(ticket.Id, out var packet))
                return false;
            if (packet.TrafficClass == PacketTrafficClass.Background)
                _backgroundReroutesInFlight--;
            WakeAvailableRerouteCapacity();
            _rerouteCompleted++;
            RecordRerouteWait(ticket);
            if (!packet.Active || packet.PendingRerouteId != ticket.Id ||
                packet.Path[packet.Index] != ticket.Source ||
                packet.Destination != ticket.Destination)
                return false;

            packet.PendingRerouteId = 0;
            if (!route.Found || route.Path is null || route.Path.Count == 0 ||
                route.Path[0] != ticket.Source || route.Path[^1] != ticket.Destination)
            {
                // A failed search from an older snapshot is not evidence that
                // the present graph is still disconnected. Retry soon, with
                // a minimum spacing, and do not poison the backoff streak.
                if (route.Version != _network.Snapshot.Version)
                {
                    _staleNegativeResults++;
                    packet.NextRetryTick = _lastTick + _settings.NoRouteRetryDelayTicks;
                    if (_settings.EventDrivenPacketWaiting) WaitUntil(packet, packet.NextRetryTick);
                    return true;
                }
                _noRouteDeferrals++;
                packet.NoRouteStreak++;
                var delay = RetryDelay(packet.NoRouteStreak);
                _noRouteBackoffs++;
                _noRouteBackoffTicks += delay;
                packet.NextRetryTick = _lastTick + delay;
                packet.SeenLocalRevision = LocalRevision(packet);
                if (_settings.EventDrivenPacketWaiting) WaitUntil(packet, packet.NextRetryTick);
                return true;
            }
            // Detached from the router's reusable collections, if any.
            if (_settings.EventDrivenPacketWaiting) LeaveWait(packet);
            packet.Path = route.Path.ToArray();
            packet.Index = 0;
            packet.NoRouteStreak = 0;
            packet.NextRetryTick = _lastTick + 1;
            if (_settings.EventDrivenPacketWaiting) WaitUntil(packet, packet.NextRetryTick);
            return true;
        }
    }

    /// <summary>
    /// Called when global CPU admission rejects a reroute or when a scheduled
    /// search is cancelled or fails. Keep the packet queued for a later tick.
    /// </summary>
    public bool CancelReroute(PacketRerouteRequest ticket, bool schedulerRejected = false)
    {
        lock (_gate)
        {
            if (!_inflight.Remove(ticket.Id, out var packet))
                return false;
            if (packet.TrafficClass == PacketTrafficClass.Background)
                _backgroundReroutesInFlight--;
            WakeAvailableRerouteCapacity();
            _rerouteCancelled++;
            if (schedulerRejected) _rerouteSchedulerRejections++;
            RecordRerouteWait(ticket);
            if (!packet.Active || packet.PendingRerouteId != ticket.Id)
                return false;
            packet.PendingRerouteId = 0;
            packet.NextRetryTick = _lastTick + 1;
            if (_settings.EventDrivenPacketWaiting) WaitUntil(packet, packet.NextRetryTick);
            return true;
        }
    }

    public void RecordTickAdmissionFailure()
    {
        lock (_gate) _tickAdmissionFailures++;
    }

    private void RecordRerouteWait(PacketRerouteRequest ticket)
    {
        var age = Math.Max(0, _lastTick - ticket.RequestedTick);
        _rerouteWaitAges[(int)(_rerouteSampleCount++ % SampleWindow)] = age;
        if (ticket.TrafficClass == PacketTrafficClass.Interactive)
            _interactiveRerouteWaitAges[
                (int)(_interactiveRerouteWaitSamples++ % SampleWindow)] = age;
        else
            _backgroundRerouteWaitAges[
                (int)(_backgroundRerouteWaitSamples++ % SampleWindow)] = age;
    }

    private void Expire(PendingPacket packet, long tick)
    {
        RecordReceipt(packet, PacketCompletionStatus.Expired,
            PacketCompletionReason.TimeToLive, tick);
        _expired++;
        if (packet.TrafficClass == PacketTrafficClass.Interactive) _interactiveExpired++;
        else _backgroundExpired++;
        Retire(packet);
    }

    private void Drop(PendingPacket packet, PacketCompletionReason reason, long tick)
    {
        RecordReceipt(packet, PacketCompletionStatus.Dropped, reason, tick);
        _dropped++;
        if (packet.TrafficClass == PacketTrafficClass.Interactive) _interactiveDropped++;
        else _backgroundDropped++;
        Retire(packet);
    }

    private void Retire(PendingPacket packet)
    {
        if (!packet.Active) return;
        packet.Active = false;
        if (packet.TrafficClass == PacketTrafficClass.Interactive) _queuedInteractive--;
        else _queuedBackground--;
        if (_settings.EventDrivenPacketWaiting) LeaveWait(packet);
        _active.Remove(packet.Id);
        if (_settings.EventDrivenPacketWaiting) CompactExpirations();
        // Tickets already in progress are harmless; result application checks
        // Active. Pending tickets are skipped when the worker next drains them.
    }

    private void Delivered(PendingPacket packet, long tick)
    {
        RecordReceipt(packet, PacketCompletionStatus.Delivered,
            PacketCompletionReason.Delivered, tick);
        _delivered++;
        _bytesDelivered += packet.Bytes;
        var age = Math.Max(0, tick - packet.CreatedTick);
        if (packet.TrafficClass == PacketTrafficClass.Interactive)
        {
            _interactiveDelivered++;
            _interactiveAges[(int)((_interactiveDelivered - 1) % SampleWindow)] = age;
            if (age > _settings.InteractiveLatencyObjectiveTicks)
                _interactiveDeliveredBeyondObjective++;
        }
        else
        {
            _backgroundDelivered++;
            _backgroundAges[(int)((_backgroundDelivered - 1) % SampleWindow)] = age;
        }
        _completedAges[(int)((_delivered - 1) % SampleWindow)] = age;
        _totalAge += age;
        _totalDeliveredHops += packet.SuccessfulHops;
        Retire(packet);
    }
}
