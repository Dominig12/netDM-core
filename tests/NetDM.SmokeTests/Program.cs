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
CheckDenseRadioAStar();
CheckPacketTransport();
CheckAdaptivePacketTransport();
CheckEventDrivenPacketWaiting();
CheckEventDrivenRerouteCapacity();
CheckTrafficPriorities();
CheckCongestionAndLatencyTelemetry();
CheckPacketReceiptContract();
CheckInteractiveRerouteQos();
CheckRerouteAdaptiveBackoff();
await CheckDedicatedPacketTicksAsync();
CheckSpatialRadioAirtime();
CheckInteractiveAirtimeQos();
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

static void CheckAdaptivePacketTransport()
{
    static (SharedMediumNetwork Medium, RouteResult Route) MakeTwoNodes()
    {
        var runtime = new NetworkRuntime();
        runtime.Apply([
            new TopologyChange(TopologyOperation.AddNode, new NodeId(1),
                Position:new NodePosition(0,0)),
            new TopologyChange(TopologyOperation.AddNode, new NodeId(2),
                Position:new NodePosition(10,0))
        ]);
        var medium = new SharedMediumNetwork(runtime, bytesPerTick:1_000_000);
        medium.Update([
            new RadioNodeUpdate(new NodeId(1),
                Profile:new RadioProfile(14,1,8),ChangeProfile:true),
            new RadioNodeUpdate(new NodeId(2),
                Profile:new RadioProfile(14,1,8),ChangeProfile:true)
        ]);
        var route = medium.Route(new NodeId(1), new NodeId(2));
        Check(route.Found && route.Path.Count==2);
        return (medium,route);
    }

    var (radio,route) = MakeTwoNodes();
    var admission = new RadioPacketTransport(radio, new PacketTransportOptions(
        Capacity:2,MaxAdvancesPerTick:2));
    var lease1 = admission.TryReserveAdmission();
    var lease2 = admission.TryReserveAdmission();
    Check(lease1 is not null && lease2 is not null);
    Check(admission.TryReserveAdmission() is null);
    Check(!admission.Enqueue(route,1,0)); // Cannot steal a reserved slot.
    Check(admission.Capture().PreRouteBackpressureRejections==1);
    Check(admission.Capture().ReservedAdmissions==2);
    lease1!.Dispose();
    lease1.Dispose(); // Must not double-release.
    Check(admission.Capture().ReservedAdmissions==1);
    Check(admission.Enqueue(route,1,0)); // Unreserved free slot.
    Check(admission.EnqueueReserved(route,1,0,lease2!));
    lease2!.Dispose(); // Consumed lease is now inert.
    Check(admission.Capture().ReservedAdmissions==0);
    Check(admission.Capture().Enqueued==2);
    Check(admission.TryReserveAdmission() is null);
    Check(admission.Advance(1)==2);
    Check(admission.Capture().Delivered==2);

    var (failedRadio,failedRoute) = MakeTwoNodes();
    var failed = new RadioPacketTransport(failedRadio,
        new PacketTransportOptions(Capacity:1));
    var invalidLease = failed.TryReserveAdmission();
    Check(invalidLease is not null);
    Check(!failed.EnqueueReserved(RouteResult.Missing(
        failedRadio.Snapshot.Version),1,0,invalidLease!));
    invalidLease!.Dispose();
    Check(failed.Capture().ReservedAdmissions==0);
    Check(failed.Capture().InvalidRejections==1);
    // A route cancelled BEFORE it executes simply disposes the lease.
    using(var cancelLease = failed.TryReserveAdmission())
    {
        Check(cancelLease is not null);
        Check(failed.TryReserveAdmission() is null);
    }
    Check(failed.Capture().ReservedAdmissions==0);
    Check(failed.Enqueue(failedRoute,1,0));

    var (concurrentRadio,_) = MakeTwoNodes();
    var concurrent = new RadioPacketTransport(concurrentRadio,
        new PacketTransportOptions(Capacity:24));
    var leases = new System.Collections.Concurrent.ConcurrentBag<PacketAdmissionLease>();
    Parallel.For(0,100, _ =>
    {
        var lease = concurrent.TryReserveAdmission();
        if(lease is not null)leases.Add(lease);
    });
    Check(leases.Count==24);
    Check(concurrent.Capture().ReservedAdmissions==24);
    Parallel.ForEach(leases, lease => lease.Dispose());
    Check(concurrent.Capture().ReservedAdmissions==0);

    // A high forwarding workload should grow the visit ceiling while
    // still preserving one hop per packet and respecting queue capacity.
    var (fastRadio,fastRoute)=MakeTwoNodes();
    var fast=new RadioPacketTransport(fastRadio,new PacketTransportOptions(
        Capacity:4096, MaxAdvancesPerTick:4096,
        MaxTickWorkMilliseconds:1000, AdaptivePacketVisits:true));
    for(int i=0;i<4096;i++)
        Check(fast.Enqueue(fastRoute,1,0));
    Check(fast.Capture().CurrentVisitLimit==1024);
    Check(fast.Advance(1)==1024);
    Check(fast.Advance(1)==0);
    Check(fast.Capture().CurrentVisitLimit>1024);
    for(int tick=2;tick<=10 && fast.Capture().Queued>0;tick++)
        fast.Advance(tick);
    var stats=fast.Capture();
    Check(stats.Delivered==4096 && stats.Queued==0);
    Check(stats.ForwardingVisits==4096);
    Check(stats.AdaptiveLimitIncreases>0);
    Check(stats.CurrentVisitLimit<=4096);
    Check(stats.VisitBudgetStops>0);

    // Fixed mode stays identical to the previous transport behavior.
    var (legacyRadio,legacyRoute)=MakeTwoNodes();
    var legacy=new RadioPacketTransport(legacyRadio,new PacketTransportOptions(
        Capacity:2048,MaxAdvancesPerTick:1024,AdaptivePacketVisits:false));
    for(int i=0;i<2048;i++)
        Check(legacy.Enqueue(legacyRoute,1,0));
    Check(legacy.Advance(1)==1024);
    Check(legacy.Capture().Queued==1024);
    Check(legacy.Capture().CurrentVisitLimit==1024);
    Check(legacy.Capture().AdaptiveLimitIncreases==0);
    Check(legacy.Advance(2)==1024);
    Check(legacy.Capture().Delivered==2048);
}

