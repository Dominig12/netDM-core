using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>
/// §9.3: a host's alert level is local, rises when the host notices something, is capped at Lockdown
/// and steps back down when nothing happens. Nothing here changes authorization by itself.
/// </summary>
internal static class AlertTests
{
    public static void Run()
    {
        Section("alerts");
        var clock = new ManualSimulationClock();
        var alerts = new AlertRegistry(clock, new HostAlertOptions(StepDownMilliseconds: 10_000));
        var host = new HostId(1);
        var other = new HostId(2);

        Equal(HostAlertLevel.Normal, alerts.Level(host), "a quiet host is normal");
        var first = alerts.Raise(host, 1, "breach");
        Equal(HostAlertLevel.Suspicious, first.Level, "a noticed attempt makes the host suspicious");
        Equal("breach", alerts.Capture(host)?.Reason, "the reason is kept for the defender");

        var second = alerts.Raise(host, 1, "ice_engaged");
        Equal(HostAlertLevel.Alert, second.Level, "a second notice raises it again");
        Equal(HostAlertLevel.Normal, alerts.Level(other), "another host is untouched: alerts are local");

        alerts.Raise(host, 10, "forced");
        Equal(HostAlertLevel.Lockdown, alerts.Level(host), "the level is capped at Lockdown");
        var locked = alerts.Raise(host, 1, "again");
        Equal(HostAlertLevel.Lockdown, locked.Level, "a locked host stays locked");

        // Nothing new happened for the whole step-down window: the level falls one step.
        clock.Advance(10_100);
        Equal(HostAlertLevel.Hostile, alerts.Level(host), "quiet time steps the level down by one");
        clock.Advance(10_100);
        Equal(HostAlertLevel.Alert, alerts.Level(host), "and again");
        clock.Advance(10_100);
        clock.Advance(10_100);
        Equal(HostAlertLevel.Normal, alerts.Level(host), "a level that reached Normal leaves no record");
        Check(alerts.Capture(host) is null, "no state is kept for a normal host");

        // A raise resets the timer, so a burst of notices is not decayed in the middle of it.
        alerts.Raise(host, 2, "burst");
        clock.Advance(5_000);
        alerts.Raise(host, 1, "burst");
        clock.Advance(5_100);
        Equal(HostAlertLevel.Hostile, alerts.Level(host), "the newest notice restarts the quiet window");

        // An operator can lower it at once; forgetting the host drops its alert with it.
        alerts.Lower(host);
        Check(alerts.Level(host) < HostAlertLevel.Hostile, "a repair lowers the level");
        alerts.Forget(host);
        Equal(HostAlertLevel.Normal, alerts.Level(host), "a destroyed host takes its alert with it");
    }
}
