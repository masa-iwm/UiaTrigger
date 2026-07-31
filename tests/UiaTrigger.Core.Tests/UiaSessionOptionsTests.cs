using Microsoft.Extensions.Time.Testing;
using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// B5 の設定口がセッション側にあること、そして設定ミスが **呼び出し元スレッドで** 弾かれること。
///
/// 呼び出し上限時間の設定が <c>TriggerMonitorOptions</c> 経由だけだと、
/// ピッカー / インスペクタ経路は既定値しか使えない。1 セッション = 1 設定にまとめてある。
/// </summary>
public sealed class UiaSessionOptionsTests
{
    [Fact]
    public void Defaults_AreTheValuesChosenForHeadOfLineBlocking()
    {
        var options = new UiaSessionOptions();

        // UIA 自身の既定は 20 秒。単一スレッドを 20 秒塞がれては困るので短くしてある
        Assert.Equal(TimeSpan.FromSeconds(5), options.TransactionTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), options.ConnectionTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), UiaSessionOptions.DefaultTransactionTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), UiaSessionOptions.DefaultConnectionTimeout);
        Assert.Same(TimeProvider.System, options.TimeProvider);
        Assert.Null(options.Logger);
        Assert.True(options.SkipOwnProcessElements);
    }

    /// <summary>
    /// 無効な値はセッションを作った時点で例外になること。
    /// UIA が受け付けるのは正のミリ秒だけであり、0 やマイナスを黙って渡すと
    /// 「タイムアウトを設定したつもりで効いていない」状態になる。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Construction_WithANonPositiveTimeout_Throws(int seconds)
    {
        var options = new UiaSessionOptions { TransactionTimeout = TimeSpan.FromSeconds(seconds) };

        Assert.Throws<ArgumentOutOfRangeException>(() => new UiaSession(options));
    }

    [Fact]
    public void Construction_WithATimeoutBeyondWhatUiaAccepts_Throws()
    {
        var options = new UiaSessionOptions
        {
            // UIA は uint のミリ秒しか受け取れない
            ConnectionTimeout = TimeSpan.FromMilliseconds((double)uint.MaxValue + 1),
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new UiaSession(options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Construction_WithANonPositiveLimit_Throws(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiaSession(new UiaSessionOptions { MaxChildren = value }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiaSession(new UiaSessionOptions { MaxDepth = value }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiaSession(new UiaSessionOptions { MaxOverlapWindows = value }));
    }

    /// <summary>
    /// 単体で作った monitor は options.Session の設定でセッションを作ること
    /// (ここが効かないと、B5 の設定口が単体利用のホストから消える)。
    /// </summary>
    [Fact]
    public async Task TriggerMonitor_UsesTheSessionOptionsItWasGiven()
    {
        var time = new FakeTimeProvider();
        await using var monitor = new TriggerMonitor(new TriggerMonitorOptions
        {
            Session = new UiaSessionOptions { TimeProvider = time, ThreadName = "test-session" },
        });

        Assert.Same(time, monitor.Session.TimeProvider);
    }

    /// <summary>
    /// 単体で作った monitor の設定ミスも、コンストラクタの時点で弾かれること
    /// (StartAsync まで遅れると「なぜか動かない」に化ける)。
    /// </summary>
    [Fact]
    public void TriggerMonitor_WithAnInvalidSessionTimeout_ThrowsOnConstruction()
    {
        var options = new TriggerMonitorOptions
        {
            Session = new UiaSessionOptions { TransactionTimeout = TimeSpan.Zero },
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new TriggerMonitor(options));
    }

    /// <summary>
    /// 発火時刻は注入した時計から取ること (docs/DESIGN.md C10)。
    /// ライブラリが <c>DateTimeOffset.Now</c> を読んでいると、ホストの時計と食い違い
    /// UTC でもないので機械間で比較できない。
    /// </summary>
    [Fact]
    public void TriggerFiredEventArgs_TimestampDefaultsToUnsetNotToNow()
    {
        var args = new TriggerFiredEventArgs { TriggerId = "t", On = Models.TriggerOn.ElementAppeared };

        // 既定値を持たない = 必ず monitor 側が TimeProvider から入れる
        Assert.Equal(default, args.Timestamp);
    }
}
