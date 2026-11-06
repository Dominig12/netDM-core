using System.Diagnostics;

namespace NetDM.Host.Benchmarks;

/// <summary>
/// A low-overhead, independent observation thread. It does not share
/// the packet, routing, benchmark or global scheduler monitor locks.
/// Timing differences are observations, not proof of OS/GC causality.
/// </summary>
public sealed record ProcessStallEvent(
    DateTimeOffset AtUtc, string Kind, double DelayMilliseconds,
    double GcPauseMillisecondsSinceStart, double CpuCoresUsed,
    int ThreadPoolThreads, long ThreadPoolPendingWorkItems,
    long GcLastIndex, int GcLastGeneration);

public sealed record ProcessStallCounters(
    long Heartbeats, double LastHeartbeatLatenessMilliseconds,
    double MaxHeartbeatLatenessMilliseconds, long HeartbeatDelaysOver250Milliseconds,
    long ThreadPoolProbes, double MaxThreadPoolDispatchMilliseconds,
    long ThreadPoolDelaysOver250Milliseconds, bool ThreadPoolProbeOutstanding,
    int ThreadPoolThreads, long ThreadPoolPendingWorkItems,
    double ProcessCpuCoresUsed, double ProcessCpuMachinePercent,
    double GcPauseMillisecondsSinceStart, int Gen2CollectionsSinceStart,
    int Gen0CollectionsSinceStart, int Gen1CollectionsSinceStart,
    long GcLastIndex, int GcLastGeneration, double GcLastPauseMilliseconds,
    bool GcLastCompacted, bool GcLastConcurrent,
    long GcLastFullBlockingIndex, double GcLastFullBlockingPauseMilliseconds,
    long GcHeapSizeBytes, long GcFragmentedBytes, long GcCommittedBytes,
    long GcMemoryLoadBytes, long GcAllocatedBytesSinceStart,
    IReadOnlyList<ProcessStallEvent> Events);

public sealed class ProcessStallMonitor : IDisposable
{
    private const int PeriodMs = 100;
    private const int SignificantDelayMs = 250;
    private const int MaxEvents = 64;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Queue<ProcessStallEvent> _events = new();
    private readonly double _gcBaseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
    private readonly int _gen0Base = GC.CollectionCount(0);
    private readonly int _gen1Base = GC.CollectionCount(1);
    private readonly int _gen2Base = GC.CollectionCount(2);
    private readonly long _allocatedBase = GC.GetTotalAllocatedBytes(precise: false);
    private Thread? _thread;
    private long _heartbeats, _heartbeatDelays, _poolProbes, _poolDelays;
    private double _lastHeartbeatLateMs, _maxHeartbeatLateMs, _maxPoolDispatchMs;
    private double _cpuCores, _gcPauseSinceStartMs;
    private long _pendingWorkItems;
    private int _threadCount, _probeOutstanding, _disposed;

