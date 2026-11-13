using NetDM.Core;

/// <summary>
/// P3.3 device attachment over a real shared medium: wired port to the APC of the served
/// segment, radio for mobile, unanchored and APC-less devices, access-point pairing,
/// endpoint-only devices and atomic wired/radio switches when the map loses an APC.
/// </summary>
static class DeviceAttachmentSmokeTests
{
    private static readonly NodeId Apc1 = new(1), Apc2 = new(2), Apc3 = new(3);

    public static void Run()
    {
        CheckSegmentLookup();
        CheckAccessPointPairing();
        CheckAttachments();
        CheckSegmentMovesAndLevels();
        CheckProfileReplacementValidation();
        CheckNodeStates();
        CheckApcBridges();
        CheckBroadcastReach();
        CheckDiagnostics();
        Console.WriteLine("P3.3 device attachment, P3.5 node state, APC bridge, broadcast reach and network diagnostics checks passed.");
    }

    /// <summary>
    /// network.diagnostics: a device reports its own ports against the designed wiring, and one bounded
    /// trace from itself. An APC without power keeps its place in the wiring and shows as the reason.
    /// </summary>
    private static void CheckDiagnostics()
    {
        var medium = new SharedMediumNetwork(new NetworkRuntime(), searchMode: SharedRadioSearchMode.AStarDense);
        TopologyChange Node(ulong id, int x, bool forward) =>
            new(TopologyOperation.AddNode, new NodeId(id), Position: new NodePosition(x, 1, 1)) { CanForward = forward };
        TopologyChange Wire(ulong a, ulong b) => new(TopologyOperation.Connect, new NodeId(a), new NodeId(b));
        // APC 1 - APC 2 - APC 3, a machine 50 plugged into APC 2.
        medium.ApplyExternalTopology([Node(1, 1, true), Node(2, 2, true), Node(3, 3, true), Node(50, 2, false),
            Wire(1, 2), Wire(2, 3), Wire(2, 50)]);
        (NodeId, NodeId)[] cables = [(Apc1, Apc2), (Apc2, Apc3)];
        var wiring = WiringReport.Create(cables, [Apc1, Apc2, Apc3], []);
        var healthy = NetworkDiagnostics.LinkState(medium.Snapshot, wiring, Apc2)!;
        Check(healthy is { State: "active", Wired: 2, WiredUp: 2, Devices: 1, Radio: false, More: false } &&
            healthy.Neighbors.SequenceEqual([new NeighborLink(Apc1, "up"), new NeighborLink(Apc3, "up")]), "a healthy APC reports its ports up");
        var machine = NetworkDiagnostics.LinkState(medium.Snapshot, wiring, new NodeId(50))!;
        Check(machine is { State: "active", Wired: 0, Devices: 1 } && machine.Neighbors.Count == 0, "a device reports its one port and no map neighbours");
        Check(NetworkDiagnostics.Trace(medium.Snapshot, new NodeId(50), Apc3) is { State: "reachable", Hops: 2, RadioHops: 0 } &&
            NetworkDiagnostics.Trace(medium.Snapshot, Apc1, Apc1) is { State: "reachable", Hops: 0 } &&
            NetworkDiagnostics.Trace(medium.Snapshot, Apc1, new NodeId(99)).State == "unknown", "a trace counts the hops to a known node");

        // APC 3 loses power: its cable carries nothing, the wiring still names it and says why.
        medium.ApplyExternalTopology([new TopologyChange(TopologyOperation.Disconnect, Apc2, Apc3)]);
        var dark = WiringReport.Create(cables, [Apc1, Apc2, Apc3], [new(Apc3, new NodeState(false, true)), new(Apc1, NodeState.Up)]);
        var degraded = NetworkDiagnostics.LinkState(medium.Snapshot, dark, Apc2)!;
        Check(degraded is { State: "active", Wired: 2, WiredUp: 1 } &&
            degraded.Neighbors.SequenceEqual([new NeighborLink(Apc1, "up"), new NeighborLink(Apc3, "unpowered")]), "an unpowered neighbour is named as the cause");
        Check(NetworkDiagnostics.LinkState(medium.Snapshot, dark, Apc3) is { State: "unpowered", Wired: 1, WiredUp: 0 } own &&
            own.Neighbors.Single() == new NeighborLink(Apc2, "down"), "an unpowered APC reports itself and its dead port");
        Check(NetworkDiagnostics.Trace(medium.Snapshot, Apc1, Apc3).State == "unreachable", "a trace does not cross a dead cable");
        var brokenWiring = WiringReport.Create(cables, [Apc1, Apc2, Apc3], [new(Apc3, new NodeState(true, false))]);
        Check(NetworkDiagnostics.LinkState(medium.Snapshot, brokenWiring, Apc2)!.Neighbors[1].State == "broken", "a broken neighbour differs from an unpowered one");
        Check(NetworkDiagnostics.LinkState(medium.Snapshot, dark, new NodeId(99)) is null, "a node outside the world reports nothing");

        // A hub with more ports than a report lists: all are counted, the list is bounded.
        var hub = new NodeId(1000);
        var spokes = Enumerable.Range(1, 20).Select(index => new NodeId((ulong)(1000 + index))).ToArray();
        medium.ApplyExternalTopology([Node(1000, 20, true), .. spokes.Select(spoke => Node(spoke.Value, 21, true)),
            .. spokes.Select(spoke => Wire(1000, spoke.Value))]);
        var wide = NetworkDiagnostics.LinkState(medium.Snapshot,
            WiringReport.Create(spokes.Select(spoke => (hub, spoke)), [hub, .. spokes], []), hub)!;
        Check(wide is { Wired: 20, WiredUp: 20, More: true } && wide.Neighbors.Count == NetworkDiagnostics.MaxNeighbors, "a report lists a bounded number of ports");
    }

