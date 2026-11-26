using System.Diagnostics;

namespace NetDM.Core;

public enum WorkPriority { Critical = 0, Interactive = 1, Background = 2 }

public sealed record WorkSchedulerOptions(
    int Workers = 2,
    int QueueCapacity = 256,
    int BudgetMillisecondsPer100Ms = 65,
    int DeliveryBudgetMillisecondsPer100Ms = 25)
{
    public void Validate()
    {
        if (Workers is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(Workers));
        if (QueueCapacity is < 1 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(QueueCapacity));
        if (BudgetMillisecondsPer100Ms is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(BudgetMillisecondsPer100Ms));
        // The delivery share is carved out of the whole budget, never added to it; without a budget it is unused.
        if (DeliveryBudgetMillisecondsPer100Ms < 0 ||
            (BudgetMillisecondsPer100Ms > 0 && DeliveryBudgetMillisecondsPer100Ms > BudgetMillisecondsPer100Ms))
            throw new ArgumentOutOfRangeException(nameof(DeliveryBudgetMillisecondsPer100Ms));
    }
}

public sealed record WorkSchedulerCounters(
    int Workers, int Running, int Queued, int CriticalQueued, int InteractiveQueued,
    int BackgroundQueued, int PeakQueued, long Accepted, long Rejected,
    long Completed, long Cancelled, long Failed,
    double QueueWaitP50Us, double QueueWaitP95Us, double WorkP95Us,
    int BudgetMillisecondsPer100Ms,
    /// <summary>Jobs that waited for the next window in the delivery lane and in the background lane.</summary>
    long BudgetWaitsDelivery, long BudgetWaitsBackground);

/// <summary>
/// A process-wide, bounded CPU-work executor. All accepted jobs are served by
/// a fixed number of workers. Weighted round-robin selection prevents a flood
/// of critical requests from permanently starving background work.
/// The optional time-window budget is soft, not CPU preemption: a started
/// operation may run past the current window and must honour cancellation.
/// </summary>
public sealed class PriorityWorkScheduler : IAsyncDisposable
{
    private interface IWork
    {
        long SubmittedAt { get; }
        WorkPriority Priority { get; }
        CancellationToken Cancellation { get; }
        bool WasCancelled { get; }
        bool WasFailed { get; }
        void Execute(CancellationToken schedulerToken);
        void Cancel();
    }

