using NetDM.Core;

/// <summary>
/// ARCH-004: the channel-wide airtime table describes one tick and must not keep an entry per channel ever
/// used. These checks read the live table size, so a regression to unbounded growth fails here rather than
/// showing up as a slowly growing working set in a long round.
/// </summary>
internal static class AirtimeBoundsChecks
{
    private static void Check(bool condition, string message)
    {
        if (condition)
            return;
        Console.Error.WriteLine($"FAILED: {message}");
        Environment.ExitCode = 1;
        throw new InvalidOperationException(message);
    }

    public static void Run()
    {
        var runtime = new NetworkRuntime();
        runtime.Apply(
        [
            new TopologyChange(TopologyOperation.AddNode, new NodeId(1), Position: new NodePosition(0, 0)),
            new TopologyChange(TopologyOperation.AddNode, new NodeId(2), Position: new NodePosition(4, 0))
        ]);
        var medium = new SharedMediumNetwork(runtime, bytesPerTick: 4_096);
        Check(medium.AirtimeScope == RadioAirtimeScope.ChannelWide,
            "the channel-wide scope is the default under test");

        // Every channel used in one tick leaves exactly one entry: the table is the tick, not the history.
        for (uint channel = 1; channel <= 8; channel++)
            Check(Transmit(medium, channel, tick: 1), $"channel {channel} transmits in tick 1");
        Check(medium.ActiveAirtimeDomains == 8,
            $"eight channels leave eight slots, got {medium.ActiveAirtimeDomains}");

        // A new tick drops everything that described the previous one.
        Check(Transmit(medium, 42, tick: 2), "a new channel transmits in tick 2");
        Check(medium.ActiveAirtimeDomains == 1,
            $"a new tick keeps only its own channel, got {medium.ActiveAirtimeDomains}");

        // An older tick can no longer debit: the ordering guard survives the sweep.
        var stale = medium.Stats().RejectedOldTick;
        Check(!Transmit(medium, 42, tick: 1), "a transfer at an older tick is refused");
        Check(medium.Stats().RejectedOldTick == stale + 1, "the refusal is counted as an old tick");
        Check(medium.ActiveAirtimeDomains == 1, "the refusal did not add a slot");

        // Reusing the same channel in the same tick still accumulates, so the sweep did not weaken accounting.
        Check(Transmit(medium, 42, tick: 2, bytes: 1_024), "a second transfer fits in the same tick");
        Check(medium.ActiveAirtimeDomains == 1, "one channel stays one slot");
        Console.WriteLine("Airtime table bounds passed.");
    }

    private static bool Transmit(SharedMediumNetwork medium, uint channel, long tick, int bytes = 512)
    {
        var profile = new RadioProfile(15, channel);
        medium.Update(
        [
            new RadioNodeUpdate(new NodeId(1), Profile: profile, ChangeProfile: true),
            new RadioNodeUpdate(new NodeId(2), Profile: profile, ChangeProfile: true)
        ]);
        var route = medium.Route(new NodeId(1), new NodeId(2));
        return route.Found && medium.TryTransmit(route, bytes, tick);
    }
}