    /// <summary>
    /// P8: one search finds every listener a broadcast reaches. Endpoints receive but never relay, a
    /// source transmits even as an endpoint, radio hops are counted apart from wires, and several
    /// sources (broadcasters) serve their nearest listeners.
    /// </summary>
    private static void CheckBroadcastReach()
    {
        var medium = new SharedMediumNetwork(new NetworkRuntime(), searchMode: SharedRadioSearchMode.AStarDense);
        TopologyChange Node(ulong id, int x, bool forward) =>
            new(TopologyOperation.AddNode, new NodeId(id), Position: new NodePosition(x, 1, 1)) { CanForward = forward };
        TopologyChange Wire(ulong a, ulong b) => new(TopologyOperation.Connect, new NodeId(a), new NodeId(b));
        // Speaker 1 (endpoint) - APC 2 = APC 3 - listener 4; endpoint 6 hangs off the speaker and
        // listener 5 only behind it; listener 7 is wired to APC 3 and reached from the second source 8.
        medium.ApplyExternalTopology([
            Node(1, 1, false), Node(2, 2, true), Node(3, 3, true), Node(4, 4, false), Node(5, 9, false), Node(6, 8, false),
            Node(7, 5, false), Node(8, 6, false),
            Wire(1, 2), Wire(2, 3), Wire(3, 4), Wire(1, 6), Wire(6, 5), Wire(3, 7), Wire(8, 3)
        ]);
        var targets = new HashSet<NodeId> { new(4), new(5), new(7) };
        var reached = SharedMediumNetwork.ReachAll(medium.Snapshot, [new NodeId(1)], targets);
        var limited = SharedMediumNetwork.ReachAll(medium.Snapshot, new NodeId(1), targets, 1, out var exhausted);
        Check(exhausted && !limited.ContainsKey(new NodeId(4)), "a reachability budget cannot claim an unexamined target");
        Check(reached.Count == 2 && reached[new NodeId(4)] is { Hops: 3, RadioHops: 0, LastHop.Value: 3 } && !reached.ContainsKey(new NodeId(5)),
            "a broadcast reaches listeners through forwarding nodes only");
        var both = SharedMediumNetwork.ReachAll(medium.Snapshot, [new NodeId(1), new NodeId(8)], targets);
        Check(both[new NodeId(7)].Hops == 2 && both[new NodeId(4)].Hops == 2, "each listener is served by its nearest source");
        Check(SharedMediumNetwork.ReachAll(medium.Snapshot, [new NodeId(99)], targets).Count == 0, "an unknown source reaches nothing");
        Check(reached[new NodeId(4)].Ticks == 1, "consecutive wires take one tick");
        // A gateway continues a route: a full cable run in the last tick pushes the next wire to a new tick.
        var gateway = new Dictionary<NodeId, BroadcastReach> { [new(3)] = new(0, 16, 0, new NodeId(2), Ticks: 2, Run: 16) };
        var continued = SharedMediumNetwork.ReachAll(medium.Snapshot, gateway, new HashSet<NodeId> { new(4) });
        Check(continued[new NodeId(4)] is { Hops: 17, Ticks: 3, Run: 1 }, "a route through a gateway keeps its hops and ticks");
    }