    public void Start()
    {
        if (_thread is not null || Volatile.Read(ref _disposed) != 0)
            throw new InvalidOperationException("Monitor already started or disposed.");
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "netDM process heartbeat"
        };
        _thread.Start();
    }

    public ProcessStallCounters Capture()
    {
        // Heap information describes the last completed GC, not the live heap.
        // Read GC diagnostics outside the heartbeat lock.
        var gc = GC.GetGCMemoryInfo();
        var fullGc = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        var gen0 = Math.Max(0, GC.CollectionCount(0) - _gen0Base);
        var gen1 = Math.Max(0, GC.CollectionCount(1) - _gen1Base);
        var gen2 = Math.Max(0, GC.CollectionCount(2) - _gen2Base);
        var allocated = Math.Max(0,
            GC.GetTotalAllocatedBytes(precise: false) - _allocatedBase);
        lock (_gate)
            return new ProcessStallCounters(
                _heartbeats, _lastHeartbeatLateMs, _maxHeartbeatLateMs,
                _heartbeatDelays, _poolProbes, _maxPoolDispatchMs,
                _poolDelays, Volatile.Read(ref _probeOutstanding) != 0,
                _threadCount, _pendingWorkItems, _cpuCores,
                _cpuCores * 100.0 / Math.Max(1, Environment.ProcessorCount),
                Math.Max(_gcPauseSinceStartMs,
                    GC.GetTotalPauseDuration().TotalMilliseconds - _gcBaseMs),
                gen2, gen0, gen1,
                gc.Index, gc.Generation, SumPauseMilliseconds(gc),
                gc.Compacted, gc.Concurrent, fullGc.Index,
                SumPauseMilliseconds(fullGc), gc.HeapSizeBytes,
                gc.FragmentedBytes, gc.TotalCommittedBytes,
                gc.MemoryLoadBytes, allocated, _events.ToArray());
    }

    private static double SumPauseMilliseconds(GCMemoryInfo info)
    {
        double total = 0;
        foreach (var pause in info.PauseDurations)
            total += pause.TotalMilliseconds;
        return total;
    }

    private void Run()
    {
        try
        {
            long lastTimestamp = Stopwatch.GetTimestamp();
            TimeSpan previousCpu = _process.TotalProcessorTime;
            long period = Math.Max(1, (long)Stopwatch.Frequency * PeriodMs / 1000);
            while (!_stop.Wait(PeriodMs))
            {
                long now = Stopwatch.GetTimestamp();
                long elapsedTicks = Math.Max(1, now - lastTimestamp);
                double elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
                double latenessMs = Math.Max(0, elapsedMs - PeriodMs);
                lastTimestamp = now;

                var cpu = _process.TotalProcessorTime;
                double cores = Math.Max(0, (cpu - previousCpu).TotalSeconds *
                    Stopwatch.Frequency / elapsedTicks);
                previousCpu = cpu;

                double gcSinceStart = Math.Max(0,
                    GC.GetTotalPauseDuration().TotalMilliseconds - _gcBaseMs);
                int threads = ThreadPool.ThreadCount;
                long pending = ThreadPool.PendingWorkItemCount;
                lock (_gate)
                {
                    _heartbeats++;
                    _lastHeartbeatLateMs = latenessMs;
                    _maxHeartbeatLateMs = Math.Max(_maxHeartbeatLateMs, latenessMs);
                    _cpuCores = cores;
                    _gcPauseSinceStartMs = gcSinceStart;
                    _threadCount = threads;
                    _pendingWorkItems = pending;
                    if (latenessMs >= SignificantDelayMs)
                    {
                        _heartbeatDelays++;
                        var lastGc = GC.GetGCMemoryInfo();
                        AddEvent(new ProcessStallEvent(DateTimeOffset.UtcNow,
                            "heartbeat", latenessMs, gcSinceStart, cores, threads, pending,
                            lastGc.Index, lastGc.Generation));
                    }
                }

                // Probe the global .NET ThreadPool every 200ms, independently
                // from the packet runner. At most one probe may be queued.
                if (_heartbeats % 2 == 0 &&
                    Interlocked.CompareExchange(ref _probeOutstanding, 1, 0) == 0)
                {
                    long submitted = Stopwatch.GetTimestamp();
                    ThreadPool.UnsafeQueueUserWorkItem(
                        static (Probe state) => state.Monitor.RecordProbe(state.Submitted),
                        new Probe(this, submitted), preferLocal: false);
                }
            }
        }
        finally
        {
            _process.Dispose();
        }
    }

    private readonly record struct Probe(ProcessStallMonitor Monitor, long Submitted);

    private void RecordProbe(long submitted)
    {
        double delay = Math.Max(0,
            (Stopwatch.GetTimestamp() - submitted) * 1000.0 / Stopwatch.Frequency);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _poolProbes++;
                _maxPoolDispatchMs = Math.Max(_maxPoolDispatchMs, delay);
                if (delay >= SignificantDelayMs)
                {
                    _poolDelays++;
                    var lastGc = GC.GetGCMemoryInfo();
                    AddEvent(new ProcessStallEvent(DateTimeOffset.UtcNow,
                        "threadpool", delay, _gcPauseSinceStartMs, _cpuCores,
                        ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount,
                        lastGc.Index, lastGc.Generation));
                }
            }
        }
        Interlocked.Exchange(ref _probeOutstanding, 0);
    }

    // Caller already holds _gate. Never retain an unbounded event history.
    private void AddEvent(ProcessStallEvent value)
    {
        if (_events.Count == MaxEvents) _events.Dequeue();
        _events.Enqueue(value);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Set();
        // The monitor's wake-up is independent of all simulation locks.
        // A permanently descheduled thread must not block benchmark shutdown.
        _thread?.Join(TimeSpan.FromSeconds(2));
    }
}
