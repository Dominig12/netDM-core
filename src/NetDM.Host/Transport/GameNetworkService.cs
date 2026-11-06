using System.Diagnostics;
using NetDM.Core;

namespace NetDM.Host.Transport;

/// <summary>
/// Production packet engine owned by the Host, separate from the synthetic
/// Observatory benchmark. Topology changes are applied via its own radio
/// medium to preserve ownership and one coherent routing snapshot.
/// </summary>
public sealed class GameNetworkService : IHostedService, IAsyncDisposable
{
    private readonly NetworkRuntime _runtime = new();
    private readonly SharedMediumNetwork _medium;
    private readonly RadioPacketTransport _packets;
    private readonly PacketTickRunner _runner;
    private readonly PriorityWorkScheduler _scheduler;
    private CancellationTokenSource? _lifetime;
    private Task? _tickTask, _rerouteTask;
    private readonly object _lifecycle = new();
    private const int QuantumMs = 100;
    private readonly long _quantumTicks = Math.Max(1, Stopwatch.Frequency / (1000 / QuantumMs));

    public GameNetworkService(PriorityWorkScheduler scheduler)
    {
        _scheduler = scheduler;
        _medium = new SharedMediumNetwork(_runtime, bytesPerTick: 8192,
            searchMode: SharedRadioSearchMode.AStarDense,
            airtimeScope: RadioAirtimeScope.SpatialCells);
        _packets = new RadioPacketTransport(_medium, new PacketTransportOptions(
            Capacity: 4096, MaxAgeTicks: 900, MaxAdvancesPerTick: 4096,
            MaxPendingReroutes: 64, MaxConcurrentReroutes: 2,
            AdaptivePacketVisits: true, EventDrivenPacketWaiting: true,
            EventDrivenRerouteCapacity: true,
            PrioritizeInteractiveTraffic: true, InteractiveReservedSlots: 128,
            PrioritizeInteractiveReroutes: true, InteractiveRerouteReservedSlots: 4,
            ReceiptHistoryCapacity: 8192));
        _runner = new PacketTickRunner(_packets, QuantumMs);
    }

    public long CurrentTick => Stopwatch.GetTimestamp() / _quantumTicks;
    public Guid StreamId => _packets.ReceiptStreamId;
    public SharedMediumSnapshot Snapshot => _medium.Snapshot;

    // Game topology is independent from the synthetic benchmark graph.
    public IReadOnlyList<RadioMediumCellView> ViewCells(int left, int top,
        int right, int bottom, int z, SharedMediumSnapshot snapshot) =>
        _medium.ViewCells(left, top, right, bottom, z, 1_500, snapshot);

    public object TopologySummary()
    {
        var snapshot = Snapshot;
        var physical = snapshot.Physical;
        var levels = physical.Nodes.Values.Where(n => n.Position.HasValue)
            .GroupBy(n => n.Position!.Value.Z).OrderBy(g => g.Key)
            .Select(g => new
            {
                z = g.Key,
                nodes = g.Count(),
                minX = g.Min(n => n.Position!.Value.X),
                minY = g.Min(n => n.Position!.Value.Y),
                maxX = g.Max(n => n.Position!.Value.X),
                maxY = g.Max(n => n.Position!.Value.Y)
            }).ToArray();
        return new
        {
            streamId = StreamId,
            version = snapshot.Version,
            connectivityVersion = physical.ConnectivityVersion,
            nodes = physical.Nodes.Count,
            links = physical.Nodes.Values.Sum(n => n.Links.Count) / 2,
            radios = snapshot.Devices.Count,
            occupiedCells = snapshot.Cells.Count,
            levels
        };
    }