    /// <summary>
    /// Parts of the wired APC network are joined by radio bridges between their nearest APCs on a level,
    /// as a spanning forest within range: no cycle, no bridge inside a part, nothing across levels.
    /// </summary>
    private static void CheckApcBridges()
    {
        NodePosition At(int x, int y, int z = 1) => new(x, y, z);
        var apcs = new Dictionary<NodeId, NodePosition>
        {
            // Station: 1-2-3 wired. Satellite 4-5 wired, 20 tiles east of APC 3. Outpost 6 near both.
            [new(1)] = At(10, 10), [new(2)] = At(20, 10), [new(3)] = At(30, 10),
            [new(4)] = At(50, 10), [new(5)] = At(60, 10),
            [new(6)] = At(40, 30),
            // Far derelict 7 and an APC on another level just above the station.
            [new(7)] = At(150, 150), [new(8)] = At(30, 10, 2)
        };
        TopologyChange Wire(ulong a, ulong b) => new(TopologyOperation.Connect, new NodeId(a), new NodeId(b));
        var wired = new[] { Wire(1, 2), Wire(2, 3), Wire(4, 5) };
        var (bridges, report) = ApcBridges.Build(apcs, wired);
        var pairs = bridges.Select(link => (link.A.Value, link.B.Value)).ToHashSet();
        // 3-4 is 20 tiles; 6 is ~22 from 3 and ~22 from 4, so one bridge reaches it and no cycle forms.
        Check(pairs.Contains((3, 4)) && bridges.Length == 2 && bridges.All(link => link.Cost == ApcBridges.Cost),
            $"the satellite and the outpost join the station by their nearest APCs: {string.Join(",", pairs)}");
        Check(pairs.Contains((3, 6)) || pairs.Contains((4, 6)), "the outpost joins through its nearest APC");
        Check(!pairs.Any(pair => pair.Item1 == 7 || pair.Item2 == 7 || pair.Item1 == 8 || pair.Item2 == 8),
            "nothing bridges beyond range or across levels");
        Check(report is { Components: 5, Groups: 3, NearestApart: > ApcBridges.Range, Apart.Length: 2 } && report.Lengths.Max() <= ApcBridges.Range,
            $"the report names what stayed apart: {report}");
        var (connected, whole) = ApcBridges.Build(apcs.Where(entry => entry.Key.Value <= 3).ToDictionary(), wired);
        Check(connected.Length == 0 && whole is { Components: 1, Groups: 1, NearestApart: null }, "a connected map needs no bridge");
        var (again, _) = ApcBridges.Build(apcs, wired.Reverse());
        Check(again.SequenceEqual(bridges), "bridges do not depend on the order of the facts");
    }

    private static readonly AreaSpan[] Spans =
    [
        new(1, 1, 1, 1, 4), new(2, 1, 1, 5, 8), new(3, 1, 1, 9, 12),
        new(4, 1, 3, 1, 4), // No APC serves this area.
        new(1, 1, 5, 1, 2), // An island of area 1 outside the served segment, like a walled-off tank.
        new(2, 1, 5, 3, 4), // An island of area 2 that touches the island of area 1.
        new(1, 2, 1, 1, 4) // Area 1 on a level without its APC.
    ];

    private static AreaApc[] Apcs(bool withFirst) =>
    [
        .. withFirst ? [new AreaApc(1, Apc1, new NodePosition(1, 1, 1))] : Array.Empty<AreaApc>(),
        new(2, Apc2, new NodePosition(8, 1, 1)), new(3, Apc3, new NodePosition(9, 1, 1))
    ];

