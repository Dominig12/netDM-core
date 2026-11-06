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
public sealed class PipeHostedService(NetworkRuntime runtime, PriorityWorkScheduler scheduler,
    GameNetworkService game, ILogger<PipeHostedService> log)
    : BackgroundService
{
    private readonly SemaphoreSlim _connectionSlots = new(16, 16);
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
        using var disconnect = CancellationTokenSource.CreateLinkedTokenSource(token);
        var tasks = new List<Task>(64);

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

            if (request.Op == "packet_send")
            {
                tasks.RemoveAll(t => t.IsCompleted);
                if (tasks.Count >= 64)
                {
                    await SendAsync(new PipeResponse(request.Id, false, "busy"));
                    continue;
                }
                tasks.Add(SendPacketAsync(request));
                continue;
            }

            if (request.Op == "route")
            {
                // No per-request routing Task.Run: the process-wide scheduler owns
                // CPU admission and worker concurrency for both players and benchmarks.
                tasks.RemoveAll(t => t.IsCompleted);
                if (tasks.Count >= 64 ||
                    !scheduler.TrySchedule(
                        ct => runtime.Route(new NodeId(request.A), new NodeId(request.B),
                            cancellationToken: ct),
                        WorkPriority.Interactive, disconnect.Token,
                        out Task<RouteResult>? scheduled))
                {
                    await SendAsync(new PipeResponse(request.Id, false, "busy"));
                    continue;
                }

                tasks.Add(SendRouteAsync(scheduled!, request.Id));
                continue;
            }

            try
            {
                var result = request.Op switch
                {
                    "stats" => Stats(request),
                    "game_stats" => new PipeResponse(request.Id, true, Game: game.Status()),
                    "game_batch" => ApplyGameBatch(request),
                    "game_reset" => new PipeResponse(request.Id, true,
                        Applied: game.ResetTopology()),
                    "game_radio_batch" => UpdateGameRadioBatch(request),
                    "game_radio" => UpdateGameRadio(request),
                    "packet_receipts" => PollPackets(request),
                    "packet_cancel" => CancelPacket(request),
                    "game_add_node" or "game_remove_node" or "game_connect"
                        or "game_disconnect" or "game_move_node" =>
                        ApplyGameChanges(request.Id, [ToChange(request)]),
                    "batch" => ApplyBatch(request),
                    "add_node" or "remove_node" or "connect" or "disconnect" or "move_node" =>
                        ApplyChanges(request.Id, [ToChange(request)]),
                    _ => new PipeResponse(request.Id, false, "Unknown operation.")
                };
                await SendAsync(result);
            }
            catch (Exception ex) when (ex is ArgumentException or OverflowException)
            {
                await SendAsync(new PipeResponse(request.Id, false, ex.Message));
            }
        }

        disconnect.Cancel(); // Disconnect must cancel work still in the global queue.
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
        catch (IOException) { }

        async Task SendPacketAsync(PipeRequest request)
        {
            try
            {
                var submitted = await game.SendAsync(
                    request.RequestId ?? request.Id ?? "",
                    request.A, request.B, request.Bytes,
                    (PacketTrafficClass)request.TrafficClass,
                    request.TimeoutTicks, disconnect.Token);
                if (!disconnect.IsCancellationRequested)
                    await SendAsync(new PipeResponse(request.Id, true,
                        Submission: submitted));
            }
            catch (OperationCanceledException) when (disconnect.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not IOException)
            {
                if (!disconnect.IsCancellationRequested)
                {
                    try { await SendAsync(new PipeResponse(request.Id, false, ex.Message)); }
                    catch (IOException) { }
                }
            }
        }

        async Task SendRouteAsync(Task<RouteResult> scheduled, string? id)
        {
            try
            {
                var route = await scheduled;
                if (!disconnect.IsCancellationRequested)
                    await SendAsync(new PipeResponse(
                        id, true, Version: route.Version, Found: route.Found,
                        Cost: route.Cost, Path: route.Path.Select(n => n.Value).ToArray()));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not IOException)
            {
                if (!disconnect.IsCancellationRequested)
                {
                    try { await SendAsync(new PipeResponse(id, false, ex.Message)); }
                    catch (IOException) { } // Client closed its pipe.
                }
            }
        }
    }

    private PipeResponse Stats(PipeRequest request)
    {
        var snapshot = runtime.Topology.Snapshot;
        return new PipeResponse(request.Id, true, Version: snapshot.Version,
            ConnectivityVersion: snapshot.ConnectivityVersion, NodeCount: snapshot.Nodes.Count);
    }

    private PipeResponse ApplyGameBatch(PipeRequest request)
    {
        if (request.Changes is null || request.Changes.Length is < 1 or > 4096)
            throw new ArgumentException("Game batch size must be 1..4096.");
        return ApplyGameChanges(request.Id, request.Changes.Select(ToChange).ToArray());
    }

    private PipeResponse ApplyGameChanges(string? requestId,
        IReadOnlyList<TopologyChange> changes)
    {
        var result = game.ApplyTopology(changes);
        return new PipeResponse(requestId, true, Version: result.Version,
            Applied: result.Applied, Skipped: result.Skipped);
    }

    private PipeResponse UpdateGameRadio(PipeRequest request)
    {
        if (request.A == 0)
            throw new ArgumentException("Game radio node ID must be nonzero.");
        RadioProfile? profile = request.Enabled == false ? null :
            new RadioProfile(request.Range, request.Channel, request.RadioCost);
        var result = game.UpdateRadio([new RadioNodeUpdate(new NodeId(request.A),
            Profile: profile, ChangeProfile: true)]);
        return new PipeResponse(request.Id, true, Version: result.Version);
    }

    private PipeResponse UpdateGameRadioBatch(PipeRequest request)
    {
        if (request.Changes is null || request.Changes.Length is < 1 or > 256)
            throw new ArgumentException("Game radio batch size must be 1..256.");
        if (request.Changes.Any(change =>
            change.Op != "game_radio" || change.A == 0))
            throw new ArgumentException("Radio batch requires valid game_radio entries.");
        var updates = request.Changes.Select(change =>
            new RadioNodeUpdate(new NodeId(change.A),
                Profile: change.Enabled == false ? null :
                    new RadioProfile(change.Range, change.Channel, change.RadioCost),
                ChangeProfile: true)).ToArray();
        var result = game.UpdateRadio(updates);
        return new PipeResponse(request.Id, true, Version: result.Version);
    }

    private PipeResponse PollPackets(PipeRequest request)
    {
        var page = game.Receipts(request.AfterSequence, request.Limit);
        if (!string.IsNullOrWhiteSpace(request.StreamId) &&
            (!Guid.TryParse(request.StreamId, out var epoch) || epoch != page.StreamId))
            return new PipeResponse(request.Id, false, "stream_changed",
                StreamId: page.StreamId);
        return new PipeResponse(request.Id, true, ReceiptPage: page);
    }

    private PipeResponse CancelPacket(PipeRequest request)
    {
        if (!Guid.TryParse(request.StreamId, out var epoch) || request.PacketId < 1)
            throw new ArgumentException("packet_cancel requires streamId and packetId.");
        var cancelled = game.Cancel(epoch, request.PacketId);
        return new PipeResponse(request.Id, true, Cancelled: cancelled,
            StreamId: game.StreamId);
    }

    private PipeResponse ApplyBatch(PipeRequest request)
    {
        if (request.Changes is null || request.Changes.Length is < 1 or > 4096)
            throw new ArgumentException("Batch size must be 1..4096.");
        return ApplyChanges(request.Id, request.Changes.Select(ToChange).ToArray());
    }

    private PipeResponse ApplyChanges(string? requestId, IReadOnlyList<TopologyChange> changes)
    {
        var applied = runtime.Apply(changes);
        return new PipeResponse(requestId, true, Version: applied.Version,
            Applied: applied.Applied, Skipped: applied.Skipped);
    }

    private static TopologyChange ToChange(PipeRequest command)
    {
        var op = command.Op.StartsWith("game_", StringComparison.Ordinal)
            ? command.Op[5..] : command.Op;
        var operation = op switch
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
        int? X = null, int? Y = null, int Z = 0, PipeRequest[]? Changes = null,
        string? RequestId = null, int Bytes = 0, int TrafficClass = 0,
        int? TimeoutTicks = null, long PacketId = 0, string? StreamId = null,
        long AfterSequence = 0, int Limit = 128, bool? Enabled = null,
        int Range = 14, uint Channel = 1, uint RadioCost = 8);

    private sealed record PipeResponse(
        string? Id, bool Ok, string? Error = null, long? Version = null,
        long? ConnectivityVersion = null, int? NodeCount = null,
        int? Applied = null, int? Skipped = null,
        bool? Found = null, ulong? Cost = null, ulong[]? Path = null,
        PacketSubmissionResult? Submission = null,
        PacketReceiptPage? ReceiptPage = null,
        bool? Cancelled = null, Guid? StreamId = null, object? Game = null);
}
