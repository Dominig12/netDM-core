using System.Diagnostics;

namespace NetDM.Core;

/// <summary>
/// Metrics from a monotonic fixed-period forwarding loop on a dedicated
/// long-running .NET worker, independent of the global CPU work queue.
/// </summary>
public sealed record PacketTickRunnerCounters(
    long ExecutedTicks, long SkippedIntervals, long FailedTicks, long LastTick,
    double StartLagP95Milliseconds, double ExecutionP95Milliseconds,
    double MaxStartLagMilliseconds);

/// <summary>
/// Dedicated packet tick driver: one call to Advance per observed monotonic
/// clock tick. Overdue intervals are intentionally SKIPPED (never replayed),
/// so a delayed worker cannot make a packet jump multiple hops instantly.
/// The caller owns cancellation and awaits the returned Task before reset.
/// </summary>
public sealed class PacketTickRunner
{
    private const int Window = 1024;
    private readonly RadioPacketTransport _packets;
    private readonly int _tickPeriodMilliseconds;
    private readonly long _periodTicks;
    private readonly object _startGate = new();
    private readonly long[] _lagUs = new long[Window];
    private readonly long[] _workUs = new long[Window];
    private Task? _task;
    private long _executed, _skipped, _failures, _lastTick = -1, _maxLagUs;

    public PacketTickRunner(RadioPacketTransport packets, int periodMilliseconds = 100)
    {
        _packets = packets ?? throw new ArgumentNullException(nameof(packets));
        if (periodMilliseconds is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(periodMilliseconds));
        _tickPeriodMilliseconds = periodMilliseconds;
        _periodTicks = Math.Max(1, (long)Stopwatch.Frequency * periodMilliseconds / 1000);
    }

    public int PeriodMilliseconds => _tickPeriodMilliseconds;

    public PacketTickRunnerCounters Capture()
    {
        int count = (int)Math.Min(Interlocked.Read(ref _executed), Window);
        var lag = new long[count];
        var work = new long[count];
        for (var i = 0; i < count; i++)
        {
            lag[i] = Volatile.Read(ref _lagUs[i]);
            work[i] = Volatile.Read(ref _workUs[i]);
        }
        Array.Sort(lag);
        Array.Sort(work);
        static double P95(long[] ordered) => ordered.Length == 0 ? 0 :
            ordered[(int)Math.Ceiling((ordered.Length - 1) * .95)] / 1000.0;
        return new PacketTickRunnerCounters(
            Interlocked.Read(ref _executed),
            Interlocked.Read(ref _skipped),
            Interlocked.Read(ref _failures),
            Interlocked.Read(ref _lastTick),
            P95(lag), P95(work), Interlocked.Read(ref _maxLagUs) / 1000.0);
    }

    /// <summary>
    /// Starts once. TaskCreationOptions.LongRunning requests a dedicated OS
    /// thread instead of occupying a pooled route worker. This is isolation
    /// from the *queue*, not CPU affinity or hard real-time scheduling.
    /// </summary>
    public Task Start(CancellationToken cancellationToken)
    {
        lock (_startGate)
        {
            if (_task is not null)
                throw new InvalidOperationException("Packet tick runner has already been started.");
            _task = Task.Factory.StartNew(
                () => Run(cancellationToken),
                CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            return _task;
        }
    }

    private void Run(CancellationToken token)
    {
        // All route producers use the same Stopwatch epoch and 100ms quantum,
        // so packet enqueue timestamps and forwarding ticks are comparable.
        long nextTick = Stopwatch.GetTimestamp() / _periodTicks + 1;
        while (!token.IsCancellationRequested)
        {
            var now = Stopwatch.GetTimestamp();
            var observedTick = now / _periodTicks;
            if (observedTick < nextTick)
            {
                var remainingTicks = nextTick * _periodTicks - now;
                int waitMs = (int)Math.Clamp(
                    remainingTicks * 1000.0 / Stopwatch.Frequency, 1, 20);
                if (token.WaitHandle.WaitOne(waitMs))
                    break;
                continue;
            }

            var skipped = observedTick - nextTick;
            if (skipped > 0)
                Interlocked.Add(ref _skipped, skipped);
            // Measure against the tick we intended to run, not observedTick.
            // The old formula always reported < one period even after a
            // multi-second stall and masked the 48/68 skipped intervals.
            var lag = (long)Math.Max(0, (now - nextTick * _periodTicks) *
                1_000_000.0 / Stopwatch.Frequency);
            InterlockedExtensions.Max(ref _maxLagUs, lag);

            var started = Stopwatch.GetTimestamp();
            try
            {
                _packets.Advance(observedTick, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                Interlocked.Increment(ref _failures);
                throw;
            }

            var elapsed = Stopwatch.GetTimestamp() - started;
            var index = Interlocked.Read(ref _executed) % Window;
            Interlocked.Exchange(ref _lagUs[(int)index], lag);
            Interlocked.Exchange(ref _workUs[(int)index],
                (long)Math.Max(0, elapsed * 1_000_000.0 / Stopwatch.Frequency));
            Interlocked.Exchange(ref _lastTick, observedTick);
            Interlocked.Increment(ref _executed);
            nextTick = observedTick + 1;
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref long location, long candidate)
        {
            long previous;
            do
            {
                previous = Volatile.Read(ref location);
                if (previous >= candidate) return;
            }
            while (Interlocked.CompareExchange(ref location, candidate, previous) != previous);
        }
    }
}
