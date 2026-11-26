namespace NetDM.Core;

/// <summary>Mounting class reported by the world. A mobile class stays on radio even while anchored.</summary>
public enum DeviceMount : byte { Fixed, Mobile }

/// <summary>
/// Reported power and operational state of a world node. Only an active node carries traffic:
/// without power, broken or EMP-disabled it keeps its ID, endpoints and attachment, but no links.
/// </summary>
public readonly record struct NodeState(bool Powered, bool Operational)
{
    public static NodeState Up { get; } = new(true, true);
    public bool Active => Powered && Operational;
}

/// <summary>
/// World facts of one network device. The world reports where the object is, how it is
/// mounted and whether it works; it never names an APC. A zero radio range means no radio.
/// </summary>
public readonly record struct DeviceFact(NodeId Id, NodePosition Position, DeviceMount Mount, bool Anchored,
    int RadioRange, bool Powered = true, bool Operational = true)
{
    public NodeState State => new(Powered, Operational);
    /// <summary>Optional processor power for devices whose radio can keep listening in standby.</summary>
    public bool? ExecutionPowered { get; init; }
}

/// <summary>
/// Wired port to one APC, a radio profile, or neither (isolated). The attachment follows the
/// world facts alone; <see cref="Live"/> says whether it carries traffic now. A wired port
/// stays wired while the device or its APC is inactive: there is no automatic radio bypass.
/// </summary>
public readonly record struct DeviceAttachment(NodeId Apc, RadioProfile? Radio, bool Live = true)
{
    public bool Wired => Apc.Value != 0;
    public string Kind => Wired ? "wired" : Radio.HasValue ? "radio" : "isolated";
}

/// <summary>
/// States of world nodes that are not devices, such as the APCs of the map. A node is active
/// until reported otherwise; only inactive nodes are stored.
/// </summary>
public sealed class NodeStateTable
{
    private readonly Dictionary<NodeId, NodeState> _inactive = [];

    public int InactiveCount => _inactive.Count;
    public IReadOnlyDictionary<NodeId, NodeState> Inactive => _inactive;
    public bool IsActive(NodeId id) => !_inactive.ContainsKey(id);
    public NodeState Get(NodeId id) => _inactive.TryGetValue(id, out var state) ? state : NodeState.Up;

    public void Set(NodeId id, NodeState state)
    {
        if (state.Active)
            _inactive.Remove(id);
        else
            _inactive[id] = state;
    }

    public void RemoveWhere(Func<NodeId, bool> predicate)
    {
        foreach (var id in _inactive.Keys.Where(predicate).ToArray())
            _inactive.Remove(id);
    }
}

/// <summary>One atomic topology transaction for devices, plus the facts it commits.</summary>
public sealed class DevicePlan
{
    public List<TopologyChange> Changes { get; } = [];
    public List<RadioNodeUpdate> Radios { get; } = [];
    public Dictionary<NodeId, DeviceAttachment> Attachments { get; } = [];
    internal Dictionary<NodeId, DeviceFact?> Facts { get; } = [];
}

/// <summary>
/// Device attachments of one world runtime. An anchored fixed device on a cell of an
/// APC-served segment has exactly one wired port, to that APC. Every other device uses its
/// radio profile within range. Devices are endpoints: they never forward foreign traffic.
/// Plans compare the desired attachment with the actual links and radio of the medium,
/// so an attachment switch removes the old path in the same publication that adds the new.
/// </summary>
public sealed class DeviceAttachmentTable
{
    public const int MaxDevices = 4096;
    public const int MaxRadioRange = 128;
    public const uint NetworkChannel = 1;
    public const uint WireCost = 1;
    private readonly Dictionary<NodeId, DeviceFact> _facts = [];

    public int Count => _facts.Count;
    public bool Contains(NodeId id) => _facts.ContainsKey(id);
    public IReadOnlyCollection<NodeId> Ids => _facts.Keys;
    public bool TryGet(NodeId id, out DeviceFact fact) => _facts.TryGetValue(id, out fact);
    public IEnumerable<KeyValuePair<NodeId, NodeState>> InactiveStates =>
        _facts.Where(pair => !pair.Value.State.Active).Select(pair => KeyValuePair.Create(pair.Key, pair.Value.State));
    public IEnumerable<NodeId> ExecutionPaused =>
        _facts.Where(pair => pair.Value.ExecutionPowered == false).Select(pair => pair.Key);

    public static DeviceAttachment Resolve(DeviceFact fact, AreaWiringIndex? map, Func<NodeId, bool> apcAvailable)
    {
        if (fact.Mount == DeviceMount.Fixed && fact.Anchored && map is not null &&
            map.TryFindApc(fact.Position, out var apc) && apcAvailable(apc))
            return new DeviceAttachment(apc, null);
        return fact.RadioRange > 0
            ? new DeviceAttachment(default, new RadioProfile(fact.RadioRange, NetworkChannel))
            : default;
    }

