using System.IO.Compression;
using System.Text.Json;
using NetDM.Core;

namespace NetDM.Host.Benchmarks;

/// <summary>
/// A reproducible comparison using identical seed, network size and offered
/// workload for three controlled movement/outage configurations.
/// Short duration/size overrides are supported for automated HTTP tests.
/// </summary>
public sealed record ThreeScenarioSuiteOptions(
    int Nodes = 12_000,
    int RoutesPerSecond = 1_500,
    int MovesPerSecond = 1_000,
    int LinkChangesPerSecond = 30,
    int DurationSeconds = 120,
    int Seed = 1337,
    int RadioRange = 14,
    int MediumBytesPerTick = 8192,
    string SharedSearch = "dijkstra",
    bool PacketTransportEnabled = false,
    bool SpatialReuseEnabled = true,
    int PacketMaxAgeTicks = 900,
    int PacketMaxAdvancesPerTick = 1024,
    int PacketRerouteWorkers = 1,
    int PacketRerouteQueueCapacity = 32,
    int PacketRerouteCpuBudgetMs = 25,
    bool DedicatedPacketTicks = true,
    int PacketTickWorkBudgetMs = 25,
    int PacketMaxNoRouteRetryDelayTicks = 96,
    int PacketRerouteQueueRetryDelayTicks = 8)
{
    public void Validate()
    {
        if (RoutesPerSecond == 0)
            throw new ArgumentException("Route request rate must be positive.");
        var workload = Build(MovesPerSecond, LinkChangesPerSecond);
        workload.Validate();
    }

    public BenchmarkOptions Build(int moves, int outages) =>
        new(Nodes: Nodes, RoutesPerSecond: RoutesPerSecond,
            MovesPerSecond: moves, LinkChangesPerSecond: outages,
            DurationSeconds: DurationSeconds, Seed: Seed,
            WirelessEnabled: true, RadioRange: RadioRange,
            RadioModel: "shared", MediumBytesPerTick: MediumBytesPerTick,
            SharedSearch: SharedSearch,
            PacketTransportEnabled: PacketTransportEnabled,
            SpatialReuseEnabled: SpatialReuseEnabled,
            PacketMaxAgeTicks: PacketMaxAgeTicks,
            PacketMaxAdvancesPerTick: PacketMaxAdvancesPerTick,
            PacketRerouteWorkers: PacketRerouteWorkers,
            PacketRerouteQueueCapacity: PacketRerouteQueueCapacity,
            PacketRerouteCpuBudgetMs: PacketRerouteCpuBudgetMs,
            DedicatedPacketTicks: DedicatedPacketTicks,
            PacketTickWorkBudgetMs: PacketTickWorkBudgetMs,
            PacketMaxNoRouteRetryDelayTicks: PacketMaxNoRouteRetryDelayTicks,
            PacketRerouteQueueRetryDelayTicks: PacketRerouteQueueRetryDelayTicks);
}

public sealed record ScenarioResultSummary(
    int Number, string Key, string Title, string FileName, int Bytes);

public sealed record ThreeScenarioSuiteState(
    string Status, string? RunId, int CurrentScenario,
    string? CurrentTitle, int Completed, int Total,
    ThreeScenarioSuiteOptions? Options, string? Error,
    IReadOnlyList<ScenarioResultSummary> Reports);

