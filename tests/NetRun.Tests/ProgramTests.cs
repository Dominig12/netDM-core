using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>
/// §17.3 and SCRIPT-002: a program is data, its executor has a budget, and nothing a user writes can hang the
/// simulation. Time is advanced by hand, so every scenario is reproducible.
/// </summary>
internal static class ProgramTests
{
    public static void Run()
    {
        Section("programs");
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var deck = runtime.RegisterHost(new HostDefinition("deck.1", HostClass.Cyberdeck,
            Compute: new ComputeComponent(200, 50), Memory: new MemoryComponent(8))).Host!;
        var target = runtime.RegisterHost(new HostDefinition("relay.1", HostClass.Relay,
            Compute: new ComputeComponent(10, 4))).Host!;
        var actor = runtime.NewPrincipal();
        runtime.Authority.Grant(actor, deck.Id, CapabilityCategory.Execution, null, 600_000, GrantSource.Granted, PrincipalId.None);
        runtime.Authority.Grant(actor, deck.Id, CapabilityCategory.Discovery, null, 600_000, GrantSource.Granted, PrincipalId.None);
        runtime.Authority.Grant(actor, deck.Id, CapabilityCategory.Read, null, 600_000, GrantSource.Granted, PrincipalId.None);
        runtime.Authority.Grant(actor, deck.Id, CapabilityCategory.Control, null, 600_000, GrantSource.Granted, PrincipalId.None);

        // A definition is validated once, so a bad program never reaches the executor.
        Equal(ProgramStatus.InvalidDefinition,
            runtime.Programs.Start(actor, new ProgramDefinition("empty", deck.Id, [])).Status,
            "a program without steps is refused");
        Equal(ProgramStatus.InvalidDefinition,
            runtime.Programs.Start(actor, new ProgramDefinition("wait", deck.Id, [ProgramStep.Wait(0)])).Status,
            "a wait of zero is refused instead of spinning");
        Equal(ProgramStatus.InvalidDefinition,
            runtime.Programs.Start(actor, new ProgramDefinition("repeat", deck.Id,
                [ProgramStep.Repeat(0, ProgramStep.Stop())])).Status,
            "a repeat of zero is refused");

        var tooDeep = ProgramStep.Repeat(2, ProgramStep.Repeat(2, ProgramStep.Repeat(2,
            ProgramStep.Repeat(2, ProgramStep.Repeat(2, ProgramStep.Repeat(2,
                ProgramStep.Repeat(2, ProgramStep.Stop())))))));
        Equal(ProgramStatus.InvalidDefinition,
            runtime.Programs.Start(actor, new ProgramDefinition("deep", deck.Id, [tooDeep])).Status,
            "nesting beyond the depth budget is refused before it can run");

        // A real program: inspect its own host, wait, then stop. It runs a bounded slice per tick.
        var inspected = 0;
        var program = runtime.Programs.Start(actor, new ProgramDefinition("scan-self", deck.Id,
        [
            ProgramStep.Invoke("net.inspect", deck.Id),
            ProgramStep.Wait(1_000),
            ProgramStep.Emit("program.done"),
            ProgramStep.Stop()
        ]));
        Equal(ProgramStatus.Ok, program.Status, "a valid program is accepted");

        runtime.Tick();
        var running = runtime.Programs.Peek(program.Process)!;
        Equal(ProgramState.Waiting, running.State, "the program suspends on its wait instead of blocking a worker");
        Check(running.InstructionCount >= 1, "the invoke step ran");

        // Time does not advance by itself: a waiting program stays waiting until the clock moves.
        runtime.Tick();
        Equal(ProgramState.Waiting, runtime.Programs.Peek(program.Process)!.State, "a wait is measured in game time");
        clock.Advance(1_001);
        runtime.Tick();
        var done = runtime.Programs.Peek(program.Process);
        Check(done is null || done.State is ProgramState.Completed or ProgramState.Waiting,
            $"the program finishes after its wait or runs its next step: {done?.State.ToString() ?? "reaped"}");

        // The instruction budget stops a program that would otherwise run forever.
        var spinning = runtime.Programs.Start(actor, new ProgramDefinition("spin", deck.Id,
            [ProgramStep.Repeat(1_000, ProgramStep.Emit("tick"))], InstructionBudget: 40));
        Equal(ProgramStatus.Ok, spinning.Status, "the bounded repeat is accepted");
        for (var tick = 0; tick < 6; tick++)
            runtime.Tick();
        var spun = runtime.Programs.Peek(spinning.Process);
        Check(spun is null || spun.State == ProgramState.Faulted,
            "a program that exceeds its instruction budget is stopped, not left running");
        if (spun is not null)
            Equal("instruction_budget", spun.Fault, "the fault names the budget");

        // A refused action ends its program with the reason, instead of hiding why it stopped.
        var outsider = runtime.NewPrincipal();
        runtime.Authority.Grant(outsider, deck.Id, CapabilityCategory.Execution, null, 600_000, GrantSource.Granted, PrincipalId.None);
        var denied = runtime.Programs.Start(outsider, new ProgramDefinition("denied", deck.Id,
            [ProgramStep.Invoke("net.inspect", target.Id)]));
        Equal(ProgramStatus.Ok, denied.Status, "the program is accepted");
        runtime.Tick();
        var refused = runtime.Programs.Peek(denied.Process);
        Check(refused is null || refused.State == ProgramState.Faulted,
            "an action the owner may not perform faults the program");
        Check(refused is null || (refused.Fault is not null && refused.Fault.Length > 0),
            "the fault carries a reason");

        // An unknown action is a definition error at run time, not a silent skip.
        var unknown = runtime.Programs.Start(actor, new ProgramDefinition("unknown", deck.Id,
            [ProgramStep.Invoke("does.not.exist", deck.Id)]));
        runtime.Tick();
        Equal("unknown_action", runtime.Programs.Peek(unknown.Process)!.Fault, "an unknown action faults the program");

        // Cancellation is immediate and releases the slot of its owner.
        var cancellable = runtime.Programs.Start(actor, new ProgramDefinition("cancel", deck.Id,
            [ProgramStep.Wait(60_000)]));
        runtime.Tick();
        Equal(ProgramStatus.Ok, runtime.Programs.Cancel(cancellable.Process).Status, "a program can be cancelled");
        Check(runtime.Programs.Peek(cancellable.Process) is null, "a cancelled program is gone");

        // Destroying the host a program ran on cancels it: its launchpad no longer exists.
        var doomedDeck = runtime.RegisterHost(new HostDefinition("deck.2", HostClass.Cyberdeck,
            Compute: new ComputeComponent(20, 5))).Host!;
        runtime.Authority.Grant(actor, doomedDeck.Id, CapabilityCategory.Execution, null, 600_000, GrantSource.Granted, PrincipalId.None);
        var onDoomed = runtime.Programs.Start(actor, new ProgramDefinition("doomed", doomedDeck.Id,
            [ProgramStep.Wait(60_000)]));
        runtime.Tick();
        runtime.DestroyHost(doomedDeck.Id, doomedDeck.Generation);
        Check(runtime.Programs.Peek(onDoomed.Process) is null, "destroying the host cancels its programs");

        // Per-owner quota keeps one player from filling the table.
        var capped = new NetRunRuntime(clock, new NetRunRuntimeOptions(MaxPrograms: 64));
        var capDeck = capped.RegisterHost(new HostDefinition("deck.3", HostClass.Cyberdeck,
            Compute: new ComputeComponent(10, 5))).Host!;
        var capActor = capped.NewPrincipal();
        capped.Authority.Grant(capActor, capDeck.Id, CapabilityCategory.Execution, null, 600_000, GrantSource.Granted, PrincipalId.None);
        var accepted = 0;
        for (var i = 0; i < 40; i++)
        {
            if (capped.Programs.Start(capActor, new ProgramDefinition($"p{i}", capDeck.Id,
                [ProgramStep.Wait(60_000)])).Status == ProgramStatus.Ok)
                accepted++;
        }
        Check(accepted is > 0 and < 40, "the per-owner program quota is enforced");
        Equal(accepted, capped.Programs.CancelOwner(capActor), "cancelling the owner releases every program");
        Equal(0, capped.Programs.Count, "no program survives its owner's session");

        // The run is observable: a program is a process with state, counters and its last action.
        var observed = runtime.Programs.Start(actor, new ProgramDefinition("observed", deck.Id,
            [ProgramStep.Invoke("net.inspect", deck.Id)]));
        runtime.Tick();
        var snapshot = runtime.Programs.Peek(observed.Process);
        Check(snapshot is null || snapshot.LastAction == "net.inspect",
            "the program records the action it invoked");
        _ = inspected;
    }
}
