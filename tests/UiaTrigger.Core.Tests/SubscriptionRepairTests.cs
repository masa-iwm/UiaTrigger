using Microsoft.Extensions.Time.Testing;
using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 購読を失ったトリガーを拾い直す再試行の回帰テスト (docs/DESIGN.md §8)。
///
/// <para>
/// <see cref="SweepDebouncer"/> と同じく <c>TimeProvider</c> を差し替えるので、
/// <b>実時間を一切待たない</b>。間隔と backoff は待って確かめると
/// 「待ち時間を延ばしただけのテスト」になり、値そのものを固定できない。
/// </para>
/// <para>
/// ここで見ているのは<b>ペース配分</b>だけである。「購読が失われたら実際に張り直る」ほうは、
/// 相手が応答しない状況を注入する継ぎ目が無いので T1 では書けない。
/// </para>
/// </summary>
public sealed class SubscriptionRepairTests
{
    private static readonly TimeSpan Initial = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(30);

    private static (SubscriptionRepair Repair, FakeTimeProvider Time, Func<int> Calls) Create()
    {
        var time = new FakeTimeProvider();
        int calls = 0;
        var repair = new SubscriptionRepair(Initial, Max, () => Interlocked.Increment(ref calls), time);
        return (repair, time, () => Volatile.Read(ref calls));
    }

    /// <summary>
    /// <b>予約しなければ何も起きないこと。</b>
    ///
    /// これが「ポーリングしない」の中身である。時間だけ進めても鳴ってはいけない。
    /// </summary>
    [Fact]
    public void WithoutScheduling_NothingEverFires()
    {
        (SubscriptionRepair repair, FakeTimeProvider time, Func<int> calls) = Create();
        using (repair)
        {
            Assert.False(repair.IsScheduled);

            time.Advance(TimeSpan.FromMinutes(10));

            Assert.Equal(0, calls());
        }
    }

    [Fact]
    public void Schedule_FiresAfterTheInitialInterval()
    {
        (SubscriptionRepair repair, FakeTimeProvider time, Func<int> calls) = Create();
        using (repair)
        {
            repair.Schedule();
            Assert.True(repair.IsScheduled);

            // 絶対値で書く。定数から引き算で作ると、間隔を変えても素通りする
            time.Advance(TimeSpan.FromMilliseconds(1999));
            Assert.Equal(0, calls());

            time.Advance(TimeSpan.FromMilliseconds(1));
            Assert.Equal(1, calls());
        }
    }

    /// <summary>
    /// 鳴るたびに間隔が倍になり、上限で頭打ちになること。
    ///
    /// <para>
    /// 再試行 1 回は掃引 1 回で、掃引はピッカーと共有している UIA スレッドの上で
    /// 相手ごとに待たされうる。<b>塞がったままの相手を 2 秒ごとに叩き続けない</b>ための
    /// 性質なので、間隔そのものを固定する。
    /// </para>
    /// </summary>
    [Fact]
    public void EachRetry_WidensTheIntervalUpToTheCap()
    {
        (SubscriptionRepair repair, FakeTimeProvider time, Func<int> calls) = Create();
        using (repair)
        {
            Assert.Equal(TimeSpan.FromSeconds(2), repair.NextInterval);

            foreach (TimeSpan expected in new[]
            {
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(16),
                TimeSpan.FromSeconds(30),  // 32 ではなく上限
                TimeSpan.FromSeconds(30),  // 以後は動かない
            })
            {
                repair.Schedule();
                time.Advance(repair.NextInterval);
                Assert.Equal(expected, repair.NextInterval);
            }

            Assert.Equal(5, calls());
        }
    }

    /// <summary>
    /// 復旧したら間隔が最初へ戻ること。
    ///
    /// 戻さないと、一度長く塞がれたあとは<b>次に壊れたときの復旧まで 30 秒待つ</b>ことになる。
    /// </summary>
    [Fact]
    public void Recovered_PutsTheIntervalBackToTheStart()
    {
        (SubscriptionRepair repair, FakeTimeProvider time, Func<int> _) = Create();
        using (repair)
        {
            repair.Schedule();
            time.Advance(Initial);
            repair.Schedule();
            time.Advance(repair.NextInterval);
            Assert.Equal(TimeSpan.FromSeconds(8), repair.NextInterval);

            repair.Recovered();

            Assert.Equal(Initial, repair.NextInterval);
            Assert.False(repair.IsScheduled);
        }
    }

    /// <summary>予約済みのあいだに重ねて呼んでも 1 回にまとまること。</summary>
    [Fact]
    public void Schedule_WhileAlreadyScheduled_DoesNotStack()
    {
        (SubscriptionRepair repair, FakeTimeProvider time, Func<int> calls) = Create();
        using (repair)
        {
            repair.Schedule();
            repair.Schedule();
            repair.Schedule();

            time.Advance(Initial);

            Assert.Equal(1, calls());
        }
    }

    /// <summary>
    /// 解放後の予約が鳴らないこと (停止と同時に来た予約で <c>ITimer</c> を触らない)。
    /// </summary>
    [Fact]
    public void Schedule_AfterDispose_IsIgnored()
    {
        (SubscriptionRepair repair, FakeTimeProvider time, Func<int> calls) = Create();
        repair.Dispose();

        repair.Schedule();
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.False(repair.IsScheduled);
        Assert.Equal(0, calls());
    }
}