    private static void CheckSegmentLookup()
    {
        var index = AreaWiring.Build(Spans, Apcs(true));
        Check(index.TryFindApc(new NodePosition(3, 1, 1), out var apc) && apc == Apc1, "served cell");
        Check(index.TryFindApc(new NodePosition(5, 1, 1), out apc) && apc == Apc2, "neighbor segment");
        Check(!index.TryFindApc(new NodePosition(2, 3, 1), out _), "area without APC");
        Check(index.TryFindApc(new NodePosition(1, 5, 1), out apc) && apc == Apc1, "an island attaches to the APC of its area");
        Check(index.TryFindApc(new NodePosition(4, 5, 1), out apc) && apc == Apc2, "an island of another area attaches to that APC");
        Check(!index.TryFindApc(new NodePosition(3, 1, 2), out _), "the area on a level without its APC");
        Check(!index.TryFindApc(new NodePosition(40, 40, 1), out _), "outside exported geometry");
        // The two islands share a side, yet only the served segments link APCs.
        Check(index.Links.Length == 2, "islands add no APC links");

        AreaSpan[] spans = [new(7, 1, 20, 1, 3), new(7, 1, 30, 1, 3), new(7, 1, 26, 10, 12)];
        AreaApc[] apcs = [new(7, new NodeId(71), new NodePosition(1, 20, 1)), new(7, new NodeId(72), new NodePosition(2, 30, 1))];
        Check(AreaWiring.Build(spans, apcs).TryFindApc(new NodePosition(11, 26, 1), out apc) && apc.Value == 72,
            "the nearest APC of the area serves an island");
    }

    private static void CheckAccessPointPairing()
    {
        foreach (var mode in Enum.GetValues<SharedRadioSearchMode>())
        {
            var medium = new SharedMediumNetwork(new NetworkRuntime(), searchMode: mode);
            var a = new NodeId(10);
            var b = new NodeId(11);
            var station = new NodeId(12);
            var access = new RadioProfile(6, 1, AccessPoint: true);
            medium.ApplyExternalTopology(
            [
                new(TopologyOperation.AddNode, a, Position: new NodePosition(1, 1, 1)),
                new(TopologyOperation.AddNode, b, Position: new NodePosition(4, 1, 1)),
                new(TopologyOperation.AddNode, station, Position: new NodePosition(2, 3, 1)) { CanForward = false }
            ],
            [
                new RadioNodeUpdate(a, Profile: access, ChangeProfile: true),
                new RadioNodeUpdate(b, Profile: access, ChangeProfile: true),
                new RadioNodeUpdate(station, Profile: new RadioProfile(6, 1), ChangeProfile: true)
            ]);
            Check(!medium.HasUsableHop(a, b) && medium.HasUsableHop(a, station), $"AP pairing ({mode})");
            // The only path between the access points needs a forwarding station.
            Check(!medium.Route(a, b).Found && medium.Route(station, b).Found, $"AP route ({mode})");

            // A route found before both ends became access points cannot transmit afterwards.
            var plain = new RadioProfile(6, 1);
            medium.ApplyExternalTopology([],
            [
                new RadioNodeUpdate(a, Profile: plain, ChangeProfile: true),
                new RadioNodeUpdate(b, Profile: plain, ChangeProfile: true)
            ]);
            var stale = medium.Route(a, b);
            Check(stale.Found && stale.Path.Count == 2, $"plain radios pair ({mode})");
            medium.ApplyExternalTopology([],
            [
                new RadioNodeUpdate(a, Profile: access, ChangeProfile: true),
                new RadioNodeUpdate(b, Profile: access, ChangeProfile: true)
            ]);
            Check(medium.TryTransmitHop(a, b, 64, 1) == RadioHopOutcome.LinkUnavailable && !medium.TryTransmit(stale, 64, 1),
                $"stale route between access points ({mode})");
        }
    }