static void CheckEventDrivenPacketWaiting()
{
    var runtime = new NetworkRuntime();
    runtime.Apply([
        new TopologyChange(TopologyOperation.AddNode, new NodeId(1),
            Position: new NodePosition(0, 0)),
        new TopologyChange(TopologyOperation.AddNode, new NodeId(2),
            Position: new NodePosition(10, 0)),
        new TopologyChange(TopologyOperation.AddNode, new NodeId(3),
            Position: new NodePosition(20, 0))
    ]);
    var medium = new SharedMediumNetwork(runtime, bytesPerTick: 8192);
    var active = new RadioProfile(14, 1, 8);
    medium.Update(Enumerable.Range(1, 3).Select(i =>
        new RadioNodeUpdate(new NodeId((ulong)i),
            Profile: active, ChangeProfile: true)).ToArray());
    var route = medium.Route(new NodeId(1), new NodeId(3));
    Check(route.Found && route.Path.Count == 3);
    var queue = new RadioPacketTransport(medium, new PacketTransportOptions(
        Capacity: 1, MaxAgeTicks: 20, EventDrivenPacketWaiting: true,
        MaxNoRouteRetryDelayTicks: 12));
    Check(queue.Enqueue(route, 128, 0));
    Check(queue.Advance(1) == 1);
    Check(queue.Advance(1) == 0); // One hop at most per tick.
    var disabled = new RadioNodeUpdate(new NodeId(3),
        Profile: null, ChangeProfile: true);
    medium.Update([disabled]);
    queue.NotifyRadioUpdates([disabled]);
    Check(queue.Advance(2) == 0);
    Check(queue.Capture().AwaitingReroutePackets == 1);
    Check(queue.TryTakeReroute(out var first));
    var missing = medium.Route(first.Source, first.Destination);
    Check(!missing.Found && queue.CompleteReroute(first, missing));
    Check(queue.Capture().DelayedPackets == 1);
    Check(queue.TryReserveAdmission() is null); // Parked packet occupies capacity.
    queue.Advance(3);
    queue.Advance(4);
    Check(queue.Capture().ForwardingVisits == 2); // No cooldown scans.
    Check(queue.Advance(5) == 0);
    Check(queue.TryTakeReroute(out var second));
    var repaired = new RadioNodeUpdate(new NodeId(3),
        Profile: active, ChangeProfile: true);
    medium.Update([repaired]);
    queue.NotifyRadioUpdates([repaired]);
    Check(queue.Capture().TopologyWakeups > 0);
    Check(queue.Advance(6) == 1);
    Check(queue.Capture().Delivered == 1 && queue.Capture().Queued == 0);
    Check(!queue.CompleteReroute(second, missing)); // Stale result cannot revive packet.
    Check(queue.Capture().ReservedAdmissions == 0);

    var expiring = new RadioPacketTransport(medium, new PacketTransportOptions(
        MaxAgeTicks: 3, EventDrivenPacketWaiting: true));
    var direct = medium.Route(new NodeId(1), new NodeId(3));
    Check(direct.Found);
    // SharedMediumNetwork tracks airtime ticks independently of transport
    // instances; this second case must use later ticks than the prior case.
    Check(expiring.Enqueue(direct, 100, 7));
    Check(expiring.Advance(8) == 1);
    medium.Update([disabled]);
    expiring.NotifyRadioUpdates([disabled]);
    Check(expiring.Advance(9) == 0);
    Check(expiring.Capture().AwaitingReroutePackets == 1);
    Check(expiring.Advance(10) == 0);
    var ttl = expiring.Capture();
    Check(ttl.Expired == 1 && ttl.ExpiredWhileParked == 1 && ttl.Queued == 0);
    Check(ttl.AwaitingReroutePackets == 0);
}

static void CheckEventDrivenRerouteCapacity()
{
    var runtime = new NetworkRuntime();
    runtime.Apply([
        new TopologyChange(TopologyOperation.AddNode, new NodeId(1),
            Position: new NodePosition(0, 0)),
        new TopologyChange(TopologyOperation.AddNode, new NodeId(2),
            Position: new NodePosition(10, 0)),
        new TopologyChange(TopologyOperation.AddNode, new NodeId(3),
            Position: new NodePosition(20, 0))
    ]);
    var medium = new SharedMediumNetwork(runtime, bytesPerTick: 8192);
    var profile = new RadioProfile(14, 1, 8);
    medium.Update(Enumerable.Range(1, 3).Select(i => new RadioNodeUpdate(
        new NodeId((ulong)i), Profile: profile, ChangeProfile: true)).ToArray());
    var route = medium.Route(new NodeId(1), new NodeId(3));
    Check(route.Found && route.Path.Count == 3);
    var packets = new RadioPacketTransport(medium, new PacketTransportOptions(
        MaxAgeTicks: 100, MaxPendingReroutes: 1, MaxConcurrentReroutes: 1,
        EventDrivenPacketWaiting: true, EventDrivenRerouteCapacity: true));
    Check(packets.Enqueue(route, 100, 0));
    Check(packets.Enqueue(route, 100, 0));
    Check(packets.Advance(1) == 2);
    var disabled = new RadioNodeUpdate(new NodeId(3), Profile: null, ChangeProfile: true);
    medium.Update([disabled]);
    packets.NotifyRadioUpdates([disabled]);
    Check(packets.Advance(2) == 0);
    Check(packets.Capture().CapacityBlockedPackets == 1);
    Check(packets.Capture().AwaitingReroutePackets == 1);
    Check(packets.TryTakeReroute(out var ticket));
    var missing = medium.Route(ticket.Source, ticket.Destination);
    Check(!missing.Found);
    Check(packets.CompleteReroute(ticket, missing));
    Check(packets.Capture().CapacityWakeups == 1);
    Check(packets.Capture().CapacityBlockedPackets == 0);
    Check(packets.Advance(3) == 0);
    Check(packets.Capture().RerouteQueued == 1);
    // Cable repair without radio update: the safety probe must recover.
    Check(medium.SetCable(new NodeId(2), new NodeId(3), 1));
    for (int tick = 4; tick <= 40 && packets.Capture().Queued > 0; tick++)
        packets.Advance(tick);
    var result = packets.Capture();
    Check(result.Delivered == 2 && result.Queued == 0);
    Check(result.CapacityBlockedPackets == 0 && result.ReservedAdmissions == 0);
    Check(!packets.CompleteReroute(ticket, missing));
}

