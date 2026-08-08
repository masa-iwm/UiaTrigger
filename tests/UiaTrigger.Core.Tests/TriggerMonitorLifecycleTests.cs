using UiaTrigger.Models;
using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// C1: トリガーを動かしたまま増減できること。
///
/// 一発起動しか無い形だと、1 件足すには全停止 → 全再開が必要になる。
/// しかも停止が <c>RemoveAllEventHandlers()</c> を呼ぶ形だと、うまく動いている購読まで
/// まとめて捨ててしまう。
///
/// ここで使う定義は存在しないプロセスを指すので解決はしない。増減そのものと、
/// **他のトリガーに触らないこと**だけを見る (実際の発火は T3)。
/// </summary>
[Collection(RealUiaLiteTests.Name)]
public sealed class TriggerMonitorLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TriggerDefinition Definition(string id) => new()
    {
        Id = id,
        // 実在しないプロセス。解決には至らないが、購読の張り替えと診断は動く
        Window = new WindowIdentity { ProcessName = "no-such-process-uiatrigger.exe" },
        On = TriggerOn.ElementAppeared,
    };

    [Fact]
    public async Task AddAsync_WhileRunning_LeavesTheExistingTriggersAlone()
    {
        await using var monitor = new TriggerMonitor();
        await monitor.StartAsync([Definition("a"), Definition("b")], Ct);

        await monitor.AddAsync(Definition("c"), Ct);

        Assert.Equal(["a", "b", "c"], (await monitor.GetTriggerIdsAsync(Ct)).Order(StringComparer.Ordinal));
        Assert.Equal(3, monitor.GetDiagnostics().TriggerCount);
    }

    /// <summary>開始前に足したトリガーも、開始時にまとめて解決対象になること。</summary>
    [Fact]
    public async Task AddAsync_BeforeStart_IsPickedUpByStart()
    {
        await using var monitor = new TriggerMonitor();
        await monitor.AddAsync(Definition("early"), Ct);

        await monitor.StartAsync([Definition("late")], Ct);

        Assert.Equal(["early", "late"], (await monitor.GetTriggerIdsAsync(Ct)).Order(StringComparer.Ordinal));
        Assert.True(monitor.GetDiagnostics().IsRunning);
    }

    /// <summary>
    /// **まだ起動していないアプリを待っているトリガーは「孤立」ではないこと。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// ここが崩れると、復旧の再試行 (<c>SubscriptionRepair</c>) が回りっぱなしになり、
    /// **イベント駆動でポーリングしないという設計の売りが静かに消える**
    /// (docs/DESIGN.md §8 / §1)。
    /// </para>
    /// <para>
    /// **この誤りは緑では気づけない。**監視は動いたままで、増えるのは
    /// 無駄な掃引だけだからである。だからここで名指しで固定する。
    /// この定義は実在しないプロセスを指すので、ウィンドウは 1 つも見つからない =
    /// 購読する先が無い = 孤立ではない (出現は <c>WindowOpened</c> が拾う)。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WhileWaitingForAnApplicationThatIsNotRunning_NoTriggerCountsAsOrphaned()
    {
        await using var monitor = new TriggerMonitor();
        await monitor.StartAsync([Definition("waiting")], Ct);

        TriggerMonitorDiagnostics diagnostics = monitor.GetDiagnostics();

        Assert.Equal(0, diagnostics.ResolvedTriggerCount);
        Assert.Equal(0, diagnostics.StructureSubscriptionCount);
        Assert.Equal(0, diagnostics.OrphanedTriggerCount);
    }

    /// <summary>
    /// **StartAsync の失敗は監視を開始前と完全に同一の状態へ戻すこと** (docs/DESIGN.md A29)。
    ///
    /// <para>
    /// 開始前に AddAsync した id と StartAsync の引数が衝突する形は、呼び出し元スレッドの
    /// 検証 (引数内の重複しか見ない) を通過して StartCore まで届く。ここで
    /// 「_started=true を確定してから登録が落ちる」実装だと、IsRunning=true・
    /// ルート購読 0 本・一部トリガーだけ登録の**半開始状態に固着**し、
    /// 再 StartAsync は AlreadyStarted で拒否される — 稼働中を名乗る死体である。
    /// 公開 doc の「cancellation never leaves monitoring half-started」の精神は
    /// 例外経路にも適用される。
    /// </para>
    /// </summary>
    [Fact]
    public async Task StartAsync_WhenItCollidesWithAPreAddedId_RollsBackToNotStarted()
    {
        await using var monitor = new TriggerMonitor();
        await monitor.AddAsync(Definition("t1"), Ct);

        // 同じ id での開始は失敗する (呼び出し元検証は引数内重複しか見ないので StartCore まで届く)
        Exception? error = await Record.ExceptionAsync(() => monitor.StartAsync([Definition("t1")], Ct));
        Assert.IsType<ArgumentException>(error);

        // 失敗した work item は状態を巻き戻している — IsRunning は false のまま
        Assert.False(monitor.GetDiagnostics().IsRunning);

        // そして**再開始できる** (半開始で固着した実装では AlreadyStarted になる)
        await monitor.StartAsync([Definition("t2")], Ct);
        Assert.True(monitor.GetDiagnostics().IsRunning);
        Assert.Equal(["t1", "t2"], (await monitor.GetTriggerIdsAsync(Ct)).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// 破棄後の公開 API の挙動が 1 通り (ObjectDisposedException) に揃っていること。
    /// ガードの顔ぶれが割れていると、破棄後の挙動がディスパッチャの生死次第で
    /// 「完了する / internal 型名を晒す faulted Task になる」に分かれる。
    /// **共有セッションで見る** — ディスパッチャが生き残る構成でだけ、ガード無しの
    /// StopAsync / GetTriggerIdsAsync が「普通に完了する」側へ割れる (所有セッションでは
    /// ディスパッチャごと死ぬので、ガードが無くても偶然 ODE になる)。
    /// </summary>
    [Fact]
    public async Task AfterDispose_EveryPublicCallReportsObjectDisposed()
    {
        await using var session = new UiaSession();
        TriggerMonitor monitor = session.CreateMonitor();
        await monitor.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.StartAsync(null, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.AddAsync(Definition("a"), Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.RemoveAsync("a", Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.StopAsync(Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.GetTriggerIdsAsync(Ct));
    }

    [Fact]
    public async Task AddAsync_WithADuplicateId_Throws()
    {
        await using var monitor = new TriggerMonitor();
        await monitor.StartAsync([Definition("a")], Ct);

        Exception? error = await Record.ExceptionAsync(() => monitor.AddAsync(Definition("a"), Ct));

        Assert.IsType<ArgumentException>(error);
        Assert.Single(await monitor.GetTriggerIdsAsync(Ct));
    }

    /// <summary>検証は呼び出し元スレッドで済み、無効な定義は監視対象に入らないこと。</summary>
    [Fact]
    public async Task AddAsync_WithAnInvalidDefinition_ThrowsAndAddsNothing()
    {
        await using var monitor = new TriggerMonitor();
        await monitor.StartAsync([Definition("a")], Ct);

        var broken = new TriggerDefinition
        {
            Id = "broken",
            Window = new WindowIdentity { ProcessName = string.Empty }, // Required なのに空
            On = TriggerOn.ElementAppeared,
        };
        Exception? error = await Record.ExceptionAsync(() => monitor.AddAsync(broken, Ct));

        Assert.IsType<ArgumentException>(error);
        Assert.Equal(["a"], await monitor.GetTriggerIdsAsync(Ct));
    }

    [Fact]
    public async Task RemoveAsync_TakesOutOnlyThatTrigger()
    {
        await using var monitor = new TriggerMonitor();
        await monitor.StartAsync([Definition("a"), Definition("b"), Definition("c")], Ct);

        Assert.True(await monitor.RemoveAsync("b", Ct));

        Assert.Equal(["a", "c"], (await monitor.GetTriggerIdsAsync(Ct)).Order(StringComparer.Ordinal));
        Assert.True(monitor.GetDiagnostics().IsRunning);
    }

    [Fact]
    public async Task RemoveAsync_ForAnUnknownId_ReturnsFalse()
    {
        await using var monitor = new TriggerMonitor();
        await monitor.StartAsync([Definition("a")], Ct);

        Assert.False(await monitor.RemoveAsync("nope", Ct));
        Assert.Equal(["a"], await monitor.GetTriggerIdsAsync(Ct));
    }

    /// <summary>停止 → 再開ができること (停止時に残った状態が次の開始を壊さないこと)。</summary>
    [Fact]
    public async Task StopAsync_ThenStartAsync_StartsClean()
    {
        await using var monitor = new TriggerMonitor();
        await monitor.StartAsync([Definition("a")], Ct);
        await monitor.StopAsync(Ct);

        Assert.False(monitor.GetDiagnostics().IsRunning);
        Assert.Empty(await monitor.GetTriggerIdsAsync(Ct));

        await monitor.StartAsync([Definition("a")], Ct);

        Assert.Equal(["a"], await monitor.GetTriggerIdsAsync(Ct));
    }

    [Fact]
    public async Task StartAsync_Twice_Throws()
    {
        await using var monitor = new TriggerMonitor();
        await monitor.StartAsync([Definition("a")], Ct);

        Exception? error = await Record.ExceptionAsync(() => monitor.StartAsync([Definition("b")], Ct));

        Assert.IsType<InvalidOperationException>(error);
    }

    /// <summary>
    /// セッションを共有した monitor を捨ててもセッションは生きていること。
    /// これが崩れると、ピッカーと監視を同居させたホストで
    /// 「監視を止めたらピッカーが動かなくなる」ことになる。
    /// </summary>
    [Fact]
    public async Task CreateMonitor_DoesNotTakeOwnershipOfTheSession()
    {
        await using var session = new UiaSession();
        TriggerMonitor monitor = session.CreateMonitor();
        Assert.Same(session, monitor.Session);

        await monitor.StartAsync([Definition("a")], Ct);
        await monitor.DisposeAsync();

        // セッションがまだ使えること (捨てられていれば ObjectDisposedException になる)
        Assert.True(await session.GetSupportsTimeoutsAsync());
    }

    /// <summary>
    /// 2 つの monitor が 1 つのセッションを共有でき、片方を止めても他方が動き続けること。
    /// 停止が <c>RemoveAllEventHandlers()</c> を呼ぶ形だと、これは必ず壊れる
    /// (実イベントで壊れる側は T3 の <c>SharedSessionTests</c>)。
    /// </summary>
    [Fact]
    public async Task TwoMonitors_CanShareOneSession()
    {
        await using var session = new UiaSession();
        await using TriggerMonitor first = session.CreateMonitor();
        TriggerMonitor second = session.CreateMonitor();

        await first.StartAsync([Definition("a")], Ct);
        await second.StartAsync([Definition("b")], Ct);
        await second.DisposeAsync();

        Assert.True(first.GetDiagnostics().IsRunning);
        Assert.Equal(["a"], await first.GetTriggerIdsAsync(Ct));
    }

    /// <summary>
    /// 使用後の monitor は API を拒むこと (Dispose 後に足せてしまうと、購読が
    /// どこにも紐づかないまま残る形の漏れになる)。
    /// </summary>
    [Fact]
    public async Task AddAsync_AfterDispose_Throws()
    {
        var monitor = new TriggerMonitor();
        await monitor.StartAsync(cancellationToken: Ct);
        await monitor.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.AddAsync(Definition("a"), Ct));
    }
}
