namespace NetRun.Core;

/// <summary>Where a digital presence stands (§8.1). One presence exists at one host at a time, or in transit.</summary>
public enum PresenceState : byte { Absent, Idle, Traveling }

public sealed record PresenceSnapshot(
    IdentityId Identity,
    PrincipalId Controller,
    HostId Host,
    PresenceState State,
    HostId Destination,
    int Hops,
    long ArriveAtMilliseconds,
    string? Reason)
{
    public bool Moving => State == PresenceState.Traveling;
}

public enum PresenceStatus : byte
{
    Ok, UnknownIdentity, AlreadyThere, Busy, NoRoute, HostUnavailable, NotTraveling, LimitExceeded, InvalidRequest
}

public sealed record PresenceResult(PresenceStatus Status, PresenceSnapshot? Presence, string? Reason = null)
{
    public bool Succeeded => Status == PresenceStatus.Ok;
}

/// <summary>A route and the time it takes, as the transport sees it. The domain never computes a path itself.</summary>
public readonly record struct RoutePlan(int Hops, long TravelMilliseconds);

public interface IRoutePlanner
{
    /// <summary>The route between two hosts, or null when the transport cannot reach the destination.</summary>
    RoutePlan? Plan(HostId from, HostId to);
}

public sealed record PresenceOptions(
    int MaxPresences = 4_096,
    long PerHopMilliseconds = 1_000,
    long MaxTravelMilliseconds = 15_000);