    /// <summary>One fact moves a wired device between segments; a shared APC serves its zone on two levels.</summary>
    private static void CheckSegmentMovesAndLevels()
    {
        AreaSpan[] spans = [new(1, 1, 1, 1, 4), new(2, 1, 1, 5, 8), new(2, 2, 1, 5, 8)];
        AreaApc[] apcs =
        [
            new(1, Apc1, new NodePosition(1, 1, 1)), new(2, Apc2, new NodePosition(8, 1, 1)),
            new(2, Apc2, new NodePosition(8, 1, 2))
        ];
        var index = AreaWiring.Build(spans, apcs);
        var medium = new SharedMediumNetwork(new NetworkRuntime(), searchMode: SharedRadioSearchMode.AStarDense);
        medium.ApplyExternalTopology(
        [
            new(TopologyOperation.AddNode, Apc1, Position: new NodePosition(2, 1, 1)),
            new(TopologyOperation.AddNode, Apc2, Position: new NodePosition(6, 1, 1)),
            .. index.Links
        ]);
        var table = new DeviceAttachmentTable();
        var device = Device(201, 3, 1, DeviceMount.Fixed, true, 5);
        Apply(medium, table, index, [device]);
        Apply(medium, table, index, [device with { Position = new NodePosition(7, 1, 1) }]);
        var links = medium.Snapshot.Physical.Nodes[device.Id].Links;
        Check(links.Count == 1 && links.ContainsKey(Apc2), "moving between segments rewires to exactly one APC");
        var upper = Device(202, 6, 1, DeviceMount.Fixed, true, 5) with { Position = new NodePosition(6, 1, 2) };
        Apply(medium, table, index, [upper]);
        Check(medium.Route(new NodeId(201), upper.Id) is { Found: true } route &&
            route.Path.Select(id => id.Value).SequenceEqual(new ulong[] { 201, 2, 202 }), "a shared APC wires both levels");
        Check(medium.Route(Apc1, upper.Id).Found, "the second level joins the APC chain");
    }

