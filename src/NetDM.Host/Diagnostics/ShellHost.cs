using System.Globalization;
using System.Text;
using System.Text.Json;
using NetDM.Core;
using NetDM.Host.Transport;
using NetDM.Services;
using NetDM.Services.Shell;

namespace NetDM.Host.Diagnostics;

/// <summary>
/// network.shell (§14.6, P11): a device runs a pipeline or script. The directory is read through the
/// asker's delivery, so visibility is the asker's and reachability is the device's.
/// </summary>
public static class ShellHost
{
    public const string ServiceType = "network.shell";
    public const int MaxRows = 64, MaxOutputChars = 4096;
    private static readonly ShellLimits Limits = new(MaxRecords: MaxRows);
    private static readonly string ActionKeyNamespace = $"sh.{Guid.NewGuid():N}";

    /// <summary>
    /// Whose line it is and where it runs. A line that arrived as a command reads through its delivery; a
    /// program that outlived its request reads as its owner's endpoint, which must still be alive.
    /// </summary>
    /// <param name="Publisher">The device's own endpoint when the device runs its own line or program: only then
    /// may <c>publish</c> speak for the device's shell service. Null for a line another endpoint asked for.</param>
    internal sealed record Scope(EndpointKey Initiator, string Instance, NodeId Origin, long? DeliveryId, string KeyPrefix,
        EndpointKey? Publisher = null);

    internal static JsonElement Answer(GameNetworkService game, GameNetworkService.Session session, HostCommand command, out Action? applied)
    {
        applied = null;
        using var arguments = JsonDocument.Parse(command.Arguments);
        if (command.Command is "scan" or "knowledge" or "enter" or "detach" or "leave" or "presence" or "act")
            return NetworkSpace.Answer(game, session, command, arguments.RootElement);
        if (command.Command is "start" or "stop" or "migrate")
            return ShellPrograms.Answer(session, command, arguments.RootElement, out applied);
        var scope = new Scope(command.InitiatorKey, command.Instance, command.ProviderNode, command.DeliveryId,
            $"{ActionKeyNamespace}.{command.DeliveryId.ToString(CultureInfo.InvariantCulture)}",
            command.InitiatorKey == command.ProviderKey ? command.ProviderKey : null);
        var source = new Source(game, session, scope);
        var sink = new Sink(session, scope);
        var result = command.Command == "run_script"
            ? NetShell.RunScript(arguments.RootElement.GetProperty("source").GetString()!, source, Limits, sink)
            : NetShell.Run(arguments.RootElement.GetProperty("line").GetString()!, source, Limits, sink);
        return Output(result);
    }

    /// <summary>A line's result as <c>shell.output</c>.</summary>
    internal static JsonElement Output(ShellResult result)
    {
        var rows = result.Ok ? Format(result.Records) : "";
        if (rows.Length > MaxOutputChars)
            result = new ShellResult(ShellStatus.LimitExceeded, [], result.FuelUsed, "output too long: select fewer fields or records");
        var status = ServiceNames.Code(result.Status);
        return result.Ok
            ? JsonSerializer.SerializeToElement(new { status, count = result.Records.Count, fuel = result.FuelUsed, rows })
            : JsonSerializer.SerializeToElement(new
            {
                status, count = 0, fuel = result.FuelUsed, rows = "", error = Printable(result.Error ?? status), position = result.Position
            });
    }

    /// <summary>Records as <c>name=value,name=value;…</c>; a value never holds the separators.</summary>
    internal static readonly ShellLimits ProgramLimits = Limits;

    internal static string Format(IReadOnlyList<ShellRecord> records)
    {
        var text = new StringBuilder();
        foreach (var record in records)
        {
            if (text.Length > 0)
                text.Append(';');
            var first = true;
            foreach (var (name, value) in record.Fields)
            {
                if (!first)
                    text.Append(',');
                first = false;
                text.Append(name).Append('=').Append(Printable(value switch
                {
                    bool flag => flag ? "true" : "false",
                    IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                    _ => value.ToString() ?? ""
                }));
            }
        }
        return text.ToString();
    }

