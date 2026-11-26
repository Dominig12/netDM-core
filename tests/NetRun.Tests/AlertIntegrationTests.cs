using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>
/// §9.3 end to end: a breach the host notices raises its own alert, and a host at Lockdown stops
/// answering ordinary authority while staying breakable.
/// </summary>
internal static class AlertIntegrationTests
{
    public static void Run()
    {
        Section("alerts: breach raises, lockdown denies");
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var deck = runtime.RegisterHost(new HostDefinition("deck.1", HostClass.Cyberdeck,
            Compute: new ComputeComponent(60, 20), Memory: new MemoryComponent(4))).Host!;
        var target = runtime.RegisterHost(new HostDefinition("target.1", HostClass.Server,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4),
            Security: new SecurityComponent(SecurityProfile.Standard, 4, 2))).Host!;
        var actor = runtime.NewPrincipal();
        foreach (var grant in new[]
                 {
                     (Host: deck.Id, Category: CapabilityCategory.Execution),
                     (Host: deck.Id, Category: CapabilityCategory.Discovery),
                     (Host: target.Id, Category: CapabilityCategory.Discovery)
                 })
        {
            runtime.Authority.Grant(actor, grant.Host, grant.Category, null, 600_000,
                GrantSource.Granted, PrincipalId.None);
        }
        runtime.Knowledge.Remember(actor, target.Id, KnowledgeLevel.Observed, 90);

        Equal(HostAlertLevel.Normal, runtime.Alerts.Level(target.Id), "an untouched host is normal");
        var action = runtime.Actions.GetAction("security.breach.forced")!;
        var outcome = runtime.Actions.Execute(new ActionRequest(actor, deck.Id, target.Id,
            EntityId.None, ProcessId.None, CorrelationId.None, "alert-breach"), action);
        Equal(ActionStatus.Committed, outcome.Status, $"a forced breach runs: {outcome.Reason}");
        Check(runtime.Alerts.Level(target.Id) >= HostAlertLevel.Alert,
            $"a forced attempt that was noticed raises the host's alert ({runtime.Alerts.Level(target.Id)})");
        Check((outcome.Result ?? "").Contains("alert", StringComparison.Ordinal),
            "the verdict names the alert the attempt met");

        // Ordinary authority keeps working while the host is only alarmed; at Lockdown it stops.
        runtime.Authority.Grant(actor, target.Id, CapabilityCategory.Control, null, 600_000,
            GrantSource.Granted, PrincipalId.None);
        Equal(AuthorizationStatus.Ok, runtime.Authority.Authorize(actor, target.Id,
            CapabilityCategory.Control, null, SecurityProfile.Standard).Status,
            "a granted control works while the host is not locked down");
        runtime.Alerts.Raise(target.Id, 10, "test");
        Equal(HostAlertLevel.Lockdown, runtime.Alerts.Level(target.Id), "the alert reaches Lockdown");
        Equal(AuthorizationStatus.PolicyDenied, runtime.Authority.Authorize(actor, target.Id,
            CapabilityCategory.Control, null, SecurityProfile.Standard, lockdown: true).Status,
            "lockdown refuses ordinary authority even with a grant");
        Equal(AuthorizationStatus.Ok, runtime.Authority.Authorize(actor, target.Id,
            CapabilityCategory.Control, null, SecurityProfile.Standard).Status,
            "the grant itself is untouched: only the pipeline's lockdown flag refuses it");
    }
}
