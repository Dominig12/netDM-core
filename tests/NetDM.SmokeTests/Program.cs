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

var runtime = new NetworkRuntime();
runtime.Apply(batch);
Check(runtime.Route(a, c).Cost == 5);
Check(runtime.Route(a, c).Cost == 5);
Check(runtime.Metrics.Capture(runtime.Cache.Count).CacheHits == 1);
runtime.Apply([new TopologyChange(TopologyOperation.Disconnect, a, b)]);
Check(!runtime.Route(a, c).Found);

// Verify parallel immutable reads remain consistent during batches of moves.
var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
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
Console.WriteLine("All netDM-core smoke tests passed.");

static void Check(bool condition)
{
    if (!condition) throw new InvalidOperationException("Smoke test failed.");
}