static void CheckTrafficPriorities()
{
    var runtime = new NetworkRuntime();
    runtime.Apply([
        new TopologyChange(TopologyOperation.AddNode, new NodeId(1),
            Position: new NodePosition(0, 0)),
        new TopologyChange(TopologyOperation.AddNode, new NodeId(2),
            Position: new NodePosition(10, 0))
    ]);
    var medium = new SharedMediumNetwork(runtime, bytesPerTick: 8192);
    medium.Update([
        new RadioNodeUpdate(new NodeId(1),
            Profile: new RadioProfile(14, 1, 8), ChangeProfile: true),
        new RadioNodeUpdate(new NodeId(2),
            Profile: new RadioProfile(14, 1, 8), ChangeProfile: true)
    ]);
    var route = medium.Route(new NodeId(1), new NodeId(2));
    Check(route.Found);
    var options = new PacketTransportOptions(Capacity: 16, MaxAdvancesPerTick: 1,
        MaxAgeTicks: 100, EventDrivenPacketWaiting: true,
        PrioritizeInteractiveTraffic: true, InteractiveReservedSlots: 2);
    var packets = new RadioPacketTransport(medium, options);
    for (int i = 0; i < 6; i++)
        Check(packets.Enqueue(route, 100, 0, PacketTrafficClass.Interactive));
    for (int i = 0; i < 3; i++)
        Check(packets.Enqueue(route, 100, 0, PacketTrafficClass.Background));
    for (int tick = 1; tick <= 4; tick++)
        packets.Advance(tick);
    var stats = packets.Capture();
    Check(stats.InteractiveDelivered == 3 && stats.BackgroundDelivered == 1);
    Check(stats.InteractiveQueued == 3 && stats.BackgroundQueued == 2);
    Check(stats.Queued == stats.InteractiveQueued + stats.BackgroundQueued);
    for (int tick = 5; tick <= 20; tick++) packets.Advance(tick);
    stats = packets.Capture();
    Check(stats.Delivered == 9 && stats.Queued == 0);
    Check(stats.InteractiveDelivered == 6 && stats.BackgroundDelivered == 3);
    Check(stats.InteractiveDeliveryAgeP95Ticks > 0);
    Check(stats.BackgroundDeliveryAgeP95Ticks > 0);

    // Background queue cannot consume the two reserved interactive slots.
    // A consumed lease must keep its class to prevent reservation bypass.
    var reserved = new RadioPacketTransport(medium,
        options with { Capacity = 8, MaxAdvancesPerTick = 8 });
    for (int i = 0; i < 5; i++)
        Check(reserved.Enqueue(route, 100, 0, PacketTrafficClass.Background));
    using var backgroundLease = reserved.TryReserveAdmission(PacketTrafficClass.Background);
    Check(backgroundLease is not null);
    Check(reserved.TryReserveAdmission(PacketTrafficClass.Background) is null);
    using var firstInteractive = reserved.TryReserveAdmission(PacketTrafficClass.Interactive);
    using var secondInteractive = reserved.TryReserveAdmission(PacketTrafficClass.Interactive);
    Check(firstInteractive is not null && secondInteractive is not null);
    Check(reserved.TryReserveAdmission(PacketTrafficClass.Interactive) is null);
    Check(reserved.EnqueueReserved(route, 100, 0, backgroundLease!));
    Check(reserved.EnqueueReserved(route, 100, 0, firstInteractive!));
    Check(reserved.EnqueueReserved(route, 100, 0, secondInteractive!));
    var capacity = reserved.Capture();
    Check(capacity.Queued == 8 && capacity.ReservedAdmissions == 0);
    Check(capacity.BackgroundPreRouteRejections == 1);
    Check(capacity.InteractivePreRouteRejections == 1);
    Check(capacity.InteractiveQueued == 2 && capacity.BackgroundQueued == 6);
    // Same shared medium was already advanced to tick 20 by the first test.
    reserved.Advance(21);
    Check(reserved.Capture().Queued < 8);
    using (var abandoned = reserved.TryReserveAdmission(PacketTrafficClass.Interactive))
        Check(abandoned is not null);
    Check(reserved.Capture().ReservedAdmissions == 0);

    // Weighted lanes must not process the same packet twice per tick.
    var chain = new NetworkRuntime();
    chain.Apply([
        new TopologyChange(TopologyOperation.AddNode, new NodeId(1),
            Position: new NodePosition(0, 0)),
        new TopologyChange(TopologyOperation.AddNode, new NodeId(2),
            Position: new NodePosition(10, 0)),
        new TopologyChange(TopologyOperation.AddNode, new NodeId(3),
            Position: new NodePosition(20, 0))
    ]);
    var chainMedium = new SharedMediumNetwork(chain, bytesPerTick: 8192);
    chainMedium.Update(Enumerable.Range(1, 3).Select(x => new RadioNodeUpdate(
        new NodeId((ulong)x), Profile: new RadioProfile(14, 1, 8),
        ChangeProfile: true)).ToArray());
    var chainRoute = chainMedium.Route(new NodeId(1), new NodeId(3));
    Check(chainRoute.Found && chainRoute.Path.Count == 3);
    var twoHop = new RadioPacketTransport(chainMedium,
        options with { Capacity = 32, MaxAdvancesPerTick = 32 });
    for (int i = 0; i < 6; i++)
    {
        Check(twoHop.Enqueue(chainRoute, 100, 0, PacketTrafficClass.Interactive));
        Check(twoHop.Enqueue(chainRoute, 100, 0, PacketTrafficClass.Background));
    }
    Check(twoHop.Advance(1) == 12);
    Check(twoHop.Capture().Delivered == 0);
    Check(twoHop.Capture().FirstHopSamples == 12);
    Check(twoHop.Advance(2) == 12);
    Check(twoHop.Capture().Delivered == 12);

    bool rejected = false;
    try { new PacketTransportOptions(PrioritizeInteractiveTraffic: true).Validate(); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected);
}

static void CheckCongestionAndLatencyTelemetry()
{
    var runtime = new NetworkRuntime();
    runtime.Apply([
        new TopologyChange(TopologyOperation.AddNode, new NodeId(1),
            Position: new NodePosition(0, 0)),
        new TopologyChange(TopologyOperation.AddNode, new NodeId(2),
            Position: new NodePosition(10, 0))
    ]);
    var medium = new SharedMediumNetwork(runtime, bytesPerTick: 8192);
    medium.Update([
        new RadioNodeUpdate(new NodeId(1), Profile: new RadioProfile(14, 1, 8),
            ChangeProfile: true),
        new RadioNodeUpdate(new NodeId(2), Profile: new RadioProfile(14, 1, 8),
            ChangeProfile: true)
    ]);
    var route = medium.Route(new NodeId(1), new NodeId(2));
    Check(route.Found);

    var settings = new PacketTransportOptions(Capacity: 10,
        MaxAdvancesPerTick: 1, MaxAgeTicks: 100,
        EventDrivenPacketWaiting: true, PrioritizeInteractiveTraffic: true,
        BackgroundCongestionHighPercent: 70,
        BackgroundCongestionLowPercent: 30,
        InteractiveLatencyObjectiveTicks: 1);
    var transport = new RadioPacketTransport(medium, settings);
    Check(transport.Enqueue(route, 100, 0, PacketTrafficClass.Interactive));
    for (int i = 0; i < 5; i++)
        Check(transport.Enqueue(route, 100, 0, PacketTrafficClass.Background));
    using var lease = transport.TryReserveAdmission(PacketTrafficClass.Background);
    Check(lease is not null);
    Check(transport.Capture().ReservedAdmissions == 1);
    Check(!transport.Enqueue(route, 100, 0, PacketTrafficClass.Background));
    Check(transport.TryReserveAdmission(PacketTrafficClass.Background) is null);
    var congestion = transport.Capture();
    Check(congestion.BackgroundCongestionThrottled);
    Check(congestion.BackgroundCongestionRejections == 2);
    Check(congestion.QueueFullRejections == 0);
    Check(congestion.BackgroundPreRouteRejections == 1);
    Check(transport.EnqueueReserved(route, 100, 0, lease!));
    Check(transport.Capture().Queued == 7);
    // Completion of the interactive work clears the throttle even with
    // six background packets still waiting above the high watermark.
    Check(transport.Advance(1) == 1);
    Check(transport.Capture().InteractiveDelivered == 1);
    Check(!transport.Capture().BackgroundCongestionThrottled);
    Check(transport.Enqueue(route, 100, 1, PacketTrafficClass.Background));
    Check(transport.Capture().Queued == 7);

    // Outcomes and percentiles cover completed work AND unfinished work.
    // Successful-delivery P95 alone hides packets still queued or expired.
    var latency = new RadioPacketTransport(medium, settings with {
        BackgroundCongestionHighPercent = 0, MaxAgeTicks = 4
    });
    for (int i = 0; i < 6; i++)
        Check(latency.Enqueue(route, 100, 0, PacketTrafficClass.Interactive));
    for (int i = 0; i < 2; i++)
        Check(latency.Enqueue(route, 100, 0, PacketTrafficClass.Background));
    Check(latency.Advance(1) == 1);
    Check(latency.Advance(2) == 1);
    var pending = latency.Capture();
    Check(pending.InteractiveDelivered == 2);
    Check(pending.InteractiveCompletedBeyondObjective == 1);
    Check(pending.InteractiveOutstandingBeyondObjective == 4);
    Check(pending.InteractiveOldestOutstandingAgeTicks == 2);
    Check(pending.BackgroundOldestOutstandingAgeTicks == 2);
    Check(pending.InteractiveFirstHopSamples == 2);
    Check(pending.BackgroundFirstHopSamples == 0);
    Check(pending.InteractiveFirstHopAgeP95Ticks == 2);
    Check(pending.InteractiveFirstHopAgeP99Ticks == 2);
    Check(pending.InteractiveDeliveryAgeP99Ticks == 2);
    Check(latency.Advance(3) == 1);
    latency.Advance(4);
    var finished = latency.Capture();
    Check(finished.InteractiveDelivered == 3);
    Check(finished.InteractiveExpired == 3 && finished.BackgroundExpired == 2);
    Check(finished.Expired == finished.InteractiveExpired + finished.BackgroundExpired);
    Check(finished.Dropped == finished.InteractiveDropped + finished.BackgroundDropped);
    Check(finished.Queued == 0 && finished.InteractiveOutstandingBeyondObjective == 0);

    bool invalid = false;
    try { (settings with { BackgroundCongestionHighPercent = 30,
        BackgroundCongestionLowPercent = 60 }).Validate(); }
    catch (ArgumentOutOfRangeException) { invalid = true; }
    Check(invalid);
    invalid = false;
    try { (settings with { PrioritizeInteractiveTraffic = false }).Validate(); }
    catch (ArgumentException) { invalid = true; }
    Check(invalid);
}

