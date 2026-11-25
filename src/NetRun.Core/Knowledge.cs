namespace NetRun.Core;

/// <summary>
/// How much one observer knows about one object (§9.4). The levels are not a mandatory sequence: a scan
/// may identify a host at once, and knowledge can decay back to an anomaly. What matters is that an
/// observer never learns more than the levels it actually reached.
/// </summary>
public enum KnowledgeLevel : byte
{
    /// <summary>Nothing. The object is indistinguishable from one that does not exist.</summary>
    Unknown = 0,
    /// <summary>Something is there, but not what or where exactly.</summary>
    Anomaly = 1,
    /// <summary>The object and its position are known.</summary>
    Observed = 2,
    /// <summary>The identity behind the object is established.</summary>
    Identified = 3
}

/// <summary>
/// What one observer holds about one host. It is a projection, never an owner: the host's authoritative
/// state stays in the registry and only visible fields are ever copied here.
/// </summary>
public sealed record KnowledgeRecord(
    PrincipalId Observer,
    HostId Host,
    KnowledgeLevel Level,
    long FirstSeenAtMilliseconds,
    long LastConfirmedAtMilliseconds,
    int Confidence)
{
    /// <summary>Age since the last confirmation. A projection shows it, and staleness is derived from it.</summary>
    public long AgeMilliseconds(long nowMilliseconds) => Math.Max(0, nowMilliseconds - LastConfirmedAtMilliseconds);
}

public sealed record KnowledgeOptions(
    long StaleAfterMilliseconds = 60_000,
    long ForgetAfterMilliseconds = 600_000,
    int MaxPerObserver = 1_024,
    int MaxObservers = 4_096);

/// <summary>
/// Per-observer knowledge (§9.4, PRIM-002). Knowledge lives in the Host for the round, ages by game time and
/// disappears with the object it describes. Nothing here reveals an object: an observer that never reached
/// a level simply has no entry, and the answer for an unknown object is identical to the answer for a
/// non-existent one (AT-12).
/// </summary>
public sealed class KnowledgeStore(ISimulationClock clock, KnowledgeOptions? options = null)
{
    private readonly ISimulationClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly KnowledgeOptions _options = options ?? new KnowledgeOptions();
    private readonly Dictionary<ulong, Dictionary<ulong, KnowledgeRecord>> _byObserver = [];

    public int ObserverCount => _byObserver.Count;

    public int RecordCount
    {
        get
        {
            var total = 0;
            foreach (var records in _byObserver.Values)
                total += records.Count;
            return total;
        }
    }

    public long StaleAfterMilliseconds => _options.StaleAfterMilliseconds;

    /// <summary>
    /// Raises what an observer knows. Levels never fall by observation: learning that a host is a relay does
    /// not unlearn where it is. Decay happens through <see cref="Prune"/> and through staleness of the record.
    /// </summary>
    public KnowledgeRecord Remember(PrincipalId observer, HostId host, KnowledgeLevel level, int confidence)
    {
        if (observer.IsNone || host.IsNone || level == KnowledgeLevel.Unknown)
            return new KnowledgeRecord(observer, host, KnowledgeLevel.Unknown, 0, 0, 0);
        var now = _clock.NowMilliseconds;
        if (!_byObserver.TryGetValue(observer.Value, out var records))
        {
            if (_byObserver.Count >= _options.MaxObservers)
                return new KnowledgeRecord(observer, host, KnowledgeLevel.Unknown, 0, 0, 0);
            records = [];
            _byObserver[observer.Value] = records;
        }
        if (records.TryGetValue(host.Value, out var existing))
        {
            var raised = level > existing.Level ? level : existing.Level;
            var updated = existing with
            {
                Level = raised,
                LastConfirmedAtMilliseconds = now,
                Confidence = Math.Clamp(Math.Max(confidence, existing.Confidence), 0, 100)
            };
            records[host.Value] = updated;
            return updated;
        }
        if (records.Count >= _options.MaxPerObserver)
            EvictOldest(records);
        var created = new KnowledgeRecord(observer, host, level, now, now, Math.Clamp(confidence, 0, 100));
        records[host.Value] = created;
        return created;
    }

    /// <summary>Re-confirms an existing record without raising its level, for example after a successful scan.</summary>
    public void Confirm(PrincipalId observer, HostId host)
    {
        if (!_byObserver.TryGetValue(observer.Value, out var records) ||
            !records.TryGetValue(host.Value, out var existing))
            return;
        records[host.Value] = existing with { LastConfirmedAtMilliseconds = _clock.NowMilliseconds };
    }

    public KnowledgeRecord? Lookup(PrincipalId observer, HostId host) =>
        _byObserver.TryGetValue(observer.Value, out var records) && records.TryGetValue(host.Value, out var record)
            ? record
            : null;

    public KnowledgeLevel Level(PrincipalId observer, HostId host) =>
        Lookup(observer, host)?.Level ?? KnowledgeLevel.Unknown;

    /// <summary>Whether the observer currently holds at least this level and the record is fresh enough to act on.</summary>
    public bool Holds(PrincipalId observer, HostId host, KnowledgeLevel level, bool requireFresh = true)
    {
        var record = Lookup(observer, host);
        if (record is null || record.Level < level)
            return false;
        return !requireFresh || !IsStale(record);
    }

    public bool IsStale(KnowledgeRecord record) =>
        record.AgeMilliseconds(_clock.NowMilliseconds) > _options.StaleAfterMilliseconds;

    /// <summary>Everything one observer holds, freshest first. A bounded projection for the UI.</summary>
    public KnowledgeRecord[] Known(PrincipalId observer, int limit)
    {
        if (limit <= 0 || !_byObserver.TryGetValue(observer.Value, out var records))
            return [];
        return records.Values
            .OrderByDescending(record => record.LastConfirmedAtMilliseconds)
            .Take(limit)
            .ToArray();
    }

    /// <summary>Forgets one object, for example because it was destroyed: a stale id must not resolve again.</summary>
    public int Forget(HostId host)
    {
        var removed = 0;
        foreach (var records in _byObserver.Values)
            if (records.Remove(host.Value))
                removed++;
        return removed;
    }

    /// <summary>Drops everything an observer knew, for example when its session ends.</summary>
    public int ForgetObserver(PrincipalId observer) => _byObserver.Remove(observer.Value) ? 1 : 0;

    /// <summary>
    /// Ages the store: records older than the forget window disappear entirely. Called once per tick, so an
    /// observer reading a stale record always sees the same answer the simulation would give it.
    /// </summary>
    public int Prune()
    {
        var now = _clock.NowMilliseconds;
        var removed = 0;
        foreach (var (observer, records) in _byObserver.ToArray())
        {
            foreach (var (host, record) in records.ToArray())
            {
                if (record.AgeMilliseconds(now) <= _options.ForgetAfterMilliseconds)
                    continue;
                records.Remove(host);
                removed++;
            }
            if (records.Count == 0)
                _byObserver.Remove(observer);
        }
        return removed;
    }

    private static void EvictOldest(Dictionary<ulong, KnowledgeRecord> records)
    {
        ulong oldestHost = 0;
        var oldestAt = long.MaxValue;
        foreach (var (host, record) in records)
        {
            if (record.LastConfirmedAtMilliseconds >= oldestAt)
                continue;
            oldestAt = record.LastConfirmedAtMilliseconds;
            oldestHost = host;
        }
        if (oldestHost != 0)
            records.Remove(oldestHost);
    }
}
