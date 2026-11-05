using System.Diagnostics;
using NetDM.Core;

var graph = new TopologyStore();
var a = new NodeId(1);
var b = new NodeId(2);
var c = new NodeId(3);
Check(graph.AddNode(a));
Check(graph.AddNode(b));
Check(graph.AddNode(c));
Check(graph.Connect(a, b, 5));
Check(graph.Connect(b, c, 2));
Check(graph.Connect(a, c, 20));

var before = graph.Snapshot;
var route = Router.Find(before, a, c);
Check(route.Found && route.Cost == 7 && route.Path.SequenceEqual(new[] { a, b, c }));

Check(graph.RemoveNode(b));
var after = graph.Snapshot;
Check(after.Version > before.Version);
Check(Router.Find(after, a, c).Cost == 20);
Check(Router.Find(before, a, c).Cost == 7);
Check(!graph.AddNode(a));
Check(!graph.Disconnect(a, b));
Check(!Router.Find(after, a, new NodeId(42)).Found);
Check(graph.RemoveNode(c));
Check(!Router.Find(graph.Snapshot, a, c).Found);

// A large batch yields exactly one new version.
var store = new TopologyStore();
var batch = new[]
{
    new TopologyChange(TopologyOperation.AddNode, a, Position: new NodePosition(-1, -1)),
    new TopologyChange(TopologyOperation.AddNode, b, Position: new NodePosition(32, 0)),
    new TopologyChange(TopologyOperation.AddNode, c, Position: new NodePosition(100, 100)),
    new TopologyChange(TopologyOperation.Connect, a, b, 3),
    new TopologyChange(TopologyOperation.Connect, b, c, 2),
};
var applied = store.ApplyBatch(batch);
Check(applied.Applied == 5 && applied.Version == 1);
Check(store.Snapshot.Nodes[a].Position == new NodePosition(-1, -1));
Check(TopologyStore.QueryViewport(store.Snapshot, -10, -10, 32, 0).Count == 2);
var oldSnapshot = store.Snapshot;
Check(store.MoveNode(a, new NodePosition(100, 100)));
Check(TopologyStore.QueryViewport(store.Snapshot, -10, -10, 32, 0).Count == 1);
Check(TopologyStore.QueryViewport(oldSnapshot, -10, -10, 32, 0).Count == 2);
Check(TopologyStore.QueryViewport(store.Snapshot, 90, 90, 110, 110).Count == 2);
Check(!store.MoveNode(a, new NodePosition(100, 100)));
Check(oldSnapshot.ConnectivityVersion == store.Snapshot.ConnectivityVersion);

var runtime = new NetworkRuntime();
runtime.Apply(batch);
Check(runtime.Route(a, c).Cost == 5);
Check(runtime.Route(a, c).Cost == 5);
Check(runtime.Metrics.Capture(runtime.Cache.Count).CacheHits == 1);
runtime.Apply([new TopologyChange(TopologyOperation.Disconnect, a, b)]);
Check(!runtime.Route(a, c).Found);

// Verify parallel immutable reads remain consistent during batches of moves.
var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
var tasks = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
{
    try
    {
        for (var i = 0; i < 600; i++)
        {
            var snapshot = store.Snapshot;
            _ = Router.Find(snapshot, a, c);
            _ = TopologyStore.QueryViewport(snapshot, -100, -100, 200, 200);
        }
    }
    catch (Exception ex) { errors.Enqueue(ex); }
})).ToArray();
for (var i = 0; i < 250; i++)
    store.MoveNode(a, new NodePosition(i - 125, i - 125));
Task.WaitAll(tasks);
Check(errors.IsEmpty);

await CheckSchedulerAsync();
CheckSharedMedium();
CheckSharedMediumOracle();
CheckRadioAStar();
CheckPacketTransport();
CheckRerouteAdaptiveBackoff();
await CheckDedicatedPacketTicksAsync();
CheckSpatialRadioAirtime();
CheckRadioSafety();
CheckWirelessTopology();
CheckRadioOracle();
CheckRoutingEquivalence();
CheckRouteCacheBudgets();
Console.WriteLine("All netDM-core smoke tests passed.");





static async Task CheckSchedulerAsync()
{
    // One worker is held deliberately while a bounded backlog accumulates.
    // This verifies rejection, weighted fairness and no spawn-per-request.
    await using var scheduler = new PriorityWorkScheduler(
        new WorkSchedulerOptions(Workers: 1, QueueCapacity: 13,
            BudgetMillisecondsPer100Ms: 0));
    using var gate = new ManualResetEventSlim(false);
    using var entered = new ManualResetEventSlim(false);
    var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
    Check(scheduler.TrySchedule(token =>
    {
        entered.Set();
        gate.Wait(token);
        return 0;
    }, WorkPriority.Critical, CancellationToken.None, out Task<int>? blocker));
    Check(entered.Wait(3000));

    var jobs = new List<Task<int>>();
    for (var i = 0; i < 12; i++)
    {
        var number = i;
        Check(scheduler.TrySchedule(_ =>
        {
            order.Enqueue("critical:" + number);
            return number;
        }, WorkPriority.Critical, CancellationToken.None, out Task<int>? job));
        jobs.Add(job!);
    }
    Check(scheduler.TrySchedule(_ =>
    {
        order.Enqueue("background");
        return -1;
    }, WorkPriority.Background, CancellationToken.None, out Task<int>? background));
    jobs.Add(background!);
    Check(!scheduler.TrySchedule(_ => 100, WorkPriority.Background,
        CancellationToken.None, out Task<int>? rejected));
    Check(rejected is null);

    var queued = scheduler.Capture();
    Check(queued.Queued == 13 && queued.Running == 1);
    Check(queued.Rejected == 1 && queued.BackgroundQueued == 1);
    gate.Set();
    await Task.WhenAll(jobs.Append(blocker!));
    var completed = order.ToArray();
    Check(completed.Length == 13);
    Check(Array.IndexOf(completed, "background") < 12); // No permanent starvation.
    Check(SpinWait.SpinUntil(() => scheduler.Capture().Completed == 14, 3000));
    Check(scheduler.Capture().QueueWaitP95Us >= 0);

    // A cancellation must not execute the operation when it reaches a worker.
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    Check(scheduler.TrySchedule(_ => 5, WorkPriority.Interactive, cancelled.Token,
        out Task<int>? cancelledTask));
    bool observedCancel = false;
    try { await cancelledTask!; }
    catch (OperationCanceledException) { observedCancel = true; }
    Check(observedCancel);

    Check(scheduler.TrySchedule<int>(_ => throw new InvalidOperationException("expected"),
        WorkPriority.Critical, CancellationToken.None, out Task<int>? failure));
    bool observedFailure = false;
    try { await failure!; }
    catch (InvalidOperationException) { observedFailure = true; }
    Check(observedFailure);
    Check(SpinWait.SpinUntil(() => scheduler.Capture().Failed == 1, 3000));
}

