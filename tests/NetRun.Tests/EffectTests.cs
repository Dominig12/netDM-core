using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>PRIM-003: effects are bounded by their server-side definition and stacking is decided by kind.</summary>
internal static class EffectTests
{
    public static void Run()
    {
        Section("effects");
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var target = runtime.RegisterHost(new HostDefinition("server.1", HostClass.Server,
            Compute: new ComputeComponent(1, 1))).Host!;
        var source = runtime.NewPrincipal();

        // The definition, not the caller, decides what is possible.
        Equal(EffectStatus.Applied,
            runtime.Effects.Apply(EffectKind.Damage, target.Id, source, 40, 0, "test").Status,
            "instant damage at the declared ceiling is applied");
        Equal(EffectStatus.MagnitudeOutOfRange,
            runtime.Effects.Apply(EffectKind.Damage, target.Id, source, 41, 0, "test").Status,
            "damage above the declared ceiling is refused, not clamped");
        Equal(EffectStatus.DurationOutOfRange,
            runtime.Effects.Apply(EffectKind.Cloak, target.Id, source, 1, 15_001, "test").Status,
            "a duration above the declared ceiling is refused");
        Equal(EffectStatus.UnknownKind,
            runtime.Effects.Apply(EffectKind.Corrupt, target.Id, source, 1, 100, "test").Status,
            "an effect without a definition cannot be applied");

        // An instantaneous effect leaves nothing behind.
        Equal(0, runtime.Effects.On(target.Id, 8).Length, "instant effects keep no instance");
        Equal(EffectStatus.Applied,
            runtime.Effects.Apply(EffectKind.Cloak, target.Id, source, 1, 5_000, "program-a").Status,
            "a lasting effect is applied");
        Equal(1, runtime.Effects.On(target.Id, 8).Length, "a lasting effect is stored once");

        // Refresh extends the existing instance instead of making a second one.
        var refreshed = runtime.Effects.Apply(EffectKind.Cloak, target.Id, source, 1, 9_000, "program-a");
        Equal(EffectStatus.Refreshed, refreshed.Status, "a refreshable effect is refreshed");
        Equal(1, runtime.Effects.On(target.Id, 8).Length, "refreshing does not create a second instance");
        Equal(9_000L, refreshed.Effect!.ExpiresAtMilliseconds, "the refresh extended the expiry");

        // Duration is measured in game time.
        clock.Advance(9_001);
        Equal(1, runtime.Effects.Tick(clock.NowMilliseconds), "the tick expires the effect");
        Equal(0, runtime.Effects.On(target.Id, 8).Length, "an expired effect is gone");

        // A definition that is not removable refuses removal; one that is removable honours it.
        Equal(EffectStatus.UnknownEffect, runtime.Effects.Remove(new EffectInstanceId(4_000)),
            "removing an unknown effect is refused");

        // An exclusive definition refuses a second live instance.
        var exclusiveRuntime = new NetRunRuntime(clock, new NetRunRuntimeOptions(RegisterMvpEffects: false));
        exclusiveRuntime.Effects.Register(new EffectDefinition(EffectKind.Jam, 1, 5_000, 2, 3, true,
            EffectStacking.Exclusive));
        var jamTarget = exclusiveRuntime.RegisterHost(new HostDefinition("relay.1", HostClass.Relay)).Host!;
        var jammer = exclusiveRuntime.NewPrincipal();
        Equal(EffectStatus.Applied,
            exclusiveRuntime.Effects.Apply(EffectKind.Jam, jamTarget.Id, jammer, 1, 1_000, "jam-a").Status,
            "the first exclusive effect is applied");
        Equal(EffectStatus.Rejected,
            exclusiveRuntime.Effects.Apply(EffectKind.Jam, jamTarget.Id, jammer, 1, 1_000, "jam-b").Status,
            "a second exclusive effect is refused");

        // Stacking respects the declared limit.
        exclusiveRuntime.Effects.Register(new EffectDefinition(EffectKind.Drain, 5, 2_000, 1, 1, true,
            EffectStacking.Stack, 2));
        Equal(EffectStatus.Applied,
            exclusiveRuntime.Effects.Apply(EffectKind.Drain, jamTarget.Id, jammer, 1, 1_000, "drain").Status,
            "the first stack is applied");
        Equal(EffectStatus.Stacked,
            exclusiveRuntime.Effects.Apply(EffectKind.Drain, jamTarget.Id, jammer, 1, 1_000, "drain").Status,
            "the second stack is applied");
        Equal(EffectStatus.Rejected,
            exclusiveRuntime.Effects.Apply(EffectKind.Drain, jamTarget.Id, jammer, 1, 1_000, "drain").Status,
            "a stack above the declared limit is refused");

        // Effects of a stopped program disappear only if their lifecycle says so.
        var byOrigin = new NetRunRuntime(clock, new NetRunRuntimeOptions(RegisterMvpEffects: false));
        byOrigin.Effects.Register(new EffectDefinition(EffectKind.Mark, 1, 60_000, 1, 1, true,
            EffectStacking.Independent));
        byOrigin.Effects.Register(new EffectDefinition(EffectKind.Shield, 5, 10_000, 0, 1, false,
            EffectStacking.Independent));
        var marked = byOrigin.RegisterHost(new HostDefinition("apc.9", HostClass.AccessPoint)).Host!;
        var marker = byOrigin.NewPrincipal();
        byOrigin.Effects.Apply(EffectKind.Mark, marked.Id, marker, 1, 30_000, "program-x");
        byOrigin.Effects.Apply(EffectKind.Shield, marked.Id, marker, 5, 10_000, "program-x");
        Equal(2, byOrigin.Effects.On(marked.Id, 8).Length, "both effects are live");
        Equal(1, byOrigin.Effects.RemoveByOrigin("program-x"),
            "only the removable effect of the stopped program is removed");
        Equal(1, byOrigin.Effects.On(marked.Id, 8).Length, "the persisting effect stays");

        // An effect applied through the pipeline goes through the registry, never around it.
        var deck = runtime.RegisterHost(new HostDefinition("deck.1", HostClass.Cyberdeck,
            Compute: new ComputeComponent(4, 2))).Host!;
        var runner = runtime.NewPrincipal();
        runtime.Authority.Grant(runner, deck.Id, CapabilityCategory.Execution, null, 60_000,
            GrantSource.Granted, PrincipalId.None);
        runtime.Authority.Grant(runner, target.Id, CapabilityCategory.Execution, null, 60_000,
            GrantSource.Granted, PrincipalId.None);
        var cloak = new CloakAction(1, 5_000);
        runtime.Actions.Register(cloak);
        var request = new ActionRequest(runner, deck.Id, target.Id, EntityId.None, ProcessId.None,
            CorrelationId.None, "cloak-1");
        Equal(ActionStatus.Committed, runtime.Actions.Execute(request, cloak).Status,
            "the pipeline commits the effect action");
        Equal(1, runtime.Effects.On(target.Id, 8).Length, "the effect reached the registry");

        // An out-of-range effect requested at commit fails the operation and returns its reserves.
        var tooStrong = new CloakAction(9, 5_000, "effect.cloak.strong");
        runtime.Actions.Register(tooStrong);
        var buffer = runtime.Resources.Snapshot(deck.Id)!.BufferMilliCu;
        var refused = runtime.Actions.Execute(
            new ActionRequest(runner, deck.Id, target.Id, EntityId.None, ProcessId.None, CorrelationId.None, "cloak-2"),
            tooStrong);
        Equal(ActionStatus.Rejected, refused.Status, "an effect outside its definition refuses the commit");
        Equal(buffer, runtime.Resources.Snapshot(deck.Id)!.BufferMilliCu,
            "the refused effect action returned its reserved compute");

        // Destroying a host clears the effects that lived on it.
        runtime.DestroyHost(target.Id, target.Generation);
        Equal(0, runtime.Effects.On(target.Id, 8).Length, "destroying a host removes its effects");
    }
}
