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
    int MaxConcurrentReroutes = 2)
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
    double ForwardingP95Milliseconds);

/// <summary>
/// Bounded round-robin store-and-forward. Advance never calculates a route:
/// invalid hops only emit bounded reroute requests. The host schedules these
/// requests independently at background priority, and applies results after
/// validating the packet identity and its actual current position.
/// </summary>
public sealed class RadioPacketTransport
{
    private const int SampleWindow = 1024;

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
    }

    private readonly SharedMediumNetwork _network;
    private readonly PacketTransportOptions _settings;
    private readonly object _gate = new();
    private readonly Queue<PendingPacket> _queue = new();
    private readonly Dictionary<long, PendingPacket> _active = new();
    private readonly Queue<PacketRerouteRequest> _rerouteQueue = new();
    private readonly Dictionary<long, PendingPacket> _inflight = new();
    private readonly long[] _completedAges = new long[SampleWindow];
    private readonly long[] _rerouteWaitAges = new long[SampleWindow];
    private readonly long[] _forwardUs = new long[SampleWindow];
    private long _enqueued, _rejected, _delivered, _dropped, _expired;
    private long _reroutes, _busy, _radioHops, _cableHops, _bytesDelivered;
    private long _noRouteDeferrals, _linkFailureDrops, _oversizeDrops;
    private long _fullRejects, _invalidRejects, _totalAge, _totalDeliveredHops;
    private long _rerouteCompleted, _rerouteCancelled, _rerouteQueueDeferrals;
    private long _rerouteSchedulerRejections, _skippedTicks, _tickAdmissionFailures;
    private long _rerouteSampleCount, _forwardSampleCount;
    private long _lastTick = -1, _nextPacketId, _nextRequestId;
    private int _peak;

    public RadioPacketTransport(SharedMediumNetwork network, PacketTransportOptions? settings = null)
    {
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _settings = settings ?? new PacketTransportOptions();
        _settings.Validate();
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
                _skippedTicks, _tickAdmissionFailures, Percentile(forwardUs, .95) / 1000.0);
        }
    }

    public bool Enqueue(RouteResult route, int bytes, long tick)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (bytes is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        lock (_gate)
        {
            if (!route.Found || route.Path is null || route.Path.Count == 0 ||
                (_lastTick >= 0 && tick < _lastTick))
            {
                _rejected++;
                _invalidRejects++;
                return false;
            }
            if (_queue.Count >= _settings.Capacity)
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
        lock (_gate)
        {
            if (tick <= _lastTick) return 0;
            if (_lastTick >= 0 && tick - _lastTick > 1)
                _skippedTicks += tick - _lastTick - 1;
            _lastTick = tick;
            int count = Math.Min(_queue.Count, _settings.MaxAdvancesPerTick);
            int forwarded = 0;
            for (var i = 0; i < count; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;
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
                if (packet.PendingRerouteId != 0 || tick < packet.NextRetryTick)
                {
                    _queue.Enqueue(packet);
                    continue;
                }

                var from = packet.Path[packet.Index];
                var to = packet.Path[packet.Index + 1];
                var result = _network.TryTransmitHop(from, to, packet.Bytes, tick);
                switch (result)
                {
                    case RadioHopOutcome.TransmittedRadio:
                    case RadioHopOutcome.TransmittedCable:
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
                            packet.NextRetryTick = tick + 1;
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
                _noRouteDeferrals++;
                packet.NextRetryTick = _lastTick + _settings.NoRouteRetryDelayTicks;
                return true;
            }
            // Detached from the router's reusable collections, if any.
            packet.Path = route.Path.ToArray();
            packet.Index = 0;
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
