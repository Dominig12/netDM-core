using System.Diagnostics;

namespace NetRun.Core;

/// <summary>
/// The one monotonic game clock of NETRUN (§17.2). Cooldowns, lease expiry, effect duration and
/// resource recovery are expressed in this time, never in wall clock or frame count. Tests advance it
/// by hand, so a scenario is reproducible and a paused simulation really is paused.
/// </summary>
public interface ISimulationClock
{
    /// <summary>Milliseconds of simulated game time since the round started. Never goes backwards.</summary>
    long NowMilliseconds { get; }
}

/// <summary>Game time driven by a monotonic stopwatch. Pausing the world must use a different clock.</summary>
public sealed class SteadySimulationClock : ISimulationClock
{
    private readonly long _origin = Stopwatch.GetTimestamp();
    private long _last;

    public long NowMilliseconds
    {
        get
        {
            var elapsed = (Stopwatch.GetTimestamp() - _origin) * 1000L / Stopwatch.Frequency;
            var now = Math.Max(_last, elapsed);
            _last = now;
            return now;
        }
    }
}

/// <summary>
/// A clock a caller advances explicitly. Every subsystem that reads time is deterministic under it,
/// which is what the acceptance scenarios for recovery, leases and cooldowns are written against.
/// </summary>
public sealed class ManualSimulationClock(long startMilliseconds = 0) : ISimulationClock
{
    private long _now = startMilliseconds;

    public long NowMilliseconds => _now;

    /// <summary>Moves game time forward. A non-positive step is ignored so time never runs backwards.</summary>
    public void Advance(long milliseconds)
    {
        if (milliseconds > 0)
            _now = checked(_now + milliseconds);
    }
}
