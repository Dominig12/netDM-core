namespace NetRun.Core;

/// <summary>
/// How alarmed one host is (§9.3). The levels are local to the host: a station-wide alarm is a
/// consequence of hosts raising theirs and telling each other, never a global switch.
/// </summary>
public enum HostAlertLevel : byte
{
    Normal = 0,
    Suspicious = 1,
    Alert = 2,
    Hostile = 3,
    Lockdown = 4
}

public sealed record HostAlertOptions(
    /// <summary>How long a host stays at its level with nothing new before it steps down one level.</summary>
    long StepDownMilliseconds = 120_000,
    /// <summary>How many hosts may hold a non-normal level at once.</summary>
    int MaxHosts = 4_096);

/// <summary>One host's alert, with when it was last raised and why. A projection, never an owner.</summary>
public sealed record HostAlertState(
    HostId Host,
    HostAlertLevel Level,
    long RaisedAtMilliseconds,
    string Reason);

/// <summary>
/// Per-host alert levels (§9.3, SEC-002). A host raises its own level when it notices something —
/// a breach attempt, an engaged ICE — and steps back down when nothing happens for a while. The level
/// never exceeds <see cref="HostAlertLevel.Lockdown"/> and never falls below Normal; nothing here
/// changes authorization by itself, it only describes how watchful the host is.
/// </summary>
public sealed class AlertRegistry(ISimulationClock clock, HostAlertOptions? options = null)
{
    private readonly ISimulationClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly HostAlertOptions _options = options ?? new HostAlertOptions();
    private readonly Dictionary<ulong, HostAlertState> _states = [];

    public int Count => _states.Count;

    /// <summary>The level a host is at now, after settling the decay that has come due.</summary>
    public HostAlertLevel Level(HostId host)
    {
        Sweep();
        return _states.TryGetValue(host.Value, out var state) ? state.Level : HostAlertLevel.Normal;
    }

    /// <summary>
    /// Raises the host by at most <paramref name="steps"/> levels, capped at Lockdown. Raising is what
    /// noticing an intrusion does; it never lowers a level and never extends beyond the bound.
    /// </summary>
    public HostAlertState Raise(HostId host, int steps, string reason)
    {
        if (host.IsNone)
            return new HostAlertState(host, HostAlertLevel.Normal, _clock.NowMilliseconds, "");
        steps = Math.Max(1, steps);
        var now = _clock.NowMilliseconds;
        if (_states.TryGetValue(host.Value, out var existing))
        {
            if (existing.Level == HostAlertLevel.Lockdown)
            {
                // A host that is already locked down stays where it is; the reason is the newest one.
                var locked = existing with { RaisedAtMilliseconds = now, Reason = reason };
                _states[host.Value] = locked;
                return locked;
            }
            var raised = (HostAlertLevel)Math.Min((int)HostAlertLevel.Lockdown, (int)existing.Level + steps);
            var next = existing with { Level = raised, RaisedAtMilliseconds = now, Reason = reason };
            _states[host.Value] = next;
            return next;
        }
        if (_states.Count >= _options.MaxHosts)
        {
            // Bounded like every other store: the oldest observation loses its place, not a new one.
            var oldest = _states.Values.OrderBy(state => state.RaisedAtMilliseconds).First();
            _states.Remove(oldest.Host.Value);
        }
        // A loud signal may skip levels on a host that was quiet: the first raise counts its steps too,
        // otherwise a forced attempt would only ever make a fresh host "suspicious".
        var level = (HostAlertLevel)Math.Min((int)HostAlertLevel.Lockdown,
            (int)HostAlertLevel.Suspicious + steps - 1);
        var created = new HostAlertState(host, level, now, reason);
        _states[host.Value] = created;
        return created;
    }

    /// <summary>Lowers one host by one level now, for a repair or an operator decision.</summary>
    public HostAlertLevel Lower(HostId host)
    {
        if (!_states.TryGetValue(host.Value, out var state))
            return HostAlertLevel.Normal;
        if (state.Level <= HostAlertLevel.Suspicious)
        {
            _states.Remove(host.Value);
            return HostAlertLevel.Normal;
        }
        var lowered = state with { Level = state.Level - 1, RaisedAtMilliseconds = _clock.NowMilliseconds };
        _states[host.Value] = lowered;
        return lowered.Level;
    }

    /// <summary>
    /// Steps down every host whose level has stood for <see cref="HostAlertOptions.StepDownMilliseconds"/>
    /// without a new reason. Called by the runtime tick; returns how many hosts moved.
    /// </summary>
    public int Sweep()
    {
        if (_states.Count == 0)
            return 0;
        var now = _clock.NowMilliseconds;
        var moved = 0;
        foreach (var (key, state) in _states.ToArray())
        {
            if (now - state.RaisedAtMilliseconds < _options.StepDownMilliseconds)
                continue;
            if (state.Level <= HostAlertLevel.Suspicious)
            {
                _states.Remove(key);
            }
            else
            {
                _states[key] = state with { Level = state.Level - 1, RaisedAtMilliseconds = now };
            }
            moved++;
        }
        return moved;
    }

    /// <summary>The current alert of one host, or null when it is normal.</summary>
    public HostAlertState? Capture(HostId host) =>
        _states.TryGetValue(host.Value, out var state) ? state : null;

    /// <summary>Everything not normal, freshest first: a bounded projection for a defender's console.</summary>
    public HostAlertState[] Alerts(int limit)
    {
        if (limit <= 0)
            return [];
        return _states.Values
            .OrderByDescending(state => state.RaisedAtMilliseconds)
            .Take(limit)
            .ToArray();
    }

    /// <summary>Forgets a host: its alert disappears with it, like every other projection of the object.</summary>
    public void Forget(HostId host) => _states.Remove(host.Value);
}
