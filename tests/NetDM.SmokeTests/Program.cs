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
Check(Router.Find(before, a, c).Cost == 7); // Older snapshot stays valid.
Check(!graph.AddNode(a));
Check(!graph.Disconnect(a, b));
Check(!Router.Find(after, a, new NodeId(42)).Found);
Check(graph.RemoveNode(c));
Check(!Router.Find(graph.Snapshot, a, c).Found);
Console.WriteLine("All netDM-core smoke tests passed.");

static void Check(bool condition)
{
    if (!condition) throw new InvalidOperationException("Smoke test failed.");
}
