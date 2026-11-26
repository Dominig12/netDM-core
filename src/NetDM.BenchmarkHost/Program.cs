using NetDM.Benchmarks;
using NetDM.Core;

// The developer tool of ADR-0008: synthetic benchmarks over NetDM.Core on their own port.
// It is not a game host — no IPC, no bridge, no round state; the game keeps its own HTTP surface.
var builder = WebApplication.CreateBuilder(args);
var httpPort = builder.Configuration.GetValue("NetDM:Benchmarks:HttpPort", 5080);
builder.WebHost.UseUrls($"http://127.0.0.1:{httpPort}");
builder.Services.AddSingleton<NetworkRuntime>();
builder.Services.AddSingleton(_ => new PriorityWorkScheduler(new WorkSchedulerOptions(
    Workers: Math.Clamp(Environment.ProcessorCount / 2, 1, 8), QueueCapacity: 256, BudgetMillisecondsPer100Ms: 65)));
builder.Services.AddSingleton<BenchmarkRunner>();
builder.Services.AddSingleton<ThreeScenarioBenchmarkSuite>();
builder.Services.AddSingleton<RoutingComparisonRunner>();
var app = builder.Build();

app.MapGet("/api/benchmark", (BenchmarkRunner benchmark) => Results.Ok(benchmark.State()));

app.MapPost("/api/benchmark/start", (BenchmarkOptions options, BenchmarkRunner benchmark, RoutingComparisonRunner comparison) =>
{
    try
    {
        options.Validate();
        if (comparison.State().Status is "running" or "stopping")
            return Results.Conflict(new { error = "Algorithm comparison in progress." });
        return benchmark.Start(options)
            ? Results.Accepted("/api/benchmark", benchmark.State())
            : Results.Conflict(new { error = "A benchmark is already running." });
    }
    catch (ArgumentOutOfRangeException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/benchmark/stop", (BenchmarkRunner benchmark) =>
    Results.Ok(new { stopping = benchmark.Stop() }));

app.MapGet("/api/history", (BenchmarkRunner benchmark) => Results.Ok(benchmark.History()));
app.MapGet("/api/packets", (BenchmarkRunner benchmark) => Results.Ok(benchmark.PacketStatistics));

app.MapGet("/api/benchmark-suite", (ThreeScenarioBenchmarkSuite suite) => Results.Ok(suite.State()));

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

app.MapGet("/api/comparison", (RoutingComparisonRunner comparison) => Results.Ok(comparison.State()));

app.MapPost("/api/comparison/start", (ComparisonOptions options,
    RoutingComparisonRunner comparison, BenchmarkRunner benchmark, ThreeScenarioBenchmarkSuite suite) =>
{
    try
    {
        options.Validate();
        if (benchmark.State().Status is "running" or "building" or "stopping" || suite.IsActive)
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

app.MapGet("/api/metrics", (BenchmarkRunner benchmark) => Results.Ok(new
{
    at = DateTimeOffset.UtcNow,
    benchmark = benchmark.State(),
    workingSetBytes = Environment.WorkingSet
}));

Console.WriteLine($"BENCHMARK_READY http://127.0.0.1:{httpPort}");
app.Run();
