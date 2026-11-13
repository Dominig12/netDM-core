using System.Globalization;
using System.Text.Json;
using NetDM.Core;
using NetDM.Host.Transport;
using NetDM.Services;

namespace NetDM.Host.Diagnostics;

/// <summary>
/// network.diagnostics (§7.6, §14.1). A network device offers the service; the Host answers on its
/// behalf once the command reached the device's node over the simulated network, like the research
/// server's commands. The device reports its own ports and one bounded trace from itself. It holds no
/// state: every answer is read from the published world at the moment the command arrived.
/// </summary>
public static class DiagnosticsHost
{
    public const string ServiceType = "network.diagnostics";

    public static JsonElement Answer(GameNetworkService game, HostCommand command)
    {
        var world = game.Snapshot;
        var node = game.Services.ProviderNode(command.Instance);
        using var arguments = JsonDocument.Parse(command.Arguments);
        if (command.Command == "trace")
        {
            var text = arguments.RootElement.TryGetProperty("node", out var value) ? value.GetString() ?? "" : "";
            var trace = node is { } from && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var to) && to != 0
                ? NetworkDiagnostics.Trace(world, from, new NodeId(to))
                : new TraceReport(NetworkDiagnostics.Unknown, 0, 0, 0, 0);
            return JsonSerializer.SerializeToElement(new
            {
                node = text, state = trace.State, hops = trace.Hops, radioHops = trace.RadioHops,
                cost = (long)Math.Min(trace.Cost, 9_007_199_254_740_991UL), ticks = trace.Ticks
            });
        }
        // link_state. A provider whose node left the world between delivery and answer reports nothing live.
        return LinkState(node is { } self ? NetworkDiagnostics.LinkState(world, game.Wiring, self) : null);
    }

    private static JsonElement LinkState(LinkStateReport? report) =>
        JsonSerializer.SerializeToElement(new
        {
            state = report?.State ?? NetworkDiagnostics.Broken,
            wired = report?.Wired ?? 0,
            wiredUp = report?.WiredUp ?? 0,
            radio = report?.Radio ?? false,
            devices = report?.Devices ?? 0,
            neighbors = report is null ? "" : string.Join(",", report.Neighbors.Select(link => $"{link.Node.Value}:{link.State}")),
            more = report?.More ?? false
        });

    public const int MaxWatched = 64;
    /// <summary>Longest wait between a change of a watched device's ports and its publication.</summary>
    public static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// State watch (§7.6): a device with subscribers publishes its link state when it changes. The Host
    /// compares the state of each watched device with what it published last and sends only a difference,
    /// from the device's node over the network, so a subscriber the network does not reach learns nothing.
    /// A new subscriber of a device that is already watched asks <c>link_state</c> for the current state.
    /// </summary>
    public sealed class Watch
    {
        private readonly Dictionary<string, string> _published = new(StringComparer.Ordinal);
        private Guid _stream;
        private long _next;

        public long Publications { get; private set; }

        public int Publish(GameNetworkService game)
        {
            var now = Environment.TickCount64;
            if (now < _next)
                return 0;
            _next = now + (long)WatchInterval.TotalMilliseconds;
            if (_stream != game.StreamId)
            {
                _stream = game.StreamId; // A new runtime has no subscriptions and no sequences of the old one.
                _published.Clear();
            }
            var watched = game.Services.WatchedHostServices(ServiceType, MaxWatched);
            if (watched.Length == 0)
            {
                _published.Clear();
                return 0;
            }
            var world = game.Snapshot;
            var wiring = game.Wiring;
            var sent = 0;
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var service in watched)
            {
                live.Add(service.Instance);
                var state = LinkState(NetworkDiagnostics.LinkState(world, wiring, service.Node));
                var text = state.GetRawText();
                if (_published.TryGetValue(service.Instance, out var last) && last == text)
                    continue;
                var result = game.Publish(service.Owner, service.Instance, service.LastSequence + 1, state, null,
                    PacketTrafficClass.Background);
                if (result.Status is not (ServiceStatus.Ok or ServiceStatus.Duplicate))
                    continue; // Busy or over the fan-out: the difference stays, and the next pass tries again.
                _published[service.Instance] = text;
                Publications++;
                sent++;
            }
            foreach (var instance in _published.Keys.Where(instance => !live.Contains(instance)).ToArray())
                _published.Remove(instance);
            return sent;
        }
    }
}
