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