static void CheckSharedMedium()
{
    var runtime = new NetworkRuntime();
    runtime.Apply([
        new TopologyChange(TopologyOperation.AddNode, new NodeId(1), Position: new NodePosition(0, 0)),
        new TopologyChange(TopologyOperation.AddNode, new NodeId(2), Position: new NodePosition(10, 0)),
        new TopologyChange(TopologyOperation.AddNode, new NodeId(3), Position: new NodePosition(24, 0)),
        new TopologyChange(TopologyOperation.AddNode, new NodeId(4), Position: new NodePosition(1, 1, 1))
    ]);
    var medium = new SharedMediumNetwork(runtime, bytesPerTick: 1024);
    var radio = new RadioProfile(15, 7, 8);
    var changed = medium.Update([
        new RadioNodeUpdate(new NodeId(1), Profile: radio, ChangeProfile: true),
        new RadioNodeUpdate(new NodeId(2), Profile: radio, ChangeProfile: true),
        new RadioNodeUpdate(new NodeId(3), Profile: radio, ChangeProfile: true),
        new RadioNodeUpdate(new NodeId(4), Profile: radio, ChangeProfile: true)
    ]);
    Check(changed.ChangedRadios == 4 && changed.Moves == 0);
    Check(medium.Stats().Domains == 2);
    Check(medium.Stats().ConfiguredDevices == 4);
    Check(medium.Stats().PairwiseRadioEdges == 0);
    Check(runtime.Topology.Snapshot.Nodes.Values.All(node => node.Links.Count == 0));
    Check(!medium.Route(new NodeId(1), new NodeId(3)).Found || medium.Route(new NodeId(1), new NodeId(3)).Cost == 16);
    Check(medium.Route(new NodeId(1), new NodeId(3)).Cost == 16);
    Check(!medium.Route(new NodeId(1), new NodeId(4)).Found); // Z separation

    var toTwo = medium.Route(new NodeId(1), new NodeId(2));
    Check(toTwo.Found && toTwo.Cost == 8);
    Check(medium.TryTransmit(toTwo, 400, 1));
    Check(medium.TryTransmit(toTwo, 400, 1));
    Check(!medium.TryTransmit(toTwo, 400, 1));
    Check(medium.TryTransmit(toTwo, 400, 2));
    Check(medium.Stats().AcceptedTransfers == 3);
    Check(medium.Stats().RejectedCapacity == 1);
    Check(medium.Stats().RejectedTransfers == 1);
    Check(medium.Stats().RejectedTransfers == 1);

    Check(medium.SetCable(new NodeId(2), new NodeId(3), 2));
    Check(medium.Route(new NodeId(1), new NodeId(3)).Cost == 10);
    // Global snapshot advanced because a different cable changed. The original
    // 1 -> 2 radio route still exists and can be delivered without re-routing.
    Check(medium.TryTransmit(toTwo, 100, 3));
    Check(medium.Stats().AcceptedRevalidatedTransfers == 1);
    var lastVersion = medium.Snapshot.Version;
    var update = medium.Update([new RadioNodeUpdate(new NodeId(2),
        Position: new NodePosition(-25, 0))]);
    Check(update.Version > lastVersion);
    Check(!medium.Route(new NodeId(1), new NodeId(3)).Found);
    Check(medium.Route(new NodeId(2), new NodeId(3)).Cost == 2);
    Check(!medium.TryTransmit(toTwo, 100, 3)); // stale route cannot send
    Check(medium.Stats().RejectedStaleRoute == 1);
    // An untrusted/synthetic route with a removed endpoint must never throw.
    var nonexistent = toTwo with { Path = new[] { new NodeId(1), new NodeId(9999) } };
    Check(!medium.TryTransmit(nonexistent, 100, 4));
    Check(medium.Stats().RejectedStaleRoute == 2);
    Check(medium.SetCable(new NodeId(1), new NodeId(2), 1));
    Check(medium.Route(new NodeId(1), new NodeId(3)).Cost == 3);
    Check(medium.TryTransmit(medium.Route(new NodeId(1), new NodeId(3)), 900, 3));
    // After radio has disappeared, an older route remains deliverable if a
    // cable has replaced its hop. Actual current cable cost is used.
    var formerRadioRoute = medium.Route(new NodeId(1), new NodeId(3));
    Check(medium.SetCable(new NodeId(1), new NodeId(3), 1));
    Check(medium.TryTransmit(formerRadioRoute, 900, 4));
    Check(medium.Stats().AcceptedRevalidatedTransfers == 2);
    // One two-hop wireless packet larger than the shared channel's entire
    // tick cannot succeed even when the channel is otherwise idle.
    var oversizedRuntime = new NetworkRuntime();
    oversizedRuntime.Apply([
        new TopologyChange(TopologyOperation.AddNode, new NodeId(1),
            Position: new NodePosition(0, 0)),
        new TopologyChange(TopologyOperation.AddNode, new NodeId(2),
            Position: new NodePosition(10, 0)),
        new TopologyChange(TopologyOperation.AddNode, new NodeId(3),
            Position: new NodePosition(20, 0))
    ]);
    var smallMedium = new SharedMediumNetwork(oversizedRuntime, bytesPerTick: 1024);
    smallMedium.Update([
        new RadioNodeUpdate(new NodeId(1), Profile: new RadioProfile(15,1), ChangeProfile: true),
        new RadioNodeUpdate(new NodeId(2), Profile: new RadioProfile(15,1), ChangeProfile: true),
        new RadioNodeUpdate(new NodeId(3), Profile: new RadioProfile(15,1), ChangeProfile: true)
    ]);
    var multiHop = smallMedium.Route(new NodeId(1), new NodeId(3));
    Check(multiHop.Found && multiHop.Path.Count == 3);
    Check(!smallMedium.TryTransmit(multiHop, 600, 1));
    Check(smallMedium.Stats().RejectedTransferTooLarge == 1);
    Check(smallMedium.Stats().AttemptedWirelessHops == 2);
    Check(smallMedium.TryTransmit(multiHop, 400, 2));
    Check(!smallMedium.TryTransmit(multiHop, 400, 1));
    Check(smallMedium.Stats().RejectedOldTick == 1);
    var malformed = multiHop with { Path = new[] { new NodeId(1), new NodeId(3) } };
    Check(!smallMedium.TryTransmit(malformed, 100, 3));
    Check(smallMedium.Stats().RejectedInvalidPath == 1);
    var radioStats = smallMedium.Stats();
    Check(radioStats.RejectedTransfers ==
        radioStats.RejectedTransferTooLarge + radioStats.RejectedCapacity +
        radioStats.RejectedStaleRoute + radioStats.RejectedInvalidPath +
        radioStats.RejectedOldTick);
    Check(medium.SetCable(new NodeId(1), new NodeId(3), null));
    Check(medium.RemoveNodes([new NodeId(2)]) == 1);
    Check(!medium.Route(new NodeId(1), new NodeId(3)).Found);
    Check(medium.Stats().ConfiguredDevices == 3);

    // Dense channel: a single shared cell stores 400 memberships, no pairwise edges.
    var dense = new NetworkRuntime();
    dense.Apply(Enumerable.Range(1, 400)
        .Select(i => new TopologyChange(TopologyOperation.AddNode,
            new NodeId((ulong)i), Position: new NodePosition(0, 0))).ToArray());
    var domain = new SharedMediumNetwork(dense);
    domain.Update(Enumerable.Range(1, 400)
        .Select(i => new RadioNodeUpdate(new NodeId((ulong)i),
            Profile: new RadioProfile(14, 1), ChangeProfile: true)).ToArray());
    Check(domain.Snapshot.Cells.Count == 1);
    Check(domain.Stats().ConfiguredDevices == 400);
    Check(dense.Topology.Snapshot.Nodes.Values.All(n => n.Links.Count == 0));
    Check(domain.Route(new NodeId(1), new NodeId(400)).Cost == 8);
    Check(domain.Route(new NodeId(1), new NodeId(400)).Explored <= 2);

    // Snapshot remains consistent after movement / profile updates.
    var frozen = domain.Snapshot;
    domain.Update([new RadioNodeUpdate(new NodeId(400),
        Position: new NodePosition(100, 100))]);
    Check(!domain.Route(new NodeId(1), new NodeId(400)).Found);
    Check(SharedMediumNetwork.RouteCore(frozen, new NodeId(1), new NodeId(400)).Found);
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var oldVersion = domain.Snapshot.Version;
    bool cancelled = false;
    try
    {
        domain.Update([new RadioNodeUpdate(new NodeId(1),
            Position: new NodePosition(12, 12))], cts.Token);
    }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled && domain.Snapshot.Version == oldVersion);
}