    private sealed class Work<T>(WorkPriority priority, Func<CancellationToken, T> action,
        CancellationToken cancellation) : IWork
    {
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long SubmittedAt { get; } = Stopwatch.GetTimestamp();
        public WorkPriority Priority => priority;
        public CancellationToken Cancellation => cancellation;
        public bool WasCancelled { get; private set; }
        public bool WasFailed { get; private set; }
        public Task<T> Task => _completion.Task;

        public void Execute(CancellationToken schedulerToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, schedulerToken);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                _completion.TrySetResult(action(linked.Token));
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                WasCancelled = true;
                _completion.TrySetCanceled(linked.Token);
            }
            catch (Exception ex)
            {
                WasFailed = true;
                _completion.TrySetException(ex);
            }
        }

        public void Cancel() => _completion.TrySetCanceled();
    }

    private const int WindowSamples = 1024;

    private static readonly int[] WeightedOrder = [0, 0, 0, 0, 1, 1, 2];
    private readonly Queue<IWork>[] _queues = [new(), new(), new()];
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private readonly WorkSchedulerOptions _options;
    private readonly long[] _queueWaits = new long[WindowSamples];
    private readonly long[] _workTimes = new long[WindowSamples];
    private int _queueCount, _peak, _running, _cursor;
    private long _accepted, _rejected, _completed, _cancelled, _failed, _latencyIndex;
    private bool _stopping;
    /// <summary>The window budget, or null when the budget is off.</summary>
    private readonly WorkBudgetWindow? _budget;
    private long _budgetWaitsDelivery, _budgetWaitsBackground;

    public PriorityWorkScheduler(WorkSchedulerOptions options)
    {
        options.Validate();
        _options = options;
        _budget = options.BudgetMillisecondsPer100Ms == 0
            ? null
            : new WorkBudgetWindow(options.BudgetMillisecondsPer100Ms, options.DeliveryBudgetMillisecondsPer100Ms);
        _workers = Enumerable.Range(0, options.Workers)
            .Select(_ => Task.Run(WorkerAsync)).ToArray();
    }

    public WorkSchedulerCounters Capture()
    {
        int queued, peak, critical, interactive, background;
        lock (_gate)
        {
            queued = _queueCount;
            peak = _peak;
            critical = _queues[0].Count;
            interactive = _queues[1].Count;
            background = _queues[2].Count;
        }
        var length = (int)Math.Min(Volatile.Read(ref _latencyIndex), WindowSamples);
        var waits = new long[length]; var works = new long[length];
        for (var i = 0; i < length; i++)
        {
            waits[i] = Volatile.Read(ref _queueWaits[i]);
            works[i] = Volatile.Read(ref _workTimes[i]);
        }
        Array.Sort(waits); Array.Sort(works);
        static double Quantile(long[] sorted, double q) =>
            sorted.Length == 0 ? 0 :
            sorted[(int)Math.Ceiling((sorted.Length - 1) * q)];
        return new WorkSchedulerCounters(
            _options.Workers, Volatile.Read(ref _running), queued, critical,
            interactive, background, peak, Interlocked.Read(ref _accepted),
            Interlocked.Read(ref _rejected), Interlocked.Read(ref _completed),
            Interlocked.Read(ref _cancelled), Interlocked.Read(ref _failed),
            Quantile(waits, .5), Quantile(waits, .95), Quantile(works, .95),
            _options.BudgetMillisecondsPer100Ms,
            Interlocked.Read(ref _budgetWaitsDelivery), Interlocked.Read(ref _budgetWaitsBackground));
    }

    /// <summary>
    /// Enqueue without blocking the caller. A false result means rejection:
    /// the caller retains ownership and should report 'busy' rather than wait.
    /// </summary>
    public bool TrySchedule<T>(Func<CancellationToken, T> action, WorkPriority priority,
        CancellationToken cancellationToken, out Task<T>? result)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (priority is < WorkPriority.Critical or > WorkPriority.Background)
            throw new ArgumentOutOfRangeException(nameof(priority));
        if (cancellationToken.IsCancellationRequested)
        {
            result = Task.FromCanceled<T>(cancellationToken);
            return true;
        }

        lock (_gate)
        {
            if (_stopping || _queueCount >= _options.QueueCapacity)
            {
                Interlocked.Increment(ref _rejected);
                result = null;
                return false;
            }
            var item = new Work<T>(priority, action, cancellationToken);
            _queues[(int)priority].Enqueue(item);
            _queueCount++;
            _peak = Math.Max(_peak, _queueCount);
            Interlocked.Increment(ref _accepted);
            result = item.Task;
            _signal.Release();
            return true;
        }
    }

    private async Task WorkerAsync()
    {
        while (true)
        {
            try { await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            IWork? item = null;
            lock (_gate)
            {
                for (var i = 0; i < WeightedOrder.Length; i++)
                {
                    var bucket = WeightedOrder[_cursor++ % WeightedOrder.Length];
                    if (_queues[bucket].Count == 0) continue;
                    item = _queues[bucket].Dequeue();
                    _queueCount--;
                    break;
                }
            }
            if (item is null) continue;

            if (item.Cancellation.IsCancellationRequested)
            {
                item.Cancel();
                Interlocked.Increment(ref _cancelled);
                continue;
            }
            try
            {
                await AwaitWindowBudgetAsync(item.Priority, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { item.Cancel(); break; }
            if (item.Cancellation.IsCancellationRequested)
            {
                item.Cancel();
                Interlocked.Increment(ref _cancelled);
                continue;
            }
            var queuedUs = Microseconds(Stopwatch.GetTimestamp() - item.SubmittedAt);
            Interlocked.Increment(ref _running);
            var start = Stopwatch.GetTimestamp();
            try { item.Execute(_shutdown.Token); }
            finally
            {
                var ticks = Stopwatch.GetTimestamp() - start;
                _budget?.Charge(item.Priority, Microseconds(ticks), Microseconds(Stopwatch.GetTimestamp()));
                Interlocked.Decrement(ref _running);
                // Use per-task metrics from the TaskCompletionSource state.
                if (item.WasCancelled) Interlocked.Increment(ref _cancelled);
                else if (item.WasFailed) Interlocked.Increment(ref _failed);
                else Interlocked.Increment(ref _completed);
                var position = (Interlocked.Increment(ref _latencyIndex) - 1) % WindowSamples;
                Interlocked.Exchange(ref _queueWaits[(int)position], queuedUs);
                Interlocked.Exchange(ref _workTimes[(int)position], Microseconds(ticks));
            }
        }
    }

    /// <summary>
    /// Waits for the lane's share of the window. Delivery work (critical and interactive) may spend the
    /// whole budget; background work stops at the share reserved for delivery, so a flood of reroutes
    /// never delays a service delivery (P8).
    /// </summary>
    private async Task AwaitWindowBudgetAsync(WorkPriority priority, CancellationToken token)
    {
        if (_budget is null) return;
        while (true)
        {
            if (_budget.TryEnter(priority, Microseconds(Stopwatch.GetTimestamp()), out var waitMs))
                return;
            if (priority == WorkPriority.Background)
                Interlocked.Increment(ref _budgetWaitsBackground);
            else
                Interlocked.Increment(ref _budgetWaitsDelivery);
            await Task.Delay(waitMs, token).ConfigureAwait(false);
        }
    }

    private static long Microseconds(long ticks) =>
        (long)Math.Max(0, (double)ticks * 1_000_000 / Stopwatch.Frequency);

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
        }
        _shutdown.Cancel();
        await Task.WhenAll(_workers).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var queue in _queues)
                while (queue.TryDequeue(out var item))
                {
                    item.Cancel();
                    Interlocked.Increment(ref _cancelled);
                }
            _queueCount = 0;
        }
        _shutdown.Dispose();
        _signal.Dispose();
    }
}
