using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>
/// SVC-001/002/003 and §6.2: the pipeline enforces identity, rights, cost, cooldown, idempotency and the
/// operation state machine for every action, whoever calls it.
/// </summary>
internal static class ActionTests
{
    public static void Run()
    {
        Section("action pipeline");
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock, new NetRunRuntimeOptions(MaxOperations: 2));
        var deck = runtime.RegisterHost(new HostDefinition("deck.1", HostClass.Cyberdeck,
            Compute: new ComputeComponent(100, 20), Memory: new MemoryComponent(4))).Host!;
        var door = runtime.RegisterHost(new HostDefinition("door.1", HostClass.Terminal,
            Compute: new ComputeComponent(1, 1), Memory: new MemoryComponent(1))).Host!;
        var actor = runtime.NewPrincipal();

        var probe = new ProbeAction();
        var doorAction = new DoorAction();
        var rejected = new RejectedAction();
        var copyAction = new DoorAction("door.control.copy", "door.control");
        runtime.Actions.Register(probe);
        runtime.Actions.Register(doorAction);
        runtime.Actions.Register(rejected);
        runtime.Actions.Register(copyAction);

        // The catalogue is the single description source (ADR-0003).
        var descriptor = runtime.Actions.Describe("probe.read")!;
        Equal(1, descriptor.ComputeCost, "the descriptor carries the cost");
        Equal(ActionKind.Query, descriptor.Kind, "the descriptor carries the operation kind");
        Check(runtime.Actions.Catalogue(10_000).Length == runtime.Actions.ActionCount,
            "the catalogue lists every registered action exactly once");
        Check(runtime.Actions.Describe("net.inspect") is not null && runtime.Actions.Describe("net.scan") is not null,
            "the built-in perception actions are registered by the domain itself");
        Check(runtime.Actions.Describe("device.door.open") is not null &&
            runtime.Actions.Describe("device.door.close") is not null,
            "the built-in physical actions are registered by the domain itself");

