using Microsoft.Extensions.Time.Testing;
using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 発火レート制限 (docs/DESIGN.md C11)。
///
/// 時計は TimeProvider から取るので (C10)、タイマーも実時間の待ちも使わずに検証できる。
/// </summary>
public sealed class MinIntervalGateTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    [Fact]
    public void WithoutAnInterval_EverythingPasses()
    {
        var time = new FakeTimeProvider();
        var gate = new MinIntervalGate(null, time);

        Assert.True(gate.IsUnlimited);
        for (int i = 0; i < 5; i++)
        {
            Assert.True(gate.TryPass());
            time.Advance(TimeSpan.FromMilliseconds(1));
        }
        Assert.Equal(0, gate.SuppressedCount);
    }

    /// <summary>間隔内の発火は落とし、間隔を越えたものは通すこと。</summary>
    [Fact]
    public void WithinTheInterval_FiringsAreDropped()
    {
        var time = new FakeTimeProvider();
        var gate = new MinIntervalGate(OneSecond, time);

        Assert.True(gate.TryPass());            // 初回は必ず通す (0.0s)
        Advance(time, 0.3);
        Assert.False(gate.TryPass());           // 0.3s
        Advance(time, 0.6);
        Assert.False(gate.TryPass());           // 0.9s
        Advance(time, 0.1);
        Assert.True(gate.TryPass());            // 1.0s — ちょうどで通ること
        Advance(time, 0.5);
        Assert.False(gate.TryPass());           // 1.5s
        Advance(time, 0.6);
        Assert.True(gate.TryPass());            // 2.1s

        Assert.Equal(3, gate.SuppressedCount);
    }

    /// <summary>
    /// 間隔は「最後に通した時刻」から測ること。落とした発火で時計を進めてしまうと、
    /// 変化が続いている間ずっと通らなくなる。
    /// </summary>
    [Fact]
    public void TheIntervalIsMeasuredFromTheLastPassNotTheLastAttempt()
    {
        var time = new FakeTimeProvider();
        var gate = new MinIntervalGate(OneSecond, time);

        Assert.True(gate.TryPass());
        for (int i = 1; i < 10; i++)
        {
            Advance(time, 0.1);
            Assert.False(gate.TryPass());
        }

        Advance(time, 0.15); // 合計 1.05s
        Assert.True(gate.TryPass());
    }

    /// <summary>停止・再開で間隔をまたがせないこと (再開直後の 1 回目は必ず通す)。</summary>
    [Fact]
    public void Reset_LetsTheNextFiringThrough()
    {
        var time = new FakeTimeProvider();
        var gate = new MinIntervalGate(TimeSpan.FromMinutes(10), time);

        Assert.True(gate.TryPass());
        Advance(time, 0.001);
        Assert.False(gate.TryPass());

        gate.Reset();

        Advance(time, 0.001);
        Assert.True(gate.TryPass());
        Assert.Equal(0, gate.SuppressedCount);
    }

    [Fact]
    public void ZeroOrNegativeIntervalsAreTreatedAsUnlimited()
    {
        var time = new FakeTimeProvider();

        Assert.True(new MinIntervalGate(TimeSpan.Zero, time).IsUnlimited);
        Assert.True(new MinIntervalGate(TimeSpan.FromSeconds(-1), time).IsUnlimited);
    }

    /// <summary>
    /// ホストが差し替えた時計に従うこと。
    /// 実時間が 1 秒進んでも、注入した時計が止まっていれば制限は解けない — これが成り立たないと
    /// 「TimeProvider を受け取っているが内部では Stopwatch を読んでいる」に戻ったことに気づけない。
    /// </summary>
    [Fact]
    public void TheGateFollowsTheInjectedClockNotTheWallClock()
    {
        var time = new FakeTimeProvider();
        var gate = new MinIntervalGate(TimeSpan.FromMilliseconds(50), time);

        Assert.True(gate.TryPass());
        Thread.Sleep(120); // 実時間では間隔を越えている
        Assert.False(gate.TryPass());

        Advance(time, 0.05);
        Assert.True(gate.TryPass());
    }

    private static void Advance(FakeTimeProvider time, double seconds) =>
        time.Advance(TimeSpan.FromSeconds(seconds));
}