    internal static string Printable(string text)
    {
        var clean = new StringBuilder(Math.Min(text.Length, 200));
        foreach (var c in text)
        {
            if (clean.Length >= 200)
                break;
            clean.Append(char.IsControl(c) || c is ',' or ';' or '=' ? ' ' : c);
        }
        return clean.ToString();
    }

    internal static ShellRecord Record(params (string Name, object Value)[] fields)
    {
        var record = new Dictionary<string, object>(fields.Length, StringComparer.Ordinal);
        foreach (var (name, value) in fields)
            record[name] = value;
        return new ShellRecord(record);
    }

    /// <summary>
    /// <c>invoke</c>: the one command that acts. It sends an ordinary service command as the asker, from the
    /// asker's node, under the asker's rights and quotas; the device that runs the line lends it nothing.
    /// The line does not wait: each command answers with its operation, and the result returns to the asker
    /// over the network like the result of any command. Alone: <c>invoke instance command name=value…</c>;
    /// after a pipeline: <c>… | invoke command name=value…</c> for the <c>instance</c> of each record.
    /// </summary>
    internal sealed class Sink(GameNetworkService.Session session, Scope command) : IShellSink
    {
        private readonly string _keyPrefix = command.KeyPrefix;
        private int _ordinal;
        public List<ShellRecord> Results { get; } = [];

        public bool Knows(string name) => name is "invoke" or "publish";

        internal static string? Validate(string name, ShellArguments arguments, bool piped)
        {
            if (name == "publish")
                return piped && arguments.Positional.Count + arguments.Named.Count == 0 ? null : "… | publish";
            return arguments.Positional.Count == (piped ? 1 : 2) &&
                arguments.Positional.All(word => word is string text && ServiceNames.IsValid(text)) ? null :
                piped ? "… | invoke <command> [name=value…]" : "invoke <instance> <command> [name=value…]";
        }

        public IReadOnlyList<ShellRecord>? Act(string name, IReadOnlyList<ShellRecord>? input, ShellArguments arguments, out string? error)
        {
            error = Validate(name, arguments, input is not null);
            if (error is not null)
                return null;
            if (name == "publish")
                return Publish(input!, out error);
            var targets = input is null
                ? [(string)arguments.Positional[0]]
                : input.Select(record => record["instance"] as string).ToArray();
            if (targets.Any(target => !ServiceNames.IsValid(target)))
            {
                error = "every record needs an instance to invoke";
                return null;
            }
            var verb = (string)arguments.Positional[^1];
            var payload = JsonSerializer.SerializeToElement(arguments.Named);
            var done = new List<ShellRecord>(targets.Length);
            for (var i = 0; i < targets.Length; i++)
            {
                var instance = targets[i]!;
                string status, operation = "";
                // Visibility is the asker's; a line may not start further lines.
                var descriptor = command.DeliveryId is { } delivery
                    ? session.Services.QueryService(delivery, instance, false)?.Descriptor
                    : session.Services.Describe(command.Initiator, instance);
                if (descriptor is null)
                    status = "unknown_service";
                else if (descriptor.ServiceType == ServiceType)
                    status = "forbidden";
                else
                {
                    var key = $"{_keyPrefix}.{(_ordinal++).ToString(CultureInfo.InvariantCulture)}";
                    var result = session.Services.Invoke(command.Initiator, instance, verb, key, payload,
                        null, PacketTrafficClass.Interactive);
                    status = result.Status == ServiceStatus.Ok ? "sent" : ServiceNames.Code(result.Status);
                    operation = result.Operation?.OperationId.ToString(CultureInfo.InvariantCulture) ?? "";
                }
                done.Add(Record(("instance", instance), ("command", verb), ("status", status), ("operation", operation)));
            }
            Results.AddRange(done);
            return done;
        }