static void CheckPacketReceiptContract()
{
    static (SharedMediumNetwork Medium, RouteResult Route) MakeRadio()
    {
        var runtime = new NetworkRuntime();
        runtime.Apply([
            new TopologyChange(TopologyOperation.AddNode, new NodeId(1),
                Position: new NodePosition(0, 0)),
            new TopologyChange(TopologyOperation.AddNode, new NodeId(2),
                Position: new NodePosition(10, 0))
        ]);
        var medium = new SharedMediumNetwork(runtime, bytesPerTick: 200);
        medium.Update([
            new RadioNodeUpdate(new NodeId(1), Profile: new RadioProfile(14, 1, 8),
                ChangeProfile: true),
            new RadioNodeUpdate(new NodeId(2), Profile: new RadioProfile(14, 1, 8),
                ChangeProfile: true)
        ]);
        var route = medium.Route(new NodeId(1), new NodeId(2));
        Check(route.Found);
        return (medium, route);
    }

    var (medium, route) = MakeRadio();
    var opts = new PacketTransportOptions(
        Capacity: 8, MaxAgeTicks: 10, MaxAdvancesPerTick: 8,
        EventDrivenPacketWaiting: true, ReceiptHistoryCapacity: 2);
    var transport = new RadioPacketTransport(medium, opts);
    var empty = transport.CaptureReceipts();
    Check(empty.StreamId == transport.ReceiptStreamId &&
        empty.Receipts.Length == 0 && empty.LatestSequence == 0 && !empty.Gap);
    var accepted = transport.Submit("dm-interaction-001", route, 60, 0,
        PacketTrafficClass.Interactive);
    Check(accepted.Accepted && accepted.PacketId > 0 &&
        accepted.Rejection == PacketSubmissionRejection.None);
    Check(accepted.StreamId == empty.StreamId && accepted.RequestId == "dm-interaction-001");
    Check(transport.CaptureReceipts().Receipts.Length == 0); // Accepted != delivered.
    Check(transport.Advance(1) == 1);
    var first = transport.CaptureReceipts();
    Check(first.Receipts.Length == 1 && first.Receipts[0].Sequence == 1);
    Check(first.Receipts[0].RequestId == accepted.RequestId &&
        first.Receipts[0].PacketId == accepted.PacketId &&
        first.Receipts[0].Status == PacketCompletionStatus.Delivered &&
        first.Receipts[0].Reason == PacketCompletionReason.Delivered &&
        first.Receipts[0].CompletedTick == 1 &&
        first.Receipts[0].SuccessfulHops == 1);
    Check(transport.CaptureReceipts().Receipts[0] == first.Receipts[0]); // Retry-safe polling.
    Check(transport.CaptureReceipts(first.NextSequence).Receipts.Length == 0);
    Check(!transport.TryCancel(accepted.PacketId)); // Exactly one terminal event.

    var invalid = transport.Submit("bad-route", RouteResult.Missing(route.Version),
        60, 1);
    Check(!invalid.Accepted && invalid.PacketId == 0 &&
        invalid.Rejection == PacketSubmissionRejection.InvalidRoute &&
        invalid.StreamId == accepted.StreamId);
    Check(transport.CaptureReceipts().LatestSequence == 1); // No phantom receipt.

    var oversized = transport.Submit("oversized", route, 300, 1,
        PacketTrafficClass.Background);
    Check(oversized.Accepted);
    transport.Advance(2);
    var timed = transport.Submit("short-ttl", route, 60, 2,
        PacketTrafficClass.Interactive, timeoutTicks: 1);
    Check(timed.Accepted);
    transport.Advance(3);
    var gap = transport.CaptureReceipts(0);
    Check(gap.Gap && gap.OldestSequence == 2 && gap.LatestSequence == 3 &&
        gap.Receipts.Length == 2);
    Check(gap.Receipts[0].RequestId == "oversized" &&
        gap.Receipts[0].Status == PacketCompletionStatus.Dropped &&
        gap.Receipts[0].Reason == PacketCompletionReason.PacketTooLarge);
    Check(gap.Receipts[1].RequestId == "short-ttl" &&
        gap.Receipts[1].Status == PacketCompletionStatus.Expired &&
        gap.Receipts[1].Reason == PacketCompletionReason.TimeToLive &&
        gap.Receipts[1].CompletedTick == 3);
    var chunk = transport.CaptureReceipts(1, 1);
    Check(!chunk.Gap && chunk.Receipts.Length == 1 &&
        chunk.Receipts[0].Sequence == 2 && chunk.NextSequence == 2);
    var tail = transport.CaptureReceipts(chunk.NextSequence, 1);
    Check(tail.Receipts.Length == 1 && tail.NextSequence == 3);
    Check(!tail.Gap);
    var fromFuture = transport.CaptureReceipts(100);
    Check(fromFuture.Gap && fromFuture.Receipts.Length == 2);

    var canceled = transport.Submit("cancel", route, 40, 3);
    Check(canceled.Accepted &&
        !transport.TryCancel(Guid.NewGuid(), canceled.PacketId) &&
        transport.TryCancel(canceled.StreamId, canceled.PacketId) &&
        !transport.TryCancel(canceled.StreamId, canceled.PacketId));
    var cancelEvent = transport.CaptureReceipts(3);
    Check(cancelEvent.Receipts.Length == 1 &&
        cancelEvent.Receipts[0].Status == PacketCompletionStatus.Cancelled &&
        cancelEvent.Receipts[0].Reason == PacketCompletionReason.CancelledByCaller);
    transport.Advance(4);
    Check(transport.CaptureReceipts(3).Receipts.Length == 1);
    Check(transport.Capture().Queued == 0);
    Check(transport.Capture().Expired == 1 && transport.Capture().Dropped == 1);

    // A new instance has a different epoch even though its packet IDs
    // restart at 1; the bridge must not mix receipt cursors across sessions.
    var (newMedium, newRoute) = MakeRadio();
    var other = new RadioPacketTransport(newMedium, opts);
    Check(other.ReceiptStreamId != transport.ReceiptStreamId);
    using var lease = other.TryReserveAdmission(PacketTrafficClass.Interactive);
    Check(lease is not null);
    var leased = other.SubmitReserved("leased", newRoute, 50, 0, lease!);
    Check(leased.Accepted && leased.PacketId == 1 &&
        leased.StreamId == other.ReceiptStreamId);
    Check(other.Capture().ReservedAdmissions == 0);
    Check(other.Advance(1) == 1 && other.CaptureReceipts().Receipts.Length == 1);
    bool threw = false;
    try { other.SubmitReserved("duplicate", newRoute, 50, 1, lease!); }
    catch (InvalidOperationException) { threw = true; }
    Check(threw);
    threw = false;
    try { other.Submit("invalid-id".PadRight(129, 'x'), newRoute, 50, 1); }
    catch (ArgumentException) { threw = true; }
    Check(threw);
    threw = false;
    try { other.Submit("bad-ttl", newRoute, 50, 1, timeoutTicks: 11); }
    catch (ArgumentOutOfRangeException) { threw = true; }
    Check(threw);

    // In legacy FIFO mode cancellation must remove the physical queue
    // entry immediately, otherwise an already-retired packet could forward.
    var (oldMedium, oldRoute) = MakeRadio();
    var fifo = new RadioPacketTransport(oldMedium, opts with {
        EventDrivenPacketWaiting = false
    });
    var oldPacket = fifo.Submit("legacy-cancel", oldRoute, 60, 0);
    Check(oldPacket.Accepted && fifo.Capture().Queued == 1);
    Check(oldPacket.StreamId != transport.ReceiptStreamId);
    using var held = fifo.TryReserveAdmission(PacketTrafficClass.Background,
        out var reserveOk);
    Check(held is not null && reserveOk == PacketSubmissionRejection.None);
    var denied = new RadioPacketTransport(oldMedium,
        opts with { Capacity = 1, EventDrivenPacketWaiting = false });
    using var only = denied.TryReserveAdmission(PacketTrafficClass.Background,
        out var allowed);
    Check(only is not null && allowed == PacketSubmissionRejection.None);
    Check(denied.TryReserveAdmission(PacketTrafficClass.Background,
        out var fullReason) is null &&
        fullReason == PacketSubmissionRejection.QueueFull);
    Check(fifo.TryCancel(oldPacket.StreamId, oldPacket.PacketId) &&
        fifo.Capture().Queued == 0);
    Check(fifo.Advance(1) == 0 && fifo.Capture().Delivered == 0);
    Check(fifo.CaptureReceipts().Receipts.Single().Status ==
        PacketCompletionStatus.Cancelled);

    // A missing radio hop is not a successful route or a TTL expiry.
    var (cutMedium, cutRoute) = MakeRadio();
    var cut = new RadioPacketTransport(cutMedium, opts with { MaxReroutes = 0 });
    var cutSubmit = cut.Submit("cut-link", cutRoute, 50, 0);
    Check(cutSubmit.Accepted);
    var offline = new RadioNodeUpdate(new NodeId(2), Profile: null, ChangeProfile: true);
    cutMedium.Update([offline]);
    cut.NotifyRadioUpdates([offline]);
    cut.Advance(1);
    Check(cut.CaptureReceipts().Receipts.Single().Status == PacketCompletionStatus.Dropped);
    Check(cut.CaptureReceipts().Receipts.Single().Reason == PacketCompletionReason.LinkFailure);

    // Web JSON defaults use camelCase names and numerical enum codes.
    // This is the schema that a future BYOND bridge must match explicitly.
    var payload = System.Text.Json.JsonSerializer.Serialize(
        cancelEvent, new System.Text.Json.JsonSerializerOptions(
            System.Text.Json.JsonSerializerDefaults.Web));
    using (var document = System.Text.Json.JsonDocument.Parse(payload))
    {
        var root = document.RootElement;
        Check(root.GetProperty("streamId").GetGuid() == transport.ReceiptStreamId);
        Check(root.GetProperty("receipts")[0].GetProperty("requestId")
            .GetString() == "cancel");
        Check(root.GetProperty("receipts")[0].GetProperty("status").GetInt32() ==
            (int)PacketCompletionStatus.Cancelled);
        Check(root.GetProperty("nextSequence").GetInt64() == 4);
    }

    // Running transport with receipt capture disabled must still operate.
    var (noMedium, noRoute) = MakeRadio();
    var noReceipts = new RadioPacketTransport(noMedium, opts with {
        ReceiptHistoryCapacity = 0
    });
    Check(noReceipts.Submit("no-retention", noRoute, 50, 0).Accepted);
    noReceipts.Advance(1);
    Check(noReceipts.CaptureReceipts().Receipts.Length == 0);
}

