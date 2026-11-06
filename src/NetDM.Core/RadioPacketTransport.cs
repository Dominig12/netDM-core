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
    int InteractiveVisitWeight = 3)
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
    }
}

public readonly record struct PacketRerouteRequest(
    long Id, long PacketId, NodeId Source, NodeId Destination, long RequestedTick);

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
    double InteractiveDeliveryAgeP95Ticks, double BackgroundDeliveryAgeP95Ticks);

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
    private long _timerSequence, _timerWakeups, _topologyWakeups, _expiredWhileParked;
    private int _delayedCount, _reroutingCount, _capacityBlockedCount, _retirementsSinceExpiryCompaction;
    private long _capacityWakeups;
    private readonly Dictionary<long, PendingPacket> _active = new();
    private readonly Queue<PacketRerouteRequest> _rerouteQueue = new();
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
    private readonly long[] _rerouteWaitAges = new long[SampleWindow];
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
    private int QueuedCount => _settings.EventDrivenPacketWaiting ? _active.Count : _queue.Count;
    private int ReadyQueueCount => _ready.Count + _readyInteractive.Count;
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
            else _capacityBlockedCount--;
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
        if (_capacityBlocked.Count <= Math.Max(512, _active.Count * 4)) return;
        int count = _capacityBlocked.Count;
        for (int i = 0; i < count; i++)
        {
            var entry = _capacityBlocked.Dequeue();
            if (entry.Packet.Active && entry.Packet.WaitState == PacketWaitState.CapacityBlocked &&
                entry.Packet.WaitVersion == entry.Version)
                _capacityBlocked.Enqueue(entry);
        }
    }

    private void WakeAvailableRerouteCapacity()
    {
        if (!_settings.EventDrivenRerouteCapacity) return;
        int available = _settings.MaxPendingReroutes - _rerouteQueue.Count - _inflight.Count;
        while (available > 0 && _capacityBlocked.TryDequeue(out var entry))
        {
            var packet = entry.Packet;
            if (!packet.Active || packet.WaitState != PacketWaitState.CapacityBlocked ||
                packet.WaitVersion != entry.Version) continue;
            packet.NextRetryTick = Math.Max(0, _lastTick + 1);
            Ready(packet);
            _capacityWakeups++;
            available--;
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
            _expired++;
            Retire(packet);
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
            _expirations.Enqueue(packet, checked(packet.CreatedTick + _settings.MaxAgeTicks));
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
            return new PacketTransportCounters(
                _enqueued, _rejected, _delivered, _dropped, _expired, _reroutes,
                _busy, _radioHops, _cableHops, _bytesDelivered, QueuedCount,
                _peak, _lastTick, _noRouteDeferrals, _linkFailureDrops, _oversizeDrops,
                _fullRejects, _invalidRejects, Percentile(ages, .5),
                Percentile(ages, .95),
                _delivered == 0 ? 0 : (double)_totalDeliveredHops / _delivered,
                _totalAge, _rerouteQueue.Count, _inflight.Count,
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
                Percentile(interactiveAges, .95), Percentile(backgroundAges, .95));
        }
    }

    /// <summary>
    /// Reserve a *real* queue slot before scheduling an expensive route.
    /// Includes all pending reservations when checking capacity. Without the
    /// lease, simply checking queue depth races between concurrent producers.
    /// </summary>
    public PacketAdmissionLease? TryReserveAdmission(
        PacketTrafficClass trafficClass = PacketTrafficClass.Background)
    {
        ValidateTrafficClass(trafficClass);
        lock (_gate)
        {
            if (IsAdmissionFull(trafficClass))
            {
                _preRouteRejects++;
                if (trafficClass == PacketTrafficClass.Interactive) _interactivePreRouteRejects++;
                else _backgroundPreRouteRejects++;
                return null;
            }
            _reservedAdmissions++;
            if (trafficClass == PacketTrafficClass.Background) _reservedBackgroundAdmissions++;
            _admissionReservations++;
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
        lock (_gate) return EnqueueCore(route, bytes, tick, trafficClass);
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
            return EnqueueCore(route, bytes, tick, lease.TrafficClass);
        }
    }

    private static void ValidateEnqueue(RouteResult route, int bytes, long tick)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (bytes is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
    }

    private bool EnqueueCore(RouteResult route, int bytes, long tick, PacketTrafficClass trafficClass)
    {
        if (!route.Found || route.Path is null || route.Path.Count == 0 ||
            (_lastTick >= 0 && tick < _lastTick))
        {
            _rejected++;
            _invalidRejects++;
            return false;
        }
        if (IsAdmissionFull(trafficClass))
        {
            _rejected++;
            _fullRejects++;
            return false;
        }
        var detached = route with { Path = route.Path.ToArray() };
        var packet = new PendingPacket(detached, bytes, tick, ++_nextPacketId, trafficClass);
        if (trafficClass == PacketTrafficClass.Interactive)
        {
            _interactiveEnqueued++;
            _queuedInteractive++;
        }
        else _queuedBackground++;
        _active.Add(packet.Id, packet);
        Ready(packet);
        if (_settings.EventDrivenPacketWaiting)
            _expirations.Enqueue(packet, checked(tick + _settings.MaxAgeTicks));
        _enqueued++;
        _peak = Math.Max(_peak, QueuedCount);
        return true;
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
            // Snapshot both lanes at tick start. A packet requeued after a
            // hop is not eligible to move a second hop during this tick.
            int backgroundReadyAtStart = _settings.EventDrivenPacketWaiting ? _ready.Count : 0;
            int interactiveReadyAtStart = _settings.EventDrivenPacketWaiting ? _readyInteractive.Count : 0;
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
                if (tick - packet.CreatedTick >= _settings.MaxAgeTicks)
                {
                    _expired++;
                    Retire(packet);
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
                var result = _network.TryTransmitHop(from, to, packet.Bytes, tick);
                switch (result)
                {
                    case RadioHopOutcome.TransmittedRadio:
                    case RadioHopOutcome.TransmittedCable:
                        if (packet.NoRouteStreak > 0) _recoveredHops++;
                        packet.NoRouteStreak = 0;
                        packet.NextRetryTick = 0;
                        packet.PendingRerouteId = 0; // Cancel logical ticket on healing.
                        if (packet.SuccessfulHops == 0)
                            _firstHopAges[(int)(_firstHopSampleCount++ % SampleWindow)] =
                                Math.Max(0, tick - packet.CreatedTick);
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
                            _dropped++;
                            _linkFailureDrops++;
                            Retire(packet);
                            break;
                        }
                        if (_rerouteQueue.Count + _inflight.Count >=
                            _settings.MaxPendingReroutes)
                        {
                            _rerouteQueueDeferrals++;
                            WaitForRerouteCapacity(packet, tick);
                            break;
                        }
                        var ticket = new PacketRerouteRequest(++_nextRequestId,
                            packet.Id, from, packet.Destination, tick);
                        packet.PendingRerouteId = ticket.Id;
                        _rerouteQueue.Enqueue(ticket);
                        AwaitReroute(packet);
                        break;

                    case RadioHopOutcome.PacketTooLarge:
                    default:
                        _dropped++;
                        _oversizeDrops++;
                        Retire(packet);
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
            while (_rerouteQueue.TryDequeue(out ticket))
            {
                if (!_active.TryGetValue(ticket.PacketId, out var packet) ||
                    packet.PendingRerouteId != ticket.Id) continue;
                _inflight.Add(ticket.Id, packet);
                packet.Reroutes++;
                packet.LastRouteAttemptTick = _lastTick;
                packet.SeenLocalRevision = LocalRevision(packet);
                _reroutes++;
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
        _delivered++;
        _bytesDelivered += packet.Bytes;
        var age = Math.Max(0, tick - packet.CreatedTick);
        if (packet.TrafficClass == PacketTrafficClass.Interactive)
        {
            _interactiveDelivered++;
            _interactiveAges[(int)((_interactiveDelivered - 1) % SampleWindow)] = age;
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