static void CheckSharedMediumOracle()
{
    for (var seed = 0; seed < 3; seed++)
    {
        var random = new Random(seed + 9654);
        var oracleStore = new NetworkRuntime();
        var sharedStore = new NetworkRuntime();
        var nodes = new List<TopologyChange>();
        for (var i = 0; i < 72; i++)
        {
            var point = new NodePosition(random.Next(-50, 50),
                random.Next(-50, 50), random.Next(0, 2));
            nodes.Add(new TopologyChange(TopologyOperation.AddNode,
                new NodeId((ulong)(i + 1)), Position: point));
        }
        oracleStore.Apply(nodes);
        sharedStore.Apply(nodes);
        var oldRadio = new DynamicRadioNetwork(oracleStore);
        var medium = new SharedMediumNetwork(sharedStore);
        var updates = new List<RadioNodeUpdate>();
        for (var i = 1; i <= 72; i++)
        {
            var profile = new RadioProfile(random.Next(12, 24),
                (uint)random.Next(1, 5), (uint)random.Next(3, 15));
            updates.Add(new RadioNodeUpdate(new NodeId((ulong)i),
                Profile: profile, ChangeProfile: true));
        }
        oldRadio.Update(updates);
        medium.Update(updates);
        Compare();

        for (var batch = 0; batch < 15; batch++)
        {
            var ids = new HashSet<int>();
            var delta = new List<RadioNodeUpdate>();
            for (var n = 0; n < 7; n++)
            {
                var id = random.Next(1, 73);
                if (!ids.Add(id)) continue;
                if (random.Next(4) == 0)
                {
                    var profile = random.Next(6) == 0 ? (RadioProfile?)null :
                        new RadioProfile(random.Next(12, 24),
                            (uint)random.Next(1, 5), (uint)random.Next(3, 15));
                    delta.Add(new RadioNodeUpdate(new NodeId((ulong)id),
                        Profile: profile, ChangeProfile: true));
                }
                else
                {
                    var p = sharedStore.Topology.Snapshot.Nodes[new NodeId((ulong)id)].Position!.Value;
                    delta.Add(new RadioNodeUpdate(new NodeId((ulong)id),
                        Position: new NodePosition(p.X + random.Next(-14, 15),
                            p.Y + random.Next(-14, 15), random.Next(0, 2))));
                }
            }
            oldRadio.Update(delta);
            medium.Update(delta);
            Compare();
        }

        void Compare()
        {
            var reference = oracleStore.Topology.Snapshot;
            for (var sample = 0; sample < 25; sample++)
            {
                var from = new NodeId((ulong)random.Next(1, 73));
                var to = new NodeId((ulong)random.Next(1, 73));
                var expected = Router.Find(reference, from, to);
                var actual = medium.Route(from, to);
                Check(actual.Found == expected.Found);
                Check(actual.Cost == expected.Cost);
            }
        }
    }
}