static void CheckInteractiveRerouteQos()
{
    static (SharedMediumNetwork Medium, RouteResult Route) MakeBrokenChain()
    {
        var runtime = new NetworkRuntime();
        runtime.Apply([
            new TopologyChange(TopologyOperation.AddNode, new NodeId(1),
                Position: new NodePosition(0, 0)),
            new TopologyChange(TopologyOperation.AddNode, new NodeId(2),
                Position: new NodePosition(10, 0)),
            new TopologyChange(TopologyOperation.AddNode, new NodeId(3),
                Position: new NodePosition(20, 0))
        ]);
        var radio = new SharedMediumNetwork(runtime, bytesPerTick: 8192);
        var profile = new RadioProfile(14, 1, 8);
        radio.Update(Enumerable.Range(1, 3).Select(x => new RadioNodeUpdate(
            new NodeId((ulong)x), Profile: profile, ChangeProfile: true)).ToArray());
        var route = radio.Route(new NodeId(1), new NodeId(3));
        Check(route.Found && route.Path.Count == 3);
        return (radio, route);
    }

    // Reserving reroute space must include queued AND running background work.
    // Background traffic uses six of eight tickets, leaving two for players.
    var (medium, route) = MakeBrokenChain();
    var options = new PacketTransportOptions(
        Capacity: 32, MaxAgeTicks: 100, MaxAdvancesPerTick: 32,
        MaxPendingReroutes: 8, MaxConcurrentReroutes: 1,
        EventDrivenPacketWaiting: true, EventDrivenRerouteCapacity: true,
        PrioritizeInteractiveTraffic: true,
        PrioritizeInteractiveReroutes: true,
        InteractiveRerouteReservedSlots: 2);
    var packets = new RadioPacketTransport(medium, options);
    for (int i = 0; i < 8; i++)
        Check(packets.Enqueue(route, 100, 0, PacketTrafficClass.Background));
    Check(packets.Advance(1) == 8);
    var offline = new RadioNodeUpdate(new NodeId(3), Profile: null,
        ChangeProfile: true);
    medium.Update([offline]);
    packets.NotifyRadioUpdates([offline]);
    Check(packets.Advance(2) == 0);
    var full = packets.Capture();
    Check(full.RerouteQueued == 6 && full.BackgroundRerouteQueued == 6);
    Check(full.InteractiveRerouteQueued == 0);
    Check(full.BackgroundCapacityBlocked == 2);
    for (int i = 0; i < 4; i++)
        Check(packets.Enqueue(route, 100, 2, PacketTrafficClass.Interactive));
    Check(packets.Advance(3) == 4);
    Check(packets.Advance(4) == 0);
    full = packets.Capture();
    Check(full.RerouteQueued == 8 && full.InteractiveRerouteQueued == 2);
    Check(full.BackgroundRerouteQueued == 6);
    Check(full.InteractiveCapacityBlocked == 2 &&
          full.BackgroundCapacityBlocked == 2);
    Check(full.CapacityBlockedPackets == 4);
    Check(packets.TryTakeReroute(out var first));
    Check(first.TrafficClass == PacketTrafficClass.Interactive);
    var missing = medium.Route(first.Source, first.Destination);
    Check(!missing.Found);
    Check(packets.CompleteReroute(first, missing));
    full = packets.Capture();
    Check(full.InteractiveCapacityBlocked == 1);
    Check(full.BackgroundCapacityBlocked == 2);
    Check(full.InteractiveCapacityWakeups == 1);
    Check(packets.Advance(5) == 0);
    full = packets.Capture();
    Check(full.InteractiveRerouteQueued == 2);
    Check(full.RerouteQueued + full.RerouteInFlight <= options.MaxPendingReroutes);
    Check(packets.TryTakeReroute(out var second));
    Check(second.TrafficClass == PacketTrafficClass.Interactive);
    Check(packets.CancelReroute(second));
    Check(packets.Capture().InteractiveCapacityBlocked == 0);
    Check(packets.Capture().BackgroundCapacityBlocked == 2);

    // Fairness: even under a persistent interactive backlog, the fourth
    // selected reroute is background (weighted 3:1 service).
    var (fairMedium, fairRoute) = MakeBrokenChain();
    var fair = new RadioPacketTransport(fairMedium,
        options with { MaxPendingReroutes = 16, InteractiveRerouteReservedSlots = 2 });
    for (int i = 0; i < 6; i++)
    {
        Check(fair.Enqueue(fairRoute, 100, 0, PacketTrafficClass.Background));
        Check(fair.Enqueue(fairRoute, 100, 0, PacketTrafficClass.Interactive));
    }
    Check(fair.Advance(1) == 12);
    // A weighted lane cannot process a newly requeued packet again in
    // the same tick: all twelve are still at the intermediate node.
    Check(fair.Capture().Delivered == 0);
    Check(fair.Capture().FirstHopSamples == 12);
    fairMedium.Update([offline]);
    fair.NotifyRadioUpdates([offline]);
    Check(fair.Advance(2) == 0);
    Check(fair.Capture().RerouteQueued == 12);
    int interactive = 0, background = 0;
    for (int i = 0; i < 12; i++)
    {
        Check(fair.TryTakeReroute(out var ticket));
        if (i < 4)
            Check(ticket.TrafficClass ==
                (i == 3 ? PacketTrafficClass.Background : PacketTrafficClass.Interactive));
        if (ticket.TrafficClass == PacketTrafficClass.Interactive) interactive++;
        else background++;
        Check(fair.CancelReroute(ticket));
    }
    Check(interactive == 6 && background == 6);
    var end = fair.Capture();
    Check(end.RerouteQueued == 0 && end.RerouteInFlight == 0);
    Check(end.InteractiveReroutesStarted == 6 && end.BackgroundReroutesStarted == 6);
    Check(end.InteractiveRerouteInFlight == 0 && end.BackgroundRerouteInFlight == 0);
    Check(!fair.TryTakeReroute(out _));

    bool invalid = false;
    try { (options with { PrioritizeInteractiveTraffic = false }).Validate(); }
    catch (ArgumentException) { invalid = true; }
    Check(invalid);
    invalid = false;
    try { (options with { InteractiveRerouteReservedSlots = 8 }).Validate(); }
    catch (ArgumentOutOfRangeException) { invalid = true; }
    Check(invalid);
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
        Check(state.MaxExecutionMilliseconds >= state.ExecutionP95Milliseconds);
        Check(state.OverPeriodExecutionTicks >= 0);
        var packetTiming = packets.Capture();
        Check(packetTiming.MaxAdvanceMilliseconds >= packetTiming.ForwardingP95Milliseconds);
        Check(packetTiming.MaxLockWaitMilliseconds >= 0);
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

static void CheckInteractiveAirtimeQos()
{
    static SharedMediumNetwork MakeMedium(RadioAirtimeScope scope)
    {
        var runtime = new NetworkRuntime();
        runtime.Apply([
            new TopologyChange(TopologyOperation.AddNode, new NodeId(1),
                Position: new NodePosition(0, 0)),
            new TopologyChange(TopologyOperation.AddNode, new NodeId(2),
                Position: new NodePosition(10, 0)),
            new TopologyChange(TopologyOperation.AddNode, new NodeId(3),
                Position: new NodePosition(500, 0)),
            new TopologyChange(TopologyOperation.AddNode, new NodeId(4),
                Position: new NodePosition(510, 0))
        ]);
        var medium = new SharedMediumNetwork(runtime, bytesPerTick: 400,
            airtimeScope: scope);
        medium.Update(Enumerable.Range(1, 4).Select(i => new RadioNodeUpdate(
            new NodeId((ulong)i), Profile: new RadioProfile(14, 1, 8),
            ChangeProfile: true)).ToArray());
        return medium;
    }

    foreach (var scope in new[] {
        RadioAirtimeScope.ChannelWide, RadioAirtimeScope.SpatialCells
    })
    {
        var medium = MakeMedium(scope);
        // Reserve 25% for interactive: background may only consume 300/400.
        Check(medium.TryTransmitHop(new NodeId(1), new NodeId(2), 300, 1,
            PacketTrafficClass.Background, 25) == RadioHopOutcome.TransmittedRadio);
        // This hop fits the physical channel, but exceeds background quota.
        Check(medium.TryTransmitHop(new NodeId(1), new NodeId(2), 1, 1,
            PacketTrafficClass.Background, 25) == RadioHopOutcome.ChannelBusy);
        Check(medium.TryTransmitHop(new NodeId(1), new NodeId(2), 100, 1,
            PacketTrafficClass.Interactive, 25) == RadioHopOutcome.TransmittedRadio);
        Check(medium.TryTransmitHop(new NodeId(1), new NodeId(2), 1, 1,
            PacketTrafficClass.Interactive, 25) == RadioHopOutcome.ChannelBusy);
        // No protected demand on tick 2; background borrows the full 400B.
        Check(medium.TryTransmitHop(new NodeId(1), new NodeId(2), 400, 2,
            PacketTrafficClass.Background) == RadioHopOutcome.TransmittedRadio);
        Check(medium.TryTransmitHop(new NodeId(1), new NodeId(2), 50, 1,
            PacketTrafficClass.Background) == RadioHopOutcome.OldTick);
        var stats = medium.Stats();
        Check(stats.ProtectedBackgroundDeferrals == 1);
        Check(stats.InteractiveHopTransmissions == 1);
        Check(stats.BackgroundHopTransmissions == 2);
        Check(stats.InteractiveHopBytes == 100);
        Check(stats.BackgroundHopBytes == 700);
        if (scope == RadioAirtimeScope.SpatialCells)
        {
            // A distant region does not consume any shared local cell budget.
            Check(medium.TryTransmitHop(new NodeId(3), new NodeId(4), 400, 2,
                PacketTrafficClass.Background) == RadioHopOutcome.TransmittedRadio);
            Check(medium.Stats().BackgroundHopBytes == 1100);
        }
        else
        {
            Check(medium.TryTransmitHop(new NodeId(3), new NodeId(4), 1, 2,
                PacketTrafficClass.Background) == RadioHopOutcome.ChannelBusy);
        }
    }

    // Airtime is protected while tick-start interactive work remains;
    // the background can borrow unused capacity after that work drains.
    var shared = MakeMedium(RadioAirtimeScope.SpatialCells);
    var route = shared.Route(new NodeId(1), new NodeId(2));
    Check(route.Found);
    var options = new PacketTransportOptions(Capacity: 16,
        MaxAdvancesPerTick: 16, EventDrivenPacketWaiting: true,
        PrioritizeInteractiveTraffic: true,
        PrioritizeInteractiveAirtime: true, InteractiveAirtimeReservePercent: 25);
    var transport = new RadioPacketTransport(shared, options);
    // Ready lane order: I,I,I,B,I,B. First B (320B) is blocked by
    // the 300B quota, despite fitting physical 400B total at that point.
    // After last I, second B borrows the full remaining 320B.
    for (int i = 0; i < 4; i++)
        Check(transport.Enqueue(route, 20, 0, PacketTrafficClass.Interactive));
    for (int i = 0; i < 2; i++)
        Check(transport.Enqueue(route, 320, 0, PacketTrafficClass.Background));
    Check(transport.Advance(1) == 5);
    Check(transport.Capture().InteractiveDelivered == 4);
    Check(transport.Capture().BackgroundDelivered == 1);
    Check(transport.Capture().BackgroundQueued == 1);
    Check(shared.Stats().ProtectedBackgroundDeferrals == 1);
    Check(transport.Advance(2) == 1);
    Check(transport.Capture().BackgroundDelivered == 2);
    Check(transport.Capture().Queued == 0);
    Check(shared.Stats().BackgroundHopBytes == 640);

    bool invalid = false;
    try { (options with { PrioritizeInteractiveTraffic = false }).Validate(); }
    catch (ArgumentException) { invalid = true; }
    Check(invalid);
    invalid = false;
    try { (options with { InteractiveAirtimeReservePercent = 100 }).Validate(); }
    catch (ArgumentOutOfRangeException) { invalid = true; }
    Check(invalid);
    invalid = false;
    try { shared.TryTransmitHop(new NodeId(1), new NodeId(2), 10, 3,
        (PacketTrafficClass)200, 25); }
    catch (ArgumentOutOfRangeException) { invalid = true; }
    Check(invalid);
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
                var pooled = SharedMediumNetwork.RouteAStarPooledCore(snapshot,
                    from, to, maximumRadioRange: 26, minimumRadioCost: 3);
                Check(astar.Found == baseline.Found);
                Check(astar.Cost == baseline.Cost);
                Check(astar.Version == baseline.Version);
                Check(pooled.Found == baseline.Found);
                Check(pooled.Cost == baseline.Cost);
                Check(pooled.Version == baseline.Version);
                Check(pooled.Explored == astar.Explored);
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
    var p = SharedMediumNetwork.RouteAStarPooledCore(pinned, start, end,
        maximumRadioRange: 14, minimumRadioCost: 8);
    Check(a.Found && a.Cost == d.Cost);
    Check(p.Found && p.Cost == d.Cost);
    Check(p.Explored == a.Explored);
    Check(a.Explored <= d.Explored);

    // Reuse across concurrent workers must never cross-contaminate scratch
    // distances/parents. All threads pin the same immutable graph snapshot.
    var mismatches = new System.Collections.Concurrent.ConcurrentQueue<string>();
    Parallel.For(0, 160, iteration =>
    {
        var source = new NodeId((ulong)(iteration % 900 + 1));
        var destination = new NodeId((ulong)((iteration * 41 + 172) % 900 + 1));
        var original = SharedMediumNetwork.RouteAStarCore(pinned,
            source, destination, maximumRadioRange: 14, minimumRadioCost: 8);
        var pooled = SharedMediumNetwork.RouteAStarPooledCore(pinned,
            source, destination, maximumRadioRange: 14, minimumRadioCost: 8);
        if (original.Found != pooled.Found || original.Cost != pooled.Cost ||
            original.Explored != pooled.Explored ||
            !original.Path.SequenceEqual(pooled.Path))
            mismatches.Enqueue($"Route mismatch on query {iteration}");
    });
    Check(mismatches.IsEmpty);

    // A single-threaded repeatable micro-probe reports thread-local
    // allocations without hard-coding machine-specific latency targets.
    // A subsequent 12k-node hardware run remains the performance authority.
    const int samples = 32;
    for (int i = 0; i < 4; i++)
    {
        _ = SharedMediumNetwork.RouteAStarCore(pinned, start, end, 14, 8);
        _ = SharedMediumNetwork.RouteAStarPooledCore(pinned, start, end, 14, 8);
    }
    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < samples; i++)
        _ = SharedMediumNetwork.RouteAStarCore(pinned, start, end, 14, 8);
    long originalAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < samples; i++)
        _ = SharedMediumNetwork.RouteAStarPooledCore(pinned, start, end, 14, 8);
    long pooledAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    Console.WriteLine($"Radio A* allocations per route, 900 nodes: original={originalAllocated / samples:N0} B, pooled={pooledAllocated / samples:N0} B");
    Check(pooledAllocated < originalAllocated);

    // A cancellation must release a cleared workspace. Following queries
    // must still produce exact results instead of reusing partial distances.
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    bool wasCancelled = false;
    try
    {
        _ = SharedMediumNetwork.RouteAStarPooledCore(pinned, start, end,
            maximumRadioRange: 14, minimumRadioCost: 8,
            cancellationToken: cancelled.Token);
    }
    catch (OperationCanceledException) { wasCancelled = true; }
    Check(wasCancelled);
    Check(SharedMediumNetwork.RouteAStarPooledCore(pinned, start, end,
        maximumRadioRange: 14, minimumRadioCost: 8).Cost == d.Cost);
    Check(SharedMediumNetwork.RouteAStarPooledCore(pinned, start, end,
        maximumRadioRange: 14, minimumRadioCost: 8,
        maxExplored: 1).Found == false);
}

