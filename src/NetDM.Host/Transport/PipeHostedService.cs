using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using NetDM.Core;

namespace NetDM.Host.Transport;

/// <summary>
/// Bootstrap x86-shim target. All graph mutations and routing happen in the x64 host.
/// Routing has global admission control, so unbounded client requests cannot create
/// unbounded routing tasks. One connection never writes concurrently to its stream.
/// </summary>
public sealed class PipeHostedService(NetworkRuntime runtime, ILogger<PipeHostedService> log)
    : BackgroundService
{
    private readonly SemaphoreSlim _connectionSlots = new(16, 16);
    private readonly SemaphoreSlim _routeSlots = new(
        Math.Clamp(Environment.ProcessorCount, 1, 8), Math.Clamp(Environment.ProcessorCount, 1, 8));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxMessageChars = 64 * 1024;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _connectionSlots.WaitAsync(stoppingToken);
            var pipe = new NamedPipeServerStream(
                "netdm-core", PipeDirection.InOut, 16,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try { await pipe.WaitForConnectionAsync(stoppingToken); }
            catch
            {
                await pipe.DisposeAsync();
                _connectionSlots.Release();
                throw;
            }
            _ = Task.Run(async () =>
            {
                try { await ServeAsync(pipe, stoppingToken); }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    log.LogWarning(ex, "Pipe client disconnected");
                }
                finally
                {
                    await pipe.DisposeAsync();
                    _connectionSlots.Release();
                }
            }, CancellationToken.None);
        }
    }

    private async Task ServeAsync(Stream stream, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
            { AutoFlush = true };
        using var writeGate = new SemaphoreSlim(1, 1);
        var tasks = new List<Task>(16);

        async Task SendAsync(PipeResponse result)
        {
            var json = JsonSerializer.Serialize(result, JsonOptions);
            await writeGate.WaitAsync(token);
            try { await writer.WriteLineAsync(json); }
            finally { writeGate.Release(); }
        }

        while (!token.IsCancellationRequested && await reader.ReadLineAsync(token) is { } line)
        {
            if (line.Length > MaxMessageChars)
            {
                await SendAsync(new PipeResponse(null, false, "Request exceeds 64 KiB."));
                continue;
            }

            PipeRequest? request;
            try { request = JsonSerializer.Deserialize<PipeRequest>(line, JsonOptions); }
            catch (JsonException ex)
            {
                await SendAsync(new PipeResponse(null, false, $"Invalid JSON: {ex.Message}"));
                continue;
            }
            if (request is null || string.IsNullOrWhiteSpace(request.Op))
            {
                await SendAsync(new PipeResponse(request?.Id, false, "Missing operation."));
                continue;
            }

            if (request.Op == "route")
            {
                if (!await _routeSlots.WaitAsync(0, token))
                {
                    await SendAsync(new PipeResponse(request.Id, false, "busy"));
                    continue;
                }
                // Route tasks are admitted globally; only a small bounded set
                // of completed/in-flight tasks lives in each connection.
                tasks.RemoveAll(t => t.IsCompleted);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var route = runtime.Route(new NodeId(request.A), new NodeId(request.B), cancellationToken: token);
                        await SendAsync(new PipeResponse(
                            request.Id, true, Version: route.Version, Found: route.Found,
                            Cost: route.Cost, Path: route.Path.Select(n => n.Value).ToArray()));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await SendAsync(new PipeResponse(request.Id, false, ex.Message));
                    }
                    finally { _routeSlots.Release(); }
                }, CancellationToken.None));
                continue;
            }

            try
            {
                var result = request.Op switch
                {
                    "stats" => Stats(request),
                    "batch" => ApplyBatch(request),
                    "add_node" or "remove_node" or "connect" or "disconnect" or "move_node" =>
                        ApplyBatch(request with { Changes = [ToChange(request)] }),
                    _ => new PipeResponse(request.Id, false, "Unknown operation.")
                };
                await SendAsync(result);
            }
            catch (Exception ex) when (ex is ArgumentException or OverflowException)
            {
                await SendAsync(new PipeResponse(request.Id, false, ex.Message));
            }
        }

        await Task.WhenAll(tasks);
    }

    private PipeResponse Stats(PipeRequest request)
    {
        var snapshot = runtime.Topology.Snapshot;
        return new PipeResponse(request.Id, true, Version: snapshot.Version,
            ConnectivityVersion: snapshot.ConnectivityVersion, NodeCount: snapshot.Nodes.Count);
    }

    private PipeResponse ApplyBatch(PipeRequest request)
    {
        if (request.Changes is null || request.Changes.Length is < 1 or > 4096)
            throw new ArgumentException("Batch size must be 1..4096.");
        var changes = request.Changes.Select(ToChange).ToArray();
        var applied = runtime.Apply(changes);
        return new PipeResponse(request.Id, true, Version: applied.Version,
            Applied: applied.Applied, Skipped: applied.Skipped);
    }

    private static TopologyChange ToChange(PipeRequest command)
    {
        var operation = command.Op switch
        {
            "add_node" => TopologyOperation.AddNode,
            "remove_node" => TopologyOperation.RemoveNode,
            "connect" => TopologyOperation.Connect,
            "disconnect" => TopologyOperation.Disconnect,
            "move_node" => TopologyOperation.MoveNode,
            _ => throw new ArgumentException("Unknown topology operation.")
        };
        return new TopologyChange(operation, new NodeId(command.A),
            new NodeId(command.B), command.Cost,
            command.X.HasValue && command.Y.HasValue
                ? new NodePosition(command.X.Value, command.Y.Value, command.Z)
                : null);
    }

    private sealed record PipeRequest(
        string? Id, string Op, ulong A = 0, ulong B = 0, uint Cost = 1,
        int? X = null, int? Y = null, int Z = 0, PipeRequest[]? Changes = null);

    private sealed record PipeResponse(
        string? Id, bool Ok, string? Error = null, long? Version = null,
        long? ConnectivityVersion = null, int? NodeCount = null,
        int? Applied = null, int? Skipped = null,
        bool? Found = null, ulong? Cost = null, ulong[]? Path = null);
}
