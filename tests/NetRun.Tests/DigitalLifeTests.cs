using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>
/// LIFE-001 and §13.4/§13.5: one active controlling core per identity, a migration that cannot produce two of
/// them, and a core that survives losing its host only in the ways the model allows.
/// </summary>
internal static class DigitalLifeTests
{
    public static void Run()
    {
        Section("digital life");
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var deck = runtime.RegisterHost(new HostDefinition("deck.1", HostClass.Cyberdeck,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4))).Host!;
        var server = runtime.RegisterHost(new HostDefinition("server.1", HostClass.Server,
            Compute: new ComputeComponent(40, 10), Memory: new MemoryComponent(8))).Host!;
        var dumb = runtime.RegisterHost(new HostDefinition("relay.1", HostClass.Relay)).Host!;
        var actor = runtime.NewPrincipal();
        var identity = runtime.NewIdentity();

        // Bringing a life into existence needs a host that can actually hold a core.
        Equal(LifeStatus.InsufficientResources, runtime.Lives.Create(runtime.NewIdentity(), actor, dumb.Id).Status,
            "a host without compute or memory cannot hold a core");
        var created = runtime.Lives.Create(identity, actor, deck.Id);
        Equal(LifeStatus.Ok, created.Status, "a core is created on a capable host");
        Equal(1UL, created.Core!.ControlGeneration, "a new core starts at generation 1");
        Equal(LifeState.Active, created.Core.State, "a new core is active");
        Equal(LifeStatus.Duplicate, runtime.Lives.Create(identity, actor, deck.Id).Status,
            "one identity has one core, not two");

        // The controlling generation is a fence: only it may act for the life.
        Check(runtime.Lives.Owns(identity, created.Core.ControlGeneration), "the current generation owns the life");
        Check(!runtime.Lives.Owns(identity, 99), "an unknown generation does not own the life");

        // A migration reserves the destination, moves the core and retires the old generation.
        var begun = runtime.Lives.BeginMigration(identity, server.Id);
        Equal(LifeStatus.Ok, begun.Status, "the move begins");
        Check(begun.Core!.Migrating, "the move is pending");
        Equal(deck.Id, begun.Core.Host, "until commit the core still belongs to the source host");
        Equal(LifeStatus.AlreadyMigrating, runtime.Lives.BeginMigration(identity, server.Id).Status,
            "a second move cannot start while one is pending");
        Equal(LifeStatus.StaleGeneration, runtime.Lives.CommitMigration(identity, 99).Status,
            "a commit with the wrong generation is refused");

        var committed = runtime.Lives.CommitMigration(identity, begun.Core.ControlGeneration);
        Equal(LifeStatus.Ok, committed.Status, "the move commits");
        Equal(server.Id, committed.Core!.Host, "the core now lives on the destination");
        Equal(2UL, committed.Core.ControlGeneration, "the commit retired the previous generation");
        Check(!runtime.Lives.Owns(identity, begun.Core.ControlGeneration),
            "the old generation can no longer act for the life");
        Check(runtime.Lives.Owns(identity, committed.Core.ControlGeneration),
            "the new generation owns the life");
        Equal(server.Generation, committed.Core.HostGeneration, "the core carries the destination generation");

        // Cancelling before commit restores the source and returns the reserve.
        var before = runtime.Resources.Snapshot(server.Id)!.BufferMilliCu;
        var pending = runtime.Lives.BeginMigration(identity, deck.Id);
        Equal(LifeStatus.Ok, pending.Status, "a second move begins");
        Equal(before, runtime.Resources.Snapshot(server.Id)!.BufferMilliCu,
            "a pending move reserves the destination without charging the source");
        var cancelled = runtime.Lives.CancelMigration(identity, "test");
        Equal(LifeStatus.Ok, cancelled.Status, "the move is cancelled before commit");
        Equal(server.Id, cancelled.Core!.Host, "the source keeps the life");
        Equal(committed.Core.ControlGeneration, cancelled.Core.ControlGeneration,
            "a cancelled move does not change the controlling generation");