        // Registration is by contract: the same declaration is idempotent, a different one fails.
        runtime.Actions.Register(new ProbeAction());
        var threw = false;
        try
        {
            runtime.Actions.Register(new ProbeAction(computeCost: 9));
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        Check(threw, "a conflicting action contract is refused at composition time");

        // An action that is not registered through this pipeline cannot run.
        Equal(ActionStatus.UnknownAction,
            runtime.Actions.Execute(Request(actor, deck.Id, deck.Id, "unregistered"), new ProbeAction()).Status,
            "an unregistered instance is refused even when its id matches");

        // Rights are checked before anything is spent.
        Equal(ActionStatus.Forbidden, runtime.Actions.Execute(Request(actor, deck.Id, door.Id, "no-grant"), probe).Status,
            "an action without a capability is forbidden");

        var read = runtime.Authority.Grant(actor, door.Id, CapabilityCategory.Read, null, 60_000, GrantSource.Granted, PrincipalId.None);
        var deckRead = runtime.Authority.Grant(actor, deck.Id, CapabilityCategory.Read, null, 60_000, GrantSource.Granted, PrincipalId.None);
        var control = runtime.Authority.Grant(actor, door.Id, CapabilityCategory.Control, "machine.door", 60_000,
            GrantSource.Granted, PrincipalId.None);
        var broadControl = runtime.Authority.Grant(actor, door.Id, CapabilityCategory.Control, null, 60_000,
            GrantSource.Granted, PrincipalId.None);
        var execution = runtime.Authority.Grant(actor, deck.Id, CapabilityCategory.Execution, null, 60_000,
            GrantSource.Granted, PrincipalId.None);
        Check(read.Allowed && control.Allowed && broadControl.Allowed && execution.Allowed, "the test grants are issued");

        // Spending a host's compute is a right of its own.
        var noExecution = runtime.NewPrincipal();
        runtime.Authority.Grant(noExecution, door.Id, CapabilityCategory.Read, null, 60_000, GrantSource.Granted, PrincipalId.None);
        Equal(ActionStatus.Forbidden, runtime.Actions.Execute(Request(noExecution, deck.Id, door.Id, "no-exec"), probe).Status,
            "a principal without execution on the host cannot launch there");

        // A query with a compute cost spends it at commit.
        var before = runtime.Resources.Snapshot(deck.Id)!.BufferMilliCu;
        var executed = runtime.Actions.Execute(Request(actor, deck.Id, door.Id, "probe-1"), probe);
        Equal(ActionStatus.Committed, executed.Status, "the action commits");
        Check(!executed.OperationId.IsNone, "the outcome names the operation");
        var after = runtime.Resources.Snapshot(deck.Id)!.BufferMilliCu;
        Equal(before - 1_000L, after, "the compute cost is charged to the execution host");

        var operation = runtime.Actions.Operation(executed.OperationId)!;
        Equal(ActionStatus.Committed, operation.Status, "the operation reports its terminal state");

        // Idempotency: the same key and content is a duplicate, different content is a conflict.
        var duplicate = runtime.Actions.Execute(Request(actor, deck.Id, door.Id, "probe-1"), probe);
        Equal(ActionStatus.Duplicate, duplicate.Status, "a retried request is recognised as a duplicate");
        Equal(executed.OperationId, duplicate.OperationId, "the duplicate names the first operation");
        var conflicting = runtime.Actions.Execute(Request(actor, deck.Id, deck.Id, "probe-1"), probe);
        Equal(ActionStatus.IdempotencyConflict, conflicting.Status,
            "the same key with different content is a conflict, not a rerun");
        Equal(ActionStatus.UnknownAction,
            runtime.Actions.Execute(Request(actor, deck.Id, door.Id, "probe-1"), new ProbeAction()).Status,
            "another instance under the same key is not the same action");

        // Cooldown belongs to the principal and the group, not to the program that calls it.
        var doorFirst = runtime.Actions.Execute(Request(actor, deck.Id, door.Id, "door-1"), doorAction);
        Equal(ActionStatus.Committed, doorFirst.Status, "the guarded action commits");
        var again = runtime.Actions.Execute(Request(actor, deck.Id, door.Id, "door-2"), doorAction);
        Equal(ActionStatus.Cooldown, again.Status, "a second call inside the cooldown is refused");
        Check(again.CooldownRemainingMilliseconds > 0, "the refusal reports the remaining cooldown");
        Equal(ActionStatus.Cooldown, runtime.Actions.Execute(Request(actor, deck.Id, door.Id, "door-3"), copyAction).Status,
            "a copy of the program does not reset the shared cooldown");
        clock.Advance(1_001);
        Equal(ActionStatus.Committed, runtime.Actions.Execute(Request(actor, deck.Id, door.Id, "door-4"), doorAction).Status,
            "the cooldown ends on game time");

        // A refused authoritative change releases its reserves.
        var bufferBefore = runtime.Resources.Snapshot(deck.Id)!.BufferMilliCu;
        var refused = runtime.Actions.Execute(Request(actor, deck.Id, door.Id, "rejected-1"), rejected);
        Equal(ActionStatus.Rejected, refused.Status, "the world's refusal is returned as the outcome");
        Equal(bufferBefore, runtime.Resources.Snapshot(deck.Id)!.BufferMilliCu,
            "a refused action returns its reserved compute");

        // Prepare/commit split: cancellation releases and never starts the cooldown.
        clock.Advance(2_000);
        var prepared = runtime.Actions.Prepare(Request(actor, deck.Id, door.Id, "prepared-1"), doorAction);
        Check(prepared.Succeeded && prepared.Prepared is not null, "the operation is prepared");
        var held = runtime.Resources.Snapshot(deck.Id)!.BufferMilliCu;
        var cancelled = runtime.Actions.Cancel(prepared.Prepared!, "test");
        Equal(ActionStatus.Cancelled, cancelled.Status, "the prepared operation is cancelled");
        Equal(held + 2_000L, runtime.Resources.Snapshot(deck.Id)!.BufferMilliCu,
            "cancellation returns the reserved compute");
        Equal(ActionStatus.Committed,
            runtime.Actions.Execute(Request(actor, deck.Id, door.Id, "prepared-2"), doorAction).Status,
            "a cancelled operation did not start the cooldown");

        // A revocation ordered before commit stops the commit (fencing).
        clock.Advance(2_000);
        var fenced = runtime.Actions.Prepare(Request(actor, deck.Id, door.Id, "fenced-1"), doorAction);
        Check(fenced.Succeeded, "the operation to be fenced is prepared");
        runtime.Authority.Revoke(control.Token.Grant);
        runtime.Authority.Revoke(broadControl.Token.Grant);
        var fencedOutcome = runtime.Actions.Commit(fenced.Prepared!);
        Equal(ActionStatus.Forbidden, fencedOutcome.Status, "the commit is refused after revocation");
        Equal(ActionStatus.Cancelled, runtime.Actions.Operation(fenced.OperationId)!.Status,
            "the fenced operation ends as cancelled, not as committed");

        // Prepare windows expire on game time and return capacity.
        runtime.Authority.Grant(actor, door.Id, CapabilityCategory.Control, "machine.door", 60_000,
            GrantSource.Granted, PrincipalId.None);
        var pending = runtime.Actions.Prepare(Request(actor, deck.Id, door.Id, "expire-1"), doorAction);
        Check(pending.Succeeded, "an operation with a prepare window is created");
        clock.Advance(6_000);
        var tick = runtime.Tick();
        Check(tick.ExpiredOperations >= 1, "the simulation tick expires the prepared operation");
        Equal(ActionStatus.Cancelled, runtime.Actions.Operation(pending.OperationId)!.Status,
            "an expired prepare is cancelled rather than left hanging");

        // Destroying the target cancels its pending operations.
        var doomed = runtime.RegisterHost(new HostDefinition("door.2", HostClass.Terminal,
            Compute: new ComputeComponent(2, 1))).Host!;
        runtime.Authority.Grant(actor, doomed.Id, CapabilityCategory.Control, "machine.door", 60_000,
            GrantSource.Granted, PrincipalId.None);
        var victim = runtime.Actions.Prepare(Request(actor, deck.Id, doomed.Id, "victim-1"), doorAction);
        Check(victim.Succeeded, "an operation targets the doomed host");
        runtime.DestroyHost(doomed.Id, doomed.Generation);
        Equal(ActionStatus.Cancelled, runtime.Actions.Operation(victim.OperationId)!.Status,
            "destroying the target cancels its pending operation");

        // The idempotency window is finite: after eviction a replay is an unknown outcome, never a rerun.
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(2_000);
            runtime.Actions.Execute(Request(actor, deck.Id, door.Id, $"window-{i}"), probe);
        }
        var aged = runtime.Actions.Execute(Request(actor, deck.Id, door.Id, "window-0"), probe);
        Equal(ActionStatus.UnknownOutcome, aged.Status,
            "a replay outside the idempotency window is unknown, not executed again");
    }

    private static ActionRequest Request(PrincipalId actor, HostId executionHost, HostId targetHost, string key) =>
        new(actor, executionHost, targetHost, EntityId.None, ProcessId.None, CorrelationId.None, key);
}
