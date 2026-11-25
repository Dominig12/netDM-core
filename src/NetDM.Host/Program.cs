using NetDM.Core;
using NetDM.Host.Benchmarks;
using NetDM.Host.Transport;

var workingDirectory = Directory.GetCurrentDirectory();
var projectDirectory = Directory.Exists(Path.Combine(workingDirectory, "wwwroot"))
    ? workingDirectory
    : Path.Combine(workingDirectory, "src", "NetDM.Host");
var contentRoot = Directory.Exists(Path.Combine(projectDirectory, "wwwroot"))
    ? projectDirectory : AppContext.BaseDirectory;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRoot
});
// Deliberately localhost-only; this development observability endpoint has no authentication.
var httpPort = builder.Configuration.GetValue("NetDM:HttpPort", 5087);
var readinessPort = builder.Configuration.GetValue<int?>("NetDM:ReadinessPort");
if (readinessPort is { } port)
{
    if (port is < 1 or > 65535 || port == httpPort)
        throw new ArgumentException("ReadinessPort must be distinct from HttpPort.");
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Listen(System.Net.IPAddress.Loopback, httpPort);
        options.ListenAnyIP(port);
    });
}
else
{
    builder.WebHost.UseUrls($"http://127.0.0.1:{httpPort}");
}
var deployment = builder.Configuration.GetValue("NetDM:Deployment", false);
NetDM.Host.Diagnostics.ShellPrograms.StateBudget = Math.Clamp(
    builder.Configuration.GetValue("NetDM:ProgramStateBytes", NetDM.Host.Diagnostics.ShellPrograms.MaxStateBytes), 256, 65_536);
builder.Services.AddSingleton<NetworkRuntime>();
builder.Services.AddSingleton(_ => new PriorityWorkScheduler(new WorkSchedulerOptions(
    Workers: Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
    QueueCapacity: 256,
    BudgetMillisecondsPer100Ms: 65)));
builder.Services.AddSingleton<BenchmarkRunner>();
builder.Services.AddSingleton<ThreeScenarioBenchmarkSuite>();
builder.Services.AddSingleton<RoutingComparisonRunner>();
// Modules are registered once at composition root, not in a central IPC switch.
builder.Services.AddSingleton<IHostIpcModule, ResearchIpcModule>();
builder.Services.AddSingleton<IHostIpcModule, NetworkLifeIpcModule>();
builder.Services.AddSingleton<IHostIpcModule, NetRunIpcModule>();
builder.Services.AddSingleton<HostIpcModuleRouter>();
// Every registered feature also adds its service type to the Host delivery queue.
builder.Services.AddSingleton<IHostServiceExtension, DiagnosticsHostService>();
builder.Services.AddSingleton<IHostServiceExtension, QueryHostService>();
builder.Services.AddSingleton<IHostServiceExtension, ShellHostService>();
builder.Services.AddSingleton<GameNetworkService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<GameNetworkService>());
// NETRUN owns the digital state of the round and ticks on the same quantum clock as the transport.
builder.Services.AddSingleton<NetRunHostService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<NetRunHostService>());
builder.Services.AddSingleton<NetDM.Host.Research.ResearchHost>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<NetDM.Host.Research.ResearchHost>());
builder.Services.AddSingleton<PipeHostedService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<PipeHostedService>());

var app = builder.Build();
if (readinessPort.HasValue || deployment)
{
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        if (context.Connection.LocalPort == readinessPort &&
            (!HttpMethods.IsGet(context.Request.Method) ||
             (path != "/api/readiness/ipc" && path != "/api/readiness/game" && path != "/api/readiness/network")))
        {
            context.Response.StatusCode = 404;
            return;
        }
        if (deployment && (path.StartsWithSegments("/api/benchmark") ||
            path.StartsWithSegments("/api/benchmark-suite") || path.StartsWithSegments("/api/comparison")))
        {
            context.Response.StatusCode = 404;
            return;
        }
        await next(context);
    });
}
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/metrics", (NetworkRuntime runtime, BenchmarkRunner benchmark,
    PriorityWorkScheduler scheduler) =>
{
    var snapshot = runtime.Topology.Snapshot;
    return Results.Ok(new
    {
        at = DateTimeOffset.UtcNow,
        version = snapshot.Version,
        connectivityVersion = snapshot.ConnectivityVersion,
        nodeCount = snapshot.Nodes.Count,
        edgeCount = snapshot.Nodes.Values.Sum(n => n.Links.Count) / 2,
        runtime = runtime.Metrics.Capture(runtime.Cache.Count),
        scheduler = scheduler.Capture(),
        workingSetBytes = Environment.WorkingSet,
        benchmark = benchmark.State()
    });
});

