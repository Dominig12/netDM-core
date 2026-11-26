namespace NetDM.Core;

/// <summary>
/// The time-window budget of <see cref="PriorityWorkScheduler"/>. Work may start while the window has
/// budget; a share of that budget is reserved for delivery work (critical and interactive), so a flood
/// of background jobs — reroutes, scans — cannot spend the share and delay a service delivery (P8, §6.3).
/// The budget is soft: an admitted job may run past the window and is never preempted. Time is
/// counted in microseconds of a monotonic epoch supplied by the caller, which keeps the rule testable
/// without waiting for real windows.
/// </summary>
public sealed class WorkBudgetWindow
{
    /// <summary>One window is one hundred milliseconds.</summary>
    public const long WindowMicroseconds = 100_000;

    private readonly int _totalMicroseconds;
    private readonly int _deliveryMicroseconds;
    private readonly object _gate = new();
    private long _windowStartMicroseconds;
    private long _spentMicroseconds;
    private long _spentBackgroundMicroseconds;

    /// <param name="totalMillisecondsPerWindow">The whole budget of one window; zero disables the budget.</param>
    /// <param name="deliveryMillisecondsPerWindow">The share of it that only delivery work may spend.</param>
    public WorkBudgetWindow(int totalMillisecondsPerWindow, int deliveryMillisecondsPerWindow)
    {
        if (totalMillisecondsPerWindow is < 0 or > (int)(WindowMicroseconds / 1_000))
            throw new ArgumentOutOfRangeException(nameof(totalMillisecondsPerWindow));
        if (deliveryMillisecondsPerWindow < 0 || deliveryMillisecondsPerWindow > totalMillisecondsPerWindow)
            throw new ArgumentOutOfRangeException(nameof(deliveryMillisecondsPerWindow));
        _totalMicroseconds = totalMillisecondsPerWindow * 1_000;
        _deliveryMicroseconds = deliveryMillisecondsPerWindow * 1_000;
    }

    /// <summary>Whether the budget is off: every job starts at once.</summary>
    public bool Disabled => _totalMicroseconds == 0;

    /// <summary>
    /// Whether work of this priority may start at <paramref name="nowMicroseconds"/>. When it may not,
    /// <paramref name="waitMilliseconds"/> is the pause that ends the current window.
    /// </summary>
    public bool TryEnter(WorkPriority priority, long nowMicroseconds, out int waitMilliseconds)
    {
        waitMilliseconds = 0;
        if (_totalMicroseconds == 0)
            return true;
        lock (_gate)
        {
            Roll(nowMicroseconds);
            if (_spentMicroseconds >= _totalMicroseconds)
            {
                waitMilliseconds = WaitMilliseconds(nowMicroseconds);
                return false;
            }
            if (priority == WorkPriority.Background &&
                _spentBackgroundMicroseconds >= _totalMicroseconds - _deliveryMicroseconds)
            {
                waitMilliseconds = WaitMilliseconds(nowMicroseconds);
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Charges finished work to the window that is current when it ends. A job that started in an older
    /// window pays the current one; the bookkeeping only ever bounds the next admission.
    /// </summary>
    public void Charge(WorkPriority priority, long elapsedMicroseconds, long nowMicroseconds)
    {
        if (_totalMicroseconds == 0 || elapsedMicroseconds <= 0)
            return;
        lock (_gate)
        {
            Roll(nowMicroseconds);
            _spentMicroseconds = Math.Min(int.MaxValue, _spentMicroseconds + elapsedMicroseconds);
            if (priority == WorkPriority.Background)
                _spentBackgroundMicroseconds = Math.Min(int.MaxValue, _spentBackgroundMicroseconds + elapsedMicroseconds);
        }
    }

    private void Roll(long nowMicroseconds)
    {
        if (nowMicroseconds - _windowStartMicroseconds < WindowMicroseconds)
            return;
        _windowStartMicroseconds = nowMicroseconds;
        _spentMicroseconds = 0;
        _spentBackgroundMicroseconds = 0;
    }

    private int WaitMilliseconds(long nowMicroseconds) =>
        (int)Math.Max(1, (_windowStartMicroseconds + WindowMicroseconds - nowMicroseconds + 999) / 1_000);
}