static void CheckPacketTransport()
{
    static (SharedMediumNetwork Medium, RouteResult Route) MakeChain(int capacity)
    {
        var runtime = new NetworkRuntime();
        runtime.Apply([
            new TopologyChange(TopologyOperation.AddNode, new NodeId(1),
                Position: new NodePosition(0,0)),
            new TopologyChange(TopologyOperation.AddNode, new NodeId(2),
                Position: new NodePosition(10,0)),
            new TopologyChange(TopologyOperation.AddNode, new NodeId(3),
                Position: new NodePosition(20,0))
        ]);
        var medium = new SharedMediumNetwork(runtime, bytesPerTick: capacity);
        medium.Update(new[] {1,2,3}.Select(i => new RadioNodeUpdate(
            new NodeId((ulong)i), Profile: new RadioProfile(14,1,8),
            ChangeProfile:true)).ToArray());
        var route = medium.Route(new NodeId(1),new NodeId(3));
        Check(route.Found && route.Path.Count == 3);
        return (medium, route);
    }

    // Whole-route reservation rejects a 2*400-byte path with a 512-byte
    // budget. Store-and-forward delivers it over two independent ticks.
    var (medium, route) = MakeChain(512);
    Check(!medium.TryTransmit(route, 400, 0));
    var queue = new RadioPacketTransport(medium,
        new PacketTransportOptions(Capacity:2,MaxAgeTicks:10,
            MaxReroutes:2,MaxAdvancesPerTick:2));
    Check(queue.Enqueue(route,400,0));
    Check(queue.Enqueue(route,400,0));
    Check(!queue.Enqueue(route,400,0));
    Check(queue.Capture().Rejected == 1);
    Check(queue.Advance(1) == 1);
    Check(queue.Advance(1) == 0); // Never two hops in one tick.
    Check(queue.Capture().Delivered == 0);
    for (var tick = 2; tick <= 6; tick++) queue.Advance(tick);
    var stats = queue.Capture();
    Check(stats.Delivered == 2 && stats.Queued == 0);
    Check(stats.WirelessHops == 4);
    Check(stats.BusyDeferrals > 0);
    Check(stats.DeliveredBytes == 800);
    Check(stats.DeliveryAgeP95Ticks >= 2);
    Check(stats.FirstHopSamples==2);
    Check(stats.FirstHopAgeP95Ticks>=1);
    Check(stats.MeanDeliveredHops == 2);
    Check(stats.QueueFullRejections == 1);

    // A broken link only creates a *ticket*. No route computation is done
    // inside Advance or while the packet queue lock is owned.
    var (broken, original) = MakeChain(600);
    var disconnected = new RadioPacketTransport(broken,
        new PacketTransportOptions(MaxReroutes:2));
    Check(disconnected.Enqueue(original,300,0));
    Check(disconnected.Advance(1)==1);
    broken.Update([new RadioNodeUpdate(new NodeId(3),Profile:null,
        ChangeProfile:true)]);
    Check(disconnected.Advance(2)==0);
    Check(disconnected.Capture().Dropped==0);
    Check(disconnected.Capture().RerouteQueued==1);
    Check(disconnected.Capture().Reroutes==0);
    Check(disconnected.TryTakeReroute(out var ticket1));
    Check(ticket1.Source==new NodeId(2) && ticket1.Destination==new NodeId(3));
    Check(disconnected.Capture().RerouteInFlight==1);
    // Other packets still enter while a broken route is being calculated.
    Check(disconnected.Enqueue(broken.Route(new NodeId(1),new NodeId(2)),100,2));
    Check(disconnected.CompleteReroute(ticket1,
        broken.Route(ticket1.Source,ticket1.Destination)));
    Check(disconnected.Capture().NoRouteDeferrals==1);
    // Exponential no-route backoff: second failed attempt is at tick 5,
    // followed by a 6-tick pause before the final bounded drop at tick 11.
    for (var tick=3; tick<=11; tick++)
    {
        disconnected.Advance(tick);
        if (disconnected.TryTakeReroute(out var next))
            Check(disconnected.CompleteReroute(next,
                broken.Route(next.Source,next.Destination)));
    }
    Check(disconnected.Capture().Dropped==1);
    Check(disconnected.Capture().LinkFailureDrops==1);
    Check(disconnected.Capture().Reroutes==2);
    Check(disconnected.Capture().Delivered==1);

    // A cancelled search returns ownership to its packet. When the radio
    // recovers, the old route can resume without additional route searches.
    var (recovering, recoveringRoute) = MakeChain(600);
    var recovery = new RadioPacketTransport(recovering,
        new PacketTransportOptions(MaxReroutes:3, NoRouteRetryDelayTicks:2));
    Check(recovery.Enqueue(recoveringRoute,300,0));
    Check(recovery.Advance(1)==1);
    recovering.Update([new RadioNodeUpdate(new NodeId(3),
        Profile:null, ChangeProfile:true)]);
    Check(recovery.Advance(2)==0);
    Check(recovery.TryTakeReroute(out var cancelTicket));
    Check(recovery.CancelReroute(cancelTicket));
    Check(recovery.Capture().RerouteCancelled==1);
    recovering.Update([new RadioNodeUpdate(new NodeId(3),
        Profile:new RadioProfile(14,1,8),ChangeProfile:true)]);
    Check(recovery.Advance(3)==1);
    Check(recovery.Capture().Delivered==1);
    Check(recovery.Capture().Dropped==0);

    // Pending reroute admission remains finite. If full, the additional
    // broken packet stays queued and retries later instead of blocking.
    var (limitedMedium, limitedPath)=MakeChain(600);
    var limited=new RadioPacketTransport(limitedMedium,new PacketTransportOptions(
        MaxPendingReroutes:1, MaxConcurrentReroutes:1, MaxAdvancesPerTick:3));
    Check(limited.Enqueue(limitedPath,200,0));
    Check(limited.Enqueue(limitedPath,200,0));
    Check(limited.Advance(1)==2);
    limitedMedium.Update([new RadioNodeUpdate(new NodeId(3),
        Profile:null,ChangeProfile:true)]);
    limited.Advance(2);
    Check(limited.Capture().RerouteQueued==1);
    Check(limited.Capture().RerouteQueueDeferrals==1);
    Check(limited.Capture().Queued==2);
    Check(limited.TryTakeReroute(out var limitedTicket));
    Check(!limited.TryTakeReroute(out _)); // in-flight quota
    Check(limited.CancelReroute(limitedTicket,schedulerRejected:true));
    Check(limited.Capture().RerouteSchedulerRejections==1);

    // An expensive search can finish after the packet expires. Its stale
    // completion MUST NOT resurrect a dead packet.
    var (expiredMedium, expiredRoute)=MakeChain(600);
    var expired=new RadioPacketTransport(expiredMedium,new PacketTransportOptions(
        MaxAgeTicks:3,MaxPendingReroutes:1,MaxConcurrentReroutes:1));
    Check(expired.Enqueue(expiredRoute,300,0));
    Check(expired.Advance(1)==1);
    expiredMedium.Update([new RadioNodeUpdate(new NodeId(3),
        Profile:null,ChangeProfile:true)]);
    expired.Advance(2);
    Check(expired.TryTakeReroute(out var expiredTicket));
    expired.Advance(3);
    Check(expired.Capture().Expired==1 && expired.Capture().Queued==0);
    Check(!expired.CompleteReroute(expiredTicket,expiredRoute));
    Check(expired.Capture().Delivered==0);
    Check(expired.Capture().RerouteInFlight==0);
    Check(expired.Capture().Enqueued==expired.Capture().Expired);

    // A gap between real forwarding passes is measured separately from
    // global scheduler admissions. No retroactive packet teleport occurs.
    var (gapMedium,gapRoute)=MakeChain(600);
    var gaps=new RadioPacketTransport(gapMedium);
    Check(gaps.Enqueue(gapRoute,250,0));
    Check(gaps.Advance(1)==1);
    gaps.RecordTickAdmissionFailure();
    Check(gaps.Advance(5)==1);
    Check(gaps.Capture().SkippedTicks==3);
    Check(gaps.Capture().TickAdmissionFailures==1);
    Check(gaps.Capture().FirstHopSamples==1);
    Check(gaps.Capture().ForwardingP95Milliseconds>=0);

    // A reroute already in-flight must not prevent a physically healed link
    // from advancing. Its late result is ignored by the generation guard.
    var (healedMedium, healedRoute)=MakeChain(600);
    var healedQueue=new RadioPacketTransport(healedMedium,
        new PacketTransportOptions(MaxConcurrentReroutes:1));
    Check(healedQueue.Enqueue(healedRoute,300,0));
    Check(healedQueue.Advance(1)==1);
    healedMedium.Update([new RadioNodeUpdate(new NodeId(3),
        Profile:null,ChangeProfile:true)]);
    healedQueue.Advance(2);
    Check(healedQueue.TryTakeReroute(out var healingTicket));
    healedMedium.Update([new RadioNodeUpdate(new NodeId(3),
        Profile:new RadioProfile(14,1,8),ChangeProfile:true)]);
    Check(healedQueue.Advance(3)==1);
    Check(healedQueue.Capture().Delivered==1);
    Check(!healedQueue.CompleteReroute(healingTicket,healedRoute));
    Check(healedQueue.Capture().RerouteInFlight==0);

    // A physical cable can restore the missing hop while a packet is
    // already en route. The final hop uses the cable, not shared airtime.
    var (cableMedium, cablePath) = MakeChain(400);
    var cableQueue = new RadioPacketTransport(cableMedium);
    Check(cableQueue.Enqueue(cablePath,300,0));
    Check(cableQueue.Advance(1)==1);
    cableMedium.Update([new RadioNodeUpdate(new NodeId(3),
        Profile:null,ChangeProfile:true)]);
    Check(cableMedium.SetCable(new NodeId(2),new NodeId(3),1));
    Check(cableQueue.Advance(2)==1);
    Check(cableQueue.Capture().Delivered==1);
    Check(cableQueue.Capture().CableHops==1);

    // TTL / zero-hop packets / copies of externally mutable path buffers.
    var (ttlMedium,ttlRoute)=MakeChain(400);
    var ttlQueue=new RadioPacketTransport(ttlMedium,new PacketTransportOptions(
        MaxAgeTicks:2,MaxAdvancesPerTick:1));
    Check(ttlQueue.Enqueue(ttlRoute,300,0));
    Check(ttlQueue.Advance(2)==0);
    Check(ttlQueue.Capture().Expired==1);
    var same=ttlMedium.Route(new NodeId(1),new NodeId(1));
    Check(ttlQueue.Enqueue(same,100,2));
    ttlQueue.Advance(3);
    Check(ttlQueue.Capture().Delivered==1);
}

