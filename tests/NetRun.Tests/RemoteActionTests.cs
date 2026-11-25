using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>
/// A remote action whose authoritative change happens somewhere else. The transport is a fake, so the
/// test controls exactly what admission says and what the target later reports.
/// </summary>
internal sealed class RemoteProbeAction(string id = "remote.probe", int computeCost = 2) : IRemoteGameAction
{
    public ActionDescriptor Descriptor { get; } = new(id, 1, ActionKind.Command, ActionCategory.Connectivity,
        CapabilityCategory.Control, "remote.probe", computeCost, 0, 500, 6, "Удалённое действие",
        "Отправляет команду через транспорт и ждёт подтверждения цели.", ServiceKey: "machine.door");

    public string ContentDigest => "remote.probe";

    public ActionValidation Validate(in ActionContext context, INetRunWorldReader world) => ActionValidation.Pass;

    public ActionExecution Commit(in ActionContext context, INetRunMutationScope scope) =>
        new(ActionStatus.Rejected, "remote_only");

    public RemoteDispatch Dispatch(in ActionContext context, INetRunDispatchScope scope) =>
        scope.Send("machine.door", "door.a", "set_state", "k", "{\"press\":\"p\",\"state\":\"open\"}");
}

/// <summary>Records what was sent and answers with a scripted admission outcome.</summary>
internal sealed class FakeTransport(ActionStatus admission = ActionStatus.Accepted) : INetRunTransport
{
    public int Sends { get; private set; }

    public string? LastArguments { get; private set; }

    public RemoteDispatch Send(in ActionContext context, string serviceType, string instance, string command,
        string idempotencyKey, string argumentsJson)
    {
        Sends++;
        LastArguments = argumentsJson;
        return admission == ActionStatus.Accepted
            ? new RemoteDispatch(ActionStatus.Accepted)
            : RemoteDispatch.Rejected(admission, admission.ToString());
    }
}

