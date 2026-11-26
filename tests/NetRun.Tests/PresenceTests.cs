using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>
/// PLAY-001 / §8.1: a presence stands on one host, travels a planned route, and arrives only if that route
/// still exists. It never teleports, never arrives through a link that is gone, and cannot act while moving.
/// </summary>
internal static class PresenceTests
{
    private sealed class Routes : IRoutePlanner
    {
        public Dictionary<(ulong, ulong), RoutePlan> Paths = [];

        public RoutePlan? Plan(HostId from, HostId to) =>
            Paths.TryGetValue((from.Value, to.Value), out var plan) ? plan : null;

        public void Link(HostId a, HostId b, int hops, long milliseconds)
        {
            Paths[(a.Value, b.Value)] = new RoutePlan(hops, milliseconds);
            Paths[(b.Value, a.Value)] = new RoutePlan(hops, milliseconds);
        }

        public void Cut(HostId a, HostId b)
        {
            Paths.Remove((a.Value, b.Value));
            Paths.Remove((b.Value, a.Value));
        }
    }

    public static void Run()
    {
        Section("presence");
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var deck = runtime.RegisterHost(new HostDefinition("deck.1", HostClass.Cyberdeck,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4))).Host!;
        var relay = runtime.RegisterHost(new HostDefinition("relay.1", HostClass.Relay,
            Compute: new ComputeComponent(4, 1))).Host!;
        var closed = runtime.RegisterHost(new HostDefinition("closed.1", HostClass.Terminal,
            Compute: new ComputeComponent(4, 1),
            Security: new SecurityComponent(SecurityProfile.Standard, 2, 0))).Host!;
        var actor = runtime.NewPrincipal();
        var identity = runtime.NewIdentity();
        runtime.Lives.Create(identity, actor, deck.Id);
        var routes = new Routes();
        routes.Link(deck.Id, relay.Id, 3, 3_000);
        runtime.AttachRoutePlanner(routes);

        // A presence needs a core: a lens with nothing behind it has nowhere to stand.
        var homeless = runtime.NewIdentity();
        Equal(PresenceStatus.HostUnavailable, runtime.Presences.Enter(homeless, deck.Id).Status,
            "a presence without a core is refused");
        var entered = runtime.Presences.Enter(identity, deck.Id);
        Equal(PresenceStatus.Ok, entered.Status, "a presence enters where its core lives");
        Equal(PresenceState.Idle, entered.Presence!.State, "a new presence is idle");
        Equal(deck.Id, runtime.Presences.HostOf(identity), "an idle presence stands on its host");

        // Travel is planned, takes the planned time and only then arrives.
        var moving = runtime.Presences.Move(identity, relay.Id);
        Equal(PresenceStatus.Ok, moving.Status, "a move over a planned route starts");
        Equal(PresenceState.Traveling, moving.Presence!.State, "the presence is in transit");
        Equal(3, moving.Presence.Hops, "the plan carries its hop count");
        Equal(HostId.None, runtime.Presences.HostOf(identity), "a presence in transit has no host to act from");
        Equal(PresenceStatus.Busy, runtime.Presences.Move(identity, deck.Id).Status,
            "a presence in transit cannot start a second move");

        // Still in transit: nothing happens until the planned arrival.
        clock.Advance(2_000);
        runtime.Tick();
        Equal(PresenceState.Traveling, runtime.Presences.Get(identity)!.State, "arrival waits for its time");
        clock.Advance(1_001);
        runtime.Tick();
        var arrived = runtime.Presences.Get(identity)!;
        Equal(PresenceState.Idle, arrived.State, "the presence arrives at the planned time");
        Equal(relay.Id, arrived.Host, "it stands on the destination");
        Equal(relay.Id, runtime.Presences.HostOf(identity), "the arrived presence may act from there");

        // A route that disappears mid-flight returns the presence instead of delivering it.
        runtime.Presences.Move(identity, deck.Id);
        routes.Cut(relay.Id, deck.Id);
        clock.Advance(4_000);
        runtime.Tick();
        var returned = runtime.Presences.Get(identity)!;
        Equal(PresenceState.Idle, returned.State, "a broken route ends the move");
        Equal(relay.Id, returned.Host, "the presence is back where it started");
        Equal("route_lost", returned.Reason, "the reason names the broken route");
        Check(runtime.Presences.HostOf(identity) == relay.Id, "it can act again from its own host");

        // Abort works before arrival and leaves the presence where it was.
        routes.Link(relay.Id, deck.Id, 2, 2_000);
        runtime.Presences.Move(identity, deck.Id);
        clock.Advance(500);
        Equal(PresenceStatus.Ok, runtime.Presences.Abort(identity, "test").Status, "an in-flight move aborts");
        Equal(relay.Id, runtime.Presences.Get(identity)!.Host, "an aborted move leaves the presence in place");
        Equal(PresenceStatus.NotTraveling, runtime.Presences.Abort(identity).Status, "an idle presence has nothing to abort");

        // An unreachable destination and a host that cannot hold a presence are both refused before leaving.
        var remote = runtime.RegisterHost(new HostDefinition("remote.1", HostClass.Server,
            Compute: new ComputeComponent(4, 1), Memory: new MemoryComponent(1))).Host!;
        Equal(PresenceStatus.NoRoute, runtime.Presences.Move(identity, remote.Id).Status,
            "a destination without a route is refused");
        routes.Link(relay.Id, closed.Id, 1, 1_000);
        Equal(PresenceStatus.HostUnavailable, runtime.Presences.Move(identity, closed.Id).Status,
            "a host that holds no presence is refused");

        // Losing the host a presence stands on loses the presence; losing the destination returns it.
        var doomed = runtime.RegisterHost(new HostDefinition("doomed.1", HostClass.Server,
            Compute: new ComputeComponent(4, 1), Memory: new MemoryComponent(1))).Host!;
        var doomedIdentity = runtime.NewIdentity();
        runtime.Lives.Create(doomedIdentity, actor, doomed.Id);
        runtime.Presences.Enter(doomedIdentity, doomed.Id);
        runtime.DestroyHost(doomed.Id, doomed.Generation);
        Check(runtime.Presences.Get(doomedIdentity) is null, "a presence is lost with its host");

        // Without a planner travel is refused, not guessed.
        var noRoutes = new NetRunRuntime(clock);
        var lonelyDeck = noRoutes.RegisterHost(new HostDefinition("deck.9", HostClass.Cyberdeck,
            Compute: new ComputeComponent(8, 2), Memory: new MemoryComponent(2))).Host!;
        var lonelyActor = noRoutes.NewPrincipal();
        var lonely = noRoutes.NewIdentity();
        noRoutes.Lives.Create(lonely, lonelyActor, lonelyDeck.Id);
        noRoutes.Presences.Enter(lonely, lonelyDeck.Id);
        var otherDeck = noRoutes.RegisterHost(new HostDefinition("deck.10", HostClass.Cyberdeck,
            Compute: new ComputeComponent(8, 2), Memory: new MemoryComponent(2))).Host!;
        var moveWithoutPlanner = noRoutes.Presences.Move(lonely, otherDeck.Id);
        Equal(PresenceStatus.NoRoute, moveWithoutPlanner.Status, "travel without a planner is refused");
        Equal("no_transport", moveWithoutPlanner.Reason, "the refusal names the missing planner");
    }
}
