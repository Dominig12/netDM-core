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
    bool AdaptivePacketVisits = false)
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
    int ReservedAdmissions, long AdmissionReservations);

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

    internal PacketAdmissionLease(RadioPacketTransport owner) => _owner = owner;

    internal bool Consume(RadioPacketTransport owner) =>
        ReferenceEquals(Interlocked.CompareExchange(ref _owner, null, owner), owner);

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseReservedAdmission();
}

public sealed class RadioPacketTransport
{
    private const int SampleWindow = 1024;
    private const int MaxRememberedChangedNodes = 65_536;

    private sealed class PendingPacket(RouteResult route, int size, long tick, long id)
    {
        public long Id = id;
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
    }

    private readonly SharedMediumNetwork _network;
    private readonly PacketTransportOptions _settings;
    private readonly object _gate = new();
    private readonly Queue<PendingPacket> _queue = new();
    private readonly Dictionary<long, PendingPacket> _active = new();
    private readonly Queue<PacketRerouteRequest> _rerouteQueue = new();
    private readonly Dictionary<long, PendingPacket> _inflight = new();
    // Local node-change revisions are bounded by the size of the network.
    // They let an affected waiting packet retry early without waking every
    // packet for each unrelated topology update elsewhere in the graph.
    private readonly Dictionary<NodeId, long> _localRevisions = new();
    private long _topologyChangeRevision;
    private readonly long[] _completedAges = new long[SampleWindow];
    private readonly long[] _firstHopAges = new long[SampleWindow];
    private readonly long[] _rerouteWaitAges = new long[SampleWindow];
    private readonly long[] _forwardUs = new long[SampleWindow];
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
            return new PacketTransportCounters(
                _enqueued, _rejected, _delivered, _dropped, _expired, _reroutes,
                _busy, _radioHops, _cableHops, _bytesDelivered, _queue.Count,
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
                _reservedAdmissions, _admissionReservations);
        }
    }

    /// <summary>
    /// Reserve a *real* queue slot before scheduling an expensive route.
    /// Includes all pending reservations when checking capacity. Without the
    /// lease, simply checking queue depth races between concurrent producers.
    /// </summary>
    public PacketAdmissionLease? TryReserveAdmission()
    {
        lock (_gate)
        {
            if (_queue.Count + _reservedAdmissions >= _settings.Capacity)
            {
                _preRouteRejects++;
                return null;
            }
            _reservedAdmissions++;
            _admissionReservations++;
            return new PacketAdmissionLease(this);
        }
    }

    internal void ReleaseReservedAdmission()
    {
        lock (_gate)
        {
            if (_reservedAdmissions <= 0)
                throw new InvalidOperationException("Packet admission reservation underflow.");
            _reservedAdmissions--;
        }
    }

    public bool Enqueue(RouteResult route, int bytes, long tick)
    {
        ValidateEnqueue(route, bytes, tick);
        lock (_gate) return EnqueueCore(route, bytes, tick);
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
            return EnqueueCore(route, bytes, tick);
        }
    }

    private static void ValidateEnqueue(RouteResult route, int bytes, long tick)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (bytes is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
    }

    private bool EnqueueCore(RouteResult route, int bytes, long tick)
    {
        if (!route.Found || route.Path is null || route.Path.Count == 0 ||
            (_lastTick >= 0 && tick < _lastTick))
        {
            _rejected++;
            _invalidRejects++;
            return false;
        }
        if (_queue.Count + _reservedAdmissions >= _settings.Capacity)
        {
            _rejected++;
            _fullRejects++;
            return false;
        }
        var detached = route with { Path = route.Path.ToArray() };
        var packet = new PendingPacket(detached, bytes, tick, ++_nextPacketId);
        _queue.Enqueue(packet);
        _active.Add(packet.Id, packet);
        _enqueued++;
        _peak = Math.Max(_peak, _queue.Count);
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
            foreach (var update in updates)
            {
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
            if (tick <= _lastTick) return 0;
            if (_lastTick >= 0 && tick - _lastTick > 1)
                _skippedTicks += tick - _lastTick - 1;
            _lastTick = tick;
            int visitLimit = _settings.AdaptivePacketVisits
                ? _currentVisitLimit : _settings.MaxAdvancesPerTick;
            int count = Math.Min(_queue.Count, visitLimit);
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
                var packet = _queue.Dequeue();
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
                        _queue.Enqueue(packet);
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
                        else _queue.Enqueue(packet);
                        break;

                    case RadioHopOutcome.ChannelBusy:
                    case RadioHopOutcome.OldTick:
                        _busy++;
                        _queue.Enqueue(packet);
                        break;

                    case RadioHopOutcome.LinkUnavailable:
                        if (packet.PendingRerouteId != 0)
                        {
                            _queue.Enqueue(packet);
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
                            packet.NextRetryTick = tick + _settings.RerouteQueueRetryDelayTicks;
                            _queue.Enqueue(packet);
                            break;
                        }
                        var ticket = new PacketRerouteRequest(++_nextRequestId,
                            packet.Id, from, packet.Destination, tick);
                        packet.PendingRerouteId = ticket.Id;
                        _rerouteQueue.Enqueue(ticket);
                        _queue.Enqueue(packet);
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
            if (visited == visitLimit && _queue.Count != 0)
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
                else if (visited == visitLimit && _queue.Count > 0 &&
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
                    return true;
                }
                _noRouteDeferrals++;
                packet.NoRouteStreak++;
                var delay = RetryDelay(packet.NoRouteStreak);
                _noRouteBackoffs++;
                _noRouteBackoffTicks += delay;
                packet.NextRetryTick = _lastTick + delay;
                packet.SeenLocalRevision = LocalRevision(packet);
                return true;
            }
            // Detached from the router's reusable collections, if any.
            packet.Path = route.Path.ToArray();
            packet.Index = 0;
            packet.NoRouteStreak = 0;
            packet.NextRetryTick = _lastTick + 1;
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
            _rerouteCancelled++;
            if (schedulerRejected) _rerouteSchedulerRejections++;
            RecordRerouteWait(ticket);
            if (!packet.Active || packet.PendingRerouteId != ticket.Id)
                return false;
            packet.PendingRerouteId = 0;
            packet.NextRetryTick = _lastTick + 1;
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
        packet.Active = false;
        _active.Remove(packet.Id);
        // Tickets already in progress are harmless; result application checks
        // Active. Pending tickets are skipped when the worker next drains them.
    }

    private void Delivered(PendingPacket packet, long tick)
    {
        _delivered++;
        _bytesDelivered += packet.Bytes;
        var age = Math.Max(0, tick - packet.CreatedTick);
        _completedAges[(int)((_delivered - 1) % SampleWindow)] = age;
        _totalAge += age;
        _totalDeliveredHops += packet.SuccessfulHops;
        Retire(packet);
    }
}