        /// <summary>
        /// <c>… | publish</c>: the records become the next publication of the device's shell service
        /// (<c>shell.output</c>), delivered to its subscribers like any service's. Only the device itself
        /// publishes for itself; a line another endpoint asked for is refused.
        /// </summary>
        private IReadOnlyList<ShellRecord>? Publish(IReadOnlyList<ShellRecord> input, out string? error)
        {
            error = null;
            var rows = Format(input);
            if (rows.Length > MaxOutputChars)
            {
                error = "publication too long: select fewer fields or records";
                return null;
            }
            string status;
            long recipients = 0;
            if (command.Publisher is not { } publisher)
                status = "forbidden";
            else
            {
                var sequence = (session.Services.LastSequence(publisher, command.Instance) ?? 0) + 1;
                var payload = JsonSerializer.SerializeToElement(new { status = "ok", count = input.Count, fuel = 0, rows });
                var published = session.Services.Publish(publisher, command.Instance, sequence, payload, null, PacketTrafficClass.Background);
                status = published.Status == ServiceStatus.Ok ? "published" : ServiceNames.Code(published.Status);
                recipients = published.Recipients;
            }
            ShellRecord[] done = [Record(("instance", command.Instance), ("command", "publish"), ("status", status), ("operation", ""),
                ("recipients", recipients))];
            Results.AddRange(done);
            return done;
        }
    }

    internal sealed class Source(GameNetworkService game, GameNetworkService.Session session, Scope command) : IShellSource
    {
        private int _graphQueriesRemaining = 2;
        /// <summary>Sources a line has read: a rule is evaluated again only when one of them changed.</summary>
        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);

        internal static string? Validate(string name, ShellArguments args, bool variable = false)
        {
            if (variable)
                return args.Positional.Count + args.Named.Count == 0 ? null : "a variable takes no arguments";
            if (name == "services")
                return args.Positional.Count == 0 && args.Named.All(pair => pair switch
                {
                    { Key: "type" or "prefix", Value: string text } => ServiceNames.IsValid(text),
                    { Key: "hops", Value: long hops } => hops is >= 0 and <= QueryHost.MaxHopsFilter,
                    _ => false
                }) ? null : "services [-type <serviceType>] [-prefix <instance prefix>] [-hops <0-64>]";
            if (name == "trace")
                return args.Named.Count == 0 && args.Positional.Count == 1 && args.Positional[0] is long and > 0 ? null : "trace <node>";
            if (name == "route")
                return args.Named.Count == 0 && args.Positional.Count == 1 && args.Positional[0] is string instance &&
                    ServiceNames.IsValid(instance) ? null : "route <instance>";
            if (name is "programs" or "links" or "self" or "jobs")
                return args.Positional.Count + args.Named.Count == 0 ? null : $"{name} takes no arguments";
            return "unknown command";
        }

