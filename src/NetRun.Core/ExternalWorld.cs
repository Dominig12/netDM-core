using System.Globalization;

namespace NetRun.Core;

/// <summary>What a site is for (§14.2). A template picks the shape of a functional place, not a random graph.</summary>
public enum SiteTemplate : byte
{
    /// <summary>An abandoned archive: knowledge, old services, little power.</summary>
    Archive,
    /// <summary>A public node: message boards, directories, an open door and nothing secret.</summary>
    PublicNode,
    /// <summary>An independent hosting service: compute for rent, files, contracts.</summary>
    Hosting,
    /// <summary>A viral colony: corrupted hosts, a replicator, a defended core.</summary>
    ViralColony,
    /// <summary>An anomaly: services that answer strangely and change what they touch.</summary>
    Anomaly
}

/// <summary>
/// How much one observer has learned about a site (§14.1). Steps are taken by acting on the site, never by
/// the fact that it exists: a beacon is not a route and a route is not access.
/// </summary>
public enum DiscoveryStage : byte
{
    Unknown = 0,
    /// <summary>A signal was heard: the address is known, nothing else is.</summary>
    Beacon = 1,
    /// <summary>The kind of place was identified from its answer.</summary>
    Identified = 2,
    /// <summary>A link was established and traffic flows both ways.</summary>
    Connected = 3,
    /// <summary>The internal topology has been explored.</summary>
    Explored = 4
}

/// <summary>How much of a site is simulated right now (§14.3). Lower detail never rewrites the outcome of what happened.</summary>
public enum ActivityLevel : byte
{
    Active, Background, Dormant, Unvisited
}

public sealed record VirtualSiteSnapshot(
    ulong Address,
    string Name,
    SiteTemplate Template,
    int Seed,
    ActivityLevel Activity,
    int Hosts,
    int Links,
    string Feature,
    bool Powered);

public sealed record SiteResult(LifeStatus Status, VirtualSiteSnapshot? Site, string? Reason = null)
{
    public bool Succeeded => Status == LifeStatus.Ok;
}

public sealed record StageResult(LifeStatus Status, DiscoveryStage Stage, string? Reason = null)
{
    public bool Succeeded => Status == LifeStatus.Ok;
}

public sealed record ExternalNetworkOptions(
    int MaxSites = 64,
    int MinHosts = 8,
    int MaxHosts = 25,
    int MaxLinks = 96,
    int MaxObservers = 1_024);

/// <summary>
/// The outside world (§14): functional places with provenance, purpose, topology, resources and a reason to
/// go there. Generation is deterministic from the seed, so a site is reproducible for tests and for a round
/// that has to be replayed; a session that merely stops looking never resets one (WORLD-001).
/// </summary>
public sealed class ExternalNetwork(NetRunRuntime runtime, ISimulationClock clock, ExternalNetworkOptions? options = null)
{
    private sealed class Site(ulong address, string name, SiteTemplate template, int seed, string feature)
    {
        public readonly ulong Address = address;
        public readonly string Name = name;
        public readonly SiteTemplate Template = template;
        public readonly int Seed = seed;
        public readonly string Feature = feature;
        public readonly List<HostId> Hosts = [];
        public readonly List<(HostId A, HostId B)> Links = [];
        public ActivityLevel Activity = ActivityLevel.Active;
        public bool Powered = true;
    }

    private readonly NetRunRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    private readonly ISimulationClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ExternalNetworkOptions _options = options ?? new ExternalNetworkOptions();
    private readonly Dictionary<ulong, Site> _sites = [];
    private readonly Dictionary<ulong, Dictionary<ulong, DiscoveryStage>> _knowledge = [];
    private ulong _nextAddress = 0x0C00;

    public int Count => _sites.Count;

    /// <summary>
    /// Materialises one site. The same seed and template always produce the same place: the generator is a
    /// plain deterministic sequence, not a random graph, and every host it makes publishes something.
    /// </summary>
    public SiteResult Create(int seed, SiteTemplate template, string? name = null)
    {
        if (_sites.Count >= _options.MaxSites)
            return new SiteResult(LifeStatus.InvalidRequest, null, "limit");
        if (!Enum.IsDefined(template))
            return new SiteResult(LifeStatus.InvalidRequest, null, "template");
        var address = _nextAddress + (ulong)(seed & 0xFF);
        if (_sites.ContainsKey(address))
            return new SiteResult(LifeStatus.Duplicate, Snapshot(_sites[address]), "address");

        var random = new XorShift(unchecked((uint)seed * 2_654_435_761u) | 1u);
        var hostCount = _options.MinHosts + (int)(random.Next() % (uint)(_options.MaxHosts - _options.MinHosts + 1));
        var siteName = name ?? string.Create(CultureInfo.InvariantCulture, $"{template} {seed & 0xFF:x2}");
        var feature = FeatureOf(template, random);
        var site = new Site(address, siteName, template, seed, feature);
        for (var index = 0; index < hostCount; index++)
        {
            var host = MakeHost(site, index, random);
            if (host?.Host is { } created)
                site.Hosts.Add(created.Id);
        }
        if (site.Hosts.Count < 2)
            return new SiteResult(LifeStatus.InsufficientResources, null, "hosts");

        // Links: a chain so every host is reachable, then a few extra edges for routes worth choosing.
        for (var index = 1; index < site.Hosts.Count; index++)
            site.Links.Add((site.Hosts[index - 1], site.Hosts[index]));
        var extra = Math.Min(_options.MaxLinks - site.Links.Count, site.Hosts.Count / 2);
        for (var index = 0; index < extra; index++)
        {
            var a = site.Hosts[(int)(random.Next() % (uint)site.Hosts.Count)];
            var b = site.Hosts[(int)(random.Next() % (uint)site.Hosts.Count)];
            if (a != b)
                site.Links.Add((a, b));
        }
        _sites[address] = site;
        return new SiteResult(LifeStatus.Ok, Snapshot(site));
    }

