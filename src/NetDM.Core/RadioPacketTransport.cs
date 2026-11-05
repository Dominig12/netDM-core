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
    int MaxAgeTicks = 180,
    int MaxReroutes = 4,
    int MaxAdvancesPerTick = 512)
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
    }
}

public sealed record PacketTransportCounters(
    long Enqueued, long Rejected, long Delivered, long Dropped,
    long Expired, long Reroutes, long BusyDeferrals, long WirelessHops,
    long CableHops, long DeliveredBytes, int Queued, int PeakQueued,
    long LastTick);

/// <summary>
/// Deterministic store-and-forward packet simulator. A packet may make at most
/// ONE hop in one simulation tick. Memory, work, retries and life-time are
/// bounded. A broken next hop triggers bounded re-routing from the current
/// position; capacity failures defer packets to a later tick.
/// This is a prototype logical transport, not a TCP or RF stack.
/// </summary>
public sealed class RadioPacketTransport
{
    private sealed class PendingPacket(RouteResult path, int size, long tick)
    {
        public IReadOnlyList<NodeId> Path = path.Path;
        public NodeId Destination = path.Path[^1];
        public int Index;
        public int Bytes = size;
        public long CreatedTick = tick;
        public int Reroutes;
    }

    private readonly SharedMediumNetwork _network;
    private readonly PacketTransportOptions _settings;
    private readonly object _gate = new();
    private readonly Queue<PendingPacket> _queue = new();
    private long _enqueued, _rejected, _delivered, _dropped, _expired;
    private long _reroutes, _busy, _radioHops, _cableHops, _bytesDelivered;
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
            return new PacketTransportCounters(_enqueued, _rejected,
                _delivered, _dropped, _expired, _reroutes, _busy, _radioHops,
                _cableHops, _bytesDelivered, _queue.Count, _peak, _lastTick);
    }

    /// <summary>
    /// Admission never waits. Route is a snapshot-based path and will be checked
    /// hop-by-hop against actual current links during forwarding.
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
                _queue.Count >= _settings.Capacity ||
                (_lastTick >= 0 && tick < _lastTick))
            {
                _rejected++;
                return false;
            }
            // Packet path ownership must be immutable: callers can mutate
            // their supplied arrays after Enqueue returns.
            var detached = route with { Path = route.Path.ToArray() };
            _queue.Enqueue(new PendingPacket(detached, bytes, tick));
            _enqueued++;
            _peak = Math.Max(_peak, _queue.Count);
            return true;
        }
    }

    /// <summary>
    /// Advances a bounded number of packets at one discrete simulation tick.
    /// Repeating the same tick is a no-op, preventing accidental multi-hop
    /// forwarding by repeated calls inside a server frame.
    /// </summary>
    public int Advance(long tick, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (tick <= _lastTick) return 0;
            _lastTick = tick;
            var count = Math.Min(_queue.Count, _settings.MaxAdvancesPerTick);
            var forwarded = 0;
            for (var i = 0; i < count; i++)
            {
                // Once a packet has been accepted, cancellation must never
                // remove it silently. Stop between items, not in the middle
                // of their accounting.
                if (cancellationToken.IsCancellationRequested) break;
                var packet = _queue.Dequeue();
                if (tick - packet.CreatedTick >= _settings.MaxAgeTicks)
                {
                    _expired++;
                    continue;
                }

                if (packet.Index >= packet.Path.Count - 1)
                {
                    Delivered(packet);
                    continue;
                }

                var current = packet.Path[packet.Index];
                var next = packet.Path[packet.Index + 1];
                var outcome = _network.TryTransmitHop(current, next, packet.Bytes, tick);
                switch (outcome)
                {
                    case RadioHopOutcome.TransmittedCable:
                    case RadioHopOutcome.TransmittedRadio:
                        packet.Index++;
                        forwarded++;
                        if (outcome == RadioHopOutcome.TransmittedRadio) _radioHops++;
                        else _cableHops++;
                        if (packet.Index == packet.Path.Count - 1)
                            Delivered(packet);
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
                            break;
                        }
                        packet.Reroutes++;
                        _reroutes++;
                        // Recompute from the *actual current* node; never
                        // teleport to the original source after a broken hop.
                        RouteResult candidate;
                        try
                        {
                            candidate = _network.Route(current, packet.Destination,
                                cancellationToken: cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            // A cancelled tick must not silently lose the
                            // packet popped from the bounded queue.
                            _queue.Enqueue(packet);
                            break;
                        }
                        if (!candidate.Found || candidate.Path.Count == 0 ||
                            candidate.Path[0] != current ||
                            candidate.Path[^1] != packet.Destination)
                        {
                            _dropped++;
                            break;
                        }
                        packet.Path = candidate.Path;
                        packet.Index = 0;
                        _queue.Enqueue(packet);
                        break;

                    case RadioHopOutcome.PacketTooLarge:
                    default:
                        _dropped++;
                        break;
                }
            }
            return forwarded;
        }
    }

    private void Delivered(PendingPacket packet)
    {
        _delivered++;
        _bytesDelivered += packet.Bytes;
    }
}