internal static class RemoteActionTests
{
    public static void Run()
    {
        Section("remote actions");
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var deck = runtime.RegisterHost(new HostDefinition("deck.1", HostClass.Cyberdeck,
            Compute: new ComputeComponent(50, 10), Memory: new MemoryComponent(4))).Host!;
        var door = runtime.RegisterHost(new HostDefinition("door.1", HostClass.Terminal,
            Compute: new ComputeComponent(2, 1),
            Services: new ServicesComponent(new Dictionary<string, string> { ["machine.door"] = "door.a" }))).Host!;
        var actor = runtime.NewPrincipal();
        runtime.Authority.Grant(actor, deck.Id, CapabilityCategory.Execution, null, 120_000, GrantSource.Granted, PrincipalId.None);
        runtime.Authority.Grant(actor, door.Id, CapabilityCategory.Control, "machine.door", 120_000,
            GrantSource.Granted, PrincipalId.None);

        var action = new RemoteProbeAction();
        runtime.Actions.Register(action);

        // Without a transport the domain refuses instead of pretending the command was sent.
        var noTransport = runtime.Actions.Execute(Request(actor, deck.Id, door.Id, "rt-0"), action);
        Equal(ActionStatus.InvalidState, noTransport.Status, "a remote action without a transport does not commit");

        var accepted = new FakeTransport();
        runtime.Actions.AttachTransport(accepted);
        var bufferBefore = runtime.Resources.Snapshot(deck.Id)!.BufferMilliCu;

        var sent = runtime.Actions.Execute(Request(actor, deck.Id, door.Id, "rt-1"), action);
        Equal(ActionStatus.Accepted, sent.Status, "admission is reported as accepted, not as committed");
        Equal(1, accepted.Sends, "the command reached the transport once");
        Check(accepted.LastArguments!.Contains("\"state\":\"open\"", StringComparison.Ordinal),
            "the transport received the arguments of the action");
        Equal(bufferBefore - 2_000L, runtime.Resources.Snapshot(deck.Id)!.BufferMilliCu,
            "the compute of an accepted remote action is spent");
        Equal(ActionStatus.Accepted, runtime.Actions.Operation(sent.OperationId)!.Status,
            "the operation stays accepted until the target answers");

        // Acceptance is not execution: the target's report finishes the operation.
        var completed = runtime.Actions.Complete(sent.OperationId, ActionStatus.Committed);
        Equal(ActionStatus.Committed, completed.Status, "the target's confirmation commits the operation");
        Equal(ActionStatus.Committed, runtime.Actions.Operation(sent.OperationId)!.Status,
            "the operation reports its final state");
        Equal(ActionStatus.InvalidState,
            runtime.Actions.Complete(sent.OperationId, ActionStatus.Committed).Status,
            "a second confirmation of the same operation changes nothing");

        // The cooldown started at dispatch, so a second attempt inside it is refused.
        Equal(ActionStatus.Cooldown, runtime.Actions.Execute(Request(actor, deck.Id, door.Id, "rt-2"), action).Status,
            "the cooldown of a remote action starts at dispatch");
        clock.Advance(501);

        // A refused admission releases the reserve: nothing was sent, nothing is spent.
        var refusing = new FakeTransport(ActionStatus.Forbidden);
        runtime.Actions.AttachTransport(refusing);
        var beforeRefusal = runtime.Resources.Snapshot(deck.Id)!.BufferMilliCu;
        var refused = runtime.Actions.Execute(Request(actor, deck.Id, door.Id, "rt-3"), action);
        Equal(ActionStatus.Forbidden, refused.Status, "a refused admission is reported as such");
        Equal(beforeRefusal, runtime.Resources.Snapshot(deck.Id)!.BufferMilliCu,
            "a refused admission returns the reserved compute");

        // An answer that never arrives becomes an unknown outcome and is never repeated.
        runtime.Actions.AttachTransport(accepted);
        clock.Advance(1_000);
        var pending = runtime.Actions.Execute(Request(actor, deck.Id, door.Id, "rt-4"), action);
        Equal(ActionStatus.Accepted, pending.Status, "the operation is dispatched");
        clock.Advance(31_000);
        var tick = runtime.Tick();
        Check(tick.ExpiredOperations >= 1, "the tick expires the unanswered remote operation");
        Equal(ActionStatus.UnknownOutcome, runtime.Actions.Operation(pending.OperationId)!.Status,
            "a lost answer is an unknown outcome, not a success and not a repeat");

        // The real door action refuses to commit locally: only the door's own handler may move it.
        var doorAction = runtime.Actions.GetAction("device.door.open")!;
        clock.Advance(2_000);
        var doorSent = runtime.Actions.Execute(Request(actor, deck.Id, door.Id, "rt-door-1"), doorAction);
        Equal(ActionStatus.Accepted, doorSent.Status, "the door action is dispatched over the transport");
        Check(accepted.LastArguments!.Contains("\"press\":", StringComparison.Ordinal),
            "the door arguments carry a press identifier");

        // A host that publishes no door service cannot be a door target.
        var bare = runtime.RegisterHost(new HostDefinition("bare.1", HostClass.Terminal,
            Compute: new ComputeComponent(1, 1))).Host!;
        runtime.Authority.Grant(actor, bare.Id, CapabilityCategory.Control, "machine.door", 120_000,
            GrantSource.Granted, PrincipalId.None);
        Equal(ActionStatus.InvalidState, runtime.Actions.Execute(Request(actor, deck.Id, bare.Id, "rt-door-2"), doorAction).Status,
            "a host without the service is refused before anything is sent");
    }

    private static ActionRequest Request(PrincipalId actor, HostId executionHost, HostId targetHost, string key) =>
        new(actor, executionHost, targetHost, EntityId.None, ProcessId.None, CorrelationId.None, key);
}
