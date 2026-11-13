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

    /// <summary>Devices whose directory is watched at once: each costs one reachability search per pass.</summary>
    public const int MaxWatched = 16;
    /// <summary>Directory entries one watched device examines per pass; beyond them the state is marked partial.</summary>
    public const int WatchScanBudget = 256;

    /// <summary>
    /// WatchQuery (§14.5): a device with subscribers publishes when the directory it reaches changes: the
    /// revision it was read at, how many services it reaches and a digest of their names. A watcher asks
    /// <c>services</c> again when the digest moves; the publication carries no service names. The view is
    /// the device's own (what its endpoint may discover), and the default subscribe policy admits only its
    /// domain, so a watcher learns of nothing it could not list itself.
    /// </summary>
    public static DiagnosticsHost.Watch DirectoryWatch() => new(ServiceType, DirectoryState, MaxWatched);

    private static JsonElement DirectoryState(GameNetworkService game, WatchedService service)
    {
        var slice = game.Services.QueryDirectory(service.Owner.Id, null, null, WatchScanBudget);
        if (slice is null)
            return JsonSerializer.SerializeToElement(new { services = 0, digest = "", partial = false });
        var targets = slice.Entries.Select(entry => entry.Node).Where(node => node != service.Node).ToHashSet();
        var reached = targets.Count == 0 ? [] : SharedMediumNetwork.ReachAll(game.Snapshot, [service.Node], targets, ReachBudget);
        var names = slice.Entries.Where(entry => entry.Node == service.Node || reached.ContainsKey(entry.Node))
            .Select(entry => entry.Instance + "|" + entry.ServiceType).ToArray();
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join(",", names)));
        // The revision is not part of the state: a service that came and left between two passes, or one
        // out of reach, changes nothing a query from here would return.
        return JsonSerializer.SerializeToElement(new { services = names.Length, digest = Convert.ToHexString(hash, 0, 8), partial = slice.More });
    }

    private static JsonElement Page(string status, long revision, IReadOnlyList<string> items, string? cursor) =>
        cursor is null
            ? JsonSerializer.SerializeToElement(new { status, revision, count = items.Count, items = string.Join(",", items) })
            : JsonSerializer.SerializeToElement(new { status, revision, count = items.Count, items = string.Join(",", items), cursor });
}