    private static void CheckAttachments()
    {
        var medium = new SharedMediumNetwork(new NetworkRuntime(), searchMode: SharedRadioSearchMode.AStarDense,
            airtimeScope: RadioAirtimeScope.SpatialCells);
        var index = AreaWiring.Build(Spans, Apcs(true));
        var access = new RadioProfile(4, DeviceAttachmentTable.NetworkChannel, AccessPoint: true);
        medium.ApplyExternalTopology(
        [
            new(TopologyOperation.AddNode, Apc1, Position: new NodePosition(2, 1, 1)),
            new(TopologyOperation.AddNode, Apc2, Position: new NodePosition(6, 1, 1)),
            new(TopologyOperation.AddNode, Apc3, Position: new NodePosition(10, 1, 1)),
            .. index.Links
        ], [.. new[] { Apc1, Apc2, Apc3 }.Select(id => new RadioNodeUpdate(id, Profile: access, ChangeProfile: true))]);
        Check(!medium.Snapshot.Physical.Nodes[Apc1].Links.ContainsKey(Apc3), "non-adjacent APCs stay unlinked");

        var table = new DeviceAttachmentTable();
        var wiredA = Device(101, 3, 1, DeviceMount.Fixed, true, 5);
        var wiredC = Device(102, 11, 1, DeviceMount.Fixed, true, 5);
        var mobile = Device(103, 4, 1, DeviceMount.Mobile, true, 5);
        var noApc = Device(104, 2, 3, DeviceMount.Fixed, true, 5);
        var isolated = Device(105, 3, 3, DeviceMount.Fixed, true, 0);
        var island = Device(106, 1, 5, DeviceMount.Fixed, true, 0);
        var relayCandidate = Device(107, 6, 5, DeviceMount.Mobile, false, 4);
        var farStation = Device(108, 6, 9, DeviceMount.Mobile, false, 4);
        var plan = Apply(medium, table, index, [wiredA, wiredC, mobile, noApc, isolated, island, relayCandidate, farStation]);
        Check(plan.Attachments[wiredA.Id] == new DeviceAttachment(Apc1, null), "anchored fixed device uses its APC");
        Check(plan.Attachments[mobile.Id].Kind == "radio", "mobile class stays on radio while anchored");
        Check(plan.Attachments[noApc.Id].Kind == "radio", "area without APC uses radio");
        Check(plan.Attachments[isolated.Id].Kind == "isolated", "device without radio and APC is isolated");
        Check(plan.Attachments[island.Id] == new DeviceAttachment(Apc1, null) &&
            medium.Route(island.Id, wiredA.Id) is { Found: true } islandRoute &&
            islandRoute.Path.Select(id => id.Value).SequenceEqual(new ulong[] { 106, 1, 101 }),
            "a device in a walled-off island of the area is wired to its APC");
        var route = medium.Route(wiredA.Id, wiredC.Id);
        Check(route.Found && route.Path.Select(id => id.Value).SequenceEqual(new ulong[] { 101, 1, 2, 3, 102 }),
            "wired chain follows adjacent areas");
        Check(medium.Route(mobile.Id, wiredC.Id).Found && medium.Route(noApc.Id, wiredC.Id).Found,
            "radio devices reach the wired network through an access point");
        Check(!medium.Route(isolated.Id, wiredC.Id).Found, "isolated device has no path");
        Check(medium.Route(farStation.Id, relayCandidate.Id).Found && !medium.Route(farStation.Id, Apc2).Found,
            "a radio endpoint never relays foreign traffic");
        Check(medium.Snapshot.Physical.Nodes.Values.Where(node => node.Id.Value > 100).All(node => !node.CanForward),
            "devices are endpoints");
        Check(Plan(medium, table, index, [wiredA]) is { Changes.Count: 0, Radios.Count: 0 }, "repeated fact is a no-op");

        // Unanchoring removes the wired port and enables radio in one publication.
        var version = medium.Snapshot.Version;
        Apply(medium, table, index, [wiredA with { Anchored = false }]);
        var node = medium.Snapshot.Physical.Nodes[wiredA.Id];
        Check(medium.Snapshot.Version == version + 1 && node.Links.Count == 0 &&
            medium.Snapshot.Devices.ContainsKey(wiredA.Id), "unanchor switches to radio atomically");
        Check(medium.Route(wiredA.Id, wiredC.Id) is { Found: true } radioRoute &&
            radioRoute.Path[1] is var first && (first == Apc1 || first == Apc2), "unanchored device joins an access point");
        Apply(medium, table, index, [wiredA with { Anchored = false, Position = new NodePosition(30, 30, 1) }]);
        Check(!medium.Route(wiredA.Id, wiredC.Id).Found, "out of radio range means no path");
        Apply(medium, table, index, [wiredA]);
        Check(medium.Snapshot.Physical.Nodes[wiredA.Id].Links.ContainsKey(Apc1) &&
            !medium.Snapshot.Devices.ContainsKey(wiredA.Id), "anchoring restores the wired port without radio");

        // A map update without APC 1: its devices fall back to radio inside the same batch.
        var withoutFirst = AreaWiring.Build(Spans, Apcs(false));
        HashSet<NodeId> removed = [Apc1];
        var world = medium.Snapshot;
        var update = table.Plan(world, withoutFirst, id => id != Apc1 && world.Physical.Nodes.ContainsKey(id),
            _ => true, removed, [], [], table.Ids.ToArray());
        Check(!update.Changes.Any(change => change.Operation == TopologyOperation.Disconnect && change.B == Apc1),
            "links of a removed APC are not disconnected twice");
        medium.ApplyExternalTopology([new TopologyChange(TopologyOperation.RemoveNode, Apc1), .. update.Changes], update.Radios);
        table.Commit(update);
        Check(update.Attachments[wiredA.Id].Kind == "radio" && medium.Route(wiredA.Id, wiredC.Id) is { Found: true } fallback &&
            fallback.Path[1] == Apc2, "devices of a destroyed APC continue over radio");

        Reject(() => Plan(medium, table, withoutFirst, [Device(2, 6, 1, DeviceMount.Fixed, true, 5)]), "device cannot take an APC ID");
        Reject(() => Plan(medium, table, withoutFirst, [wiredC, wiredC]), "duplicate device facts");
        Reject(() => table.Plan(medium.Snapshot, withoutFirst, _ => true, _ => true, [], [], [new NodeId(999)], []), "unknown removal");
        Reject(() => Plan(medium, table, withoutFirst, [wiredC with { RadioRange = 129 }]), "radio range bound");
        var removal = table.Plan(medium.Snapshot, withoutFirst, _ => true, _ => true, [], [], [wiredC.Id], []);
        medium.ApplyExternalTopology(removal.Changes, removal.Radios);
        table.Commit(removal);
        Check(!medium.Snapshot.Physical.Nodes.ContainsKey(wiredC.Id) && !medium.Snapshot.Devices.ContainsKey(wiredC.Id) &&
            !table.Contains(wiredC.Id), "removal drops the node, its radio and its fact");
    }