static void CheckRerouteAdaptiveBackoff()
{
    static (SharedMediumNetwork Medium, RadioPacketTransport Transport,
        RouteResult Original) Setup()
    {
        var runtime = new NetworkRuntime();
        runtime.Apply([
            new TopologyChange(TopologyOperation.AddNode, new NodeId(1),
                Position: new NodePosition(0, 0)),
            new TopologyChange(TopologyOperation.AddNode, new NodeId(2),
                Position: new NodePosition(10, 0)),
            new TopologyChange(TopologyOperation.AddNode, new NodeId(3),
                Position: new NodePosition(20, 0)),
            new TopologyChange(TopologyOperation.AddNode, new NodeId(4),
                Position: new NodePosition(100, 100))
        ]);
        var radio = new SharedMediumNetwork(runtime, bytesPerTick: 8192);
        radio.Update(Enumerable.Range(1, 4).Select(i =>
            new RadioNodeUpdate(new NodeId((ulong)i),
                Profile: new RadioProfile(14, 1, 8),
                ChangeProfile: true)).ToArray());
        var original = radio.Route(new NodeId(1), new NodeId(3));
        Check(original.Found && original.Path.Count == 3);
        return (radio, new RadioPacketTransport(radio,
            new PacketTransportOptions(MaxAgeTicks: 100,
                MaxReroutes: 8, MaxNoRouteRetryDelayTicks: 12,
                RerouteQueueRetryDelayTicks: 8)), original);
    }

    var (radio, packets, route) = Setup();
    Check(packets.Enqueue(route, 256, 0));
    Check(packets.Advance(1) == 1);
    var disabled = new RadioNodeUpdate(new NodeId(3),
        Profile: null, ChangeProfile: true);
    radio.Update([disabled]);
    packets.NotifyRadioUpdates([disabled]);
    Check(!radio.HasUsableHop(new NodeId(2), new NodeId(3)));
    packets.Advance(2);
    Check(packets.TryTakeReroute(out var first));
    Check(packets.CompleteReroute(first, radio.Route(first.Source, first.Destination)));
    Check(packets.Capture().NoRouteBackoffs == 1);
    Check(packets.Capture().NoRouteBackoffTicks == 3);
    // The first failure skips two interim ticks instead of invoking the
    // expensive shortest-path algorithm on every pass.
    packets.Advance(3);
    packets.Advance(4);
    Check(packets.Capture().Reroutes == 1);
    Check(packets.Capture().CooldownSkips == 2);
    packets.Advance(5);
    Check(packets.TryTakeReroute(out var second));
    Check(packets.CompleteReroute(second, radio.Route(second.Source, second.Destination)));
    Check(packets.Capture().NoRouteBackoffs == 2);
    Check(packets.Capture().NoRouteBackoffTicks == 9); // 3 + 6

    // A global change to a DIFFERENT node does not wake this packet.
    var unrelated = new RadioNodeUpdate(new NodeId(4),
        Position: new NodePosition(101, 101));
    radio.Update([unrelated]);
    packets.NotifyRadioUpdates([unrelated]);
    packets.Advance(8);
    Check(packets.Capture().Reroutes == 2);
    Check(packets.Capture().LocalChangeWakeups == 0);
    // A move of the packet's current source does trigger an early retry,
    // but repeated changes are rate-limited by NoRouteRetryDelayTicks.
    var moved = new RadioNodeUpdate(new NodeId(2),
        Position: new NodePosition(11, 0));
    radio.Update([moved]);
    packets.NotifyRadioUpdates([moved]);
    packets.Advance(9);
    Check(packets.TryTakeReroute(out var third));
    Check(packets.Capture().LocalChangeWakeups == 1);
    Check(packets.CompleteReroute(third, radio.Route(third.Source, third.Destination)));
    Check(packets.Capture().NoRouteBackoffTicks == 21); // 3+6+12

    // Repairing the original radio hop must bypass the remaining 12-tick
    // negative backoff. This is a local, non-reserving availability check.
    var repaired = new RadioNodeUpdate(new NodeId(3),
        Profile: new RadioProfile(14, 1, 8), ChangeProfile: true);
    radio.Update([repaired]);
    packets.NotifyRadioUpdates([repaired]);
    Check(radio.HasUsableHop(new NodeId(2), new NodeId(3)));
    Check(packets.Advance(10) == 1);
    var recovered = packets.Capture();
    Check(recovered.Delivered == 1 && recovered.RecoveredOriginalHops == 1);
    Check(recovered.LocalChangeWakeups == 2);
    Check(recovered.Queued == 0 && recovered.NoRouteBackoffs == 3);

    // No-path answers from an old immutable snapshot must not become
    // authoritative negative cache entries after topology changes.
    var (staleRadio, staleTransport, stalePath) = Setup();
    Check(staleTransport.Enqueue(stalePath, 200, 0));
    Check(staleTransport.Advance(1) == 1);
    staleRadio.Update([disabled]);
    staleTransport.Advance(2);
    Check(staleTransport.TryTakeReroute(out var oldTicket));
    var oldNegative = staleRadio.Route(oldTicket.Source, oldTicket.Destination);
    Check(!oldNegative.Found);
    staleRadio.Update([repaired]);
    Check(staleTransport.CompleteReroute(oldTicket, oldNegative));
    Check(staleTransport.Capture().StaleNegativeResults == 1);
    Check(staleTransport.Capture().NoRouteBackoffs == 0);
    // Its old hop is already restored, and must be deliverable.
    Check(staleTransport.Advance(5) == 1);
    Check(staleTransport.Capture().Delivered == 1);

    // Rejecting a ticket due to full reroute capacity must not generate
    // a new ticket every subsequent tick while the capacity is still full.
    var (fullRadio, fullTransport, fullPath) = Setup();
    var constrained = new RadioPacketTransport(fullRadio,
        new PacketTransportOptions(
            MaxPendingReroutes: 1, MaxConcurrentReroutes: 1,
            RerouteQueueRetryDelayTicks: 8));
    Check(constrained.Enqueue(fullPath, 200, 0));
    Check(constrained.Enqueue(fullPath, 200, 0));
    Check(constrained.Advance(1) == 2);
    fullRadio.Update([disabled]);
    constrained.Advance(2);
    Check(constrained.Capture().RerouteQueueDeferrals == 1);
    constrained.Advance(3);
    constrained.Advance(4);
    Check(constrained.Capture().RerouteQueueDeferrals == 1);
    Check(constrained.Capture().Queued == 2);
}

static async Task CheckDedicatedPacketTicksAsync()
{
    var runtime = new NetworkRuntime();
    runtime.Apply([
        new TopologyChange(TopologyOperation.AddNode, new NodeId(1),
            Position: new NodePosition(0, 0)),
        new TopologyChange(TopologyOperation.AddNode, new NodeId(2),
            Position: new NodePosition(10, 0))
    ]);
    var network = new SharedMediumNetwork(runtime, bytesPerTick: 4096);
    network.Update([
        new RadioNodeUpdate(new NodeId(1),
            Profile: new RadioProfile(14, 1, 8), ChangeProfile: true),
        new RadioNodeUpdate(new NodeId(2),
            Profile: new RadioProfile(14, 1, 8), ChangeProfile: true)
    ]);
    var path = network.Route(new NodeId(1), new NodeId(2));
    Check(path.Found);
    var packets = new RadioPacketTransport(network,
        new PacketTransportOptions(MaxAdvancesPerTick: 64,
            MaxTickWorkMilliseconds: 25, MaxAgeTicks: 200));
    using var cancelled = new CancellationTokenSource();
    var runner = new PacketTickRunner(packets, periodMilliseconds: 20);
    // Global routing worker is fully occupied, with another task queued
    // and the queue rejecting new admissions. The dedicated packet tick
    // thread must remain independent of that queue.
    await using var saturated = new PriorityWorkScheduler(
        new WorkSchedulerOptions(Workers: 1, QueueCapacity: 1,
            BudgetMillisecondsPer100Ms: 0));
    using var held = new ManualResetEventSlim(false);
    using var entered = new ManualResetEventSlim(false);
    Check(saturated.TrySchedule(token =>
    {
        entered.Set();
        held.Wait(token);
        return 1;
    }, WorkPriority.Critical, CancellationToken.None, out Task<int>? holdingTask));
    Check(entered.Wait(3000));
    Check(saturated.TrySchedule(_ => 2, WorkPriority.Background,
        CancellationToken.None, out Task<int>? queuedTask));
    Check(!saturated.TrySchedule(_ => 3, WorkPriority.Interactive,
        CancellationToken.None, out Task<int>? rejectedTask));
    Check(rejectedTask is null);
    // The runner and the route producer use the same monotonic tick epoch.
    long tick = Stopwatch.GetTimestamp() /
        Math.Max(1, Stopwatch.Frequency * 20L / 1000);
    for (var i = 0; i < 12; i++)
        Check(packets.Enqueue(path, 100, tick));
    var worker = runner.Start(cancelled.Token);
    bool refusedSecondStart = false;
    try { _ = runner.Start(cancelled.Token); }
    catch (InvalidOperationException) { refusedSecondStart = true; }
    Check(refusedSecondStart);
    try
    {
        Check(SpinWait.SpinUntil(
            () => packets.Capture().Delivered == 12, 3500));
        var state = runner.Capture();
        Check(state.ExecutedTicks >= 1);
        Check(state.FailedTicks == 0);
        Check(state.LastTick >= tick);
        Check(state.ExecutionP95Milliseconds >= 0);
        Check(saturated.Capture().Queued == 1);
        Check(packets.Capture().TickAdmissionFailures == 0);
    }
    finally
    {
        cancelled.Cancel();
        held.Set();
        await worker.WaitAsync(TimeSpan.FromSeconds(5));
    }
    await Task.WhenAll(holdingTask!, queuedTask!);
    Check(runner.Capture().ExecutedTicks >= 1);
    var invalid = new PacketTransportOptions(MaxTickWorkMilliseconds: 0);
    bool rejectedBadBudget = false;
    try { invalid.Validate(); }
    catch (ArgumentOutOfRangeException) { rejectedBadBudget = true; }
    Check(rejectedBadBudget);
}

