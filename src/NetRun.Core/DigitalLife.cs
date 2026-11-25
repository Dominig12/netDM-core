namespace NetRun.Core;

/// <summary>
/// The condition of a digital life (§13.5). The path down is Active → Degraded → Critical → Stasis or CoreLost,
/// and every step back is possible while a copy of the state still exists.
/// </summary>
public enum LifeState : byte
{
    Active, Degraded, Critical, Stasis, CoreLost, Terminated
}

public enum LifeStatus : byte
{
    Ok, UnknownIdentity, Duplicate, HostUnavailable, NoHost, InsufficientResources,
    AlreadyMigrating, NoMigration, StaleGeneration, AlreadyTerminated, InvalidRequest
}

/// <summary>
/// One digital identity and its current core. <see cref="ControlGeneration"/> is the fencing token: a message
/// from an older generation describes a controller that no longer exists and is refused (LIFE-001).
/// </summary>
public sealed record DigitalCoreSnapshot(
    IdentityId Identity,
    PrincipalId Controller,
    HostId Host,
    HostGeneration HostGeneration,
    ulong ControlGeneration,
    int Integrity,
    LifeState State,
    bool Migrating,
    HostId MigrationDestination,
    long CreatedAtMilliseconds,
    long MigrationStartedAtMilliseconds)
{
    public bool Alive => State is not (LifeState.CoreLost or LifeState.Terminated);
}

public sealed record LifeResult(LifeStatus Status, DigitalCoreSnapshot? Core, string? Reason = null)
{
    public bool Succeeded => Status == LifeStatus.Ok;
}

public sealed record DigitalLifeOptions(
    int MaxIdentities = 4_096,
    int MigrationReservationCu = 1,
    long MigrationLifetimeMilliseconds = 30_000,
    int FullIntegrity = 100,
    int DegradedAt = 60,
    int CriticalAt = 30);

