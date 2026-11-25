using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>
/// SCRIPT-003: a program travels as text. Import compiles to the same IR, never carries rights or a host
/// identifier from another round, and the result becomes runnable only when the trusted side supplies the
/// owner and the execution host.
/// </summary>
internal static class ProgramSourceTests
{
    public static void Run()
    {
        Section("program text");

        // A small source compiles to the IR and back without losing anything.
        const string text = """
            program "Test"
              budget 64
              repeat 2
              {
                invoke net.inspect self
                wait 250
              }
              emit program.test.done
              stop
            """;
        Equal(ProgramStatus.Ok, ProgramSource.TryParse(text, out var parsed, out var error), $"a valid source parses ({error})");
        Check(parsed is not null, "the parser returns a source");
        Equal("Test", parsed!.Name, "the program keeps its name");
        Equal(64, parsed.InstructionBudget, "the budget is part of the source");
        Equal(3, parsed.Steps.Count, "the top level holds the repeat, the emit and the stop");
        Equal(StepKind.Repeat, parsed.Steps[0].Kind, "the repeat is a step");
        Equal(2, parsed.Steps[0].Count, "the repeat keeps its count");
        Equal(2, parsed.Steps[0].Body!.Count, "the repeat holds its body");
        Equal(TargetKind.Self, parsed.Steps[0].Body![0].Target.Kind, "the self target survives the parse");

        var roundTrip = ProgramSource.TryParse(parsed.ToText(), out var again, out var againError);
        Equal(ProgramStatus.Ok, roundTrip, $"the written source parses again ({againError})");
        Equal(parsed.ToText(), again!.ToText(), "writing and parsing is stable");

        // A fixed host reference is text, and it is a reference, not a permission.
        Equal(ProgramStatus.Ok,
            ProgramSource.TryParse("program \"P\"\ninvoke net.inspect #42\nstop", out var fixedHost, out _),
            "a fixed host reference parses");
        Equal(new HostId(42), fixedHost!.Steps[0].Target.Host, "the host identifier is kept");
        Equal(TargetKind.Fixed, fixedHost.Steps[0].Target.Kind, "a fixed reference is not a self reference");

        // Malformed input is refused whole, never partially applied.
        foreach (var (source, label) in new[]
                 {
                     ("", "empty text"),
                     ("program \"P\"", "no steps"),
                     ("program \"P\"\nrepeat 0\n{\nstop\n}", "repeat of zero"),
                     ("program \"P\"\nwait 0\nstop", "wait of zero"),
                     ("program \"P\"\ninvoke\nstop", "invoke without an action"),
                     ("program \"P\"\ninvoke net.inspect nowhere\nstop", "an unknown target token"),
                     ("program \"P\"\nrepeat 2\n{\n", "an unclosed block"),
                     ("program \"P\"\nfly 5\nstop", "an unknown statement"),
                     ("program \"P\"\nbudget 999999\nstop", "a budget above the limit"),
                 })
        {
            Check(ProgramSource.TryParse(source, out _, out _) != ProgramStatus.Ok, $"refused: {label}");
        }

        // The starting library is text as well, and every preset compiles.
        Check(ProgramPresets.All.Count >= 5, "the preset library is not empty");
        foreach (var preset in ProgramPresets.All)
        {
            Equal(ProgramStatus.Ok, ProgramSource.TryParse(preset.ToText(), out var compiled, out var presetError),
                $"preset {preset.Name} compiles ({presetError})");
            Check(compiled!.Steps.Count > 0, $"preset {preset.Name} has steps");
        }

        // A preset runs through the ordinary pipeline: the same rights, costs and limits as a hand-written one.
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var deck = runtime.RegisterHost(new HostDefinition("deck.1", HostClass.Cyberdeck,
            Compute: new ComputeComponent(100, 40), Memory: new MemoryComponent(4))).Host!;
        var actor = runtime.NewPrincipal();
        runtime.Authority.Grant(actor, deck.Id, CapabilityCategory.Execution, null, 600_000, GrantSource.Granted, PrincipalId.None);
        runtime.Authority.Grant(actor, deck.Id, CapabilityCategory.Read, null, 600_000, GrantSource.Granted, PrincipalId.None);
        runtime.Authority.Grant(actor, deck.Id, CapabilityCategory.Discovery, null, 600_000, GrantSource.Granted, PrincipalId.None);

        var inspector = ProgramPresets.Find("Inspector")!;
        var started = runtime.Programs.Start(actor, new ProgramDefinition(inspector.Name, deck.Id, inspector.Steps,
            inspector.InstructionBudget));
        Equal(ProgramStatus.Ok, started.Status, "an imported preset starts");
        runtime.Tick();
        var running = runtime.Programs.Peek(started.Process);
        Check(running is null || running.LastOutcome is "committed" or "ok" or "accepted",
            $"the preset invoked the action as its owner: {running?.LastOutcome}");

        // The same text run by an owner without rights is refused by the pipeline, not by the importer.
        var stranger = runtime.NewPrincipal();
        var denied = runtime.Programs.Start(stranger, new ProgramDefinition(inspector.Name, deck.Id, inspector.Steps,
            inspector.InstructionBudget));
        runtime.Tick();
        var refused = runtime.Programs.Peek(denied.Process);
        Check(refused is null || refused.State == ProgramState.Faulted,
            "a program without rights stops instead of acting");

        // Reusing a name from another round grants nothing: the source carries no identity at all.
        Check(!inspector.ToText().Contains("ckey", StringComparison.OrdinalIgnoreCase),
            "a source carries no player identity");
        Check(!inspector.ToText().Contains("principal", StringComparison.OrdinalIgnoreCase),
            "a source carries no principal");
    }
}