static void CheckSpatialRadioAirtime()
{
    var runtime = new NetworkRuntime();
    var points = new[]{
        new NodePosition(0, 0), new NodePosition(10, 0),
        new NodePosition(500, 0), new NodePosition(510, 0),
        new NodePosition(29, 0), new NodePosition(39, 0)
    };
    runtime.Apply(Enumerable.Range(1, points.Length).Select(i =>
        new TopologyChange(TopologyOperation.AddNode,
            new NodeId((ulong)i),Position:points[i-1])).ToArray());
    var medium = new SharedMediumNetwork(runtime, bytesPerTick:400,
        airtimeScope:RadioAirtimeScope.SpatialCells);
    medium.Update(Enumerable.Range(1,points.Length).Select(i =>
        new RadioNodeUpdate(new NodeId((ulong)i),
            Profile:new RadioProfile(14,1,8),ChangeProfile:true)).ToArray());
    Check(medium.AirtimeScope==RadioAirtimeScope.SpatialCells);
    Check(medium.TryTransmitHop(new NodeId(1),new NodeId(2),400,100)
        ==RadioHopOutcome.TransmittedRadio);
    Check(medium.TryTransmitHop(new NodeId(3),new NodeId(4),400,100)
        ==RadioHopOutcome.TransmittedRadio); // Distant area, same channel/tick.
    Check(medium.TryTransmitHop(new NodeId(5),new NodeId(6),400,100)
        ==RadioHopOutcome.ChannelBusy); // Overlapping interference cells.
    Check(medium.TryTransmitHop(new NodeId(5),new NodeId(6),400,101)
        ==RadioHopOutcome.TransmittedRadio);
    Check(medium.TryTransmitHop(new NodeId(1),new NodeId(2),400,100)
        ==RadioHopOutcome.OldTick);
    Check(medium.TryTransmitHop(new NodeId(1),new NodeId(2),401,102)
        ==RadioHopOutcome.PacketTooLarge);
    var stats = medium.Stats();
    Check(stats.SpatialSuccessfulHops==3);
    Check(stats.SpatialBusyHops==1);
    Check(stats.SpatialPayloadBytes==1200);
    Check(stats.SpatialCellReservations>=3);
    Check(stats.SpatialActiveCells>0 && stats.SpatialLastTick==101);

    var baselineRuntime = new NetworkRuntime();
    baselineRuntime.Apply(Enumerable.Range(1,points.Length).Select(i =>
        new TopologyChange(TopologyOperation.AddNode,
            new NodeId((ulong)i),Position:points[i-1])).ToArray());
    var global = new SharedMediumNetwork(baselineRuntime,bytesPerTick:400,
        airtimeScope:RadioAirtimeScope.ChannelWide);
    global.Update(Enumerable.Range(1,points.Length).Select(i =>
        new RadioNodeUpdate(new NodeId((ulong)i),
            Profile:new RadioProfile(14,1,8),ChangeProfile:true)).ToArray());
    Check(global.TryTransmitHop(new NodeId(1),new NodeId(2),400,100)
        ==RadioHopOutcome.TransmittedRadio);
    Check(global.TryTransmitHop(new NodeId(3),new NodeId(4),400,100)
        ==RadioHopOutcome.ChannelBusy);
    Check(global.Stats().SpatialSuccessfulHops==0);
}

static void CheckRadioAStar()
{
    // Preserve optimality across moving radio nodes, profile changes, cable
    // shortcuts and disconnected networks. Baseline is the exact Dijkstra.
    for (var seed = 0; seed < 4; seed++)
    {
        var rng = new Random(4410 + seed);
        var runtime = new NetworkRuntime();
        var nodes = Enumerable.Range(1, 100)
            .Select(i => new TopologyChange(TopologyOperation.AddNode,
                new NodeId((ulong)i),
                Position: new NodePosition(rng.Next(-80, 81), rng.Next(-80, 81),
                    rng.Next(0, 2)))).ToArray();
        runtime.Apply(nodes);
        var medium = new SharedMediumNetwork(runtime, searchMode: SharedRadioSearchMode.AStar,
            maximumRadioRange: 26, minimumRadioCost: 3);
        var startup = Enumerable.Range(1, 100).Select(i =>
            new RadioNodeUpdate(new NodeId((ulong)i),
                Profile: new RadioProfile(rng.Next(12, 27),
                    (uint)rng.Next(1, 3), (uint)rng.Next(3, 13)),
                ChangeProfile: true)).ToArray();
        medium.Update(startup);
        for (var batch = 0; batch < 12; batch++)
        {
            if (batch == 5)
            {
                // A long cable invalidates the spatial heuristic; A* must
                // fall back to an exact zero heuristic for mixed graphs.
                medium.SetCable(new NodeId(1), new NodeId(100), 1);
            }
            else if (batch == 8)
                medium.SetCable(new NodeId(1), new NodeId(100), null);

            var snapshot = medium.Snapshot;
            Check(snapshot.HasPhysicalLinks == (batch is >= 5 and < 8));
            for (var query = 0; query < 45; query++)
            {
                var from = new NodeId((ulong)rng.Next(1, 101));
                var to = new NodeId((ulong)rng.Next(1, 101));
                var baseline = SharedMediumNetwork.RouteCore(snapshot, from, to);
                var astar = SharedMediumNetwork.RouteAStarCore(snapshot, from, to,
                    maximumRadioRange: 26, minimumRadioCost: 3);
                Check(astar.Found == baseline.Found);
                Check(astar.Cost == baseline.Cost);
                Check(astar.Version == baseline.Version);
                if (astar.Found)
                {
                    Check(astar.Path[0] == from && astar.Path[^1] == to);
                    Check(astar.Explored >= 1);
                }
                Check(medium.Route(from,to).Cost == baseline.Cost);
            }
            var changed = new List<RadioNodeUpdate>();
            for (var k = 0; k < 8; k++)
            {
                var id = new NodeId((ulong)(batch * 8 + k + 1));
                var profile = k == 0
                    ? (RadioProfile?)null
                    : new RadioProfile(rng.Next(12, 27), (uint)rng.Next(1, 3),
                        (uint)rng.Next(3, 13));
                var current = medium.Snapshot.Physical.Nodes[id].Position!.Value;
                changed.Add(new RadioNodeUpdate(id,
                    Position: new NodePosition(current.X + rng.Next(-15, 16),
                        current.Y + rng.Next(-15, 16), current.Z),
                    Profile: profile, ChangeProfile: true));
            }
            medium.Update(changed);
        }
    }

    // Dense shared-radio geometry: a straight route uses the same cost.
    var grid = new NetworkRuntime();
    grid.Apply(Enumerable.Range(1, 900).Select(i =>
        new TopologyChange(TopologyOperation.AddNode, new NodeId((ulong)i),
            Position: new NodePosition((i-1)%30*12,(i-1)/30*12))).ToArray());
    var radioGrid = new SharedMediumNetwork(grid,
        searchMode: SharedRadioSearchMode.AStar,maximumRadioRange:14,minimumRadioCost:8);
    radioGrid.Update(Enumerable.Range(1,900).Select(i =>
        new RadioNodeUpdate(new NodeId((ulong)i),
            Profile:new RadioProfile(14, 1, 8),ChangeProfile:true)).ToArray());
    var pinned = radioGrid.Snapshot;
    var start = new NodeId(1); var end = new NodeId(900);
    var d = SharedMediumNetwork.RouteCore(pinned, start, end);
    var a = radioGrid.Route(start, end);
    Check(a.Found && a.Cost == d.Cost);
    Check(a.Explored <= d.Explored);
}