        // A pending move whose destination dies is cancelled; the source continues.
        var doomedDestination = runtime.RegisterHost(new HostDefinition("server.2", HostClass.Server,
            Compute: new ComputeComponent(10, 2), Memory: new MemoryComponent(2))).Host!;
        runtime.Lives.BeginMigration(identity, doomedDestination.Id);
        runtime.DestroyHost(doomedDestination.Id, doomedDestination.Generation);
        var afterDestinationDeath = runtime.Lives.Get(identity)!;
        Equal(server.Id, afterDestinationDeath.Host, "a lost destination leaves the life where it was");
        Check(!afterDestinationDeath.Migrating, "the failed move is no longer pending");

        // A pending move whose source dies completes: the state was already on its way.
        var rescue = runtime.RegisterHost(new HostDefinition("server.3", HostClass.Server,
            Compute: new ComputeComponent(10, 2), Memory: new MemoryComponent(2))).Host!;
        runtime.Lives.BeginMigration(identity, rescue.Id);
        runtime.DestroyHost(server.Id, server.Generation);
        var afterSourceDeath = runtime.Lives.Get(identity)!;
        Equal(rescue.Id, afterSourceDeath.Host, "a lost source hands the life to the destination");
        Equal(LifeState.Active, afterSourceDeath.State, "the life survives its source host");
        Check(!afterSourceDeath.Migrating, "the rescue is not left pending");

        // Integrity degrades the state instead of killing the character outright.
        Equal(LifeState.Degraded, runtime.Lives.ApplyIntegrity(identity, -50).Core!.State,
            "damage degrades the life");
        Equal(LifeState.Critical, runtime.Lives.ApplyIntegrity(identity, -21).Core!.State,
            "more damage makes it critical");
        Equal(LifeState.Stasis, runtime.Lives.ApplyIntegrity(identity, -1000).Core!.State,
            "exhausted integrity leaves the life in stasis, not deleted");
        Equal(LifeState.Active, runtime.Lives.ApplyIntegrity(identity, 100).Core!.State,
            "a repaired core is active again");

        // Losing the host without a pending move costs the core, and a restore brings the identity back on a
        // healthy host with a new generation — never with the resources that were spent.
        var hostless = runtime.RegisterHost(new HostDefinition("deck.2", HostClass.Cyberdeck,
            Compute: new ComputeComponent(10, 2), Memory: new MemoryComponent(2))).Host!;
        var lostIdentity = runtime.NewIdentity();
        runtime.Lives.Create(lostIdentity, actor, hostless.Id);
        runtime.DestroyHost(hostless.Id, hostless.Generation);
        Equal(LifeState.CoreLost, runtime.Lives.Get(lostIdentity)!.State, "losing the host loses the core");
        Equal(LifeStatus.InsufficientResources, runtime.Lives.Restore(lostIdentity, dumb.Id, 10).Status,
            "a host that cannot hold a core cannot host a restore");
        var restored = runtime.Lives.Restore(lostIdentity, rescue.Id, 50);
        Equal(LifeStatus.Ok, restored.Status, "a lost core can be restored on a healthy host");
        Equal(LifeState.Degraded, restored.Core!.State, "a restore comes back degraded, not at full strength");
        Check(restored.Core.ControlGeneration > 1, "a restore issues a new controlling generation");

        // A move that is never confirmed expires and leaves the source in charge.
        var expiring = runtime.Lives.BeginMigration(identity, deck.Id);
        Equal(LifeStatus.Ok, expiring.Status, "a third move begins");
        clock.Advance(31_000);
        runtime.Tick();
        var timedOut = runtime.Lives.Get(identity)!;
        Check(!timedOut.Migrating, "an unconfirmed move expires");
        Equal(rescue.Id, timedOut.Host, "the committed host stays in charge");
        Equal(LifeStatus.InvalidRequest, runtime.Lives.Restore(lostIdentity, rescue.Id, 10).Status,
            "a living core cannot be restored over itself");
        Equal(LifeState.Stasis, runtime.Lives.ApplyIntegrity(lostIdentity, -1_000).Core!.State,
            "exhausting a restored core leaves it in stasis, not deleted");
    }
}