    /// <summary>
    /// P3.5: an inactive device keeps its node and attachment but carries no traffic; behind an
    /// inactive APC a wired port stays wired and silent instead of falling back to radio.
    /// </summary>
    private static void CheckNodeStates()
    {
        var medium = new SharedMediumNetwork(new NetworkRuntime(), searchMode: SharedRadioSearchMode.AStarDense);
        var index = AreaWiring.Build(Spans, Apcs(true));
        var access = new RadioProfile(4, DeviceAttachmentTable.NetworkChannel, AccessPoint: true);
        medium.ApplyExternalTopology(
        [
            new(TopologyOperation.AddNode, Apc1, Position: new NodePosition(2, 1, 1)),
            new(TopologyOperation.AddNode, Apc2, Position: new NodePosition(6, 1, 1)),
            new(TopologyOperation.AddNode, Apc3, Position: new NodePosition(10, 1, 1)),
            .. index.Links
        ], [.. new[] { Apc1, Apc2, Apc3 }.Select(id => new RadioNodeUpdate(id, Profile: access, ChangeProfile: true))]);
        var table = new DeviceAttachmentTable();
        var wired = Device(301, 3, 1, DeviceMount.Fixed, true, 5);
        var handset = Device(302, 4, 1, DeviceMount.Mobile, false, 5);
        var far = Device(303, 11, 1, DeviceMount.Fixed, true, 5);
        Apply(medium, table, index, [wired, handset, far]);
        Check(medium.Route(wired.Id, far.Id).Found && medium.Route(handset.Id, far.Id).Found, "active devices reach the network");

        var plan = Apply(medium, table, index, [wired with { Powered = false }, handset with { Operational = false }]);
        var node = medium.Snapshot.Physical.Nodes[wired.Id];
        Check(node.Links.Count == 0 && !medium.Snapshot.Devices.ContainsKey(wired.Id) &&
            !medium.Snapshot.Devices.ContainsKey(handset.Id), "an inactive device has no wire and no radio");
        Check(plan.Attachments[wired.Id] == new DeviceAttachment(Apc1, null, Live: false) &&
            plan.Attachments[handset.Id] is { Kind: "radio", Live: false }, "an inactive device keeps its attachment");
        Check(medium.Snapshot.Physical.Nodes.ContainsKey(handset.Id) && !medium.Route(wired.Id, far.Id).Found,
            "an inactive device stays in the world without a path");
        Check(Plan(medium, table, index, [wired with { Powered = false }]) is { Changes.Count: 0, Radios.Count: 0 },
            "a repeated inactive state is a no-op");
        Apply(medium, table, index, [wired, handset]);
        Check(medium.Snapshot.Physical.Nodes[wired.Id].Links.ContainsKey(Apc1) && medium.Snapshot.Devices.ContainsKey(handset.Id),
            "restored state brings back the same ports");

        // APC 1 goes down: its wired device loses the port and does not switch to radio.
        Check(table.AttachedTo(index, new HashSet<NodeId> { Apc1 }).SequenceEqual([wired.Id]),
            "attachment lookup follows the map, not live links");
        bool Apc1Down(NodeId id) => id != Apc1;
        plan = Apply(medium, table, index, [], Apc1Down, [wired.Id]);
        Check(medium.Snapshot.Physical.Nodes[wired.Id].Links.Count == 0 && !medium.Snapshot.Devices.ContainsKey(wired.Id) &&
            plan.Attachments[wired.Id] is { Kind: "wired", Live: false }, "a port behind an inactive APC stays wired and silent");
        Check(table.AttachedTo(index, new HashSet<NodeId> { Apc1 }).SequenceEqual([wired.Id]),
            "a silent port is still found by its APC");
        Apply(medium, table, index, [], null, [wired.Id]);
        Check(medium.Snapshot.Physical.Nodes[wired.Id].Links.ContainsKey(Apc1) && medium.Route(wired.Id, far.Id).Found,
            "the APC coming back restores the port");
    }