static void CheckDenseRadioAStar()
{
    // Snapshot isolation: positions, profiles and cable links must all come
    // from one immutable epoch. Dense ID mappings survive radio movements.
    for (int seed = 0; seed < 4; seed++)
    {
        var random = new Random(8000 + seed);
        var runtime = new NetworkRuntime();
        var ids = Enumerable.Range(1, 160)
            .Select(i => new NodeId((ulong)i * 37 + 0x8000UL)).ToArray();
        runtime.Apply(ids.Select(id =>
            new TopologyChange(TopologyOperation.AddNode, id,
                Position: new NodePosition(random.Next(-160, 161),
                    random.Next(-160, 161), random.Next(2)))).ToArray());
        var medium = new SharedMediumNetwork(runtime,
            searchMode: SharedRadioSearchMode.AStarDense,
            maximumRadioRange: 28, minimumRadioCost: 3);
        medium.Update(ids.Select(id => new RadioNodeUpdate(id,
            Profile: new RadioProfile(random.Next(12, 29),
                (uint)random.Next(1, 4), (uint)random.Next(3, 14)),
            ChangeProfile: true)).ToArray());

        for (int round = 0; round < 12; round++)
        {
            if (round == 3)
                Check(medium.SetCable(ids[0], ids[^1], 1));
            if (round == 7)
                Check(medium.SetCable(ids[0], ids[^1], null));
            var snapshot = medium.Snapshot;

            for (int query = 0; query < 50; query++)
            {
                var from = ids[random.Next(ids.Length)];
                var to = ids[random.Next(ids.Length)];
                var pooled = SharedMediumNetwork.RouteAStarPooledCore(
                    snapshot, from, to, 28, 3);
                var dense = SharedMediumNetwork.RouteAStarDenseCore(
                    snapshot, from, to, 28, 3);
                var dijkstra = SharedMediumNetwork.RouteCore(snapshot, from, to);
                Check(dense.Found == pooled.Found &&
                    dense.Found == dijkstra.Found);
                Check(dense.Cost == pooled.Cost && dense.Cost == dijkstra.Cost);
                Check(dense.Version == snapshot.Version);
                Check(dense.Explored == pooled.Explored);
                Check(dense.Path.SequenceEqual(pooled.Path));
                Check(medium.Route(from, to).Cost == dense.Cost);
            }
            // Check old immutable view again after the next movement below.
            var old = medium.Snapshot;
            var reference = SharedMediumNetwork.RouteAStarPooledCore(
                old, ids[0], ids[^1], 28, 3);
            var updates = new RadioNodeUpdate[12];
            for (int i = 0; i < updates.Length; i++)
            {
                var id = ids[(round * 11 + i) % ids.Length];
                var position = old.Physical.Nodes[id].Position!.Value;
                updates[i] = new RadioNodeUpdate(id,
                    Position: new NodePosition(position.X + random.Next(-20, 21),
                        position.Y + random.Next(-20, 21), position.Z),
                    Profile: i == 0 ? null : new RadioProfile(
                        random.Next(12, 29), (uint)random.Next(1, 4),
                        (uint)random.Next(3, 14)),
                    ChangeProfile: true);
            }
            medium.Update(updates);
            var pinned = SharedMediumNetwork.RouteAStarDenseCore(
                old, ids[0], ids[^1], 28, 3);
            Check(pinned.Found == reference.Found);
            Check(pinned.Cost == reference.Cost);
            Check(pinned.Path.SequenceEqual(reference.Path));
            Check(pinned.Version == old.Version);
        }

        // Removal requires reindexing. Arbitrary non-contiguous IDs cannot
        // be used directly as offsets into the dense arrays.
        var victims = ids.Skip(30).Take(25).ToArray();
        Check(medium.RemoveNodes(victims) == victims.Length);
        var after = medium.Snapshot;
        Check(!SharedMediumNetwork.RouteAStarDenseCore(after, ids[0],
            victims[0], 28, 3).Found);
        var expected = SharedMediumNetwork.RouteAStarPooledCore(after,
            ids[0], ids[^1], 28, 3);
        var actual = medium.Route(ids[0], ids[^1]);
        Check(actual.Found == expected.Found && actual.Cost == expected.Cost);
    }

    // Dense workspace is exclusive to each worker, even when the graph
    // mutates concurrently. Queries pin their input snapshots.
    var big = new NetworkRuntime();
    big.Apply(Enumerable.Range(1, 900).Select(i =>
        new TopologyChange(TopologyOperation.AddNode, new NodeId((ulong)i),
            Position: new NodePosition((i - 1) % 30 * 12,
                (i - 1) / 30 * 12))).ToArray());
    var radio = new SharedMediumNetwork(big,
        searchMode: SharedRadioSearchMode.AStarDense,
        maximumRadioRange: 14, minimumRadioCost: 8);
    radio.Update(Enumerable.Range(1, 900).Select(i =>
        new RadioNodeUpdate(new NodeId((ulong)i),
            Profile: new RadioProfile(14, 1, 8), ChangeProfile: true)).ToArray());
    var initial = radio.Snapshot;
    var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
    Parallel.For(0, 240, i =>
    {
        var a = new NodeId((ulong)(i % 900 + 1));
        var b = new NodeId((ulong)((i * 71 + 160) % 900 + 1));
        var expected = SharedMediumNetwork.RouteAStarPooledCore(
            initial, a, b, 14, 8);
        var actual = SharedMediumNetwork.RouteAStarDenseCore(
            initial, a, b, 14, 8);
        if (actual.Cost != expected.Cost ||
            actual.Explored != expected.Explored ||
            !actual.Path.SequenceEqual(expected.Path))
            errors.Enqueue($"Dense route differs on parallel query {i}");
    });
    Check(errors.IsEmpty);

    // Compare warmed thread-local allocations, but do not prescribe an
    // execution-time threshold that depends on GitHub runner hardware.
    var start = new NodeId(1);
    var finish = new NodeId(900);
    for (int i = 0; i < 6; i++)
    {
        _ = SharedMediumNetwork.RouteAStarPooledCore(initial, start, finish, 14, 8);
        _ = SharedMediumNetwork.RouteAStarDenseCore(initial, start, finish, 14, 8);
    }
    const int samples = 32;
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < samples; i++)
        _ = SharedMediumNetwork.RouteAStarPooledCore(initial, start, finish, 14, 8);
    long pooledBytes = GC.GetAllocatedBytesForCurrentThread() - before;
    before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < samples; i++)
        _ = SharedMediumNetwork.RouteAStarDenseCore(initial, start, finish, 14, 8);
    long denseBytes = GC.GetAllocatedBytesForCurrentThread() - before;
    Console.WriteLine($"Radio A* allocations per route, 900 nodes: pooled={pooledBytes / samples:N0} B, dense={denseBytes / samples:N0} B");
    Check(denseBytes < pooledBytes);

    // A separately constructed snapshot without a prebuilt dense view
    // must still be routable by the public static search method.
    var manual = new SharedMediumSnapshot(initial.Version,
        initial.Physical, initial.Devices, initial.Cells)
    { HasPhysicalLinks = initial.HasPhysicalLinks };
    Check(SharedMediumNetwork.RouteAStarDenseCore(manual, start, finish,
        14, 8).Cost == SharedMediumNetwork.RouteAStarPooledCore(
            initial, start, finish, 14, 8).Cost);

    const int timingQueries = 40;
    long timeBefore = Stopwatch.GetTimestamp();
    for (int i = 0; i < timingQueries; i++)
        _ = SharedMediumNetwork.RouteAStarPooledCore(initial, start, finish, 14, 8);
    long pooledTime = Stopwatch.GetTimestamp() - timeBefore;
    timeBefore = Stopwatch.GetTimestamp();
    for (int i = 0; i < timingQueries; i++)
        _ = SharedMediumNetwork.RouteAStarDenseCore(initial, start, finish, 14, 8);
    long denseTime = Stopwatch.GetTimestamp() - timeBefore;
    Console.WriteLine(
        $"Radio A* CPU micro-probe (900 nodes): pooled={pooledTime * 1_000_000.0 / Stopwatch.Frequency / timingQueries:N1} us/route, dense={denseTime * 1_000_000.0 / Stopwatch.Frequency / timingQueries:N1} us/route");

    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    bool cancelled = false;
    try
    {
        _ = SharedMediumNetwork.RouteAStarDenseCore(initial, start, finish,
            14, 8, cancellationToken: cancellation.Token);
    }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled);
    var shortest = SharedMediumNetwork.RouteAStarDenseCore(
        initial, start, finish, 14, 8);
    Check(shortest.Found);
    Check(!SharedMediumNetwork.RouteAStarDenseCore(initial, start, finish,
        14, 8, maxExplored: 1).Found);
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