static void CheckRadioSafety()
{
    // Fast-fail density admission: 130 devices sharing one cell cannot
    // accidentally create a quadratic radio clique.
    var dense = new NetworkRuntime();
    var radio = new DynamicRadioNetwork(dense);
    dense.Apply(Enumerable.Range(1, 130)
        .Select(i => new TopologyChange(TopologyOperation.AddNode,
            new NodeId((ulong)i), Position: new NodePosition(0, 0))).ToArray());
    var version = dense.Topology.Snapshot.Version;
    var rejectedDensity = false;
    try
    {
        radio.Update(Enumerable.Range(1, 130)
            .Select(i => new RadioNodeUpdate(new NodeId((ulong)i),
                Profile: new RadioProfile(15, 1), ChangeProfile: true)).ToArray());
    }
    catch (InvalidOperationException) { rejectedDensity = true; }
    Check(rejectedDensity);
    Check(dense.Topology.Snapshot.Version == version);
    Check(radio.Stats().ActiveWirelessLinks == 0);
    Check(radio.Stats().ConfiguredNodes == 0);

    var runtime = new NetworkRuntime();
    var manager = new DynamicRadioNetwork(runtime);
    var a = new NodeId(1); var b = new NodeId(2);
    runtime.Apply([
        new TopologyChange(TopologyOperation.AddNode, a, Position: new NodePosition(0, 0)),
        new TopologyChange(TopologyOperation.AddNode, b, Position: new NodePosition(10, 0))
    ]);
    manager.Update([
        new RadioNodeUpdate(a, Profile: new RadioProfile(15, 1), ChangeProfile: true),
        new RadioNodeUpdate(b, Profile: new RadioProfile(15, 1), ChangeProfile: true)
    ]);
    Check(runtime.Route(a, b).Found);

    // A previously connected neighbor can teleport to the Int32 extrema.
    // Squaring this delta without a magnitude check overflows Int64.
    manager.Update([new RadioNodeUpdate(b,
        Position: new NodePosition(int.MinValue, int.MinValue))]);
    Check(!runtime.Route(a, b).Found);
    manager.Update([new RadioNodeUpdate(b,
        Position: new NodePosition(int.MaxValue, int.MaxValue))]);
    Check(!runtime.Route(a, b).Found);

    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    var lastVersion = runtime.Topology.Snapshot.Version;
    var wasCancelled = false;
    try
    {
        manager.Update([new RadioNodeUpdate(b,
            Position: new NodePosition(1, 1))], cancelled.Token);
    }
    catch (OperationCanceledException) { wasCancelled = true; }
    Check(wasCancelled);
    Check(runtime.Topology.Snapshot.Version == lastVersion);
}

static void CheckWirelessTopology()
{
    var runtime = new NetworkRuntime();
    var radio = new DynamicRadioNetwork(runtime);
    var a = new NodeId(1);
    var b = new NodeId(2);
    var c = new NodeId(3);
    var d = new NodeId(4);
    runtime.Apply([
        new TopologyChange(TopologyOperation.AddNode, a, Position: new NodePosition(0, 0)),
        new TopologyChange(TopologyOperation.AddNode, b, Position: new NodePosition(10, 0)),
        new TopologyChange(TopologyOperation.AddNode, c, Position: new NodePosition(22, 0)),
        new TopologyChange(TopologyOperation.AddNode, d, Position: new NodePosition(0, 9, 1))
    ]);
    var before = runtime.Topology.Snapshot;
    var options = new RadioProfile(14, 1, 8);
    var registered = radio.Update([
        new RadioNodeUpdate(a, Profile: options, ChangeProfile: true),
        new RadioNodeUpdate(b, Profile: options, ChangeProfile: true),
        new RadioNodeUpdate(c, Profile: options, ChangeProfile: true),
        new RadioNodeUpdate(d, Profile: options, ChangeProfile: true)
    ]);
    Check(registered.AddedLinks == 2);
    Check(radio.Stats().ActiveWirelessLinks == 2);
    Check(!before.Nodes[a].Links.ContainsKey(b));
    Check(runtime.Topology.Snapshot.Nodes[a].Links[b].Cost == 8);
    Check(runtime.Route(a, c).Cost == 16);
    Check(!runtime.Topology.Snapshot.Nodes[a].Links.ContainsKey(d)); // Z-level isolation

    // Parallel movements are evaluated against the final positions, not stale spatial cells.
    var move = radio.Update([
        new RadioNodeUpdate(a, Position: new NodePosition(-25, 0)),
        new RadioNodeUpdate(b, Position: new NodePosition(-20, 0))
    ]);
    Check(move.Moves == 2 && move.AddedLinks == 0 && move.RemovedLinks == 1);
    Check(!runtime.Route(a, c).Found);
    Check(runtime.Route(a, b).Found);
    Check(runtime.Topology.Snapshot.Nodes[a].Links[b].Cost == 8);
    Check(radio.SetCable(b, c, 2)); // Cable survives wireless movement and outages.
    Check(runtime.Route(a, c).Cost == 10);
    Check(!radio.SetCable(b, c, 2));
    Check(radio.SetCable(a, b, 1));
    Check(runtime.Route(a, c).Cost == 3);
    Check(radio.SetCable(a, b, null));
    Check(runtime.Route(a, c).Cost == 10);

    var disconnected = radio.Update([
        new RadioNodeUpdate(a, Profile: null, ChangeProfile: true),
        new RadioNodeUpdate(b, Profile: new RadioProfile(14, 2), ChangeProfile: true)
    ]);
    Check(disconnected.RemovedLinks == 1);
    Check(!runtime.Route(a, b).Found);
    Check(runtime.Route(b, c).Cost == 2);
    Check(radio.RemoveNodes([b]) == 1);
    Check(!runtime.Topology.Snapshot.Nodes[c].Links.ContainsKey(b));
    Check(radio.Stats().ActiveWirelessLinks == 0);
    Check(radio.Stats().RegisteredCables == 0);
    Check(!runtime.Route(a, c).Found);

    // Radio-only movement must not change route connectivity version if no links change.
    var staticVersion = runtime.Topology.Snapshot.ConnectivityVersion;
    radio.Update([new RadioNodeUpdate(c, Position: new NodePosition(25, 0))]);
    Check(runtime.Topology.Snapshot.ConnectivityVersion == staticVersion);

    bool rejected = false;
    try { radio.SetCable(a, a, 5); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected);
}

