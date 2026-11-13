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

    internal static JsonElement Answer(GameNetworkService game, GameNetworkService.Session session, HostCommand command)
    {
        using var arguments = JsonDocument.Parse(command.Arguments);
        var source = new Source(game, session, command);
        var sink = new Sink(session, command);
        var result = command.Command == "run_script"
            ? NetShell.RunScript(arguments.RootElement.GetProperty("source").GetString()!, source, Limits, sink)
            : NetShell.Run(arguments.RootElement.GetProperty("line").GetString()!, source, Limits, sink);
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
    private static string Format(IReadOnlyList<ShellRecord> records)
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

    private static string Printable(string text)
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

    private static ShellRecord Record(params (string Name, object Value)[] fields)
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
    private sealed class Sink(GameNetworkService.Session session, HostCommand command) : IShellSink
    {
        private readonly string _keyPrefix = $"{ActionKeyNamespace}.{command.DeliveryId.ToString(CultureInfo.InvariantCulture)}";
        private int _ordinal;

        public bool Knows(string name) => name == "invoke";

        public IReadOnlyList<ShellRecord>? Act(string name, IReadOnlyList<ShellRecord>? input, ShellArguments arguments, out string? error)
        {
            error = null;
            var expected = input is null ? 2 : 1;
            if (arguments.Positional.Count != expected || arguments.Positional.Any(word => word is not string text || !ServiceNames.IsValid(text)))
            {
                error = input is null ? "invoke <instance> <command> [name=value…]" : "… | invoke <command> [name=value…]";
                return null;
            }
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
                if (session.Services.QueryService(command.DeliveryId, instance, false) is not { } target)
                    status = "unknown_service";
                else if (target.Descriptor.ServiceType == ServiceType)
                    status = "forbidden";
                else
                {
                    var key = $"{_keyPrefix}.{(_ordinal++).ToString(CultureInfo.InvariantCulture)}";
                    var result = session.Services.Invoke(command.InitiatorKey, instance, verb, key, payload,
                        null, PacketTrafficClass.Interactive);
                    status = result.Status == ServiceStatus.Ok ? "sent" : ServiceNames.Code(result.Status);
                    operation = result.Operation?.OperationId.ToString(CultureInfo.InvariantCulture) ?? "";
                }
                done.Add(Record(("instance", instance), ("command", verb), ("status", status), ("operation", operation)));
            }
            return done;
        }
    }

    private sealed class Source(GameNetworkService game, GameNetworkService.Session session, HostCommand command) : IShellSource
    {
        private int _graphQueriesRemaining = 2;

        public IReadOnlyList<ShellRecord>? Read(string name, ShellArguments arguments, int maxRecords, out string? error)
        {
            error = null;
            var world = session.Medium.Snapshot;
            var origin = command.ProviderNode;
            switch (name)
            {
                case "services":
                    var type = arguments.Named.GetValueOrDefault("type") as string;
                    if (arguments.Positional.Count != 0 || arguments.Named.Count > (type is null ? 0 : 1) ||
                        (type is not null && !ServiceNames.IsValid(type)))
                        return Fail("services [-type <serviceType>]", out error);
                    if (_graphQueriesRemaining-- <= 0)
                        return Fail("network search budget exceeded (services and trace share two searches)", out error);
                    if (session.Services.QueryDirectory(command.DeliveryId, type, null, maxRecords) is not { } query)
                        return [];
                    if (query.Slice.More)
                        return Fail("too many services: narrow with -type", out error);
                    var targets = query.Slice.Entries.Select(entry => entry.Node).Where(node => node != origin).ToHashSet();
                    var reached = SharedMediumNetwork.ReachAll(world, origin, targets, QueryHost.ReachBudget, out var exhausted);
                    if (exhausted)
                        return Fail("the network is too large to search from here", out error);
                    return query.Slice.Entries
                        .Where(entry => entry.Node == origin || reached.ContainsKey(entry.Node))
                        .Select(entry => Record(("instance", entry.Instance), ("type", entry.ServiceType),
                            ("hops", (long)(entry.Node == origin ? 0 : reached[entry.Node].Hops)))).ToArray();
                case "links":
                    if (arguments.Positional.Count != 0 || arguments.Named.Count != 0)
                        return Fail("links takes no arguments", out error);
                    return NetworkDiagnostics.LinkState(world, game.Wiring, origin) is { } ports
                        ? ports.Neighbors.Select(link => Record(("node", link.Node.Value.ToString(CultureInfo.InvariantCulture)), ("state", link.State))).ToArray()
                        : [];
                case "self":
                    if (arguments.Positional.Count != 0 || arguments.Named.Count != 0)
                        return Fail("self takes no arguments", out error);
                    return NetworkDiagnostics.LinkState(world, game.Wiring, origin) is { } own
                        ? [Record(("node", origin.Value.ToString(CultureInfo.InvariantCulture)), ("state", own.State), ("wired", (long)own.Wired),
                            ("wiredUp", (long)own.WiredUp), ("radio", own.Radio), ("devices", (long)own.Devices))]
                        : [];
                case "trace":
                    if (arguments.Named.Count != 0 || arguments.Positional.Count != 1 || arguments.Positional[0] is not long target || target <= 0)
                        return Fail("trace <node>", out error);
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
