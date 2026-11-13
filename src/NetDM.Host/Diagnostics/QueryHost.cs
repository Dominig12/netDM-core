using System.Globalization;
using System.Text.Json;
using NetDM.Core;
using NetDM.Host.Transport;
using NetDM.Services;

namespace NetDM.Host.Diagnostics;

/// <summary>
/// network.query (§14.5, P10): bounded typed queries a network device answers once the request reached
/// its node. The first resource is the service directory: what the asker may discover and the device
/// reaches over the network now. A service the asker may not discover, and one the device cannot reach,
/// are both simply absent: a query never tells that something hidden or cut off exists.
/// </summary>
public static class QueryHost
{
    public const string ServiceType = "network.query";
    public const int DefaultLimit = 8, MaxLimit = 16;
    /// <summary>Directory entries examined for one page, reachable or not.</summary>
    public const int ScanBudget = 64;
    /// <summary>Nodes one reachability search may expand.</summary>
    public const int ReachBudget = 20_000;

    public static JsonElement Answer(GameNetworkService game, HostCommand command)
    {
        using var arguments = JsonDocument.Parse(command.Arguments);
        var args = arguments.RootElement;
        var type = args.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
        var limit = args.TryGetProperty("limit", out var limitValue) ? Math.Clamp(limitValue.GetInt32(), 1, MaxLimit) : DefaultLimit;
        var cursor = args.TryGetProperty("cursor", out var cursorValue) ? cursorValue.GetString() : null;
        return Services(game, command.Instance, command.Initiator, string.IsNullOrEmpty(type) ? null : type, limit,
            string.IsNullOrEmpty(cursor) ? null : cursor);
    }

    private static JsonElement Services(GameNetworkService game, string instance, string initiator, string? type, int limit, string? cursor)
    {
        var services = game.Services;
        long cursorRevision = 0;
        string? after = null;
        if (cursor is not null)
        {
            // "<revision>:<asker>:<last instance>": a cursor belongs to its asker and to one directory revision.
            var parts = cursor.Split(':');
            if (parts.Length != 3 || parts[1] != initiator || !ServiceNames.IsValid(parts[2]) ||
                !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out cursorRevision))
                return Page("invalid_cursor", 0, [], null);
            after = parts[2];
        }
        var slice = services.QueryDirectory(initiator, type, after, ScanBudget);
        if (slice is null || services.ProviderNode(instance) is not { } origin)
            return Page("empty", slice?.Revision ?? 0, [], null);
        if (cursor is not null && cursorRevision != slice.Revision)
            return Page("snapshot_expired", slice.Revision, [], null);

        var targets = slice.Entries.Select(entry => entry.Node).Where(node => node != origin).ToHashSet();
        var reached = targets.Count == 0 ? [] : SharedMediumNetwork.ReachAll(game.Snapshot, [origin], targets, ReachBudget);
        var items = new List<string>(limit);
        string? last = null;
        var examined = 0;
        foreach (var entry in slice.Entries)
        {
            if (items.Count >= limit)
                break;
            examined++;
            last = entry.Instance;
            int hops;
            if (entry.Node == origin)
                hops = 0;
            else if (reached.TryGetValue(entry.Node, out var reach))
                hops = reach.Hops;
            else
                continue; // Not reachable from here now: for this query it does not exist.
            items.Add($"{entry.Instance}|{entry.ServiceType}|{hops}");
        }
        var more = examined < slice.Entries.Count || slice.More;
        return more
            ? Page("partial", slice.Revision, items, $"{slice.Revision.ToString(CultureInfo.InvariantCulture)}:{initiator}:{last}")
            : Page(items.Count == 0 && cursor is null ? "empty" : "complete", slice.Revision, items, null);
    }

    private static JsonElement Page(string status, long revision, IReadOnlyList<string> items, string? cursor) =>
        cursor is null
            ? JsonSerializer.SerializeToElement(new { status, revision, count = items.Count, items = string.Join(",", items) })
            : JsonSerializer.SerializeToElement(new { status, revision, count = items.Count, items = string.Join(",", items), cursor });
}
