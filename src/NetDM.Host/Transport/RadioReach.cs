using System.Text.Json;
using NetDM.Core;

namespace NetDM.Host.Transport;

/// <summary>
/// P8 Shadow: where would a radio transmission reach over the game network? The speaker's radio
/// reaches the nearest telecomms receiver (uplink), the receiver reaches the broadcasters (chain), and
/// the broadcasters reach each listening radio (downlink). Each leg is one analytic broadcast search
/// over the published topology; nothing is transmitted and no state changes. The receiver and the
/// broadcasters pass the transmission on as gateways within one packet, so a listener's ticks count the
/// whole route as the transport would advance it: telecomms processing adds no tick of its own.
/// </summary>
internal static class RadioReach
{
    public const int MaxRelays = 64;
    public const int MaxTargets = 1024;
    /// <summary>Wired hops the transport advances in one tick (MaxCableHopsPerTick of the game runtime).</summary>
    public const int CableHopsPerTick = 64;

    public static (bool Ok, string? Error, object? Result) Handle(SharedMediumSnapshot snapshot, JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body ||
            !TryId(body, "source", out var source) ||
            !TryIds(body, "receivers", MaxRelays, out var receivers) ||
            !TryIds(body, "broadcasters", MaxRelays, out var broadcasters) ||
            !TryIds(body, "targets", MaxTargets, out var targets))
            return (false, "invalid_request", null);
        if (!snapshot.Physical.Nodes.ContainsKey(source))
            return (true, null, Failed("no_source", targets.Length));
        var up = SharedMediumNetwork.ReachAll(snapshot, [source], receivers.ToHashSet(), cableHopsPerTick: CableHopsPerTick);
        if (up.Count == 0)
            return (true, null, Failed("no_uplink", targets.Length));
        var (receiver, uplink) = up.MinBy(entry => (entry.Value.Cost, entry.Key.Value));
        var chain = SharedMediumNetwork.ReachAll(snapshot, new Dictionary<NodeId, BroadcastReach> { [receiver] = uplink },
            broadcasters.ToHashSet(), cableHopsPerTick: CableHopsPerTick);
        if (chain.Count == 0)
            return (true, null, Failed("no_chain", targets.Length, receiver, uplink));
        var down = SharedMediumNetwork.ReachAll(snapshot, chain, targets.ToHashSet(), cableHopsPerTick: CableHopsPerTick);
        var ticks = new int[targets.Length];
        var hops = new int[targets.Length];
        var cells = new HashSet<NodeId>();
        for (var i = 0; i < targets.Length; i++)
        {
            if (!down.TryGetValue(targets[i], out var reach))
            {
                ticks[i] = hops[i] = -1;
                continue;
            }
            hops[i] = reach.Hops;
            ticks[i] = reach.Ticks;
            // One transmission per radio cell: listeners behind the same last hop share its airtime.
            if (reach.RadioHops > 0)
                cells.Add(reach.LastHop);
        }
        return (true, null, new
        {
            reason = (string?)null, receiver = receiver.ToString(), uplinkHops = uplink.Hops, uplinkRadioHops = uplink.RadioHops,
            broadcasters = chain.Count, ticks, hops, transmissions = cells.Count, reached = down.Count
        });
    }

    private static object Failed(string reason, int targets, NodeId? receiver = null, BroadcastReach? uplink = null) => new
    {
        reason, receiver = receiver?.ToString(), uplinkHops = uplink?.Hops ?? -1, uplinkRadioHops = uplink?.RadioHops ?? -1,
        broadcasters = 0, ticks = Enumerable.Repeat(-1, targets).ToArray(), hops = Enumerable.Repeat(-1, targets).ToArray(),
        transmissions = 0, reached = 0
    };

    private static bool TryId(JsonElement body, string name, out NodeId id)
    {
        id = default;
        return body.TryGetProperty(name, out var value) && TryParse(value, out id);
    }

    private static bool TryIds(JsonElement body, string name, int limit, out NodeId[] ids)
    {
        ids = [];
        if (!body.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > limit)
            return false;
        var list = new List<NodeId>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (!TryParse(item, out var id))
                return false;
            list.Add(id);
        }
        ids = [.. list];
        return true;
    }

    private static bool TryParse(JsonElement value, out NodeId id)
    {
        id = default;
        ulong raw;
        var ok = value.ValueKind switch
        {
            JsonValueKind.String => ulong.TryParse(value.GetString(), out raw),
            JsonValueKind.Number => value.TryGetUInt64(out raw),
            _ => (raw = 0) == 1
        };
        if (!ok || raw == 0)
            return false;
        id = new NodeId(raw);
        return true;
    }
}
