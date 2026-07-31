// 構造変化イベントのデバウンス (docs/DESIGN.md A14 / B8)。
//
// フラグを TriggerMonitor 側で直接管理すると、停止時のリセット漏れで
// 「Stop → デバウンス窓内に Start」した場合の次の sweep 要求が黙って捨てられる。
// 状態をこのクラスに閉じ込め、停止時に Reset() を呼べば必ず解けるようにする。
//
// Task.Delay(...).ContinueWith(...) ではなく TimeProvider.CreateTimer を使う (B8)。
// 効果は 2 つ:
//   ・予約 1 回あたりの Task / 継続 / TaskScheduler 経路の割当が消える。構造変化が
//     毎秒数百件来るアプリでは、デバウンスされる側の予約も同じ頻度で試行される
//   ・時計を差し替えられる。「窓の内側で合流する」「Reset 後は次を受け付ける」という
//     このクラスの性質は、実時間を待つテストでは薄くしか確かめられない
namespace UiaTrigger.Monitoring;

internal sealed class SweepDebouncer : IDisposable
{
    private readonly TimeSpan _delay;
    private readonly Action _onElapsed;
    private readonly ITimer _timer;
    private int _scheduled;
    private int _disposed;

    public SweepDebouncer(TimeSpan delay, Action onElapsed, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(onElapsed);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _delay = delay;
        _onElapsed = onElapsed;
        // 周期は無し。1 回の予約ごとに Change で武装する
        _timer = timeProvider.CreateTimer(static state => ((SweepDebouncer)state!).Fire(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public bool IsScheduled => Volatile.Read(ref _scheduled) != 0;

    /// <summary>
    /// 未予約なら delay 後に onElapsed を呼ぶ予約をする。予約済みなら何もしない (合流)。
    /// 予約は onElapsed 側の処理が Reset() を呼ぶまで維持される。
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
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            Volatile.Write(ref _scheduled, 0);
        }
    }

    /// <summary>
    /// 予約状態を解除して次の Schedule() を受け付けられるようにする。
    /// sweep の実行時と停止時の両方で呼ぶこと (停止時に呼び忘れると A14 が再発する)。
    /// </summary>
    public void Reset() => Volatile.Write(ref _scheduled, 0);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _timer.Dispose();
        }
    }

    private void Fire()
    {
        // 予約フラグは落とさない。落とすのは実際に sweep を走らせた側の責任であり、
        // ここで落とすと「sweep 中に来た変化」が別の予約として重なる
        _onElapsed();
    }
}
