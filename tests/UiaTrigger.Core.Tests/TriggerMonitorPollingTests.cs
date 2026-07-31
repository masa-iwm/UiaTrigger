using Microsoft.Extensions.Time.Testing;
using UiaTrigger.Models;
using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 利用者が指定したポーリングが <see cref="TriggerMonitor"/> に配線されていること
/// (docs/DESIGN.md §5)。
///
/// <para>
/// ここで使う定義は存在しないプロセスを指すので**解決はしない**。それでよい —
/// この層で確かめたいのは「頼まれなければ回らない」「頼まれたら回る」「回っても掃引はしない」
/// という**形**であり、どれも要素を必要としない。むしろ未解決のまま回すことで、
/// 「読みは解決済みスロットにしか走らない」というネガティブコントロールが同時に取れる。
/// 実際に値を読み直して発火する経路は実 UIA (T3) の担当である。
/// </para>
/// <para>
/// 時計は <c>FakeTimeProvider</c>。**実時間を一切待たない。**
/// <c>Advance</c> がタイマーのコールバックを同期に呼び、そのコールバックは
/// UIA スレッドへ 1 件投げる。投げ先は FIFO なので、そのあとに
/// <see cref="TriggerMonitor.GetTriggerIdsAsync"/> を待てば**周が終わったことが保証される**
/// (待ち合わせであって、時間待ちではない)。
/// </para>
/// </summary>
[Collection(RealUiaLiteTests.Name)]
public sealed class TriggerMonitorPollingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    /// <summary>絶対に解決しないウィンドウ。ポーリングの形だけを見たいので、要素は要らない。</summary>
    private static WindowIdentity NoSuchWindow => new() { ProcessName = "no-such-process-uiatrigger.exe" };

    private static TriggerDefinition Definition(string id, TimeSpan? pollInterval) => new()
    {
        Id = id,
        Window = NoSuchWindow,
        On = TriggerOn.WhileMatching,
        PollInterval = pollInterval,
        Clauses = [new PropertyClause { Property = TriggerProperty.Name, Op = ComparisonOp.Always }],
    };

    private static (TriggerMonitor Monitor, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider();
        var monitor = new TriggerMonitor(new TriggerMonitorOptions
        {
            Session = new UiaSessionOptions { TimeProvider = time },
        });
        return (monitor, time);
    }

    /// <summary>
    /// 時計を進め、その周が終わりきるまで待つ。
    ///
    /// <c>Advance</c> はタイマーのコールバックを同期に呼ぶので、戻った時点で
    /// 1 周ぶんの仕事は**キューに入っている**。同じキューへ後から投げたものを待てば、
    /// FIFO により前のものは済んでいる。
    /// </summary>
    private static async Task AdvanceAndDrain(TriggerMonitor monitor, FakeTimeProvider time, TimeSpan by)
    {
        time.Advance(by);
        await monitor.GetTriggerIdsAsync(Ct);
    }

    /// <summary>
    /// **頼まなければ 1 周も回らないこと。**
    ///
    /// <para>
    /// これがこの機能の中心にある約束である。<c>PollInterval</c> を指定しなければ、
    /// どれだけ時間が経っても**タイマーは 1 本も動かない**。
    /// ライブラリが自分の判断でポーリングしない、という性質はここで観測できる形になっている
    /// (<see cref="SubscriptionRepairTests.WithoutScheduling_NothingEverFires"/> と対になる検査)。
    /// </para>
    /// <para>
    /// **この誤りは緑では気づけない。**監視は動いたままで、増えるのは無駄な往復だけだからである。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task WithoutBeingAsked_NothingIsEverPolled(int? milliseconds)
    {
        (TriggerMonitor monitor, FakeTimeProvider time) = Create();
        await using (monitor)
        {
            TimeSpan? interval = milliseconds is { } ms ? TimeSpan.FromMilliseconds(ms) : null;
            await monitor.StartAsync([Definition("t", interval)], Ct);

            await AdvanceAndDrain(monitor, time, TimeSpan.FromMinutes(10));

            TriggerMonitorDiagnostics diagnostics = monitor.GetDiagnostics();
            Assert.Equal(0, diagnostics.PollCount);
            Assert.Equal(0, diagnostics.PolledReadCount);
        }
    }

    /// <summary>頼めば、指定した間隔で回ること。</summary>
    [Fact]
    public async Task WhenAsked_ItPollsOnTheGivenInterval()
    {
        (TriggerMonitor monitor, FakeTimeProvider time) = Create();
        await using (monitor)
        {
            await monitor.StartAsync([Definition("t", Interval)], Ct);
            Assert.Equal(0, monitor.GetDiagnostics().PollCount);

            for (int round = 1; round <= 3; round++)
            {
                await AdvanceAndDrain(monitor, time, Interval);
                Assert.Equal(round, monitor.GetDiagnostics().PollCount);
            }
        }
    }

    /// <summary>
    /// **ポーリングは掃引を回さないこと。**
    ///
    /// <para>
    /// 掃引は全ウィンドウ列挙と <c>OpenProcess</c> を伴い (docs/DESIGN.md B4)、
    /// ピッカーと共有している MTA スレッドの上で相手ごとに止まりうる。
    /// タイマーからそれを叩けば**解決の経路そのものをライブラリの判断でポーリングする**ことになり、
    /// この機能が引いた線を跨ぐ。読み直すのは解決済みの要素だけである。
    /// </para>
    /// <para>
    /// **ここが言っているのは「解決済みのまま推移するかぎり」である。**
    /// 周の途中で要素が消えれば掃引は予約される (それは意図した経路であり、
    /// 要素が消えたことに気づくための唯一の道である)。この定義は 1 度も解決しないので、
    /// その経路には入らない。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Polling_DoesNotDriveResolution()
    {
        (TriggerMonitor monitor, FakeTimeProvider time) = Create();
        await using (monitor)
        {
            await monitor.StartAsync([Definition("t", Interval)], Ct);
            int sweepsBefore = monitor.GetDiagnostics().SweepCount;

            for (int round = 0; round < 5; round++)
            {
                await AdvanceAndDrain(monitor, time, Interval);
            }

            TriggerMonitorDiagnostics diagnostics = monitor.GetDiagnostics();
            Assert.Equal(5, diagnostics.PollCount);
            Assert.Equal(sweepsBefore, diagnostics.SweepCount);
        }
    }

    /// <summary>
    /// **ポーリングを頼んだトリガーは「孤児」ではないこと。**
    ///
    /// <para>
    /// <see cref="SubscriptionRepair"/> の復旧の経路と、利用者が頼んだポーリングは**別のものである**。
    /// 前者はライブラリが自分の判断で回すので「壊れている間だけ」という厳しい条件が要り (docs/DESIGN.md §8)、
    /// 後者は利用者が頼んだ費用なので条件が要らない。似ているからといって片方をもう片方の
    /// 仕組みに載せると、**ポーリングを頼んだだけで復旧の再試行が armed になり、掃引が回りはじめる**。
    /// </para>
    /// <para>
    /// **この主張を名指しで固定しておく。**実際にやってみると
    /// <see cref="Polling_DoesNotDriveResolution"/> のほうが先に落ちる (掃引が増えるため) が、
    /// あれが落ちるのは**結果**であって、壊れたのがこの区別であることは名前からは分からない。
    /// 再購読 (docs/DESIGN.md §8) 側の検査はここを守らない — あちらが縛っているのは <c>IsOrphaned</c> の定義であり、
    /// ポーリングを使わない定義で回るからである。
    /// </para>
    /// </summary>
    [Fact]
    public async Task APolledTrigger_IsNotCountedAsOrphaned()
    {
        (TriggerMonitor monitor, FakeTimeProvider time) = Create();
        await using (monitor)
        {
            await monitor.StartAsync([Definition("t", Interval)], Ct);

            await AdvanceAndDrain(monitor, time, Interval);

            TriggerMonitorDiagnostics diagnostics = monitor.GetDiagnostics();
            Assert.Equal(1, diagnostics.PollCount);
            Assert.Equal(0, diagnostics.OrphanedTriggerCount);
        }
    }

    /// <summary>
    /// **未解決のスロットは読まないこと** (周は回っても往復は 0 のまま)。
    ///
    /// 費用を「周」ではなく「読み」で数えている理由がこれである。周の数だけを見ていると、
    /// 相手が起動していない間も費用が掛かっているように見えてしまう。
    /// </summary>
    [Fact]
    public async Task WhileNothingIsResolved_NoCrossProcessReadHappens()
    {
        (TriggerMonitor monitor, FakeTimeProvider time) = Create();
        await using (monitor)
        {
            await monitor.StartAsync([Definition("t", Interval)], Ct);

            for (int round = 0; round < 3; round++)
            {
                await AdvanceAndDrain(monitor, time, Interval);
            }

            TriggerMonitorDiagnostics diagnostics = monitor.GetDiagnostics();
            Assert.Equal(3, diagnostics.PollCount);
            Assert.Equal(0, diagnostics.PolledReadCount);
        }
    }

    /// <summary>
    /// 頼んだトリガーだけが回ること (頼んでいない隣のトリガーは巻き込まれない)。
    ///
    /// 粒度をトリガー単位にした意味がここにある。「壊れているアプリのトリガーだけ」
    /// ポーリングでき、費用が要素数全体に比例しない。
    /// </summary>
    [Fact]
    public async Task OnlyTheTriggersThatAskedArePolled()
    {
        (TriggerMonitor monitor, FakeTimeProvider time) = Create();
        await using (monitor)
        {
            await monitor.StartAsync(
                [Definition("polled", Interval), Definition("quiet", null), Definition("also-quiet", TimeSpan.Zero)],
                Ct);

            await AdvanceAndDrain(monitor, time, Interval);

            // 3 件のうち 1 件だけが回っている
            Assert.Equal(1, monitor.GetDiagnostics().PollCount);
        }
    }

    /// <summary>
    /// **停止でポーリングが止まり、再開でまた張られること。**
    ///
    /// デバウンス予約を停止時に解いていないと再開後の掃引が黙って捨てられる、という
    /// A14 と同じ形の落とし穴である。こちらは向きが逆で、**止めたのに回り続ける**ほうが危ない。
    /// </summary>
    [Fact]
    public async Task StopStopsPolling_AndStartPutsItBack()
    {
        (TriggerMonitor monitor, FakeTimeProvider time) = Create();
        await using (monitor)
        {
            await monitor.StartAsync([Definition("t", Interval)], Ct);
            await AdvanceAndDrain(monitor, time, Interval);
            long afterFirstRun = monitor.GetDiagnostics().PollCount;
            Assert.Equal(1, afterFirstRun);

            await monitor.StopAsync(Ct);
            await AdvanceAndDrain(monitor, time, TimeSpan.FromMinutes(10));

            Assert.Equal(afterFirstRun, monitor.GetDiagnostics().PollCount);

            await monitor.StartAsync([Definition("t", Interval)], Ct);
            await AdvanceAndDrain(monitor, time, Interval);

            Assert.Equal(afterFirstRun + 1, monitor.GetDiagnostics().PollCount);
        }
    }

    /// <summary>削除したトリガーのポーリングが止まること。</summary>
    [Fact]
    public async Task RemovingATrigger_StopsItsPolling()
    {
        (TriggerMonitor monitor, FakeTimeProvider time) = Create();
        await using (monitor)
        {
            await monitor.StartAsync([Definition("t", Interval)], Ct);
            await AdvanceAndDrain(monitor, time, Interval);
            long before = monitor.GetDiagnostics().PollCount;

            Assert.True(await monitor.RemoveAsync("t", Ct));
            await AdvanceAndDrain(monitor, time, TimeSpan.FromMinutes(10));

            Assert.Equal(before, monitor.GetDiagnostics().PollCount);
        }
    }

    /// <summary>開始後に足したトリガーもポーリングされること。</summary>
    [Fact]
    public async Task AddAsync_WhileRunning_StartsPollingToo()
    {
        (TriggerMonitor monitor, FakeTimeProvider time) = Create();
        await using (monitor)
        {
            await monitor.StartAsync(cancellationToken: Ct);
            await monitor.AddAsync(Definition("late", Interval), Ct);

            await AdvanceAndDrain(monitor, time, Interval);

            Assert.Equal(1, monitor.GetDiagnostics().PollCount);
        }
    }

    [Fact]
    public async Task ANegativePollInterval_IsRejected()
    {
        await using var monitor = new TriggerMonitor();

        Exception? error = await Record.ExceptionAsync(
            () => monitor.StartAsync([Definition("t", TimeSpan.FromMilliseconds(-1))], Ct));

        Assert.IsType<ArgumentException>(error);
    }

    /// <summary>
    /// **効かない設定を黙って受け取らないこと。**
    ///
    /// <see cref="TriggerOn.ElementAppeared"/> / <see cref="TriggerOn.ElementRemoved"/> は
    /// プロパティの変化ではなく解決・消滅で鳴る。ポーリングが読み直すのは**解決済み**の
    /// 要素だけで、解決そのものは回さないので、この組み合わせは何も起こさない。
    /// 黙って受け取ると「指定したのに鳴らない」として最も長く隠れる
    /// (<c>Error_UnreferencedClause</c> と同じ規律)。
    /// </summary>
    [Theory]
    [InlineData(TriggerOn.ElementAppeared)]
    [InlineData(TriggerOn.ElementRemoved)]
    public async Task APollIntervalThatCouldNotDoAnything_IsRejected(TriggerOn on)
    {
        await using var monitor = new TriggerMonitor();
        var definition = new TriggerDefinition
        {
            Id = "t",
            Window = NoSuchWindow,
            On = on,
            PollInterval = Interval,
        };

        Exception? error = await Record.ExceptionAsync(() => monitor.StartAsync([definition], Ct));

        Assert.IsType<ArgumentException>(error);
    }

    /// <summary>
    /// 0 は「間隔 0 でポーリングする」ではなく「ポーリングしない」なので、
    /// 上の組み合わせでも**拒否されない**こと。
    /// </summary>
    [Fact]
    public async Task AZeroPollInterval_IsAcceptedEverywhere()
    {
        await using var monitor = new TriggerMonitor();
        var definition = new TriggerDefinition
        {
            Id = "t",
            Window = NoSuchWindow,
            On = TriggerOn.ElementAppeared,
            PollInterval = TimeSpan.Zero,
        };

        await monitor.StartAsync([definition], Ct);

        Assert.Equal(["t"], await monitor.GetTriggerIdsAsync(Ct));
    }
}
