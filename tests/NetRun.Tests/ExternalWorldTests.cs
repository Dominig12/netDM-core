using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>
/// §14: the outside world is functional places, generated deterministically, discovered by acting on them,
/// and never rewritten by a change of detail.
/// </summary>
internal static class ExternalWorldTests
{
    public static void Run()
    {
        Section("external world");
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var world = new ExternalNetwork(runtime, clock);
        var explorer = runtime.NewPrincipal();

        // A site is a real place: it has hosts, links, services and a reason to exist.
        var archive = world.Create(seed: 12_345, SiteTemplate.Archive);
        Equal(LifeStatus.Ok, archive.Status, "an archive is generated");
        var site = archive.Site!;
        Check(site.Hosts >= 8 && site.Hosts <= 25, $"the site has a meaningful size: {site.Hosts}");
        Check(site.Links >= site.Hosts - 1, "every host is reachable through the link chain");
        Check(site.Feature.Length > 0, "the site has a distinguishing feature");
        var topology = world.Topology(site.Address)!.Value;
        Check(topology.Hosts.Length == site.Hosts, "the topology matches the snapshot");
        var defined = 0;
        foreach (var host in topology.Hosts)
        {
            var snapshot = runtime.Hosts.Get(host);
            Check(snapshot is not null, "every site host exists in the registry");
            if (snapshot?.Services is not null)
                defined++;
        }
        Equal(site.Hosts, defined, "every host publishes at least one service");

        // The same seed produces the same place: generation is reproducible, not random.
        var other = new ExternalNetwork(runtime, clock);
        var replay = other.Create(seed: 12_345, SiteTemplate.Archive);
        Equal(site.Hosts, replay.Site!.Hosts, "the same seed builds the same number of hosts");
        Equal(site.Links, replay.Site.Links, "the same seed builds the same number of links");
        Equal(site.Feature, replay.Site.Feature, "the same seed builds the same feature");

        // Discovery is earned step by step and by this observer alone.
        Equal(DiscoveryStage.Unknown, world.StageOf(explorer, site.Address), "a site is unknown before a signal");
        Equal(DiscoveryStage.Beacon, world.Advance(explorer, site.Address).Stage, "a heard signal is a beacon");
        Equal(DiscoveryStage.Identified, world.Advance(explorer, site.Address).Stage, "the kind of place is identified next");
        Equal(DiscoveryStage.Connected, world.Advance(explorer, site.Address).Stage, "then a link is established");
        Equal(DiscoveryStage.Explored, world.Advance(explorer, site.Address).Stage, "finally the interior is explored");
        Equal(LifeStatus.Duplicate, world.Advance(explorer, site.Address).Status, "discovery cannot go past exploration");
        var stranger = runtime.NewPrincipal();
        Equal(DiscoveryStage.Unknown, world.StageOf(stranger, site.Address),
            "one observer's knowledge is not another's");
        Equal(1, world.Known(explorer, 8).Length, "only known sites are listed for the explorer");
        Equal(0, world.Known(stranger, 8).Length, "a stranger sees nothing");

        // Detail never rewrites the place: it is the same hosts, links and state at any level.
        var beforeHosts = world.Topology(site.Address)!.Value.Hosts;
        Equal(LifeStatus.Ok, world.SetActivity(site.Address, ActivityLevel.Dormant).Status, "the site can go dormant");
        Equal(ActivityLevel.Dormant, world.Get(site.Address)!.Activity, "the level is recorded");
        Check(world.Topology(site.Address)!.Value.Hosts.SequenceEqual(beforeHosts), "detail did not rebuild the site");
        Equal(site.Hosts, world.Get(site.Address)!.Hosts, "detail created no hosts");
        Equal(LifeStatus.Ok, world.SetActivity(site.Address, ActivityLevel.Active).Status, "the site can be active again");

        // Power follows the physical expedition: without it the hosts are unavailable, with it they return.
        var firstHost = beforeHosts[0];
        Equal(LifeStatus.Ok, world.SetPowered(site.Address, false).Status, "the expedition cuts the power");
        Check(!runtime.Hosts.Get(firstHost)!.Available, "an unpowered site is not usable");
        Equal(LifeStatus.Ok, world.SetPowered(site.Address, true).Status, "the expedition restores the power");
        Check(runtime.Hosts.Get(firstHost)!.Available, "a restored site is usable again");

        // Sites are bounded and addressable; a duplicate seed does not silently replace a live place.
        Equal(LifeStatus.Duplicate, world.Create(seed: 12_345, SiteTemplate.Archive).Status,
            "the same address is not materialised twice");
        var small = new ExternalNetwork(runtime, clock, new ExternalNetworkOptions(MaxSites: 2));
        Equal(LifeStatus.Ok, small.Create(seed: 1, SiteTemplate.PublicNode).Status, "the first site fits");
        Equal(LifeStatus.Ok, small.Create(seed: 2, SiteTemplate.Hosting).Status, "the second site fits");
        Equal(LifeStatus.InvalidRequest, small.Create(seed: 3, SiteTemplate.Anomaly).Status,
            "the site budget is enforced");

        // Every template produces a usable place, each with its own character.
        foreach (var template in Enum.GetValues<SiteTemplate>())
        {
            var created = world.Create(seed: 5_000 + (int)template, template);
            Equal(LifeStatus.Ok, created.Status, $"template {template} generates");
            Check(created.Site!.Hosts >= 8, $"template {template} has a meaningful size");
        }
        _ = runtime.Programs;
    }
}
