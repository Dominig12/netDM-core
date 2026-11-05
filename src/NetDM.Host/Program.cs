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
builder.WebHost.UseUrls("http://127.0.0.1:5087");
builder.Services.AddSingleton<NetworkRuntime>();
builder.Services.AddSingleton(_ => new PriorityWorkScheduler(new WorkSchedulerOptions(
    Workers: Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
    QueueCapacity: 256,
    BudgetMillisecondsPer100Ms: 65)));
builder.Services.AddSingleton<BenchmarkRunner>();
builder.Services.AddSingleton<ThreeScenarioBenchmarkSuite>();
builder.Services.AddSingleton<RoutingComparisonRunner>();
builder.Services.AddHostedService<PipeHostedService>();

var app = builder.Build();
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
        : Results.File(zip, "application/zip", "netdm-three-scenarios.zip");
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

app.MapGet("/api/scheduler", (PriorityWorkScheduler scheduler) =>
    Results.Ok(scheduler.Capture()));

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", protocol = 1 }));

app.Run();
