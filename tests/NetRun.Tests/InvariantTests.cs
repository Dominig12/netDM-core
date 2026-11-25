using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>
/// Invariants that the conformance audit found missing: a commit re-decides an implicit grant, and probing
/// identifiers cannot reveal whether a host exists.
/// </summary>
internal static class InvariantTests
{
    public static void Run()
    {
        Section("invariants");

        // SVC-003: an operation allowed by the host profile is decided again at commit. Tightening the profile
        // between prepare and commit must stop it, exactly as a revoked grant does.
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var deck = runtime.RegisterHost(new HostDefinition("deck.1", HostClass.Cyberdeck,
            Compute: new ComputeComponent(30, 10), Memory: new MemoryComponent(4))).Host!;
        var open = runtime.RegisterHost(new HostDefinition("open.1", HostClass.Server,
            Compute: new ComputeComponent(6, 2), Memory: new MemoryComponent(2),
            Security: new SecurityComponent(SecurityProfile.Open, 4, 2))).Host!;
        var actor = runtime.NewPrincipal();
        runtime.Authority.Grant(actor, deck.Id, CapabilityCategory.Execution, null, 600_000, GrantSource.Granted, PrincipalId.None);
        runtime.Knowledge.Remember(actor, open.Id, KnowledgeLevel.Observed, 90);
        var inspect = runtime.Actions.GetAction("net.inspect")!;

        var prepared = runtime.Actions.Prepare(
            new ActionRequest(actor, deck.Id, open.Id, EntityId.None, ProcessId.None, CorrelationId.None, "i-1"), inspect);
        Check(prepared.Succeeded && prepared.Prepared is not null, "an open host allows the inspection without a grant");
        Check(prepared.Prepared!.Token.IsNone, "the permission came from the profile, not from a grant");

        // The host hardens before the commit: the implicit permission is gone.
        runtime.Hosts.Rebind("open.1", new HostDefinition("open.1", HostClass.Server,
            Compute: new ComputeComponent(6, 2), Memory: new MemoryComponent(2),
            Security: new SecurityComponent(SecurityProfile.Quarantine, 4, 2)));
        var committed = runtime.Actions.Commit(prepared.Prepared);
        Equal(ActionStatus.Forbidden, committed.Status, "the commit is refused after the profile tightened");
        Equal(ActionStatus.Cancelled, runtime.Actions.Operation(prepared.OperationId)!.Status,
            "the stopped operation is cancelled, not silently dropped");

        // PRIM-002: probing identifiers cannot tell an existing host from one that never existed.
        var stranger = runtime.NewPrincipal();
        runtime.Authority.Grant(stranger, deck.Id, CapabilityCategory.Execution, null, 600_000, GrantSource.Granted, PrincipalId.None);
        var existing = runtime.Actions.Execute(
            new ActionRequest(stranger, deck.Id, open.Id, EntityId.None, ProcessId.None, CorrelationId.None, "p-1"), inspect);
        var missing = runtime.Actions.Execute(
            new ActionRequest(stranger, deck.Id, new HostId(9_999), EntityId.None, ProcessId.None, CorrelationId.None, "p-2"), inspect);
        Equal(ActionStatus.Forbidden, missing.Status, "a nonexistent identifier is refused");
        Equal(existing.Status, missing.Status, "an existing hidden host answers exactly like a missing one");
        Equal(existing.Reason, missing.Reason, "and for the same stated reason");
    }
}
