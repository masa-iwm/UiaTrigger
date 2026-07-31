// 発火レート制限 (docs/DESIGN.md C11)。
//
// BoundingRectangle のようにドラッグ中は毎フレーム変化するプロパティを監視すると、
// 変化の数だけホストのハンドラが呼ばれる。TriggerDefinition.MinInterval を設けて
// 「最後に通してから所定時間が経つまでは落とす」ようにする。
//
// 時計は TimeProvider から取る (docs/DESIGN.md C10)。呼び出し元が
// Stopwatch.GetTimestamp() を渡す形にすると、経過時間の計算は結局
// 「その時計の周波数」を知っている側でしかできない。時計そのものを持たせるほうが
// 境界がはっきりし、ホストが差し替えた時計もそのまま効く。
namespace UiaTrigger.Monitoring;

/// <summary>1 トリガー分の最小発火間隔。</summary>
internal sealed class MinIntervalGate(TimeSpan? minInterval, TimeProvider timeProvider)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private long _lastPassed;
    private bool _hasPassed;

    /// <summary>制限なしの (常に通す) ゲートか。</summary>
    public bool IsUnlimited => minInterval is not { Ticks: > 0 };

    /// <summary>診断用: 落とした発火の累計。</summary>
    public long SuppressedCount { get; private set; }

    /// <summary>発火してよいかを判定し、通すなら時刻を記録する。</summary>
    public bool TryPass()
    {
        if (IsUnlimited)
        {
            return true;
        }
        long now = _timeProvider.GetTimestamp();
        // 初回は必ず通す。「起動直後の 1 回目を待たされる」のは制限の意図ではない
        if (_hasPassed && _timeProvider.GetElapsedTime(_lastPassed, now) < minInterval!.Value)
        {
            SuppressedCount++;
            return false;
        }
        _lastPassed = now;
        _hasPassed = true;
        return true;
    }

    /// <summary>監視の停止・再開で間隔をまたがせない。</summary>
    public void Reset()
    {
        _hasPassed = false;
        SuppressedCount = 0;
    }
}
