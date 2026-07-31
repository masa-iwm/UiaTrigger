// 利用者が指定したポーリング間隔 (docs/DESIGN.md §5)。
//
// **ライブラリの判断で回るものではない。**armed になるのは
// TriggerDefinition.PollInterval を明示的に指定したトリガーだけで、
// 指定が無ければ (既定) このクラスは**そもそも生成されない** = タイマーが 1 本も存在しない。
//
// SubscriptionRepair と対にして読むこと。2 つは似た形をしているが別のものである:
//   ・SubscriptionRepair = 復旧の経路。ライブラリが自分で決めて回す。
//                          だから「壊れている間だけ」という厳しい条件が要る (docs/DESIGN.md §8)
//   ・SlotPoller         = 利用者が頼んだ費用。頼まれなければ存在しないので、
//                          回りっぱなしになる形が無い
//
// 周期タイマーにしていないのは、相手が TransactionTimeout ぶん止まったときに**周が重なる**
// ためである。1 周ぶんの仕事が終わってから Change で武装し直し、*周と周の間隔* を保証する。
// これは SubscriptionRepair のヘッダが述べている懸念と同じで、こちらのほうが強く当てはまる —
// あちらは壊れている間だけだが、こちらは頼まれているかぎり回り続ける。
namespace UiaTrigger.Monitoring;

/// <summary>1 トリガー分の、利用者が指定したポーリング。</summary>
internal sealed class SlotPoller : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly Action _onElapsed;
    private readonly ITimer _timer;
    private int _scheduled;
    private int _disposed;

    /// <param name="interval">
    /// 周と周の間隔。**正の値でなければならない** — 0 は「ポーリングしない」であって
    /// 「間隔 0 でポーリングする」ではないので、呼び出し元がここへ来る前に弾く。
    /// </param>
    /// <param name="onElapsed">1 周ぶんの仕事。UIA スレッドへ投げるのは呼び出し元の責任である。</param>
    /// <param name="timeProvider">時計。差し替えられるので T1 が実時間を待たずに間隔を確かめられる。</param>
    public SlotPoller(TimeSpan interval, Action onElapsed, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(onElapsed);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        _interval = interval;
        _onElapsed = onElapsed;
        // 周期は無し。1 周ごとに Change で武装する (上のコメントを参照)
        _timer = timeProvider.CreateTimer(
            static state => ((SlotPoller)state!).Fire(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public bool IsScheduled => Volatile.Read(ref _scheduled) != 0;

    /// <summary>周と周の間隔。</summary>
    public TimeSpan Interval => _interval;

    /// <summary>
    /// 未予約なら、間隔のあとに <c>onElapsed</c> を呼ぶ予約をする。予約済みなら何もしない (合流)。
    /// </summary>
    public void Schedule()
    {
        // 停止と同時に来た予約は落とす。ITimer.Change が解放後に例外を投げるかは
        // 実装依存なので (TimeProvider は差し替えられる)、こちら側で判断する
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
            _timer.Change(_interval, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            Volatile.Write(ref _scheduled, 0);
        }
    }

    /// <summary>予約状態を解除して次の <see cref="Schedule"/> を受け付けられるようにする。</summary>
    public void Reset() => Volatile.Write(ref _scheduled, 0);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Volatile.Write(ref _scheduled, 0);
            _timer.Dispose();
        }
    }

    private void Fire()
    {
        // 予約フラグは落とさない。落とすのは実際に 1 周を走らせた側の責任である
        // (SweepDebouncer と同じ約束)
        _onElapsed();
    }
}