/// <summary>
/// The authoritative model of a digital life's core (LIFE-001, §13.4). One identity has exactly one active
/// controlling core: a migration suspends the source, moves the state and switches the generation, so no two
/// hosts can ever answer for the same identity. Moving is not copying — a fork is a different operation and is
/// not implemented by this registry.
/// </summary>
public sealed class DigitalLifeRegistry(
    ISimulationClock clock,
    HostRegistry hosts,
    ResourceLedger resources,
    DigitalLifeOptions? options = null)
{
    private sealed class Record(IdentityId identity, PrincipalId controller, HostId host,
        HostGeneration hostGeneration, long createdAt)
    {
        public readonly IdentityId Identity = identity;
        public readonly PrincipalId Controller = controller;
        public HostId Host = host;
        public HostGeneration HostGeneration = hostGeneration;
        public ulong ControlGeneration = 1;
        public int Integrity = 100;
        public LifeState State = LifeState.Active;
        public bool Migrating;
        public HostId Destination;
        public ReservationId Reservation;
        public long MigrationStarted;
        public long CreatedAt = createdAt;

        public DigitalCoreSnapshot Snapshot() => new(Identity, Controller, Host, HostGeneration, ControlGeneration,
            Integrity, State, Migrating, Destination, CreatedAt, MigrationStarted);
    }

    private readonly ISimulationClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly HostRegistry _hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
    private readonly ResourceLedger _resources = resources ?? throw new ArgumentNullException(nameof(resources));
    private readonly DigitalLifeOptions _options = options ?? new DigitalLifeOptions();
    private readonly Dictionary<ulong, Record> _cores = [];

    public int Count => _cores.Count;

    /// <summary>Brings a digital life into existence on a host that can hold a core.</summary>
    public LifeResult Create(IdentityId identity, PrincipalId controller, HostId host)
    {
        if (identity.IsNone || controller.IsNone || host.IsNone)
            return new LifeResult(LifeStatus.InvalidRequest, null, "arguments");
        if (_cores.ContainsKey(identity.Value))
            return new LifeResult(LifeStatus.Duplicate, _cores[identity.Value].Snapshot(), "identity");
        if (_cores.Count >= _options.MaxIdentities)
            return new LifeResult(LifeStatus.InvalidRequest, null, "limit");
        if (_hosts.Get(host) is not { Available: true } core)
            return new LifeResult(LifeStatus.HostUnavailable, null, "host");
        // A core needs somewhere to compute and somewhere to live: a relay or a bare terminal cannot hold one.
        if (core.Compute is null || core.Memory is null)
            return new LifeResult(LifeStatus.InsufficientResources, null, "components");
        var record = new Record(identity, controller, host, core.Generation, _clock.NowMilliseconds);
        _cores[identity.Value] = record;
        return new LifeResult(LifeStatus.Ok, record.Snapshot());
    }

    public DigitalCoreSnapshot? Get(IdentityId identity) =>
        _cores.TryGetValue(identity.Value, out var record) ? record.Snapshot() : null;

    /// <summary>
    /// Starts moving the core (§13.4). The destination is reserved first and the source is suspended: while the
    /// move is pending the source still answers, but it cannot start new work.
    /// </summary>
    public LifeResult BeginMigration(IdentityId identity, HostId destination)
    {
        if (!_cores.TryGetValue(identity.Value, out var record))
            return new LifeResult(LifeStatus.UnknownIdentity, null);
        if (record.State is LifeState.Terminated or LifeState.CoreLost)
            return new LifeResult(LifeStatus.AlreadyTerminated, record.Snapshot(), "dead");
        if (record.Migrating)
            return new LifeResult(LifeStatus.AlreadyMigrating, record.Snapshot(), "pending");
        if (destination == record.Host)
            return new LifeResult(LifeStatus.InvalidRequest, record.Snapshot(), "same_host");
        if (_hosts.Get(destination) is not { Available: true } target)
            return new LifeResult(LifeStatus.HostUnavailable, record.Snapshot(), "destination");
        // The destination must be able to hold a core at all: a terminal without compute can be travelled
        // through but not lived on (§8.1).
        if (target.Compute is null || target.Memory is null)
            return new LifeResult(LifeStatus.InsufficientResources, record.Snapshot(), "components");

        var reservation = _resources.ReserveCompute(destination, record.Controller, _options.MigrationReservationCu,
            _options.MigrationLifetimeMilliseconds);
        if (reservation.Status != ReservationStatus.Ok)
            return new LifeResult(LifeStatus.InsufficientResources, record.Snapshot(), reservation.Status.ToString());

        record.Migrating = true;
        record.Destination = destination;
        record.Reservation = reservation.Reservation?.Id ?? ReservationId.None;
        record.MigrationStarted = _clock.NowMilliseconds;
        return new LifeResult(LifeStatus.Ok, record.Snapshot());
    }

    /// <summary>
    /// Switches the generation and makes the destination the only active core. The source stops answering at
    /// once: its generation is retired, so any message still in flight from it is refused.
    /// </summary>
    public LifeResult CommitMigration(IdentityId identity, ulong expectedGeneration)
    {
        if (!_cores.TryGetValue(identity.Value, out var record))
            return new LifeResult(LifeStatus.UnknownIdentity, null);
        if (!record.Migrating)
            return new LifeResult(LifeStatus.NoMigration, record.Snapshot(), "none");
        if (expectedGeneration != record.ControlGeneration)
            return new LifeResult(LifeStatus.StaleGeneration, record.Snapshot(), "generation");
        if (_hosts.Get(record.Destination) is not { Available: true } destination)
        {
            // The destination vanished: the source keeps the identity instead of the life being lost.
            CancelMigrationCore(record, "destination_lost");
            return new LifeResult(LifeStatus.HostUnavailable, record.Snapshot(), "destination_lost");
        }

        if (!record.Reservation.IsNone)
            _resources.Consume(record.Reservation);
        record.Reservation = ReservationId.None;
        record.Host = record.Destination;
        record.HostGeneration = destination.Generation;
        record.ControlGeneration++;
        record.Migrating = false;
        record.Destination = HostId.None;
        // A move keeps the identity alive even when it was degraded on the way.
        if (record.State is LifeState.CoreLost)
            record.State = LifeState.Active;
        return new LifeResult(LifeStatus.Ok, record.Snapshot());
    }

    /// <summary>Abandons a pending move before commit. The source keeps the identity and the reserve returns.</summary>
    public LifeResult CancelMigration(IdentityId identity, string reason)
    {
        if (!_cores.TryGetValue(identity.Value, out var record))
            return new LifeResult(LifeStatus.UnknownIdentity, null);
        if (!record.Migrating)
            return new LifeResult(LifeStatus.NoMigration, record.Snapshot(), "none");
        CancelMigrationCore(record, reason);
        return new LifeResult(LifeStatus.Ok, record.Snapshot());
    }

    private void CancelMigrationCore(Record record, string reason)
    {
        if (!record.Reservation.IsNone)
            _resources.Release(record.Reservation, record.Controller);
        record.Reservation = ReservationId.None;
        record.Migrating = false;
        record.Destination = HostId.None;
        record.MigrationStarted = 0;
        _ = reason;
    }

    /// <summary>
    /// Damage and repair move the state machine. Nothing here kills a character by accident: only a core with no
    /// integrity left loses its state, and even then the record stays for a possible restore.
    /// </summary>
    public LifeResult ApplyIntegrity(IdentityId identity, int delta)
    {
        if (!_cores.TryGetValue(identity.Value, out var record))
            return new LifeResult(LifeStatus.UnknownIdentity, null);
        if (record.State is LifeState.Terminated)
            return new LifeResult(LifeStatus.AlreadyTerminated, record.Snapshot(), "terminated");
        record.Integrity = Math.Clamp(record.Integrity + delta, 0, _options.FullIntegrity);
        record.State = record.Integrity >= _options.DegradedAt ? LifeState.Active
            : record.Integrity >= _options.CriticalAt ? LifeState.Degraded
            : record.Integrity > 0 ? LifeState.Critical
            : LifeState.Stasis;
        return new LifeResult(LifeStatus.Ok, record.Snapshot());
    }

    /// <summary>
    /// The host that held the core is gone (§13.5). A pending migration to a live destination completes,
    /// because the state was already on its way and the source is no longer there to keep it; otherwise the
    /// core is lost but not deleted, so a restore remains possible.
    /// </summary>
    public int OnHostDestroyed(HostId host)
    {
        var affected = 0;
        foreach (var record in _cores.Values.ToArray())
        {
            if (record.Migrating && record.Destination == host)
            {
                // The destination died; the source is intact, so the life continues where it was.
                CancelMigrationCore(record, "destination_destroyed");
                affected++;
                continue;
            }
            if (record.Host != host)
                continue;
            affected++;
            if (record.Migrating && record.Destination != HostId.None &&
                _hosts.Get(record.Destination) is { Available: true } rescue)
            {
                // The source is gone but the move was already under way: the destination takes over.
                if (!record.Reservation.IsNone)
                    _resources.Consume(record.Reservation);
                record.Reservation = ReservationId.None;
                record.Host = record.Destination;
                record.HostGeneration = rescue.Generation;
                record.ControlGeneration++;
                record.Migrating = false;
                record.Destination = HostId.None;
                if (record.State is LifeState.CoreLost)
                    record.State = LifeState.Active;
                continue;
            }
            if (!record.Reservation.IsNone)
                _resources.Release(record.Reservation, record.Controller);
            record.Reservation = ReservationId.None;
            record.Migrating = false;
            record.Destination = HostId.None;
            record.State = LifeState.CoreLost;
            record.Integrity = 0;
        }
        return affected;
    }

    /// <summary>
    /// Restores a lost core onto a healthy host (§13.5). Only the identity's own state returns: not the world,
    /// not resources that were spent, not revoked rights.
    /// </summary>
    public LifeResult Restore(IdentityId identity, HostId host, int integrity)
    {
        if (!_cores.TryGetValue(identity.Value, out var record))
            return new LifeResult(LifeStatus.UnknownIdentity, null);
        if (record.State is not (LifeState.CoreLost or LifeState.Stasis))
            return new LifeResult(LifeStatus.InvalidRequest, record.Snapshot(), "alive");
        if (_hosts.Get(host) is not { Available: true } target)
            return new LifeResult(LifeStatus.HostUnavailable, record.Snapshot(), "host");
        if (target.Compute is null || target.Memory is null)
            return new LifeResult(LifeStatus.InsufficientResources, record.Snapshot(), "components");
        record.Host = host;
        record.HostGeneration = target.Generation;
        record.ControlGeneration++;
        record.Integrity = Math.Clamp(integrity, 1, _options.FullIntegrity);
        record.State = record.Integrity >= _options.DegradedAt ? LifeState.Active : LifeState.Degraded;
        record.Migrating = false;
        record.Destination = HostId.None;
        return new LifeResult(LifeStatus.Ok, record.Snapshot());
    }

    /// <summary>
    /// Whether this message belongs to the controller that currently owns the identity. A migration retires the
    /// old generation, so a late message from the source can never act for the life again.
    /// </summary>
    public bool Owns(IdentityId identity, ulong controlGeneration) =>
        _cores.TryGetValue(identity.Value, out var record) && record.ControlGeneration == controlGeneration &&
        !record.Migrating;

    /// <summary>Expires moves whose confirmation never came. The source keeps the life and the reserve returns.</summary>
    public int Tick()
    {
        var now = _clock.NowMilliseconds;
        var expired = 0;
        foreach (var record in _cores.Values.ToArray())
        {
            if (!record.Migrating || now - record.MigrationStarted < _options.MigrationLifetimeMilliseconds)
                continue;
            CancelMigrationCore(record, "migration_timeout");
            expired++;
        }
        return expired;
    }

    /// <summary>Bounded listing for the client, oldest identity first.</summary>
    public DigitalCoreSnapshot[] All(int limit)
    {
        var count = Math.Min(Math.Max(limit, 0), _cores.Count);
        var result = new DigitalCoreSnapshot[count];
        var index = 0;
        foreach (var record in _cores.Values.OrderBy(record => record.Identity.Value))
        {
            if (index >= count)
                break;
            result[index++] = record.Snapshot();
        }
        return result;
    }
}
