using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>
/// §9.3/§9.4 from the defender's side: what a guarded host does with an intrusion once it has noticed one.
/// The attempt itself is unchanged — these tests are about the trace the guard gets, the alarm the host
/// raises when its guard is torn down, and the alarm a closed host raises at a stranger.
/// </summary>
internal static class DefenderTests
{
    /// <summary>A strain of ICE that defends a host and never spreads, as the content deploys it.</summary>
    private static AgentDefinition Ice() => new("test.ice", AgentKind.Ice,
        ComputeCu: 1, MemorySlots: 0, SpreadIntervalMilliseconds: 60_000, MaxGeneration: 0,
        MaxCopiesPerHost: 4, IntegrityLossPerHop: 0);

    private static AgentDefinition Virus() => new("test.virus", AgentKind.Virus,
        ComputeCu: 1, MemorySlots: 0, SpreadIntervalMilliseconds: 60_000, MaxGeneration: 2,
        MaxCopiesPerHost: 4, IntegrityLossPerHop: 0);

    public static void Run()
    {
        Section("defender: trace, IDS, purge and a stranger at a closed host");
        TraceReachesTheGuard();
        IdsNoticesAQuietAttempt();
        PurgeIsNoticed();
        ClosedHostNoticesAStranger();
    }

    /// <summary>
    /// §9.4: a guard that noticed a loud attempt learns the host it came from. The intruder learns nothing about
    /// itself, a principal with no guard on the target is told nothing, and a target without guards has nobody
    /// to tell.
    /// </summary>
    private static void TraceReachesTheGuard()
    {
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var deck = runtime.RegisterHost(new HostDefinition("deck.trace", HostClass.Cyberdeck,
            Compute: new ComputeComponent(60, 20), Memory: new MemoryComponent(4))).Host!;
        var target = runtime.RegisterHost(new HostDefinition("target.trace", HostClass.Server,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4),
            Security: new SecurityComponent(SecurityProfile.Standard, 4, 2))).Host!;
        var bare = runtime.RegisterHost(new HostDefinition("bare.trace", HostClass.Server,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4),
            Security: new SecurityComponent(SecurityProfile.Standard, 4, 2))).Host!;

        var attacker = runtime.NewPrincipal();
        var defender = runtime.NewPrincipal();
        var bystander = runtime.NewPrincipal();
        GrantBreach(runtime, attacker, deck.Id, target.Id);
        GrantBreach(runtime, attacker, deck.Id, bare.Id);
        Check(runtime.Agents.Deploy(defender, target.Id, Ice(), 0, 100).Succeeded,
            "the defender plants ICE on the target");

        var forced = runtime.Actions.GetAction("security.breach.forced")!;
        var attempt = runtime.Actions.Execute(new ActionRequest(attacker, deck.Id, target.Id,
            EntityId.None, ProcessId.None, CorrelationId.None, "trace-forced"), forced);
        Equal(ActionStatus.Committed, attempt.Status, $"the forced attempt runs: {attempt.Reason}");

        var trace = runtime.Knowledge.Lookup(defender, deck.Id);
        Check(trace is not null, "the guard learns the host the attempt came from");
        Equal(KnowledgeLevel.Identified, trace!.Level, "a loud attempt is traced precisely, not just placed");
        Equal(KnowledgeLevel.Unknown, runtime.Knowledge.Level(attacker, deck.Id),
            "the intruder is not handed a record about itself");
        Equal(KnowledgeLevel.Unknown, runtime.Knowledge.Level(bystander, deck.Id),
            "an observer with no guard on the target learns nothing");

        // The break-in is the access: the intruder may now look at what it broke into, not only drive it.
        Equal(AuthorizationStatus.Ok, runtime.Authority.Authorize(attacker, target.Id,
            CapabilityCategory.Read, null, SecurityProfile.Standard).Status,
            "a host that was broken into can be read");

        // A host without a guard raises its own alarm but has nobody to tell.
        var otherDeck = runtime.RegisterHost(new HostDefinition("deck.bare", HostClass.Cyberdeck,
            Compute: new ComputeComponent(60, 20), Memory: new MemoryComponent(4))).Host!;
        GrantBreach(runtime, attacker, otherDeck.Id, bare.Id);
        // One breach per principal per cooldown group: the second attempt has to wait its turn.
        clock.Advance(5_000);
        var bareAttempt = runtime.Actions.Execute(new ActionRequest(attacker, otherDeck.Id, bare.Id,
            EntityId.None, ProcessId.None, CorrelationId.None, "trace-bare"), forced);
        Equal(ActionStatus.Committed, bareAttempt.Status, "the attempt on the unguarded host runs");
        Check(runtime.Alerts.Level(bare.Id) >= HostAlertLevel.Alert, "an unguarded host still raises its own alarm");
        Equal(KnowledgeLevel.Unknown, runtime.Knowledge.Level(defender, otherDeck.Id),
            "no guard on the target means no trace: nobody there is told where it came from");
    }

    /// <summary>
    /// §9.3: an IDS does not stop the attempt, it makes a quiet one noticed. Without one, the same stealth
    /// attempt on the same posture passes unseen and leaves no alarm behind.
    /// </summary>
    private static void IdsNoticesAQuietAttempt()
    {
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var deck = runtime.RegisterHost(new HostDefinition("deck.ids", HostClass.Cyberdeck,
            Compute: new ComputeComponent(60, 20), Memory: new MemoryComponent(4))).Host!;
        var watched = runtime.RegisterHost(new HostDefinition("watched.ids", HostClass.Server,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4),
            Security: new SecurityComponent(SecurityProfile.Standard, 4, 2, IntrusionDetection: true))).Host!;
        var quiet = runtime.RegisterHost(new HostDefinition("quiet.ids", HostClass.Server,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4),
            Security: new SecurityComponent(SecurityProfile.Standard, 4, 2))).Host!;
        var attacker = runtime.NewPrincipal();
        GrantBreach(runtime, attacker, deck.Id, watched.Id);
        GrantBreach(runtime, attacker, deck.Id, quiet.Id);

        var stealth = runtime.Actions.GetAction("security.breach.stealth")!;
        var onWatched = runtime.Actions.Execute(new ActionRequest(attacker, deck.Id, watched.Id,
            EntityId.None, ProcessId.None, CorrelationId.None, "ids-watched"), stealth);
        Equal(ActionStatus.Committed, onWatched.Status, $"the quiet attempt on the watched host runs: {onWatched.Reason}");
        Check((onWatched.Result ?? "").Contains("\"detected\":true", StringComparison.Ordinal),
            "the IDS made the quiet attempt noticed");
        Check(runtime.Alerts.Level(watched.Id) >= HostAlertLevel.Suspicious,
            $"the watched host raised its own alert ({runtime.Alerts.Level(watched.Id)})");

        // One breach per principal per cooldown group, whatever the target.
        clock.Advance(5_000);
        var onQuiet = runtime.Actions.Execute(new ActionRequest(attacker, deck.Id, quiet.Id,
            EntityId.None, ProcessId.None, CorrelationId.None, "ids-quiet"), stealth);
        Equal(ActionStatus.Committed, onQuiet.Status, $"the quiet attempt on the bare host runs: {onQuiet.Reason}");
        Check((onQuiet.Result ?? "").Contains("\"detected\":false", StringComparison.Ordinal),
            "without an IDS the same attempt is not noticed");
        Equal(HostAlertLevel.Normal, runtime.Alerts.Level(quiet.Id), "an unnoticed attempt raises no alarm");
    }

    /// <summary>
    /// §9.3: a host notices its guard being torn down. The same purge performed by the guard's own owner is
    /// maintenance and raises nothing.
    /// </summary>
    private static void PurgeIsNoticed()
    {
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var deck = runtime.RegisterHost(new HostDefinition("deck.purge", HostClass.Cyberdeck,
            Compute: new ComputeComponent(60, 20), Memory: new MemoryComponent(4))).Host!;
        var guarded = runtime.RegisterHost(new HostDefinition("guarded.purge", HostClass.Server,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4),
            Security: new SecurityComponent(SecurityProfile.Standard, 4, 2))).Host!;
        var owner = runtime.NewPrincipal();
        var intruder = runtime.NewPrincipal();
        Check(runtime.Agents.Deploy(owner, guarded.Id, Ice(), 0, 100).Succeeded, "the owner plants ICE");
        Check(runtime.Agents.Deploy(owner, guarded.Id, Virus(), 3, 100).Succeeded, "the owner plants a virus too");

        // The owner repairs its own host: taking its own strain away is not an intrusion.
        GrantWork(runtime, owner, deck.Id, guarded.Id);
        runtime.Knowledge.Remember(owner, guarded.Id, KnowledgeLevel.Observed, 100);
        var purge = runtime.Actions.GetAction("net.purge")!;
        var ownRepair = runtime.Actions.Execute(new ActionRequest(owner, deck.Id, guarded.Id,
            EntityId.None, ProcessId.None, CorrelationId.None, "purge-own"), purge);
        Equal(ActionStatus.Committed, ownRepair.Status, $"the owner purges its own host: {ownRepair.Reason}");
        Equal(HostAlertLevel.Normal, runtime.Alerts.Level(guarded.Id),
            "a repair by the guard's owner does not raise the alarm");

        // Somebody else clearing the guard is an intrusion the host can see.
        Check(runtime.Agents.Deploy(owner, guarded.Id, Ice(), 0, 100).Succeeded, "the guard is back on duty");
        GrantWork(runtime, intruder, deck.Id, guarded.Id);
        runtime.Knowledge.Remember(intruder, guarded.Id, KnowledgeLevel.Observed, 100);
        var hostile = runtime.Actions.Execute(new ActionRequest(intruder, deck.Id, guarded.Id,
            EntityId.None, ProcessId.None, CorrelationId.None, "purge-hostile"), purge);
        Equal(ActionStatus.Committed, hostile.Status, $"the intruder purges the host: {hostile.Reason}");
        Check(runtime.Alerts.Level(guarded.Id) >= HostAlertLevel.Alert,
            $"losing the guard raises the host's alarm ({runtime.Alerts.Level(guarded.Id)})");
        Check((hostile.Result ?? "").Contains("\"removed\":1", StringComparison.Ordinal),
            "the answer names what was actually removed, not what was asked");
    }

    /// <summary>
    /// §9.3/§9.4: a closed host notices a stranger arriving, raises its own alarm and lets its guards learn where
    /// the arrival came from. A host that already lets the principal look around has nothing to be alarmed about,
    /// and the arrival itself is never refused: a presence is not authority.
    /// </summary>
    private static void ClosedHostNoticesAStranger()
    {
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var deck = runtime.RegisterHost(new HostDefinition("deck.vault", HostClass.Cyberdeck,
            Compute: new ComputeComponent(60, 20), Memory: new MemoryComponent(4))).Host!;
        var vault = runtime.RegisterHost(new HostDefinition("vault.closed", HostClass.Server,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4),
            Security: new SecurityComponent(SecurityProfile.Secure, 4, 2))).Host!;
        var actor = runtime.NewPrincipal();
        var guardOwner = runtime.NewPrincipal();
        Check(runtime.Agents.Deploy(guardOwner, vault.Id, Ice(), 0, 100).Succeeded, "the vault has a guard");
        var identity = runtime.NewIdentity();
        Check(runtime.Lives.Create(identity, actor, deck.Id).Succeeded, "the operator has a core on the deck");
        Equal(PresenceStatus.Ok, runtime.Presences.Enter(identity, deck.Id).Status, "and a presence to move");
        var routes = new Routes();
        routes.Link(deck.Id, vault.Id, 1, 1_000);
        runtime.AttachRoutePlanner(routes);

        Equal(PresenceStatus.Ok, runtime.Presences.Move(identity, vault.Id).Status, "the move to the vault starts");
        clock.Advance(1_500);
        runtime.Tick();
        var arrival = runtime.Presences.Get(identity)!;
        Equal(vault.Id, arrival.Host, "the stranger did arrive: the closed host does not refuse the presence");
        Check(runtime.Alerts.Level(vault.Id) >= HostAlertLevel.Suspicious,
            $"the closed host noticed the arrival ({runtime.Alerts.Level(vault.Id)})");
        Check(runtime.Knowledge.Lookup(guardOwner, deck.Id) is not null,
            "its guard learned the host the stranger came from");
        Equal(KnowledgeLevel.Observed, runtime.Knowledge.Level(guardOwner, deck.Id),
            "an arrival is placed, not identified");

        // The same host, now willing to be looked at, has nothing to raise: it already let this one in.
        runtime.Authority.Grant(actor, vault.Id, CapabilityCategory.Discovery, null, 600_000,
            GrantSource.Granted, PrincipalId.None);
        var before = runtime.Alerts.Level(vault.Id);
        Equal(PresenceStatus.Ok, runtime.Presences.Move(identity, deck.Id).Status, "the stranger leaves");
        clock.Advance(1_500);
        runtime.Tick();
        Equal(PresenceStatus.Ok, runtime.Presences.Move(identity, vault.Id).Status, "and comes back");
        clock.Advance(1_500);
        runtime.Tick();
        Equal(before, runtime.Alerts.Level(vault.Id),
            "a host that already lets the principal look around raises nothing further");
    }

    /// <summary>Everything an action needs to run from a host: spend its compute and command the target.</summary>
    private static void GrantWork(NetRunRuntime runtime, PrincipalId actor, HostId deck, HostId target)
    {
        foreach (var (host, category) in new[]
                 {
                     (deck, CapabilityCategory.Execution),
                     (deck, CapabilityCategory.Control),
                     (target, CapabilityCategory.Control)
                 })
        {
            runtime.Authority.Grant(actor, host, category, null, 600_000, GrantSource.Granted, PrincipalId.None);
        }
    }

    /// <summary>Grants an attacker what every attempt needs: a place to execute and knowledge of the target.</summary>
    private static void GrantBreach(NetRunRuntime runtime, PrincipalId attacker, HostId deck, HostId target)
    {
        foreach (var grant in new[]
                 {
                     (Host: deck, Category: CapabilityCategory.Execution),
                     (Host: deck, Category: CapabilityCategory.Discovery),
                     (Host: target, Category: CapabilityCategory.Discovery)
                 })
        {
            runtime.Authority.Grant(attacker, grant.Host, grant.Category, null, 600_000,
                GrantSource.Granted, PrincipalId.None);
        }
        runtime.Knowledge.Remember(attacker, target, KnowledgeLevel.Observed, 90);
    }

    /// <summary>Routes as the transport reports them: the domain never computes a path itself.</summary>
    private sealed class Routes : IRoutePlanner
    {
        private readonly Dictionary<(ulong, ulong), RoutePlan> _paths = [];

        public RoutePlan? Plan(HostId from, HostId to) =>
            _paths.TryGetValue((from.Value, to.Value), out var plan) ? plan : null;

        public void Link(HostId a, HostId b, int hops, long milliseconds)
        {
            _paths[(a.Value, b.Value)] = new RoutePlan(hops, milliseconds);
            _paths[(b.Value, a.Value)] = new RoutePlan(hops, milliseconds);
        }
    }
}