app.MapGet("/api/history", (BenchmarkRunner benchmark) => Results.Ok(benchmark.History()));
app.MapGet("/api/packets", (BenchmarkRunner benchmark) =>
    Results.Ok(benchmark.PacketStatistics));

app.MapPost("/api/benchmark/start", (BenchmarkOptions options, BenchmarkRunner benchmark, RoutingComparisonRunner comparison) =>
{
    try
    {
        options.Validate();
        if (comparison.State().Status is "running" or "stopping")
            return Results.Conflict(new { error = "Algorithm comparison in progress." });
        return benchmark.Start(options)
            ? Results.Accepted("/api/metrics", benchmark.State())
            : Results.Conflict(new { error = "A benchmark is already running." });
    }
    catch (ArgumentOutOfRangeException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/benchmark/stop", (BenchmarkRunner benchmark) =>
    Results.Ok(new { stopping = benchmark.Stop() }));



app.MapGet("/api/benchmark-suite", (ThreeScenarioBenchmarkSuite suite) =>
    Results.Ok(suite.State()));

app.MapPost("/api/benchmark-suite/start", (ThreeScenarioSuiteOptions options,
    ThreeScenarioBenchmarkSuite suite, RoutingComparisonRunner comparison) =>
{
    try
    {
        options.Validate();
        if (comparison.State().Status is "running" or "stopping")
            return Results.Conflict(new { error = "Algorithm comparison is running." });
        return suite.Start(options)
            ? Results.Accepted("/api/benchmark-suite", suite.State())
            : Results.Conflict(new { error = "A benchmark or suite is already running." });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/benchmark-suite/stop", (ThreeScenarioBenchmarkSuite suite) =>
    Results.Ok(new { stopping = suite.Stop() }));

app.MapGet("/api/benchmark-suite/download/{index:int}",
    (int index, string? runId, ThreeScenarioBenchmarkSuite suite) =>
{
    var report = suite.Report(index, runId);
    return report is null
        ? Results.NotFound(new { error = "Report is not ready or run id is invalid." })
        : Results.File(report.Value.Data, "application/json", report.Value.Name);
});

app.MapGet("/api/benchmark-suite/download-all",
    (string? runId, ThreeScenarioBenchmarkSuite suite) =>
{
    var zip = suite.Archive(runId);
    return zip is null
        ? Results.NotFound(new { error = "Three completed reports are required." })
        : Results.File(zip.Value.Data, "application/zip", zip.Value.Name);
});

app.MapGet("/api/comparison", (RoutingComparisonRunner comparison) =>
    Results.Ok(comparison.State()));

app.MapPost("/api/comparison/start", (ComparisonOptions options,
    RoutingComparisonRunner comparison, BenchmarkRunner benchmark,
    ThreeScenarioBenchmarkSuite suite) =>
{
    try
    {
        options.Validate();
        if (benchmark.State().Status is "running" or "building" or "stopping" ||
            suite.IsActive)
            return Results.Conflict(new { error = "Live workload or three-scenario suite is running." });
        return comparison.Start(options)
            ? Results.Accepted("/api/comparison", comparison.State())
            : Results.Conflict(new { error = "Algorithm comparison already in progress." });
    }
    catch (ArgumentOutOfRangeException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/comparison/stop", (RoutingComparisonRunner comparison) =>
    Results.Ok(new { stopping = comparison.Stop() }));

// The map API returns only graph cells intersecting the requested viewport.
// This keeps serialized payloads bounded even when the simulation is much larger.
app.MapGet("/api/map", (NetworkRuntime runtime, BenchmarkRunner benchmark,
    int? minX, int? minY, int? maxX, int? maxY, int? z, int? limit) =>
{
    var medium = benchmark.ActiveSharedMedium;
    var mediumSnapshot = medium?.Snapshot;
    var snapshot = mediumSnapshot?.Physical ?? runtime.Topology.Snapshot;
    var maxNodes = Math.Clamp(limit ?? 4000, 1, 8000);
    var left = minX ?? -100;
    var top = minY ?? -100;
    var right = maxX ?? 1000;
    var bottom = maxY ?? 1000;
    if (left > right || top > bottom)
        return Results.BadRequest(new { error = "Invalid viewport bounds." });

    var nodes = TopologyStore.QueryViewport(snapshot,
        left, top, right, bottom, z ?? 0, maxNodes);
    var ids = nodes.Select(node => node.Id).ToHashSet();
    var edges = nodes.SelectMany(node => node.Links.Values
        .Where(edge => node.Id.Value < edge.To.Value && ids.Contains(edge.To))
        .Select(edge => new { a = node.Id.ToString(), b = edge.To.ToString(), cost = edge.Cost }))
        .Take(maxNodes * 8)
        .ToArray();

    var mediumCells = medium?.ViewCells(left, top, right, bottom, z ?? 0,
        1_500, mediumSnapshot);
    return Results.Ok(new
    {
        version = snapshot.Version,
        mediumVersion = mediumSnapshot?.Version,
        domainCells = mediumCells,
        totalNodes = snapshot.Nodes.Count,
        displayedNodes = nodes.Count,
        truncated = nodes.Count >= maxNodes,
        nodes = nodes.Select(node => new
        {
            id = node.Id.ToString(),
            x = node.Position!.Value.X,
            y = node.Position!.Value.Y,
            z = node.Position!.Value.Z,
            links = node.Links.Count,
            traffic = runtime.Traffic(node.Id)
        }).ToArray(),
        edges
    });
});

// BYOND gameplay positions and radio cells are never mixed with benchmarks.
app.MapGet("/api/game/summary", (GameNetworkService game) =>
    Results.Ok(game.TopologySummary()));

// Transport, tick runner and service counters of the live game runtime, for diagnostics.
app.MapGet("/api/game/status", (GameNetworkService game) => Results.Ok(game.Status()));

// The round's research ledger: storages, wallets, conservation totals and journal recovery.
app.MapGet("/api/research/status", (NetDM.Host.Research.ResearchHost research) => Results.Ok(research.Status()));
// P10 load runs: background stage timings and program counters start over (loopback only, like the rest).
app.MapPost("/api/research/background/reset", (NetDM.Host.Research.ResearchHost research) =>
{
    research.ResetBackgroundTimers();
    return Results.Accepted();
});

// outer=true also lists edges to nodes outside the viewport, such as an APC link to another level.
app.MapGet("/api/game/map", (GameNetworkService game,
    int? minX, int? minY, int? maxX, int? maxY, int? z, int? limit, bool? outer) =>
{
    var (streamId, snapshot) = game.CaptureTopology();
    var physical = snapshot.Physical;
    var left = minX ?? -100;
    var top = minY ?? -100;
    var right = maxX ?? 1000;
    var bottom = maxY ?? 1000;
    if (left > right || top > bottom)
        return Results.BadRequest(new { error = "Invalid viewport bounds." });
    var maxNodes = Math.Clamp(limit ?? 4_000, 1, 8_000);
    var layer = z ?? 1;
    var nodes = TopologyStore.QueryViewport(physical,
        left, top, right, bottom, layer, maxNodes);
    var ids = nodes.Select(node => node.Id).ToHashSet();
    var edges = nodes.SelectMany(node => node.Links.Values
        .Where(edge => outer == true ? node.Id.Value < edge.To.Value || !ids.Contains(edge.To) : node.Id.Value < edge.To.Value && ids.Contains(edge.To))
        .Select(edge => new { a = node.Id.ToString(), b = edge.To.ToString(), cost = edge.Cost }))
        .Take(maxNodes * 8).ToArray();
    return Results.Ok(new
    {
        source = "game",
        streamId,
        version = snapshot.Version,
        connectivityVersion = physical.ConnectivityVersion,
        domainCells = game.ViewCells(left, top, right, bottom, layer, snapshot),
        totalNodes = physical.Nodes.Count,
        displayedNodes = nodes.Count,
        truncated = nodes.Count >= maxNodes,
        nodes = nodes.Select(node =>
        {
            var hasRadio = snapshot.Devices.TryGetValue(node.Id, out var profile);
            return new
            {
                id = node.Id.ToString(),
                x = node.Position!.Value.X,
                y = node.Position!.Value.Y,
                z = node.Position!.Value.Z,
                links = node.Links.Count,
                traffic = 0,
                radioEnabled = hasRadio,
                radioRange = hasRadio ? profile.Range : 0,
                radioChannel = hasRadio ? profile.Channel : 0
            };
        }).ToArray(),
        edges
    });
});

app.MapGet("/api/scheduler", (PriorityWorkScheduler scheduler) =>
    Results.Ok(scheduler.Capture()));

// Report PID only over the localhost development observability endpoint.
app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok", protocol = 1, processId = Environment.ProcessId
}));

app.MapGet("/api/readiness/ipc", (PipeHostedService ipc) =>
{
    var ready = ipc.IpcReady;
    return Results.Json(new { ready }, statusCode: ready ? 200 : 503);
});
// The ability to serve the game: the simulated network and durable research storage (§17.4).
app.MapGet("/api/readiness/game", (PipeHostedService ipc) =>
{
    var state = ipc.GameReadiness();
    return Results.Json(state, statusCode: state.Ready ? 200 : 503);
});
// The network session alone, as admission sees it. A supervisor that would stop the world watches this
// one: research storage that cannot be written blocks research, not the round.
app.MapGet("/api/readiness/network", (PipeHostedService ipc) =>
{
    var state = ipc.GameReadiness();
    return Results.Json(state, statusCode: state.NetworkReady ? 200 : 503);
});

app.Run();
