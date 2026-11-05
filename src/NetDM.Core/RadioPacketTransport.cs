namespace NetDM.Core;

/// <summary>One hop is debited in one tick; the rest of the route is not reserved.</summary>
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
    int NoRouteRetryDelayTicks = 3)
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
    }
}

public sealed record PacketTransportCounters(
    long Enqueued, long Rejected, long Delivered, long Dropped,
    long Expired, long Reroutes, long BusyDeferrals, long WirelessHops,
    long CableHops, long DeliveredBytes, int Queued, int PeakQueued,
    long LastTick,
    long NoRouteDeferrals, long LinkFailureDrops, long OversizeDrops,
    long QueueFullRejections, long InvalidRejections,
    double DeliveryAgeP50Ticks, double DeliveryAgeP95Ticks,
    double MeanDeliveredHops, long TotalDeliveryAgeTicks);

/// <summary>
/// Bounded round-robin store-and-forward queue. Each live packet gets at most
/// one forwarding attempt per tick; with a full queue of N packets and a
/// processing limit of K packets per tick, every packet is considered within
/// ceil(N/K) ticks. Queued-but-busy packets remain alive until TTL.
/// </summary>
public sealed class RadioPacketTransport
{
    private const int LatencyWindow = 1024;

    private sealed class PendingPacket(RouteResult route, int size, long tick)
    {
        public IReadOnlyList<NodeId> Path = route.Path;
        public readonly NodeId Destination = route.Path[^1];
        public int Index;
        public readonly int Bytes = size;
        public readonly long CreatedTick = tick;
        public int Reroutes;
        public long NextRetryTick;
        public int SuccessfulHops;
    }

    private readonly SharedMediumNetwork _network;
    private readonly PacketTransportOptions _settings;
    private readonly object _gate = new();
    private readonly Queue<PendingPacket> _queue = new();
    private readonly long[] _completedAges = new long[LatencyWindow];
    private long _enqueued, _rejected, _delivered, _dropped, _expired;
    private long _reroutes, _busy, _radioHops, _cableHops, _bytesDelivered;
    private long _noRouteDeferrals, _linkFailureDrops, _oversizeDrops;
    private long _fullRejects, _invalidRejects, _totalAge, _totalDeliveredHops;
    private long _lastTick = -1;
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
            int length = (int)Math.Min(_delivered, LatencyWindow);
            var ages = new long[length];
            Array.Copy(_completedAges, ages, length);
            Array.Sort(ages);
            static double Percentile(long[] sorted, double factor) =>
                sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling((sorted.Length - 1) * factor)];
            return new PacketTransportCounters(_enqueued, _rejected,
                _delivered, _dropped, _expired, _reroutes, _busy, _radioHops,
                _cableHops, _bytesDelivered, _queue.Count, _peak, _lastTick,
                _noRouteDeferrals, _linkFailureDrops, _oversizeDrops,
                _fullRejects, _invalidRejects,
                Percentile(ages, .5), Percentile(ages, .95),
                _delivered == 0 ? 0 : (double)_totalDeliveredHops / _delivered,
                _totalAge);
        }
    }

    /// <summary>
    /// Nonblocking admission. Never retains a caller-mutable path array.
    /// Rejects a stale simulation tick or an invalid/disconnected source path.
    /// </summary>
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
            var path = route with { Path = route.Path.ToArray() };
            _queue.Enqueue(new PendingPacket(path, bytes, tick));
            _enqueued++;
            _peak = Math.Max(_peak, _queue.Count);
            return true;
        }
    }

    /// <summary>
    /// Does no more than MaxAdvancesPerTick packet visits. The initial queue
    /// size is fixed for this tick, so a requeued item cannot advance twice.
    /// Cancellation stops between packets and preserves every accepted item.
    /// </summary>
    public int Advance(long tick, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (tick <= _lastTick) return 0;
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
                    continue;
                }
                if (packet.Index >= packet.Path.Count - 1)
                {
                    Delivered(packet, tick);
                    continue;
                }
                if (tick < packet.NextRetryTick)
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
                        else
                            _queue.Enqueue(packet);
                        break;
                    case RadioHopOutcome.ChannelBusy:
                    case RadioHopOutcome.OldTick:
                        _busy++;
                        _queue.Enqueue(packet);
                        break;
                    case RadioHopOutcome.LinkUnavailable:
                        // Routes are rechecked from the current node, not
                        // the original source. Failed attempts can wait for
                        // a temporary radio outage to recover.
                        if (packet.Reroutes >= _settings.MaxReroutes)
                        {
                            _dropped++;
                            _linkFailureDrops++;
                            break;
                        }
                        RouteResult newRoute;
                        try
                        {
                            newRoute = _network.Route(from, packet.Destination,
                                cancellationToken: cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            _queue.Enqueue(packet);
                            break;
                        }
                        packet.Reroutes++;
                        _reroutes++;
                        if (!newRoute.Found || newRoute.Path.Count == 0 ||
                            newRoute.Path[0] != from ||
                            newRoute.Path[^1] != packet.Destination)
                        {
                            _noRouteDeferrals++;
                            packet.NextRetryTick = tick + _settings.NoRouteRetryDelayTicks;
                            _queue.Enqueue(packet);
                            break;
                        }
                        packet.Path = newRoute.Path;
                        packet.Index = 0;
                        packet.NextRetryTick = tick + 1;
                        _queue.Enqueue(packet);
                        break;
                    case RadioHopOutcome.PacketTooLarge:
                    default:
                        _dropped++;
                        _oversizeDrops++;
                        break;
                }
            }
            return forwarded;
        }
    }

    private void Delivered(PendingPacket packet, long tick)
    {
        _delivered++;
        _bytesDelivered += packet.Bytes;
        long age = Math.Max(0, tick - packet.CreatedTick);
        _completedAges[(int)((_delivered - 1) % LatencyWindow)] = age;
        _totalAge += age;
        _totalDeliveredHops += packet.SuccessfulHops;
    }
}