    private HostResult? MakeHost(Site site, int index, XorShift random)
    {
        var roll = random.Next() % 100;
        var (hostClass, compute, memory, storage, profile, service) = site.Template switch
        {
            SiteTemplate.Archive => roll < 60
                ? (HostClass.Server, new ComputeComponent(6, 2), new MemoryComponent(4), new StorageComponent(65_536),
                    SecurityProfile.Standard, "archive.read")
                : (HostClass.Terminal, new ComputeComponent(2, 1), new MemoryComponent(1), new StorageComponent(4_096),
                    SecurityProfile.Open, "archive.index"),
            SiteTemplate.PublicNode => roll < 70
                ? (HostClass.Terminal, new ComputeComponent(3, 1), new MemoryComponent(2), new StorageComponent(8_192),
                    SecurityProfile.Open, "public.board")
                : (HostClass.Relay, new ComputeComponent(4, 2), new MemoryComponent(2), new StorageComponent(2_048),
                    SecurityProfile.Open, "public.relay"),
            SiteTemplate.Hosting => roll < 55
                ? (HostClass.Server, new ComputeComponent(12, 4), new MemoryComponent(8), new StorageComponent(131_072),
                    SecurityProfile.Standard, "hosting.compute")
                : (HostClass.Server, new ComputeComponent(8, 3), new MemoryComponent(6), new StorageComponent(65_536),
                    SecurityProfile.Standard, "hosting.storage"),
            SiteTemplate.ViralColony => roll < 50
                ? (HostClass.Server, new ComputeComponent(10, 4), new MemoryComponent(6), new StorageComponent(32_768),
                    SecurityProfile.Secure, "colony.core")
                : (HostClass.Terminal, new ComputeComponent(3, 2), new MemoryComponent(2), new StorageComponent(4_096),
                    SecurityProfile.Secure, "colony.worker"),
            _ => roll < 50
                ? (HostClass.Virtual, new ComputeComponent(8, 3), new MemoryComponent(4), new StorageComponent(16_384),
                    SecurityProfile.Secure, "anomaly.node")
                : (HostClass.Virtual, new ComputeComponent(4, 2), new MemoryComponent(2), new StorageComponent(4_096),
                    SecurityProfile.Secure, "anomaly.echo")
        };
        // Host names live in the Host's alphabet: the address is stable and carries no spaces from a title.
        var name = string.Create(CultureInfo.InvariantCulture, $"site.{site.Address:x4}.{index:x2}");
        var definition = new HostDefinition(
            name,
            hostClass,
            Compute: compute,
            Memory: memory,
            Storage: storage,
            Security: new SecurityComponent(profile, 4, 2),
            Services: new ServicesComponent(new Dictionary<string, string> { [service] = name }));
        return _runtime.RegisterHost(definition);
    }

    private static string FeatureOf(SiteTemplate template, XorShift random)
    {
        var roll = random.Next() % 4;
        return (template, roll) switch
        {
            (SiteTemplate.Archive, 0) => "архив помнит удалённые записи",
            (SiteTemplate.Archive, 1) => "индекс отвечает быстрее, чем должен",
            (SiteTemplate.Archive, 2) => "часть полок уже разобрана",
            (SiteTemplate.Archive, _) => "питание держится на одном контуре",
            (SiteTemplate.PublicNode, 0) => "открытая доска без модерации",
            (SiteTemplate.PublicNode, 1) => "публичный ретранслятор с очередью",
            (SiteTemplate.PublicNode, 2) => "адрес известен всем, кто слушал",
            (SiteTemplate.PublicNode, _) => "сервис только для чтения",
            (SiteTemplate.Hosting, 0) => "вычисления сдаются в аренду",
            (SiteTemplate.Hosting, 1) => "хранилище арендуется по контракту",
            (SiteTemplate.Hosting, 2) => "оплата только по журналу",
            (SiteTemplate.Hosting, _) => "часть мощностей уже занята",
            (SiteTemplate.ViralColony, 0) => "ядро колонии размножается",
            (SiteTemplate.ViralColony, 1) => "рабочие копии не отвечают на приветствие",
            (SiteTemplate.ViralColony, 2) => "колония защищается при обнаружении",
            (SiteTemplate.ViralColony, _) => "заражение локально и ограничено",
            (SiteTemplate.Anomaly, 0) => "ответы приходят раньше запроса",
            (SiteTemplate.Anomaly, 1) => "узел меняет то, что через него идёт",
            (SiteTemplate.Anomaly, 2) => "наблюдатель видит не то, что снаружи",
            _ => "арифметика трафика не сходится"
        };
    }

