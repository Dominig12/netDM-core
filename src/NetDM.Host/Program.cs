using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using NetDM.Core;

var pipeName = args.Length > 0 ? args[0] : "netdm-core";
var store = new TopologyStore();
var mutations = System.Threading.Channels.Channel.CreateBounded<Command>(
    new System.Threading.Channels.BoundedChannelOptions(8192)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait
    });
using var cancellation = new CancellationTokenSource();

var mutationWorker = Task.Run(async () =>
{
    await foreach (var item in mutations.Reader.ReadAllAsync(cancellation.Token))
    {
        try { item.Reply.TrySetResult(ApplyMutation(item.Request)); }
        catch (Exception exception) { item.Reply.TrySetResult(new Response(item.Request.Id, false, exception.Message)); }
    }
});

Console.Error.WriteLine($"netDM-core listening on local pipe '{pipeName}'");
try
{
    while (!cancellation.IsCancellationRequested)
    {
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 32,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        try { await server.WaitForConnectionAsync(cancellation.Token); }
        catch { await server.DisposeAsync(); throw; }
        _ = Task.Run(async () =>
        {
            await using (server) { await ServeAsync(server); }
        });
    }
}
finally
{
    mutations.Writer.TryComplete();
    cancellation.Cancel();
    try { await mutationWorker; } catch (OperationCanceledException) { }
}

Response ApplyMutation(Request request)
{
    var changed = request.Op switch
    {
        "add_node" => store.AddNode(new NodeId(request.A)),
        "remove_node" => store.RemoveNode(new NodeId(request.A)),
        "connect" => store.Connect(new NodeId(request.A), new NodeId(request.B), request.Cost),
        "disconnect" => store.Disconnect(new NodeId(request.A), new NodeId(request.B)),
        _ => throw new ArgumentException("Unknown mutation.")
    };
    var snapshot = store.Snapshot;
    return new Response(request.Id, true, null, snapshot.Version, changed, snapshot.Nodes.Count);
}

async Task ServeAsync(Stream stream)
{
    using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
    await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
    { AutoFlush = true };
    using var writeGate = new SemaphoreSlim(1, 1);
    using var routes = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount), Math.Max(1, Environment.ProcessorCount));
    var pending = new ConcurrentBag<Task>();

    async Task SendAsync(Response result)
    {
        var line = JsonSerializer.Serialize(result, JsonOptions);
        await writeGate.WaitAsync();
        try { await writer.WriteLineAsync(line); }
        finally { writeGate.Release(); }
    }

    while (await reader.ReadLineAsync() is { } line)
    {
        if (line.Length > 64 * 1024)
        {
            await SendAsync(new Response(null, false, "Request too large."));
            continue;
        }

        Request? request;
        try { request = JsonSerializer.Deserialize<Request>(line, JsonOptions); }
        catch (JsonException ex)
        {
            await SendAsync(new Response(null, false, $"Invalid JSON: {ex.Message}"));
            continue;
        }
        if (request is null) { await SendAsync(new Response(null, false, "Empty request.")); continue; }

        if (request.Op == "route")
        {
            // Bound route concurrency; a topology snapshot is captured before scheduling.
            await routes.WaitAsync();
            var snapshot = store.Snapshot;
            var task = Task.Run(async () =>
            {
                try
                {
                    var result = Router.Find(snapshot, new NodeId(request.A), new NodeId(request.B));
                    await SendAsync(new Response(request.Id, true, null, result.Version,
                        null, null, result.Found, result.Cost, result.Path.Select(x => x.Value).ToArray()));
                }
                catch (Exception ex) { await SendAsync(new Response(request.Id, false, ex.Message)); }
                finally { routes.Release(); }
            });
            pending.Add(task);
        }
        else if (request.Op == "stats")
        {
            var snapshot = store.Snapshot;
            await SendAsync(new Response(request.Id, true, null, snapshot.Version,
                null, snapshot.Nodes.Count));
        }
        else if (request.Op is "add_node" or "remove_node" or "connect" or "disconnect")
        {
            var reply = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
            await mutations.Writer.WriteAsync(new Command(request, reply));
            await SendAsync(await reply.Task);
        }
        else await SendAsync(new Response(request.Id, false, "Unknown operation."));
    }

    await Task.WhenAll(pending);
}

record Command(Request Request, TaskCompletionSource<Response> Reply);
record Request(string? Id, string Op, ulong A = 0, ulong B = 0, uint Cost = 1);
record Response(string? Id, bool Ok, string? Error = null, long? Version = null,
    bool? Changed = null, int? NodeCount = null, bool? Found = null, ulong? Cost = null,
    ulong[]? Path = null);

static partial class Placeholder { }
