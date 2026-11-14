using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    private const int CursorNonceBytes = 12, CursorTagBytes = 16, CursorHeaderBytes = 16;
    private const long CursorLifetimeMs = 60_000;
    private static readonly byte[] CursorKey = RandomNumberGenerator.GetBytes(32);

    internal static JsonElement Answer(GameNetworkService.Session session, HostCommand command)
    {
        using var arguments = JsonDocument.Parse(command.Arguments);
        var args = arguments.RootElement;
        if (command.Command is "schema" or "state")
            return Instance(session.Services, session.Medium.Snapshot, command, args.GetProperty("instance").GetString());
        if (command.Command == "route")
            return Route(session.Services, session.Medium.Snapshot, command, args.GetProperty("instance").GetString());
        if (command.Command == "jobs")
        {
            var jobs = Jobs(session.Services, command.InitiatorKey,
                args.TryGetProperty("limit", out var jobLimit) ? Math.Clamp(jobLimit.GetInt32(), 1, MaxLimit) : DefaultLimit, command.Instance);
            var text = string.Join(",", jobs.Select(job => string.Join("|", job.Operation, job.Instance, job.Command, job.Outcome, job.Result)));
            return JsonSerializer.SerializeToElement(new { status = jobs.Count == 0 ? "empty" : "complete", count = jobs.Count, items = text });
        }
        var type = args.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
        var limit = args.TryGetProperty("limit", out var limitValue) ? Math.Clamp(limitValue.GetInt32(), 1, MaxLimit) : DefaultLimit;
        var cursor = args.TryGetProperty("cursor", out var cursorValue) ? cursorValue.GetString() : null;
        return Services(session, command, string.IsNullOrEmpty(type) ? null : type, limit,
            string.IsNullOrEmpty(cursor) ? null : cursor);
    }

    /// <summary>One job of the asker: its operation, where it went and how it ended so far.</summary>
    internal sealed record Job(string Operation, string Instance, string Command, string Outcome, string Result, string Reason);

    /// <summary>
    /// The asker's latest operations, newest first. The request that is asking right now (a jobs query or a
    /// shell line on the asking instance, still pending) is left out.
    /// </summary>
    internal static IReadOnlyList<Job> Jobs(ServiceRuntime services, EndpointKey asker, int limit, string? askingInstance)
    {
        var recent = services.RecentOperations(asker, ServiceRuntime.RecentOperationsPerEndpoint) ?? [];
        return recent
            .Where(operation => !(operation.Instance == askingInstance && operation.Command is "jobs" or "run" or "run_script" &&
                operation.Outcome == CommandOutcome.Pending))
            .Take(limit)
            .Select(operation => new Job(operation.OperationId.ToString(CultureInfo.InvariantCulture), operation.Instance, operation.Command,
                ServiceNames.Code(operation.Outcome), ServiceNames.Code(operation.Result), operation.OutcomeReason ?? operation.ResultReason ?? ""))
            .ToArray();
    }

    /// <summary>Items of a route: "node:link" from the asking device, link start, cable or radio.</summary>
    internal static string Path(RouteReport route) => string.Join(",", route.Nodes.Select((hop, index) =>
        $"{hop.Node.Value.ToString(CultureInfo.InvariantCulture)}:{(index == 0 ? "start" : hop.Radio ? "radio" : "cable")}"));

    private static JsonElement Route(ServiceRuntime services, SharedMediumSnapshot world, HostCommand command, string? instance)
    {
        if (!ServiceNames.IsValid(instance))
            return JsonSerializer.SerializeToElement(new { status = "invalid_request" });
        if (services.QueryService(command.DeliveryId, instance!, false) is not { } target)
            return JsonSerializer.SerializeToElement(new { status = "empty" });
        var route = NetworkDiagnostics.Route(world, target.Origin, target.Target, ReachBudget);
        if (route.State != NetworkDiagnostics.Reachable)
            return JsonSerializer.SerializeToElement(new { status = "unreachable", instance });
        return JsonSerializer.SerializeToElement(new
        {
            status = route.Truncated ? "partial" : "complete", instance, hops = route.Hops,
            radioHops = route.Nodes.Count(hop => hop.Radio), path = Path(route)
        });
    }

    private static JsonElement Instance(ServiceRuntime services, SharedMediumSnapshot world, HostCommand command, string? instance)
    {
        if (!ServiceNames.IsValid(instance))
            return JsonSerializer.SerializeToElement(new { status = "invalid_request" });
        var publicationState = command.Command == "state";
        if (services.QueryService(command.DeliveryId, instance!, publicationState) is not { } target ||
            NetworkDiagnostics.Trace(world, target.Origin, target.Target, ReachBudget).State != NetworkDiagnostics.Reachable)
            return JsonSerializer.SerializeToElement(new { status = "empty" });

        if (publicationState)
            return JsonSerializer.SerializeToElement(new
            {
                status = "complete", instance,
                publication = target.Sequence == 0 ? "idle" : "published",
                sequence = target.Sequence.ToString(CultureInfo.InvariantCulture)
            });

        var descriptor = target.Descriptor;
        return JsonSerializer.SerializeToElement(new
        {
            status = "complete", instance = descriptor.Instance, serviceType = descriptor.ServiceType,
            schema = descriptor.Schema, major = descriptor.Major, minor = descriptor.Minor, commands = descriptor.Commands
        });
    }

    private static JsonElement Services(GameNetworkService.Session session, HostCommand command, string? type, int limit, string? cursor)
    {
        if (type is not null && !ServiceNames.IsValid(type))
            return Page(cursor is null ? "empty" : "invalid_cursor", 0, [], null);
        var scope = Encoding.UTF8.GetBytes(FormattableString.Invariant(
            $"{session.Packets.ReceiptStreamId:N}\n{command.Instance}\n{command.ProviderKey.Id}\n{command.ProviderKey.Generation}\n{command.ProviderNode.Value}\n{command.InitiatorKey.Id}\n{command.InitiatorKey.Generation}\n{type}\n{limit}"));
        long cursorRevision = 0;
        var expires = Environment.TickCount64 + CursorLifetimeMs;
        string? after = null;
        if (cursor is not null && !ReadCursor(cursor, scope, out cursorRevision, out expires, out after))
            return Page("invalid_cursor", 0, [], null);
        if (session.Services.QueryDirectory(command.DeliveryId, type, after, ScanBudget) is not { } query)
            return Page("empty", 0, [], null);
        var (origin, slice) = query;
        if (origin != command.ProviderNode || (cursor is not null &&
            (cursorRevision != slice.Revision || Environment.TickCount64 >= expires)))
            return Page("snapshot_expired", slice.Revision, [], null);

        var targets = slice.Entries.Select(entry => entry.Node).Where(node => node != origin).ToHashSet();
        var reached = SharedMediumNetwork.ReachAll(session.Medium.Snapshot, origin, targets, ReachBudget, out var exhausted);
        if (exhausted)
            return Page("budget_exhausted", slice.Revision, [], null);
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
            ? Page("partial", slice.Revision, items, WriteCursor(scope, slice.Revision, expires, last!))
            : Page(items.Count == 0 && cursor is null ? "empty" : "complete", slice.Revision, items, null);
    }

    private static string WriteCursor(byte[] scope, long revision, long expires, string after)
    {
        Span<byte> data = stackalloc byte[CursorHeaderBytes + after.Length];
        BinaryPrimitives.WriteInt64LittleEndian(data, revision);
        BinaryPrimitives.WriteInt64LittleEndian(data[8..], expires);
        Encoding.ASCII.GetBytes(after, data[CursorHeaderBytes..]);
        Span<byte> token = stackalloc byte[CursorNonceBytes + CursorTagBytes + data.Length];
        var nonce = token[..CursorNonceBytes];
        RandomNumberGenerator.Fill(nonce);
        using var cipher = new AesGcm(CursorKey, CursorTagBytes);
        cipher.Encrypt(nonce, data, token[(CursorNonceBytes + CursorTagBytes)..], token.Slice(CursorNonceBytes, CursorTagBytes), scope);
        return Convert.ToBase64String(token);
    }

    private static bool ReadCursor(string cursor, byte[] scope, out long revision, out long expires, out string? after)
    {
        revision = expires = 0;
        after = null;
        const int overhead = CursorNonceBytes + CursorTagBytes + CursorHeaderBytes;
        Span<byte> buffer = stackalloc byte[overhead + 64];
        if (!Convert.TryFromBase64String(cursor, buffer, out var length) || length <= overhead ||
            cursor != Convert.ToBase64String(buffer[..length]))
            return false;
        Span<byte> data = stackalloc byte[length - CursorNonceBytes - CursorTagBytes];
        try
        {
            using var cipher = new AesGcm(CursorKey, CursorTagBytes);
            cipher.Decrypt(buffer[..CursorNonceBytes], buffer[(CursorNonceBytes + CursorTagBytes)..length],
                buffer.Slice(CursorNonceBytes, CursorTagBytes), data, scope);
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }
        revision = BinaryPrimitives.ReadInt64LittleEndian(data);
        expires = BinaryPrimitives.ReadInt64LittleEndian(data[8..]);
        after = Encoding.ASCII.GetString(data[CursorHeaderBytes..]);
        return true;
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
        var slice = game.Services.QueryDirectory(service.Owner, null, null, WatchScanBudget);
        if (slice is null)
            return JsonSerializer.SerializeToElement(new { services = 0, digest = "", partial = false });
        var targets = slice.Entries.Select(entry => entry.Node).Where(node => node != service.Node).ToHashSet();
        var reached = SharedMediumNetwork.ReachAll(game.Snapshot, service.Node, targets, ReachBudget, out var exhausted);
        var names = slice.Entries.Where(entry => entry.Node == service.Node || reached.ContainsKey(entry.Node))
            .Select(entry => entry.Instance + "|" + entry.ServiceType).ToArray();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", names)));
        // The revision is not part of the state: a service that came and left between two passes, or one
        // out of reach, changes nothing a query from here would return.
        return JsonSerializer.SerializeToElement(new { services = names.Length, digest = Convert.ToHexString(hash, 0, 8), partial = slice.More || exhausted });
    }

    private static JsonElement Page(string status, long revision, IReadOnlyList<string> items, string? cursor) =>
        cursor is null
            ? JsonSerializer.SerializeToElement(new { status, revision, count = items.Count, items = string.Join(",", items) })
            : JsonSerializer.SerializeToElement(new { status, revision, count = items.Count, items = string.Join(",", items), cursor });
}
