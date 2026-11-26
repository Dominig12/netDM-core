using System.Diagnostics;

namespace NetDM.Host.Diagnostics;

/// <summary>
/// Durations of one background stage (P10 budgets): every run is counted, the last
/// <see cref="Samples"/> runs give the percentiles. Written by the one loop that runs the stage; a reader
/// may see a sample from a run in progress, which only shifts a percentile by one sample.
/// </summary>
public sealed class StageTimer
{
    public const int Samples = 1024;
    private readonly long[] _micros = new long[Samples];
    private long _work, _maxMicros, _totalMicros;

    /// <summary>Times one run; <paramref name="worked"/> says whether it did anything (a due pass).</summary>
    public void Record(long startTimestamp, bool worked)
    {
        var micros = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMicroseconds;
        if (!worked)
            return; // Not-due passes return at once; they would only dilute the percentiles.
        _micros[_work % Samples] = micros;
        _work++;
        _totalMicros += micros;
        if (micros > _maxMicros)
            _maxMicros = micros;
    }

    /// <summary>Starts a new measurement; called by the loop that records.</summary>
    public void Reset()
    {
        _work = _maxMicros = _totalMicros = 0;
    }

    public object Snapshot()
    {
        var count = (int)Math.Min(_work, Samples);
        var sorted = _micros.AsSpan(0, count).ToArray();
        Array.Sort(sorted);
        long Percentile(double p) => count == 0 ? 0 : sorted[Math.Min(count - 1, (int)Math.Ceiling(p * count) - 1)];
        return new
        {
            passes = _work, p50Us = Percentile(0.50), p95Us = Percentile(0.95), p99Us = Percentile(0.99), maxUs = _maxMicros,
            meanUs = _work == 0 ? 0 : _totalMicros / _work
        };
    }
}
