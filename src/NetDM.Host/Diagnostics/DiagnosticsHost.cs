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
    /// <param name="serviceType">The host-provided type whose watched instances are published.</param>
    /// <param name="state">The state of one watched instance now, read from the published world.</param>
    /// <param name="maxWatched">Instances served at once; the rest wait in instance order.</param>
    /// <param name="index">
    /// What the states are read from (§14.5): while it, the watched devices and their nodes stay the same and
    /// every difference was published, a pass reads no state at all. <see cref="RefreshEvery"/> reads them
    /// anyway now and then, for what no index covers (access granted or taken back).
    /// </param>
    /// <summary>A change index of watched states: a version, a revision and an immutable report, compared by reference.</summary>
    public readonly record struct WatchIndex(long Version, long Revision, object? Report)
    {
        public bool Same(WatchIndex other) => Version == other.Version && Revision == other.Revision && ReferenceEquals(Report, other.Report);
    }

    public sealed class Watch(string serviceType, Func<GameNetworkService, WatchedService, JsonElement> state, int maxWatched,
        Func<GameNetworkService, WatchIndex> index)
    {
        /// <summary>The watch of network.diagnostics: each watched device's own ports, read from the world and its wiring.</summary>
        public static Watch Links() => new(ServiceType,
            (game, service) => LinkState(NetworkDiagnostics.LinkState(game.Snapshot, game.Wiring, service.Node)), MaxWatched,
            game => new WatchIndex(game.Snapshot.Version, 0, game.Wiring));

        public static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(30);

        private readonly Dictionary<string, string> _published = new(StringComparer.Ordinal);
        private Guid _stream;
        private long _next, _refreshAt;
        private WatchIndex? _seen;
        private string? _seenWatched;
        private bool _behind;

        /// <summary>Due passes that read every watched state, and those that read none because nothing changed.</summary>
        public long Reads { get; private set; }
        public long Skipped { get; private set; }

        public long Publications { get; private set; }
        /// <summary>Whether the last call was a due pass that read the watched states.</summary>
        public bool Due { get; private set; }
        public int LastWatched { get; private set; }

        public int Publish(GameNetworkService game)
        {
            var now = Environment.TickCount64;
            Due = now >= _next;
            if (!Due)
                return 0;
            _next = now + (long)WatchInterval.TotalMilliseconds;
            if (_stream != game.StreamId)
            {
                _stream = game.StreamId; // A new runtime has no subscriptions and no sequences of the old one.
                _published.Clear();
            }
            var watched = game.Services.WatchedHostServices(serviceType, maxWatched);
            LastWatched = watched.Length;
            if (watched.Length == 0)
            {
                _published.Clear();
                _seen = null;
                return 0;
            }
            var current = index(game);
            var watchedKey = string.Join(";", watched.Select(service => $"{service.Instance}|{service.Owner}|{service.Node.Value}"));
            if (!_behind && _seen is { } seen && seen.Same(current) && watchedKey == _seenWatched && now < _refreshAt)
            {
                Skipped++;
                Due = false; // Nothing was read: the pass costs no state.
                return 0;
            }
            _seen = current;
            _seenWatched = watchedKey;
            _refreshAt = now + (long)RefreshEvery.TotalMilliseconds;
            _behind = false;
            Reads++;
            var sent = 0;
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var service in watched)
            {
                live.Add(service.Instance);
                var value = state(game, service);
                var text = value.GetRawText();
                if (_published.TryGetValue(service.Instance, out var last) && last == text)
                    continue;
                var result = game.Publish(service.Owner, service.Instance, service.LastSequence + 1, value, null,
                    PacketTrafficClass.Background);
                if (result.Status is not (ServiceStatus.Ok or ServiceStatus.Duplicate))
                {
                    _behind = true; // Busy or over the fan-out: the difference stays, and the next pass reads again.
                    continue;
                }
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