static void CheckRadioOracle()
{
    for (var seed = 0; seed < 5; seed++)
    {
        var rng = new Random(8554 + seed);
        var runtime = new NetworkRuntime();
        var manager = new DynamicRadioNetwork(runtime);
        const int count = 72;
        var points = new NodePosition[count];
        var profiles = new RadioProfile?[count];
        var commands = new List<TopologyChange>(count);
        for (var i = 0; i < count; i++)
        {
            points[i] = new NodePosition(rng.Next(-45, 46), rng.Next(-45, 46), rng.Next(0, 2));
            commands.Add(new TopologyChange(TopologyOperation.AddNode,
                new NodeId((ulong)(i + 1)), Position: points[i]));
        }
        runtime.Apply(commands);
        var initial = new List<RadioNodeUpdate>();
        for (var i = 0; i < count; i++)
        {
            profiles[i] = new RadioProfile(rng.Next(10, 22), (uint)rng.Next(1, 4),
                (uint)rng.Next(2, 12));
            initial.Add(new RadioNodeUpdate(new NodeId((ulong)(i + 1)),
                Profile: profiles[i], ChangeProfile: true));
        }
        manager.Update(initial);
        VerifyOracle();

        for (var batch = 0; batch < 40; batch++)
        {
            var changed = new HashSet<int>();
            var updates = new List<RadioNodeUpdate>();
            for (var n = 0; n < 6; n++)
            {
                var i = rng.Next(count);
                if (!changed.Add(i)) continue;
                var mutateProfile = rng.Next(4) == 0;
                if (mutateProfile)
                {
                    profiles[i] = rng.Next(6) == 0
                        ? null
                        : new RadioProfile(rng.Next(10, 22), (uint)rng.Next(1, 4),
                            (uint)rng.Next(2, 12));
                    updates.Add(new RadioNodeUpdate(new NodeId((ulong)(i + 1)),
                        Profile: profiles[i], ChangeProfile: true));
                }
                else
                {
                    var old = points[i];
                    points[i] = new NodePosition(old.X + rng.Next(-8, 9),
                        old.Y + rng.Next(-8, 9), rng.Next(0, 2));
                    updates.Add(new RadioNodeUpdate(new NodeId((ulong)(i + 1)),
                        Position: points[i]));
                }
            }
            manager.Update(updates);
            VerifyOracle();
        }

        void VerifyOracle()
        {
            var snapshot = runtime.Topology.Snapshot;
            int expectedConnections = 0;
            for (var i = 0; i < count; i++)
            for (var j = i + 1; j < count; j++)
            {
                var aa = profiles[i]; var bb = profiles[j];
                var dx = (long)points[i].X - points[j].X;
                var dy = (long)points[i].Y - points[j].Y;
                var distanceSquared = dx * dx + dy * dy;
                var shouldLink = aa.HasValue && bb.HasValue &&
                    aa.Value.Channel == bb.Value.Channel &&
                    points[i].Z == points[j].Z &&
                    distanceSquared <= (long)Math.Min(aa.Value.Range, bb.Value.Range)
                        * Math.Min(aa.Value.Range, bb.Value.Range);
                var idA = new NodeId((ulong)(i + 1));
                var idB = new NodeId((ulong)(j + 1));
                var actual = snapshot.Nodes[idA].Links.TryGetValue(idB, out var link);
                Check(actual == shouldLink);
                if (!shouldLink) continue;
                expectedConnections++;
                Check(link.Cost == Math.Max(aa!.Value.Cost, bb!.Value.Cost));
            }
            Check(manager.Stats().ActiveWirelessLinks == expectedConnections);
        }
    }
}

static void CheckRoutingEquivalence()
{
    for (var seed = 0; seed < 30; seed++)
    {
        var random = new Random(9_601 + seed);
        var graph = new TopologyStore();
        var size = 24 + random.Next(70);
        var changes = new List<TopologyChange>();
        for (var i = 1; i <= size; i++)
            changes.Add(new TopologyChange(TopologyOperation.AddNode, new NodeId((ulong)i)));
        // Mixed connected, disconnected, sparse and redundant-link graphs.
        for (var i = 2; i <= size; i++)
        {
            if (random.NextDouble() > 0.25)
                changes.Add(new TopologyChange(TopologyOperation.Connect,
                    new NodeId((ulong)(i - 1)), new NodeId((ulong)i), (uint)random.Next(1, 25)));
        }
        for (var i = 0; i < size * 2; i++)
        {
            var from = new NodeId((ulong)random.Next(1, size + 1));
            var to = new NodeId((ulong)random.Next(1, size + 1));
            if (from == to) continue;
            changes.Add(new TopologyChange(TopologyOperation.Connect, from, to,
                (uint)random.Next(1, 25)));
        }
        graph.ApplyBatch(changes);
        var snapshot = graph.Snapshot;
        var compact = CompactGraph.Build(snapshot);
        Check(compact.NodeCount == size);
        Check(compact.DirectedArcCount == snapshot.Nodes.Values.Sum(n => n.Links.Count));
        for (var i = 0; i < 55; i++)
        {
            var from = new NodeId((ulong)random.Next(1, size + 2));
            var to = new NodeId((ulong)random.Next(1, size + 2));
            var baseline = Router.Find(snapshot, from, to);
            var alternatives = new[]
            {
                BidirectionalRouter.Find(snapshot, from, to),
                CompactRouter.Dijkstra(compact, from, to),
                CompactRouter.Bidirectional(compact, from, to)
            };
            foreach (var result in alternatives)
            {
                Check(result.Found == baseline.Found);
                Check(result.Cost == baseline.Cost);
                Check(result.Version == baseline.Version);
                if (result.Found) VerifyPath(snapshot, from, to, result);
            }
            if (baseline.Found) VerifyPath(snapshot, from, to, baseline);
        }
    }
}

static void VerifyPath(TopologySnapshot graph, NodeId from, NodeId to, RouteResult route)
{
    Check(route.Path.Count > 0 && route.Path[0] == from && route.Path[^1] == to);
    ulong cost = 0;
    for (var i = 1; i < route.Path.Count; i++)
    {
        Check(graph.Nodes[route.Path[i-1]].Links.TryGetValue(route.Path[i], out var edge));
        cost += edge.Cost;
    }
    Check(cost == route.Cost);
}

static void CheckRouteCacheBudgets()
{
    var runtime = new NetworkRuntime();
    runtime.Apply([
        new TopologyChange(TopologyOperation.AddNode, new NodeId(1)),
        new TopologyChange(TopologyOperation.AddNode, new NodeId(2)),
        new TopologyChange(TopologyOperation.AddNode, new NodeId(3)),
        new TopologyChange(TopologyOperation.Connect, new NodeId(1), new NodeId(2)),
        new TopologyChange(TopologyOperation.Connect, new NodeId(2), new NodeId(3))
    ]);
    Check(!runtime.Route(new NodeId(1), new NodeId(3), maxExplored: 1).Found);
    Check(runtime.Route(new NodeId(1), new NodeId(3)).Found);
    Check(runtime.Route(new NodeId(1), new NodeId(3)).Found);
    Check(runtime.Metrics.Capture(runtime.Cache.Count).CacheHits == 1);
}


static void Check(bool condition)
{
    if (!condition) throw new InvalidOperationException("Smoke test failed.");
}