/// <summary>
/// Presence and travel (§8.1, PLAY-001). A presence lives on a host, moves over a route the transport plans,
/// and arrives only if the route still holds. A broken route returns the presence to where it started instead
/// of silently teleporting it, and a presence in transit cannot act.
/// </summary>
public sealed class PresenceRegistry(
    ISimulationClock clock,
    HostRegistry hosts,
    DigitalLifeRegistry lives,
    PresenceOptions? options = null)
{
    private sealed class Record(IdentityId identity, PrincipalId controller, HostId host)
    {
        public readonly IdentityId Identity = identity;
        public readonly PrincipalId Controller = controller;
        public HostId Host = host;
        public PresenceState State = PresenceState.Idle;
        public HostId Destination = HostId.None;
        public HostId Source = HostId.None;
        public int Hops;
        public long ArriveAt;
        public string? Reason;

        public PresenceSnapshot Snapshot() => new(Identity, Controller, Host, State, Destination, Hops, ArriveAt, Reason);
    }

    private readonly ISimulationClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly HostRegistry _hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
    private readonly DigitalLifeRegistry _lives = lives ?? throw new ArgumentNullException(nameof(lives));
    private IRoutePlanner? _routes;
    private readonly PresenceOptions _options = options ?? new PresenceOptions();
    private readonly Dictionary<ulong, Record> _presences = [];

    public int Count => _presences.Count;

    /// <summary>
    /// Connects the transport that plans routes. Without it travel is refused rather than guessed, because a
    /// place reached by guessing is not a place the network delivered you to (§8.1).
    /// </summary>
    public void AttachPlanner(IRoutePlanner planner) => _routes = planner ?? throw new ArgumentNullException(nameof(planner));

    /// <summary>
    /// Brings a presence into being where its core lives. A lens without a core has nowhere to stand, so the
    /// host must actually hold it (LIFE-001).
    /// </summary>
    public PresenceResult Enter(IdentityId identity, HostId host)
    {
        if (identity.IsNone || host.IsNone)
            return new PresenceResult(PresenceStatus.InvalidRequest, null, "arguments");
        if (_presences.TryGetValue(identity.Value, out var existing))
            return new PresenceResult(PresenceStatus.Busy, existing.Snapshot(), "already_present");
        if (_presences.Count >= _options.MaxPresences)
            return new PresenceResult(PresenceStatus.LimitExceeded, null, "limit");
        var core = _lives.CoreOn(host);
        if (core is null || core.Identity != identity)
            return new PresenceResult(PresenceStatus.HostUnavailable, null, "no_core");
        if (_hosts.Get(host) is not { Available: true })
            return new PresenceResult(PresenceStatus.HostUnavailable, null, "host");
        var record = new Record(identity, core.Controller, host);
        _presences[identity.Value] = record;
        return new PresenceResult(PresenceStatus.Ok, record.Snapshot());
    }

    public PresenceSnapshot? Get(IdentityId identity) =>
        _presences.TryGetValue(identity.Value, out var record) ? record.Snapshot() : null;

    /// <summary>The presence and its host, for callers that must refuse an action from a moving presence.</summary>
    public HostId HostOf(IdentityId identity) =>
        _presences.TryGetValue(identity.Value, out var record) && record.State == PresenceState.Idle
            ? record.Host
            : HostId.None;

    /// <summary>
    /// Starts moving to another host over a route the transport plans now. The presence is in transit until the
    /// planned arrival, and the route is checked again when it gets there (§8.1).
    /// </summary>
    public PresenceResult Move(IdentityId identity, HostId destination)
    {
        if (!_presences.TryGetValue(identity.Value, out var record))
            return new PresenceResult(PresenceStatus.UnknownIdentity, null);
        if (record.State == PresenceState.Traveling)
            return new PresenceResult(PresenceStatus.Busy, record.Snapshot(), "moving");
        if (destination == record.Host)
            return new PresenceResult(PresenceStatus.AlreadyThere, record.Snapshot(), "same_host");
        if (_hosts.Get(destination) is not { Available: true } target)
            return new PresenceResult(PresenceStatus.HostUnavailable, record.Snapshot(), "destination");
        // A host must be able to hold a presence at all: its security component declares the limit, so a
        // relay with no place for an occupant refuses the move instead of pretending to host it (§8.1).
        if (target.Security is { MaxPresences: 0 })
            return new PresenceResult(PresenceStatus.HostUnavailable, record.Snapshot(), "no_presence");
        if (_routes is null)
            return new PresenceResult(PresenceStatus.NoRoute, record.Snapshot(), "no_transport");
        var plan = _routes.Plan(record.Host, destination);
        if (plan is not { } route || route.Hops <= 0)
            return new PresenceResult(PresenceStatus.NoRoute, record.Snapshot(), "no_route");
        record.State = PresenceState.Traveling;
        record.Source = record.Host;
        record.Destination = destination;
        record.Hops = route.Hops;
        var travel = Math.Clamp(route.TravelMilliseconds, _options.PerHopMilliseconds,
            _options.MaxTravelMilliseconds);
        record.ArriveAt = _clock.NowMilliseconds + travel;
        record.Reason = null;
        return new PresenceResult(PresenceStatus.Ok, record.Snapshot());
    }

    /// <summary>Abandons a move before arrival. The presence stays where it started.</summary>
    public PresenceResult Abort(IdentityId identity, string reason = "aborted")
    {
        if (!_presences.TryGetValue(identity.Value, out var record))
            return new PresenceResult(PresenceStatus.UnknownIdentity, null);
        if (record.State != PresenceState.Traveling)
            return new PresenceResult(PresenceStatus.NotTraveling, record.Snapshot(), "idle");
        record.State = PresenceState.Idle;
        record.Host = record.Source.IsNone ? record.Host : record.Source;
        record.Destination = HostId.None;
        record.Hops = 0;
        record.ArriveAt = 0;
        record.Reason = reason;
        return new PresenceResult(PresenceStatus.Ok, record.Snapshot());
    }

    public PresenceResult Leave(IdentityId identity)
    {
        return _presences.Remove(identity.Value)
            ? new PresenceResult(PresenceStatus.Ok, null)
            : new PresenceResult(PresenceStatus.UnknownIdentity, null);
    }

    /// <summary>
    /// Arrivals. A route that no longer exists puts the presence back where it came from instead of delivering it
    /// through a link that is gone; a destroyed destination does the same (§8.1).
    /// </summary>
    public int Tick()
    {
        var now = _clock.NowMilliseconds;
        var arrived = 0;
        foreach (var record in _presences.Values.ToArray())
        {
            if (record.State != PresenceState.Traveling || record.ArriveAt > now)
                continue;
            if (_hosts.Get(record.Destination) is not { Available: true })
            {
                Return(record, "destination_gone");
                continue;
            }
            if (_routes is null || _routes.Plan(record.Host, record.Destination) is not { } route || route.Hops <= 0)
            {
                Return(record, "route_lost");
                continue;
            }
            record.Host = record.Destination;
            record.State = PresenceState.Idle;
            record.Destination = HostId.None;
            record.Source = HostId.None;
            record.Hops = 0;
            record.ArriveAt = 0;
            record.Reason = null;
            arrived++;
        }
        return arrived;
    }

    private static void Return(Record record, string reason)
    {
        record.State = PresenceState.Idle;
        record.Host = record.Source.IsNone ? record.Host : record.Source;
        record.Destination = HostId.None;
        record.Source = HostId.None;
        record.Hops = 0;
        record.ArriveAt = 0;
        record.Reason = reason;
    }

    /// <summary>A presence whose host is gone is lost with it; the core, not the lens, decides whether it returns.</summary>
    public int OnHostDestroyed(HostId host)
    {
        var affected = 0;
        foreach (var record in _presences.Values.ToArray())
        {
            if (record.State == PresenceState.Traveling && record.Destination == host)
            {
                Return(record, "destination_gone");
                affected++;
                continue;
            }
            if (record.Host != host)
                continue;
            _presences.Remove(record.Identity.Value);
            affected++;
        }
        return affected;
    }

    public PresenceSnapshot[] All(int limit)
    {
        var count = Math.Min(Math.Max(limit, 0), _presences.Count);
        var result = new PresenceSnapshot[count];
        var index = 0;
        foreach (var record in _presences.Values.OrderBy(record => record.Identity.Value))
        {
            if (index >= count)
                break;
            result[index++] = record.Snapshot();
        }
        return result;
    }

    /// <summary>Presences one controller drives, bounded. Another runner's presence is not this observer's business.</summary>
    public PresenceSnapshot[] ControlledBy(PrincipalId controller, int limit)
    {
        if (controller.IsNone || limit <= 0)
            return [];
        var result = new List<PresenceSnapshot>();
        foreach (var record in _presences.Values.OrderBy(record => record.Identity.Value))
        {
            if (result.Count >= limit)
                break;
            if (record.Controller == controller)
                result.Add(record.Snapshot());
        }
        return result.ToArray();
    }
}
