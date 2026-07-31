using Microsoft.Extensions.Time.Testing;
using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 利用者が指定したポーリングのペース配分 (docs/DESIGN.md §5)。
///
/// <para>
/// <see cref="SubscriptionRepairTests"/> と同じく <c>TimeProvider</c> を差し替えるので、
/// <b>実時間を一切待たない</b>。間隔を待って確かめると「待ち時間を延ばしただけのテスト」になり、
/// 値そのものを固定できない。
/// </para>
/// <para>
/// この 2 つは形が似ているが別のものである。<see cref="SubscriptionRepair"/> は
/// <b>ライブラリが自分の判断で</b>回す復旧の経路なので「壊れている間だけ」という厳しい条件が要り、
/// backoff も要る。<see cref="SlotPoller"/> は<b>利用者が頼んだ費用</b>なので、
/// 頼まれなければそもそも生成されず、間隔も利用者が決めたまま動かさない。
/// </para>
/// </summary>
public sealed class SlotPollerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private static (SlotPoller Poller, FakeTimeProvider Time, Func<int> Calls) Create()
    {
        var time = new FakeTimeProvider();
        int calls = 0;
        var poller = new SlotPoller(Interval, () => Interlocked.Increment(ref calls), time);
        return (poller, time, () => Volatile.Read(ref calls));
    }

    /// <summary>
    /// <b>予約しなければ何も起きないこと。</b>
    ///
    /// 生成しただけのポーラーは動かない。武装するのは呼び出し元が決める。
    /// </summary>
    [Fact]
    public void WithoutScheduling_NothingEverFires()
    {
        (SlotPoller poller, FakeTimeProvider time, Func<int> calls) = Create();
        using (poller)
        {
            Assert.False(poller.IsScheduled);

            time.Advance(TimeSpan.FromMinutes(10));

            Assert.Equal(0, calls());
        }
    }

    /// <summary>
    /// 間隔ちょうどで鳴ること。
    ///
    /// 絶対値で書く。定数から引き算で作ると、間隔を変えても素通りする。
    /// </summary>
    [Fact]
    public void Schedule_FiresAfterTheInterval()
    {
        (SlotPoller poller, FakeTimeProvider time, Func<int> calls) = Create();
        using (poller)
        {
            poller.Schedule();
            Assert.True(poller.IsScheduled);

            time.Advance(TimeSpan.FromMilliseconds(499));
            Assert.Equal(0, calls());

            time.Advance(TimeSpan.FromMilliseconds(1));
            Assert.Equal(1, calls());
        }
    }

    /// <summary>
    /// <b>間隔は毎周同じであること</b> (<see cref="SubscriptionRepair"/> と違って伸びない)。
    ///
    /// あちらの backoff は「塞がった相手を叩き続けない」ためのものだが、
    /// こちらは利用者が指定した間隔そのものが約束である。勝手に伸ばしてはいけない。
    /// </summary>
    [Fact]
    public void EachRound_UsesTheSameInterval()
    {
        (SlotPoller poller, FakeTimeProvider time, Func<int> calls) = Create();
        using (poller)
        {
            for (int round = 1; round <= 5; round++)
            {
                poller.Reset();
                poller.Schedule();
                time.Advance(Interval);
                Assert.Equal(round, calls());
            }

            Assert.Equal(Interval, poller.Interval);
        }
    }

    /// <summary>
    /// <b>1 周ぶんの仕事が終わるまで次の周は始まらないこと。</b>
    ///
    /// <para>
    /// 周期タイマーにしていない理由がこれである。<c>Reset</c> を呼ぶまで再武装できないので、
    /// 相手が <c>TransactionTimeout</c> ぶん止まっていても周が積み上がらない。
    /// ここが崩れると、機械が苦しいときほど激しく叩く形になる。
    /// </para>
    /// </summary>
    [Fact]
    public void WithoutResetting_TheNextRoundDoesNotStack()
    {
        (SlotPoller poller, FakeTimeProvider time, Func<int> calls) = Create();
        using (poller)
        {
            poller.Schedule();
            time.Advance(Interval);
            Assert.Equal(1, calls());

            // 1 周目の仕事がまだ終わっていない = Reset していない。
            // 何度 Schedule しても、どれだけ時間が経っても 2 周目は始まらない
            poller.Schedule();
            poller.Schedule();
            time.Advance(TimeSpan.FromMinutes(10));

            Assert.Equal(1, calls());

            // 仕事が終わって初めて次が張れる
            poller.Reset();
            poller.Schedule();
            time.Advance(Interval);

            Assert.Equal(2, calls());
        }
    }

    /// <summary>予約済みのあいだに重ねて呼んでも 1 回にまとまること。</summary>
    [Fact]
    public void Schedule_WhileAlreadyScheduled_DoesNotStack()
    {
        (SlotPoller poller, FakeTimeProvider time, Func<int> calls) = Create();
        using (poller)
        {
            poller.Schedule();
            poller.Schedule();
            poller.Schedule();

            time.Advance(Interval);

            Assert.Equal(1, calls());
        }
    }

    /// <summary>
    /// 解放後の予約が鳴らないこと (停止と同時に来た予約で <c>ITimer</c> を触らない)。
    /// </summary>
    [Fact]
    public void Schedule_AfterDispose_IsIgnored()
    {
        (SlotPoller poller, FakeTimeProvider time, Func<int> calls) = Create();
        poller.Dispose();

        poller.Schedule();
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.False(poller.IsScheduled);
        Assert.Equal(0, calls());
    }

    /// <summary>
    /// <b>間隔 0 や負値ではそもそも作れないこと。</b>
    ///
    /// 0 は「間隔 0 でポーリングする」ではなく「ポーリングしない」である。
    /// その 2 つを取り違えると、既定のトリガーが全力で回る形になる。
    /// 呼び出し元が 0 を弾く約束を、型の側でも留めておく。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnIntervalThatIsNotPositive_IsRejected(int milliseconds)
    {
        var time = new FakeTimeProvider();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SlotPoller(TimeSpan.FromMilliseconds(milliseconds), () => { }, time));
    }
}