    public VirtualSiteSnapshot? Get(ulong address) => _sites.TryGetValue(address, out var site) ? Snapshot(site) : null;

    private static VirtualSiteSnapshot Snapshot(Site site) => new(site.Address, site.Name, site.Template, site.Seed,
        site.Activity, site.Hosts.Count, site.Links.Count, site.Feature, site.Powered);

    public VirtualSiteSnapshot[] All(int limit)
    {
        var count = Math.Min(Math.Max(limit, 0), _sites.Count);
        var result = new VirtualSiteSnapshot[count];
        var index = 0;
        foreach (var site in _sites.Values.OrderBy(site => site.Address))
        {
            if (index >= count)
                break;
            result[index++] = Snapshot(site);
        }
        return result;
    }

    /// <summary>The hosts and links of a site, for an explorer that has reached it.</summary>
    public (HostId[] Hosts, (HostId A, HostId B)[] Links)? Topology(ulong address) =>
        _sites.TryGetValue(address, out var site) ? (site.Hosts.ToArray(), site.Links.ToArray()) : null;

    /// <summary>What one observer knows about one site. Nothing is disclosed beyond the stage it reached.</summary>
    public DiscoveryStage StageOf(PrincipalId observer, ulong address)
    {
        if (observer.IsNone)
            return DiscoveryStage.Unknown;
        return _knowledge.TryGetValue(observer.Value, out var sites) && sites.TryGetValue(address, out var stage)
            ? stage
            : DiscoveryStage.Unknown;
    }

    /// <summary>
    /// Advances what an observer knows about a site by exactly one step. A stage can never be skipped by asking
    /// for it, and a signal heard by somebody else is not knowledge of this observer.
    /// </summary>
    public StageResult Advance(PrincipalId observer, ulong address)
    {
        if (observer.IsNone)
            return new StageResult(LifeStatus.InvalidRequest, DiscoveryStage.Unknown, "observer");
        if (!_sites.ContainsKey(address))
            return new StageResult(LifeStatus.UnknownIdentity, DiscoveryStage.Unknown, "site");
        if (!_knowledge.TryGetValue(observer.Value, out var sites))
        {
            if (_knowledge.Count >= _options.MaxObservers)
                return new StageResult(LifeStatus.InvalidRequest, DiscoveryStage.Unknown, "observers");
            sites = [];
            _knowledge[observer.Value] = sites;
        }
        var current = sites.TryGetValue(address, out var stage) ? stage : DiscoveryStage.Unknown;
        if (current == DiscoveryStage.Explored)
            return new StageResult(LifeStatus.Duplicate, current, "explored");
        sites[address] = current + 1;
        return new StageResult(LifeStatus.Ok, sites[address]);
    }

    /// <summary>
    /// Background detail (§14.3). Changing it must not create resources, undo consequences or decide an active
    /// operation: the state of the site is exactly what it was.
    /// </summary>
    public SiteResult SetActivity(ulong address, ActivityLevel level)
    {
        if (!_sites.TryGetValue(address, out var site))
            return new SiteResult(LifeStatus.UnknownIdentity, null, "site");
        if (!Enum.IsDefined(level))
            return new SiteResult(LifeStatus.InvalidRequest, null, "level");
        site.Activity = level;
        return new SiteResult(LifeStatus.Ok, Snapshot(site));
    }

    /// <summary>Power of a site, for the physical expedition that has to restore it (§14.1).</summary>
    public SiteResult SetPowered(ulong address, bool powered)
    {
        if (!_sites.TryGetValue(address, out var site))
            return new SiteResult(LifeStatus.UnknownIdentity, null, "site");
        site.Powered = powered;
        foreach (var host in site.Hosts)
        {
            if (_runtime.Hosts.Get(host) is { } snapshot)
                _runtime.SetHostLifecycle(host, snapshot.Generation, powered, snapshot.Operational);
        }
        return new SiteResult(LifeStatus.Ok, Snapshot(site));
    }

    /// <summary>Bounded listing of what one observer may see at all: only sites it has heard of.</summary>
    public VirtualSiteSnapshot[] Known(PrincipalId observer, int limit)
    {
        if (limit <= 0 || observer.IsNone || !_knowledge.TryGetValue(observer.Value, out var sites))
            return [];
        return sites
            .Where(entry => entry.Value > DiscoveryStage.Unknown && _sites.ContainsKey(entry.Key))
            .OrderBy(entry => entry.Key)
            .Take(limit)
            .Select(entry => Snapshot(_sites[entry.Key]))
            .ToArray();
    }

    public long NowMilliseconds => _clock.NowMilliseconds;

    /// <summary>A deterministic sequence that does not depend on the runtime's random implementation.</summary>
    private struct XorShift(uint seed)
    {
        private uint _state = seed == 0 ? 0x9E3779B9u : seed;

        public uint Next()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state;
        }
    }
}