/// <summary>
/// A host-owned sequential job. It continues when the browser is closed,
/// and retains up to three JSON reports in memory until the next run/restart.
/// </summary>
public sealed class ThreeScenarioBenchmarkSuite(
    BenchmarkRunner benchmark,
    RoutingComparisonRunner comparisons,
    NetworkRuntime runtime,
    PriorityWorkScheduler scheduler,
    ILogger<ThreeScenarioBenchmarkSuite> logger)
{
    private static readonly (string Key, string Title, int Moves, int Outages)[] Cases =
    [
        ("static", "Static radio network", 0, 0),
        ("movement", "Radio network with movement", 1, 0),
        ("movement-outages", "Movement with radio outages", 1, 1)
    ];

    private readonly object _gate = new();
    private readonly Dictionary<int, (string Name, byte[] Data)> _reports = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private string _status = "idle", _runId = "";
    private string? _currentTitle, _error;
    private ThreeScenarioSuiteOptions? _options;
    private int _currentScenario, _completed;

    public bool IsActive
    {
        get { lock (_gate) return _status is "running" or "stopping"; }
    }

    public ThreeScenarioSuiteState State()
    {
        lock (_gate)
        {
            var reports = _reports.OrderBy(p => p.Key).Select(p =>
            {
                var scenario = Cases[p.Key - 1];
                return new ScenarioResultSummary(p.Key, scenario.Key,
                    scenario.Title, p.Value.Name, p.Value.Data.Length);
            }).ToArray();
            return new ThreeScenarioSuiteState(_status,
                string.IsNullOrEmpty(_runId) ? null : _runId,
                _currentScenario, _currentTitle, _completed, Cases.Length,
                _options, _error, reports);
        }
    }

    public bool Start(ThreeScenarioSuiteOptions options)
    {
        options.Validate();
        lock (_gate)
        {
            if (_task is { IsCompleted: false } ||
                _status is "running" or "stopping" ||
                comparisons.State().Status is "running" or "stopping" ||
                !benchmark.TryReserveSuite())
                return false;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _reports.Clear();
            _runId = Guid.NewGuid().ToString("N");
            _status = "running";
            _currentScenario = _completed = 0;
            _currentTitle = _error = null;
            _options = options;
            var id = _runId;
            var token = _cts.Token;
            _task = Task.Run(() => RunAsync(options, id, token));
            return true;
        }
    }

    public bool Stop()
    {
        lock (_gate)
        {
            if (_status != "running" || _cts is null) return false;
            _status = "stopping";
            _cts.Cancel();
        }
        benchmark.Stop(fromSuite: true);
        return true;
    }

    public (string Name, byte[] Data)? Report(int index, string? runId)
    {
        lock (_gate)
        {
            if (runId != _runId || index is < 1 or > 3 ||
                !_reports.TryGetValue(index, out var report))
                return null;
            return report;
        }
    }

    public byte[]? Archive(string? runId)
    {
        (string Name, byte[] Data)[] reports;
        lock (_gate)
        {
            if (runId != _runId || _reports.Count != 3 || _status != "complete")
                return null;
            reports = Enumerable.Range(1, 3).Select(i => _reports[i]).ToArray();
        }
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var report in reports)
            {
                var entry = archive.CreateEntry(report.Name, CompressionLevel.Optimal);
                using var writer = entry.Open();
                writer.Write(report.Data);
            }
        }
        return buffer.ToArray();
    }

    private async Task RunAsync(ThreeScenarioSuiteOptions options, string runId,
        CancellationToken cancellationToken)
    {
        try
        {
            for (var i = 0; i < Cases.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scenario = Cases[i];
                var number = i + 1;
                var moves = scenario.Moves == 0 ? 0 : options.MovesPerSecond;
                var outages = scenario.Outages == 0 ? 0 : options.LinkChangesPerSecond;
                lock (_gate)
                {
                    _currentScenario = number;
                    _currentTitle = scenario.Title;
                }

                var workload = options.Build(moves, outages);
                var schedulerBefore = scheduler.Capture();
                if (!benchmark.Start(workload, fromSuite: true))
                    throw new InvalidOperationException("Failed to start suite scenario " + number);
                await benchmark.AwaitCompletionAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var state = benchmark.State();
                if (state.Status != "complete")
                    throw new InvalidOperationException(
                        "Scenario " + number + " ended as " + state.Status +
                        (state.Error is null ? "" : ": " + state.Error));

                var snapshot = runtime.Topology.Snapshot;
                var schedulerAfter = scheduler.Capture();
                var exportedAt = DateTimeOffset.UtcNow;
                // Keep the same top-level envelope as the manual Observatory
                // JSON export: exportedAt, metrics, history, topology.
                var report = new
                {
                    exportedAt,
                    suite = new
                    {
                        runId, scenario = scenario.Key, number, title = scenario.Title,
                        parameters = options, movesPerSecond = moves,
                        linkChangesPerSecond = outages,
                        generator = "open-loop",
                        schemaVersion = 1,
                        completed = true,
                        schedulerDelta = new
                        {
                            accepted = schedulerAfter.Accepted - schedulerBefore.Accepted,
                            rejected = schedulerAfter.Rejected - schedulerBefore.Rejected,
                            completed = schedulerAfter.Completed - schedulerBefore.Completed,
                            cancelled = schedulerAfter.Cancelled - schedulerBefore.Cancelled,
                            failed = schedulerAfter.Failed - schedulerBefore.Failed
                        }
                    },
                    metrics = new
                    {
                        at = exportedAt,
                        version = snapshot.Version,
                        connectivityVersion = snapshot.ConnectivityVersion,
                        nodeCount = snapshot.Nodes.Count,
                        edgeCount = snapshot.Nodes.Values.Sum(n => n.Links.Count) / 2,
                        runtime = runtime.Metrics.Capture(runtime.Cache.Count),
                        scheduler = scheduler.Capture(),
                        workingSetBytes = Environment.WorkingSet,
                        benchmark = state
                    },
                    history = benchmark.History(),
                    topology = new
                    {
                        version = snapshot.Version,
                        totalNodes = snapshot.Nodes.Count,
                        edgeCount = snapshot.Nodes.Values.Sum(n => n.Links.Count) / 2,
                        radioModel = "shared",
                        fullViewportOmitted = true
                    }
                };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(report,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        WriteIndented = true
                    });
                var fileName = $"netdm-{number:00}-{scenario.Key}-{options.Nodes}-seed{options.Seed}.json";
                lock (_gate)
                {
                    _reports[number] = (fileName, bytes);
                    _completed = number;
                }
            }
            lock (_gate)
            {
                _status = "complete";
                _currentTitle = null;
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                _status = "stopped";
                _currentTitle = null;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Three-scenario benchmark suite failed");
            lock (_gate)
            {
                _status = "failed";
                _error = ex.Message;
                _currentTitle = null;
            }
        }
        finally
        {
            // Never release exclusive benchmark ownership while a previous
            // workload can still mutate/reset the shared runtime.
            try
            {
                if (benchmark.State().Status is "building" or "running" or "stopping")
                {
                    benchmark.Stop(fromSuite: true);
                    await benchmark.AwaitCompletionAsync();
                }
            }
            finally
            {
                benchmark.ReleaseSuite();
            }
        }
    }
}