    private static void CheckProfileReplacementValidation()
    {
        var medium = new SharedMediumNetwork(new NetworkRuntime());
        var a = new NodeId(1);
        medium.ApplyExternalTopology([new TopologyChange(TopologyOperation.AddNode, a, Position: new NodePosition(1, 1, 1))]);
        var version = medium.Snapshot.Version;
        Reject(() => medium.ApplyExternalTopology(
            [new TopologyChange(TopologyOperation.AddNode, new NodeId(2), Position: new NodePosition(2, 1, 1))],
            [new RadioNodeUpdate(new NodeId(3), Profile: new RadioProfile(4, 1), ChangeProfile: true)]), "radio for a missing node");
        Reject(() => medium.ApplyExternalTopology([new TopologyChange(TopologyOperation.RemoveNode, a)],
            [new RadioNodeUpdate(a, Profile: new RadioProfile(4, 1), ChangeProfile: true)]), "radio for a removed node");
        var missing = new NodeId(9);
        Reject(() => medium.ApplyExternalTopology(
            [new TopologyChange(TopologyOperation.MoveNode, missing, Position: new NodePosition(3, 1, 1))],
            [new RadioNodeUpdate(missing, Profile: new RadioProfile(4, 1), ChangeProfile: true)]), "a move does not create a node");
        Reject(() => medium.ApplyExternalTopology(
            [new TopologyChange(TopologyOperation.MoveNode, a, Position: new NodePosition(2, 1, 1))],
            [new RadioNodeUpdate(missing, Profile: null, ChangeProfile: true)]), "disabling radio of a missing node");
        Check(medium.Snapshot.Version == version && medium.Snapshot.Physical.Nodes.Count == 1 &&
            medium.Snapshot.Physical.Nodes[a].Position == new NodePosition(1, 1, 1), "a rejected replacement mutates nothing");
        medium.ApplyExternalTopology([], [new RadioNodeUpdate(a, Profile: new RadioProfile(4, 1), ChangeProfile: true)]);
        Check(medium.Snapshot.Version == version + 1 && medium.Snapshot.Devices.ContainsKey(a), "radio-only replacement");
        medium.ApplyExternalTopology(
        [
            new TopologyChange(TopologyOperation.RemoveNode, a),
            new TopologyChange(TopologyOperation.AddNode, a, Position: new NodePosition(1, 1, 1))
        ]);
        Check(!medium.Snapshot.Devices.ContainsKey(a), "a node removed and added in one batch loses its old radio");
    }

    private static DeviceFact Device(ulong id, int x, int y, DeviceMount mount, bool anchored, int range) =>
        new(new NodeId(id), new NodePosition(x, y, 1), mount, anchored, range);

    private static DevicePlan Plan(SharedMediumNetwork medium, DeviceAttachmentTable table, AreaWiringIndex index,
        DeviceFact[] facts, Func<NodeId, bool>? apcActive = null, NodeId[]? reresolve = null)
    {
        var world = medium.Snapshot;
        return table.Plan(world, index, world.Physical.Nodes.ContainsKey, apcActive ?? (_ => true), [], facts, [],
            reresolve ?? []);
    }

    private static DevicePlan Apply(SharedMediumNetwork medium, DeviceAttachmentTable table, AreaWiringIndex index,
        DeviceFact[] facts, Func<NodeId, bool>? apcActive = null, NodeId[]? reresolve = null)
    {
        var plan = Plan(medium, table, index, facts, apcActive, reresolve);
        medium.ApplyExternalTopology(plan.Changes, plan.Radios);
        table.Commit(plan);
        return plan;
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException($"Device smoke test accepted: {message}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"Device smoke test failed: {message}");
    }
}