    // One trusted SS13 process owns the Host's gameplay graph.
    public int ResetTopology()
    {
        var ids = Snapshot.Physical.Nodes.Keys.ToArray();
        if (ids.Length == 0) return 0;
        var removed = _medium.RemoveNodes(ids);
        _packets.NotifyRadioUpdates(ids.Select(id => new RadioNodeUpdate(id)).ToArray());
        return removed;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycle)
        {
            if (_lifetime is not null) throw new InvalidOperationException("Game engine already started.");
            _lifetime = new CancellationTokenSource();
            _tickTask = _runner.Start(_lifetime.Token);
            _rerouteTask = Task.Run(() => RunReroutesAsync(_lifetime.Token),
                CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? lifetime;
        Task? tick, reroutes;
        lock (_lifecycle)
        {
            lifetime = _lifetime;
            tick = _tickTask;
            reroutes = _rerouteTask;
        }
        if (lifetime is null) return;
        lifetime.Cancel();
        try
        {
            await Task.WhenAll(new[] { tick, reroutes }.OfType<Task>())
                .WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _lifetime?.Dispose();
    }

    public BatchResult ApplyTopology(IReadOnlyList<TopologyChange> changes)
    {
        var result = _medium.ApplyExternalTopology(changes);
        if (result.Applied > 0)
        {
            var affected = changes.SelectMany(c => new[] { c.A, c.B })
                .Where(id => id.Value != 0).Distinct()
                .Select(id => new RadioNodeUpdate(id)).ToArray();
            _packets.NotifyRadioUpdates(affected);
        }
        return result;
    }

    public SharedMediumUpdateResult UpdateRadio(IReadOnlyList<RadioNodeUpdate> updates)
    {
        var result = _medium.Update(updates);
        _packets.NotifyRadioUpdates(updates);
        return result;
    }

    public PacketReceiptPage Receipts(long afterSequence, int limit) =>
        _packets.CaptureReceipts(afterSequence, limit);

    public bool Cancel(Guid streamId, long packetId) =>
        _packets.TryCancel(streamId, packetId);

    public object Status() => new
    {
        streamId = StreamId,
        tick = CurrentTick,
        nodes = _medium.Snapshot.Physical.Nodes.Count,
        packet = _packets.Capture(),
        radio = _medium.Stats(),
        runner = _runner.Capture()
    };

    /// <summary>
    /// A route search is queued in the global bounded scheduler and is NEVER
    /// executed while holding the radio/packet lock or the named-pipe reader.
    /// A pre-route lease owns a real packet slot across that asynchronous work.
    /// </summary>
    public async Task<PacketSubmissionResult> SendAsync(string requestId,
        ulong from, ulong to, int bytes, PacketTrafficClass trafficClass,
        int? timeoutTicks, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128)
            throw new ArgumentException("Request ID must contain 1..128 characters.");
        if (from == 0 || to == 0 || bytes is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        if (!Enum.IsDefined(trafficClass))
            throw new ArgumentOutOfRangeException(nameof(trafficClass));
        if (timeoutTicks is < 1 or > 900)
            throw new ArgumentOutOfRangeException(nameof(timeoutTicks));
        using var lease = _packets.TryReserveAdmission(trafficClass, out var refusal);
        if (lease is null)
            return new PacketSubmissionResult(false, StreamId, 0, requestId, refusal);

        if (!_scheduler.TrySchedule(token =>
                _medium.Route(new NodeId(from), new NodeId(to),
                    cancellationToken: token),
                trafficClass == PacketTrafficClass.Interactive
                    ? WorkPriority.Interactive : WorkPriority.Background,
                ct, out Task<RouteResult>? routeTask))
        {
            return new PacketSubmissionResult(false, StreamId, 0, requestId,
                PacketSubmissionRejection.RouteSchedulerBusy);
        }

        var route = await routeTask!.WaitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return _packets.SubmitReserved(requestId, route, bytes, CurrentTick,
            lease, timeoutTicks);
    }

    private async Task RunReroutesAsync(CancellationToken ct)
    {
        var jobs = new List<Task>(2);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                jobs.RemoveAll(t => t.IsCompleted);
                while (jobs.Count < 2 && _packets.TryTakeReroute(out var ticket))
                {
                    if (_scheduler.TrySchedule(token => _medium.Route(ticket.Source,
                            ticket.Destination, cancellationToken: token),
                        ticket.TrafficClass == PacketTrafficClass.Interactive
                            ? WorkPriority.Interactive : WorkPriority.Background,
                        ct, out Task<RouteResult>? result))
                        jobs.Add(CompleteRerouteAsync(ticket, result!));
                    else
                    {
                        _packets.CancelReroute(ticket, schedulerRejected: true);
                        break;
                    }
                }
                await Task.Delay(20, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            try { await Task.WhenAll(jobs); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
    }

    private async Task CompleteRerouteAsync(PacketRerouteRequest ticket,
        Task<RouteResult> scheduled)
    {
        try { _packets.CompleteReroute(ticket, await scheduled); }
        catch (OperationCanceledException) { _packets.CancelReroute(ticket); }
        catch
        {
            _packets.CancelReroute(ticket);
            throw;
        }
    }
}
