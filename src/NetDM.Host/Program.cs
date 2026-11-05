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
builder.Services.AddSingleton<BenchmarkRunner>();
builder.Services.AddHostedService<PipeHostedService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/metrics", (NetworkRuntime runtime, BenchmarkRunner benchmark) =>
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
        workingSetBytes = Environment.WorkingSet,
        benchmark = benchmark.State()
    });
});

app.MapGet("/api/history", (BenchmarkRunner benchmark) => Results.Ok(benchmark.History()));

app.MapPost("/api/benchmark/start", (BenchmarkOptions options, BenchmarkRunner benchmark) =>
{
    try
    {
        options.Validate();
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

// The map API returns only graph cells intersecting the requested viewport.
// This keeps serialized payloads bounded even when the simulation is much larger.
app.MapGet("/api/map", (NetworkRuntime runtime,
    int? minX, int? minY, int? maxX, int? maxY, int? z, int? limit) =>
{
    var snapshot = runtime.Topology.Snapshot;
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

    return Results.Ok(new
    {
        version = snapshot.Version,
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

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", protocol = 1 }));

app.Run();
