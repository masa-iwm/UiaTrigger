using Microsoft.Extensions.Time.Testing;
using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 再解決 sweep のデバウンスの回帰テスト (docs/DESIGN.md A14 / B8)。
///
/// 予約フラグを停止時 (StopCore) に落とさないと、「停止 → デバウンス窓のうちに再開」で
/// 次の sweep 要求が予約済みとみなされて黙って捨てられる。
///
/// デバウンスは TimeProvider.CreateTimer なので (B8)、ここは <b>実時間を一切待たない</b>。
/// Task.Delay の完了を待つ形だと、「窓の手前では鳴らない」という肝心の側を
/// 書けない (待ち時間を延ばすだけのテストになる)。
/// </summary>
public sealed class SweepDebouncerTests
{
    /// <summary>TriggerMonitorOptions.Debounce の既定値と同じ。</summary>
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(200);

    private static (SweepDebouncer Debouncer, FakeTimeProvider Time, Func<int> Calls) Create()
    {
        var time = new FakeTimeProvider();
        int calls = 0;
        var debouncer = new SweepDebouncer(Delay, () => Interlocked.Increment(ref calls), time);
        return (debouncer, time, () => Volatile.Read(ref calls));
    }

    [Fact]
    public void Schedule_InvokesTheCallbackWhenTheDelayElapses()
    {
        (SweepDebouncer debouncer, FakeTimeProvider time, Func<int> calls) = Create();
        using (debouncer)
        {
            Assert.False(debouncer.IsScheduled);
            debouncer.Schedule();
            Assert.True(debouncer.IsScheduled);

            time.Advance(Delay);

            Assert.Equal(1, calls());
        }
    }

    /// <summary>
    /// 窓の手前では鳴らないこと。
    ///
    /// 絶対値で書く: 「Delay より少し前」を <c>Delay - 1ms</c> のように定数から作ると、
    /// Delay を変えても素通りする無力なテストになる (実例あり)。
    /// 200ms という値そのものと、その内側の 199ms を書く。
    /// </summary>
    [Fact]
    public void Schedule_StaysQuietUntilTheDelayHasFullyElapsed()
    {
        (SweepDebouncer debouncer, FakeTimeProvider time, Func<int> calls) = Create();
        using (debouncer)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(200), Delay);

            debouncer.Schedule();
            time.Advance(TimeSpan.FromMilliseconds(199));
            Assert.Equal(0, calls());

            time.Advance(TimeSpan.FromMilliseconds(1));
            Assert.Equal(1, calls());
        }
    }

    /// <summary>デバウンス窓の中の連続要求は 1 回に合流すること。</summary>
    [Fact]
    public void Schedule_CoalescesRequestsWithinTheWindow()
    {
        (SweepDebouncer debouncer, FakeTimeProvider time, Func<int> calls) = Create();
        using (debouncer)
        {
            for (int i = 0; i < 50; i++)
            {
                time.Advance(TimeSpan.FromMilliseconds(1));
                debouncer.Schedule();
            }

            time.Advance(Delay);

            Assert.Equal(1, calls());
        }
    }

    /// <summary>
    /// A14 そのもの: Reset() 後は次の Schedule() が再び受け付けられること。
    /// 修正前の TriggerMonitor は停止時に Reset 相当を行わず、ここが 1 回で止まっていた。
    /// </summary>
    [Fact]
    public void Reset_AllowsTheNextScheduleToTakeEffect()
    {
        (SweepDebouncer debouncer, FakeTimeProvider time, Func<int> calls) = Create();
        using (debouncer)
        {
            debouncer.Schedule();
            time.Advance(Delay);
            Assert.Equal(1, calls());

            // 停止相当。予約を解いておかないと次の要求が捨てられる
            debouncer.Reset();
            Assert.False(debouncer.IsScheduled);
            debouncer.Schedule();
            time.Advance(Delay);

            Assert.Equal(2, calls());
        }
    }

    /// <summary>Reset せずに再要求しても合流するだけで、二重には走らないこと。</summary>
    [Fact]
    public void Schedule_WithoutReset_DoesNotFireTwice()
    {
        (SweepDebouncer debouncer, FakeTimeProvider time, Func<int> calls) = Create();
        using (debouncer)
        {
            debouncer.Schedule();
            time.Advance(Delay);
            Assert.Equal(1, calls());

            // コールバック側が Reset を呼んでいないので予約は立ったまま
            Assert.True(debouncer.IsScheduled);
            debouncer.Schedule();
            time.Advance(Delay * 4);

            Assert.Equal(1, calls());
        }
    }

    /// <summary>解放後の予約は落とすこと (停止と同時に届いたイベントで例外を出さない)。</summary>
    [Fact]
    public void Schedule_AfterDispose_IsDropped()
    {
        (SweepDebouncer debouncer, FakeTimeProvider time, Func<int> calls) = Create();
        debouncer.Dispose();

        debouncer.Schedule();
        time.Advance(Delay * 4);

        Assert.False(debouncer.IsScheduled);
        Assert.Equal(0, calls());
    }
}
