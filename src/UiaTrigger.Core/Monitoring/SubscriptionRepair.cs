// 購読を失ったトリガーを拾い直すための間隔付き再試行 (docs/DESIGN.md §8)。
//
// <b>これはポーリングではない。</b>armed になるのは SubscriptionHealth.IsOrphaned が
// 成立している間だけで、購読が戻れば次の予約はされない。健全なときはタイマーが 1 本も動かない。
//
// 間隔を伸ばす (backoff) のは、塞がったままの相手を同じ速さで叩き続けないためである。
// 再試行 1 回は掃引 1 回であり、掃引は<b>ピッカーと共有している MTA スレッド</b>の上で走って
// 相手ごとに TransactionTimeout ぶん止まりうる。機械が苦しいときほど激しく叩く形にしない。
//
// SweepDebouncer と同じく TimeProvider.CreateTimer を使う。時計を差し替えられるので
// T1 が実時間を待たずに間隔と backoff を確かめられる。
namespace UiaTrigger.Monitoring;

internal sealed class SubscriptionRepair : IDisposable
{
    private readonly long _initialTicks;
    private readonly long _maxTicks;
    private readonly Action _onElapsed;
    private readonly ITimer _timer;
    private long _nextTicks;
    private int _scheduled;
    private int _disposed;

    public SubscriptionRepair(TimeSpan initial, TimeSpan max, Action onElapsed, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(onElapsed);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initial, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, initial);

        _initialTicks = initial.Ticks;
        _maxTicks = max.Ticks;
        _nextTicks = initial.Ticks;
        _onElapsed = onElapsed;
        _timer = timeProvider.CreateTimer(
            static state => ((SubscriptionRepair)state!).Fire(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public bool IsScheduled => Volatile.Read(ref _scheduled) != 0;

    /// <summary>次に使う間隔 (backoff の現在値)。</summary>
    public TimeSpan NextInterval => TimeSpan.FromTicks(Volatile.Read(ref _nextTicks));

    /// <summary>
    /// 未予約なら、現在の間隔のあとに <c>onElapsed</c> を呼ぶ予約をする。予約済みなら何もしない。
    /// </summary>
    public void Schedule()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        if (Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0)
        {
            return;
        }
        try
        {
            _timer.Change(NextInterval, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            Volatile.Write(ref _scheduled, 0);
        }
    }

    /// <summary>
    /// 購読が戻った (または監視を止めた)。予約を解き、間隔を最初の値へ戻す。
    /// </summary>
    /// <remarks>
    /// 間隔を戻さないと、一度長く塞がれたあとは<b>次に壊れたときも復旧が遅い</b>ままになる。
    /// </remarks>
    public void Recovered()
    {
        Volatile.Write(ref _nextTicks, _initialTicks);
        Volatile.Write(ref _scheduled, 0);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _timer.Dispose();
        }
    }

    private void Fire()
    {
        // 次の間隔を先に伸ばす。呼び出し先が同期で Schedule() を呼び返しても、
        // 伸びた値が使われる
        long doubled = Volatile.Read(ref _nextTicks) * 2;
        Volatile.Write(ref _nextTicks, doubled > _maxTicks || doubled < 0 ? _maxTicks : doubled);
        Volatile.Write(ref _scheduled, 0);
        _onElapsed();
    }
}