    /// <summary>
    /// Plans device changes that follow <paramref name="removed"/> nodes deleted earlier in the
    /// same batch. <paramref name="apcAvailable"/> answers whether an APC exists after the batch,
    /// <paramref name="apcActive"/> whether it carries traffic. Upserts and removals must name
    /// distinct IDs; re-resolved devices keep their stored facts.
    /// </summary>
    public DevicePlan Plan(SharedMediumSnapshot world, AreaWiringIndex? map, Func<NodeId, bool> apcAvailable,
        Func<NodeId, bool> apcActive, IReadOnlyCollection<NodeId> removed, IReadOnlyList<DeviceFact> upserts,
        IReadOnlyCollection<NodeId> removals, IEnumerable<NodeId> reresolve)
    {
        var plan = new DevicePlan();
        var added = 0;
        foreach (var fact in upserts)
        {
            Validate(fact);
            if (!plan.Facts.TryAdd(fact.Id, fact))
                throw new ArgumentException("Device facts must name distinct nodes.");
            if (_facts.ContainsKey(fact.Id))
                continue;
            if (world.Physical.Nodes.ContainsKey(fact.Id))
                throw new ArgumentException("Device ID belongs to another world node.");
            added++;
        }
        foreach (var id in removals)
        {
            if (!_facts.ContainsKey(id) || !plan.Facts.TryAdd(id, null))
                throw new ArgumentException("Device removal must name a distinct known device.");
            added--;
        }
        if (_facts.Count + added > MaxDevices)
            throw new ArgumentException("device_budget_exceeded");

        foreach (var (id, fact) in plan.Facts)
        {
            if (fact is { } next)
                Reconcile(plan, world, map, apcAvailable, apcActive, removed, next);
            else
                plan.Changes.Add(new TopologyChange(TopologyOperation.RemoveNode, id));
        }
        foreach (var id in reresolve.Distinct())
            if (!plan.Facts.ContainsKey(id) && _facts.TryGetValue(id, out var stored))
                Reconcile(plan, world, map, apcAvailable, apcActive, removed, stored);
        return plan;
    }

    public void Commit(DevicePlan plan)
    {
        foreach (var (id, fact) in plan.Facts)
        {
            if (fact is { } next)
                _facts[id] = next;
            else
                _facts.Remove(id);
        }
    }

    /// <summary>
    /// Devices whose position resolves to one of <paramref name="apcs"/> in <paramref name="map"/>,
    /// whether or not the port carries traffic now: an inactive APC has no links to follow.
    /// </summary>
    public IEnumerable<NodeId> AttachedTo(AreaWiringIndex? map, IReadOnlySet<NodeId> apcs)
    {
        if (map is null || apcs.Count == 0)
            yield break;
        foreach (var (id, fact) in _facts)
            if (fact.Mount == DeviceMount.Fixed && fact.Anchored && map.TryFindApc(fact.Position, out var apc) &&
                apcs.Contains(apc))
                yield return id;
    }

    private static void Validate(DeviceFact fact)
    {
        if (fact.Id.Value == 0 || !Enum.IsDefined(fact.Mount) || fact.RadioRange is < 0 or > MaxRadioRange)
            throw new ArgumentException("Invalid device fact.");
    }

    private static void Reconcile(DevicePlan plan, SharedMediumSnapshot world, AreaWiringIndex? map,
        Func<NodeId, bool> apcAvailable, Func<NodeId, bool> apcActive, IReadOnlyCollection<NodeId> removed,
        DeviceFact fact)
    {
        var desired = Resolve(fact, map, apcAvailable);
        // An inactive device has neither wire nor radio. Behind an inactive APC the port stays
        // wired and silent: the device does not fall back to radio.
        var wire = desired.Wired && fact.State.Active && apcActive(desired.Apc) ? desired.Apc : default;
        var radio = fact.State.Active ? desired.Radio : null;
        plan.Attachments[fact.Id] = desired with { Live = wire.Value != 0 || radio.HasValue };
        if (!world.Physical.Nodes.TryGetValue(fact.Id, out var node))
        {
            plan.Changes.Add(new TopologyChange(TopologyOperation.AddNode, fact.Id, Position: fact.Position)
                { CanForward = false });
            if (wire.Value != 0)
                plan.Changes.Add(new TopologyChange(TopologyOperation.Connect, fact.Id, wire, WireCost));
            if (radio is { } profile)
                plan.Radios.Add(new RadioNodeUpdate(fact.Id, Profile: profile, ChangeProfile: true));
            return;
        }

        // Old ports go first; a link to a node removed earlier in the batch is already gone.
        foreach (var link in node.Links.Keys)
            if (link != wire && !removed.Contains(link))
                plan.Changes.Add(new TopologyChange(TopologyOperation.Disconnect, fact.Id, link));
        if (node.Position != fact.Position)
            plan.Changes.Add(new TopologyChange(TopologyOperation.MoveNode, fact.Id, Position: fact.Position));
        if (node.CanForward)
            plan.Changes.Add(new TopologyChange(TopologyOperation.SetForwarding, fact.Id) { CanForward = false });
        if (wire.Value != 0 && (!node.Links.TryGetValue(wire, out var edge) || edge.Cost != WireCost ||
                removed.Contains(wire)))
            plan.Changes.Add(new TopologyChange(TopologyOperation.Connect, fact.Id, wire, WireCost));
        RadioProfile? actual = world.Devices.TryGetValue(fact.Id, out var current) ? current : null;
        if (actual != radio)
            plan.Radios.Add(new RadioNodeUpdate(fact.Id, Profile: radio, ChangeProfile: true));
    }
}