        public IReadOnlyList<ShellRecord>? Read(string name, ShellArguments arguments, int maxRecords, out string? error)
        {
            error = Validate(name, arguments);
            if (error is not null)
            {
                if (error == "unknown command")
                    error = null;
                return null;
            }
            Names.Add(name);
            var world = session.Medium.Snapshot;
            var origin = command.Origin;
            switch (name)
            {
                case "programs":
                    return ShellPrograms.List(session, command);
                case "jobs":
                    return QueryHost.Jobs(session.Services, command.Initiator, maxRecords, command.DeliveryId is null ? null : command.Instance)
                        .Select(job => Record(("operation", job.Operation), ("instance", job.Instance), ("command", job.Command),
                            ("outcome", job.Outcome), ("result", job.Result), ("reason", job.Reason))).ToArray();
                case "route":
                    var routed = (string)arguments.Positional[0];
                    if (_graphQueriesRemaining-- <= 0)
                        return Fail("network search budget exceeded (services, trace and route share two searches)", out error);
                    var service = command.DeliveryId is { } routeDelivery
                        ? session.Services.QueryService(routeDelivery, routed, false)?.Target
                        : session.Services.DiscoverableNode(command.Initiator, routed);
                    if (service is not { } serviceNode)
                        return [];
                    var path = NetworkDiagnostics.Route(world, origin, serviceNode, QueryHost.ReachBudget);
                    if (path.State != NetworkDiagnostics.Reachable)
                        return [Record(("hop", 0L), ("node", ""), ("link", path.State))];
                    return path.Nodes.Select((hop, index) => Record(("hop", (long)index), ("node", hop.Node.Value.ToString(CultureInfo.InvariantCulture)),
                        ("link", index == 0 ? "start" : hop.Radio ? "radio" : "cable"))).ToArray();
                case "services":
                    var type = arguments.Named.GetValueOrDefault("type") as string;
                    var prefix = arguments.Named.GetValueOrDefault("prefix") as string;
                    var maxHops = arguments.Named.GetValueOrDefault("hops") is long hopLimit ? hopLimit : long.MaxValue;
                    if (_graphQueriesRemaining-- <= 0)
                        return Fail("network search budget exceeded (services and trace share two searches)", out error);
                    var slice = command.DeliveryId is { } delivery
                        ? session.Services.QueryDirectory(delivery, type, null, maxRecords, prefix)?.Slice
                        : session.Services.QueryDirectory(command.Initiator, type, null, maxRecords, prefix);
                    if (slice is null)
                        return [];
                    var query = (Slice: slice, Origin: origin);
                    if (query.Slice.More)
                        return Fail("too many services: narrow with -type or -prefix", out error);
                    var targets = query.Slice.Entries.Select(entry => entry.Node).Where(node => node != origin).ToHashSet();
                    var reached = SharedMediumNetwork.ReachAll(world, origin, targets, QueryHost.ReachBudget, out var exhausted);
                    if (exhausted)
                        return Fail("the network is too large to search from here", out error);
                    return query.Slice.Entries
                        .Where(entry => entry.Node == origin || (reached.TryGetValue(entry.Node, out var near) && near.Hops <= maxHops))
                        .Select(entry => Record(("instance", entry.Instance), ("type", entry.ServiceType),
                            ("hops", (long)(entry.Node == origin ? 0 : reached[entry.Node].Hops)))).ToArray();
                case "links":
                    return NetworkDiagnostics.LinkState(world, game.Wiring, origin) is { } ports
                        ? ports.Neighbors.Select(link => Record(("node", link.Node.Value.ToString(CultureInfo.InvariantCulture)), ("state", link.State))).ToArray()
                        : [];
                case "self":
                    return NetworkDiagnostics.LinkState(world, game.Wiring, origin) is { } own
                        ? [Record(("node", origin.Value.ToString(CultureInfo.InvariantCulture)), ("state", own.State), ("wired", (long)own.Wired),
                            ("wiredUp", (long)own.WiredUp), ("radio", own.Radio), ("devices", (long)own.Devices))]
                        : [];
                case "trace":
                    var target = (long)arguments.Positional[0];
                    if (_graphQueriesRemaining-- <= 0)
                        return Fail("network search budget exceeded (services and trace share two searches)", out error);
                    var trace = NetworkDiagnostics.Trace(world, origin, new NodeId((ulong)target), QueryHost.ReachBudget);
                    return [Record(("node", target.ToString(CultureInfo.InvariantCulture)), ("state", trace.State), ("hops", (long)trace.Hops),
                        ("radioHops", (long)trace.RadioHops), ("ticks", (long)trace.Ticks))];
                default:
                    return null;
            }
        }

        private static IReadOnlyList<ShellRecord>? Fail(string message, out string? error)
        {
            error = message;
            return null;
        }
    }
}
