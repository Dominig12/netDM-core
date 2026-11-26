namespace NetRun.Core;

public enum ResourceKind : byte
{
    Compute, Memory, Storage, Bandwidth
}

public enum ReservationState : byte
{
    /// <summary>Held: the provider cannot give the same capacity to anybody else.</summary>
    Active,
    /// <summary>Spent on a committed action. It never returns to the buffer.</summary>
    Consumed,
    /// <summary>Given back before it was spent; the capacity returns to the buffer.</summary>
    Released,
    /// <summary>The lease ran out without being confirmed. Counted as released.</summary>
    Expired,
    /// <summary>The host went away. Counted as released.</summary>
    Revoked
}

public enum ReservationStatus : byte
{
    Ok, Duplicate, UnknownHost, UnknownReservation, InsufficientCompute, InsufficientMemory,
    InsufficientStorage, InsufficientBandwidth, InvalidRequest, HostUnavailable, StalePrincipal
}

public readonly record struct ReservationId(ulong Value)
{
    public static ReservationId None => default;

    public bool IsNone => Value == 0;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// One held piece of capacity. It has an owner, a lifetime and an idempotent release, so a lost reply
/// never turns into a permanent leak and a repeated release never credits the buffer twice (RES-001).
/// </summary>
public sealed record ReservationSnapshot(
    ReservationId Id,
    ResourceKind Kind,
    HostId Provider,
    PrincipalId Owner,
    long Amount,
    long ExpiresAtMilliseconds,
    ReservationState State)
{
    public bool IsActive => State == ReservationState.Active;
}

public sealed record ReservationResult(ReservationStatus Status, ReservationSnapshot? Reservation);

/// <summary>What a reader may see of one host's resource pools. Compute is in milli-CU, exactly as accounted.</summary>
public sealed record ResourceSnapshot(
    HostId Host,
    int CapacityCu,
    int RateCuPerSecond,
    long BufferMilliCu,
    int MemorySlots,
    int MemoryHeld,
    long StorageBytes,
    long StorageHeld,
    int BandwidthUnits,
    int BandwidthHeld,
    int ActiveReservations)
{
    /// <summary>The buffer as a fractional CU value, for projections only. Never used for accounting.</summary>
    public double BufferCu => BufferMilliCu / 1000.0;
}

/// <summary>
/// The authoritative resource ledger (RES-001, §10). Every provider has its own isolated pools; the
/// sum of held capacity never exceeds what the provider has, and a unit of capacity is never counted in
/// two pools at once. Recovery is credited by <see cref="AdvanceTo"/> only, which the owner calls with
/// monotonic game time, so the same interval can never be credited twice.
/// </summary>
public sealed class ResourceLedger(ISimulationClock clock, ResourceLedgerOptions? options = null)
{
    /// <summary>Start values from §10.1 of the specification: authoring defaults, not balance.</summary>
    public sealed record AccountDefaults(int CapacityCu = 4, int RateCuPerSecond = 2, int MemorySlots = 4,
        long StorageBytes = 1_048_576, int BandwidthUnits = 8);

    private sealed class Account(HostId host, ComputeComponent? compute, MemoryComponent? memory,
        StorageComponent? storage, int bandwidthUnits, long now)
    {
        public readonly HostId Host = host;
        public readonly int CapacityCu = compute?.CapacityCu ?? 0;
        public readonly int RateCuPerSecond = compute?.RateCuPerSecond ?? 0;
        public readonly int MemorySlots = memory?.Slots ?? 0;
        public readonly long StorageBytes = storage?.Bytes ?? 0;
        public readonly int BandwidthUnits = bandwidthUnits;
        public long BufferMilliCu = compute is null ? 0 : compute.CapacityCu * 1000L;
        public long HeldComputeMilliCu;
        public int HeldMemory;
        public long HeldStorage;
        public int HeldBandwidth;
        public long LastAdvanced = now;
    }

    private readonly ISimulationClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ResourceLedgerOptions _options = options ?? new ResourceLedgerOptions();
    private readonly Dictionary<ulong, Account> _accounts = [];
    private readonly Dictionary<ulong, ReservationSnapshot> _reservations = [];
    private ulong _nextReservation;

    public int AccountCount => _accounts.Count;

    /// <summary>How many leases are held right now. Used by diagnostics and by the tick report.</summary>
    public int ActiveCount
    {
        get
        {
            var count = 0;
            foreach (var reservation in _reservations.Values)
                if (reservation.IsActive)
                    count++;
            return count;
        }
    }

    /// <summary>Creates the pools of a host from its components. A host without a component has no pool of that kind.</summary>
    public ReservationStatus Attach(HostId host, ComputeComponent? compute, MemoryComponent? memory,
        StorageComponent? storage, int bandwidthUnits = 0)
    {
        if (host.IsNone || _accounts.ContainsKey(host.Value))
            return ReservationStatus.InvalidRequest;
        if (compute is { CapacityCu: < 1 or > 100_000 } || compute is { RateCuPerSecond: < 0 or > 100_000 } ||
            memory is { Slots: < 0 or > 100_000 } || storage is { Bytes: < 0 } || bandwidthUnits is < 0 or > 100_000)
            return ReservationStatus.InvalidRequest;
        _accounts[host.Value] = new Account(host, compute, memory, storage, bandwidthUnits, _clock.NowMilliseconds);
        return ReservationStatus.Ok;
    }

    /// <summary>Drops a host and every reservation against it. Called when the host is destroyed.</summary>
    public int Detach(HostId host)
    {
        if (!_accounts.Remove(host.Value))
            return 0;
        var revoked = 0;
        foreach (var (id, reservation) in _reservations.ToArray())
        {
            if (reservation.Provider != host || !reservation.IsActive)
                continue;
            _reservations[id] = reservation with { State = ReservationState.Revoked, ExpiresAtMilliseconds = 0 };
            revoked++;
        }
        return revoked;
    }

    /// <summary>
    /// Credits sustained compute for the elapsed game time and expires leases. A non-positive or
    /// repeated interval credits nothing, which is what keeps recovery from being counted twice.
    /// </summary>
    public void AdvanceTo(long nowMilliseconds)
    {
        foreach (var account in _accounts.Values)
        {
            var elapsed = nowMilliseconds - account.LastAdvanced;
            if (elapsed <= 0)
                continue;
            account.LastAdvanced = nowMilliseconds;
            if (account.RateCuPerSecond > 0)
            {
                var ceiling = account.CapacityCu * 1000L;
                var gained = (long)account.RateCuPerSecond * elapsed;
                account.BufferMilliCu = Math.Min(ceiling, account.BufferMilliCu + gained);
            }
        }
        ExpireLeases(nowMilliseconds);
    }

    public ReservationResult ReserveCompute(HostId provider, PrincipalId owner, int computeUnits, long lifetimeMilliseconds) =>
        Reserve(provider, owner, ResourceKind.Compute, computeUnits, lifetimeMilliseconds);

    public ReservationResult ReserveMemory(HostId provider, PrincipalId owner, int slots, long lifetimeMilliseconds) =>
        Reserve(provider, owner, ResourceKind.Memory, slots, lifetimeMilliseconds);

    public ReservationResult ReserveStorage(HostId provider, PrincipalId owner, long bytes, long lifetimeMilliseconds) =>
        Reserve(provider, owner, ResourceKind.Storage, bytes, lifetimeMilliseconds);

    private ReservationResult Reserve(HostId provider, PrincipalId owner, ResourceKind kind, long amount,
        long lifetimeMilliseconds)
    {
        if (provider.IsNone || owner.IsNone || amount <= 0 || lifetimeMilliseconds <= 0 ||
            lifetimeMilliseconds > _options.MaxLeaseMilliseconds)
            return new ReservationResult(ReservationStatus.InvalidRequest, null);
        if (!_accounts.TryGetValue(provider.Value, out var account))
            return new ReservationResult(ReservationStatus.UnknownHost, null);
        if (_reservations.Count >= _options.MaxReservations)
            return new ReservationResult(ReservationStatus.InvalidRequest, null);

        var status = TryHold(account, kind, amount);
        if (status != ReservationStatus.Ok)
            return new ReservationResult(status, null);
        var snapshot = new ReservationSnapshot(new ReservationId(++_nextReservation), kind, provider, owner,
            amount, checked(_clock.NowMilliseconds + lifetimeMilliseconds), ReservationState.Active);
        _reservations[snapshot.Id.Value] = snapshot;
        return new ReservationResult(ReservationStatus.Ok, snapshot);
    }

    private ReservationStatus TryHold(Account account, ResourceKind kind, long amount)
    {
        switch (kind)
        {
            case ResourceKind.Compute:
            {
                var needed = amount * 1000L;
                if (account.CapacityCu <= 0)
                    return ReservationStatus.InsufficientCompute;
                if (account.BufferMilliCu < needed)
                    return ReservationStatus.InsufficientCompute;
                account.BufferMilliCu -= needed;
                account.HeldComputeMilliCu += needed;
                return ReservationStatus.Ok;
            }
            case ResourceKind.Memory:
            {
                if (account.MemorySlots <= 0 || account.HeldMemory + amount > account.MemorySlots)
                    return ReservationStatus.InsufficientMemory;
                account.HeldMemory += (int)amount;
                return ReservationStatus.Ok;
            }
            case ResourceKind.Storage:
            {
                if (account.StorageBytes <= 0 || account.HeldStorage + amount > account.StorageBytes)
                    return ReservationStatus.InsufficientStorage;
                account.HeldStorage += amount;
                return ReservationStatus.Ok;
            }
            case ResourceKind.Bandwidth:
            {
                if (account.BandwidthUnits <= 0 || account.HeldBandwidth + amount > account.BandwidthUnits)
                    return ReservationStatus.InsufficientBandwidth;
                account.HeldBandwidth += (int)amount;
                return ReservationStatus.Ok;
            }
            default:
                return ReservationStatus.InvalidRequest;
        }
    }

    /// <summary>Spends a held reservation for a committed action. The capacity does not come back.</summary>
    public ReservationStatus Consume(ReservationId id)
    {
        if (!_reservations.TryGetValue(id.Value, out var reservation))
            return ReservationStatus.UnknownReservation;
        return reservation.Kind == ResourceKind.Compute ? ConsumeCompute(id, reservation) : ConsumeCapacity(id, reservation);
    }

    private ReservationStatus ConsumeCapacity(ReservationId id, ReservationSnapshot reservation)
    {
        if (!reservation.IsActive)
            return ReservationStatus.Duplicate;
        if (!_accounts.TryGetValue(reservation.Provider.Value, out var account))
            return ReservationStatus.UnknownHost;
        switch (reservation.Kind)
        {
            case ResourceKind.Memory:
                account.HeldMemory -= (int)reservation.Amount;
                break;
            case ResourceKind.Storage:
                account.HeldStorage -= reservation.Amount;
                break;
            case ResourceKind.Bandwidth:
                account.HeldBandwidth -= (int)reservation.Amount;
                break;
        }
        _reservations[id.Value] = reservation with { State = ReservationState.Consumed };
        return ReservationStatus.Ok;
    }

    private ReservationStatus ConsumeCompute(ReservationId id, ReservationSnapshot reservation)
    {
        if (!reservation.IsActive)
            return ReservationStatus.Duplicate;
        if (!_accounts.TryGetValue(reservation.Provider.Value, out var account))
            return ReservationStatus.UnknownHost;
        account.HeldComputeMilliCu -= reservation.Amount * 1000L;
        _reservations[id.Value] = reservation with { State = ReservationState.Consumed };
        return ReservationStatus.Ok;
    }

    /// <summary>
    /// Gives capacity back. Releasing a reservation that is already consumed, expired or released is a
    /// duplicate and credits nothing: a retried release is never a second batch of resources.
    /// </summary>
    public ReservationStatus Release(ReservationId id, PrincipalId owner)
    {
        if (!_reservations.TryGetValue(id.Value, out var reservation))
            return ReservationStatus.UnknownReservation;
        if (reservation.Owner != owner)
            return ReservationStatus.StalePrincipal;
        return ReleaseCore(reservation, ReservationState.Released);
    }

    private ReservationStatus ReleaseCore(ReservationSnapshot reservation, ReservationState state)
    {
        if (!reservation.IsActive)
            return ReservationStatus.Duplicate;
        if (_accounts.TryGetValue(reservation.Provider.Value, out var account))
        {
            switch (reservation.Kind)
            {
                case ResourceKind.Compute:
                {
                    var held = reservation.Amount * 1000L;
                    account.HeldComputeMilliCu -= held;
                    var ceiling = account.CapacityCu * 1000L;
                    account.BufferMilliCu = Math.Min(ceiling, account.BufferMilliCu + held);
                    break;
                }
                case ResourceKind.Memory:
                    account.HeldMemory -= (int)reservation.Amount;
                    break;
                case ResourceKind.Storage:
                    account.HeldStorage -= reservation.Amount;
                    break;
                case ResourceKind.Bandwidth:
                    account.HeldBandwidth -= (int)reservation.Amount;
                    break;
            }
        }
        _reservations[reservation.Id.Value] = reservation with { State = state };
        return ReservationStatus.Ok;
    }

    /// <summary>Extends a lease. A lease that was already expired comes back as a new reservation instead.</summary>
    public ReservationStatus Confirm(ReservationId id, PrincipalId owner, long lifetimeMilliseconds)
    {
        if (lifetimeMilliseconds <= 0 || lifetimeMilliseconds > _options.MaxLeaseMilliseconds)
            return ReservationStatus.InvalidRequest;
        if (!_reservations.TryGetValue(id.Value, out var reservation))
            return ReservationStatus.UnknownReservation;
        if (reservation.Owner != owner)
            return ReservationStatus.StalePrincipal;
        if (!reservation.IsActive)
            return ReservationStatus.Duplicate;
        _reservations[id.Value] = reservation with
        {
            ExpiresAtMilliseconds = checked(_clock.NowMilliseconds + lifetimeMilliseconds)
        };
        return ReservationStatus.Ok;
    }

    private void ExpireLeases(long nowMilliseconds)
    {
        foreach (var reservation in _reservations.Values.ToArray())
        {
            if (reservation.IsActive && reservation.ExpiresAtMilliseconds <= nowMilliseconds)
                ReleaseCore(reservation, ReservationState.Expired);
        }
    }

    public ReservationSnapshot? Get(ReservationId id) =>
        _reservations.TryGetValue(id.Value, out var reservation) ? reservation : null;

    /// <summary>Every reservation made against one provider. Used by cleanup paths, not on a hot path.</summary>
    public ReservationSnapshot[] Provider(HostId host)
    {
        var found = new List<ReservationSnapshot>();
        foreach (var reservation in _reservations.Values)
            if (reservation.Provider == host)
                found.Add(reservation);
        return found.ToArray();
    }

    public ResourceSnapshot? Snapshot(HostId host) =>
        _accounts.TryGetValue(host.Value, out var account)
            ? new ResourceSnapshot(host, account.CapacityCu, account.RateCuPerSecond, account.BufferMilliCu,
                account.MemorySlots, account.HeldMemory, account.StorageBytes, account.HeldStorage,
                account.BandwidthUnits, account.HeldBandwidth, ActiveReservationsOf(host))
            : null;

    private int ActiveReservationsOf(HostId host)
    {
        var count = 0;
        foreach (var reservation in _reservations.Values)
            if (reservation.IsActive && reservation.Provider == host)
                count++;
        return count;
    }

    /// <summary>
    /// Takes compute out of a host's buffer on behalf of an effect, such as a drain. The amount is capped by
    /// what is actually there, so an effect never drives the account negative and the caller learns how much
    /// it really got.
    /// </summary>
    public long Drain(HostId provider, long milliCu)
    {
        if (milliCu <= 0 || !_accounts.TryGetValue(provider.Value, out var account))
            return 0;
        var taken = Math.Min(milliCu, account.BufferMilliCu);
        account.BufferMilliCu -= taken;
        return taken;
    }

    /// <summary>
    /// Charges a cost directly against the buffer without a reservation. Used by owners whose compute
    /// budget is checked and spent inside one critical section; callers that need a reserve/commit
    /// window use <see cref="ReserveCompute"/> instead.
    /// </summary>
    public bool TryCharge(HostId provider, int computeUnits, out long remainingMilliCu)
    {
        remainingMilliCu = 0;
        if (!_accounts.TryGetValue(provider.Value, out var account) || computeUnits <= 0)
            return false;
        var needed = computeUnits * 1000L;
        if (account.BufferMilliCu < needed)
        {
            remainingMilliCu = account.BufferMilliCu;
            return false;
        }
        account.BufferMilliCu -= needed;
        remainingMilliCu = account.BufferMilliCu;
        return true;
    }
}

public sealed record ResourceLedgerOptions(int MaxReservations = 32_768, long MaxLeaseMilliseconds = 600_000);
