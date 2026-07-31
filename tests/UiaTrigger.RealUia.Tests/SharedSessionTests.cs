using UiaTrigger.Models;
using Xunit;

namespace UiaTrigger.RealUia.Tests;

/// <summary>
/// セッション共有と、トリガーの動的な増減 (docs/DESIGN.md §3 / C1) の実 UIA 回帰。
///
/// <para>
/// 停止処理が <c>RemoveAllEventHandlers()</c> を呼ぶ形に戻ると、1 プロセスに
/// <c>IUIAutomation</c> が 1 つしかない状態では**同じセッションを使う他の購読者
/// (ピッカーや別の monitor) の購読まで一緒に消える**。ここはその形でしか壊れないので、
/// 実 UIA でイベントが届き続けることを見るしかない。
/// </para>
/// </summary>
public sealed class SharedSessionTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 1 セッションを共有した 2 つの monitor のうち片方を捨てても、他方が動き続けること。
    /// </summary>
    [Fact]
    public async Task DisposingOneMonitor_LeavesTheOtherReceivingEvents()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnA");
        target.Send("add-button btnB");

        TriggerDefinition a = (await Recording.RecordAsync(target, "btnA", "a")).PinToWindow(target).WatchName();
        TriggerDefinition b = (await Recording.RecordAsync(target, "btnB", "b")).PinToWindow(target).WatchName();

        await using var session = new UiaSession();
        await using var kept = new MonitorHarness(session);
        await using var discarded = new MonitorHarness(session);

        await kept.StartAsync(a);
        await discarded.StartAsync(b);
        kept.WaitForResolution(resolved: true);
        discarded.WaitForResolution(resolved: true);

        // 片方だけ止める。RemoveAllEventHandlers を呼ぶ実装ではここで両方の購読が消える
        await discarded.DisposeMonitorAsync();

        target.Send("set-text btnA changed");

        Assert.Equal("changed", kept.WaitForFire("a").NewValue.Value);
    }

    /// <summary>
    /// 対: 捨てた側はもう発火しないこと。上のテストが「何も外れていない」わけではないことを示す。
    /// </summary>
    [Fact]
    public async Task DisposingOneMonitor_StopsThatMonitorsEvents()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnA");
        target.Send("add-button btnB");

        TriggerDefinition a = (await Recording.RecordAsync(target, "btnA", "a")).PinToWindow(target).WatchName();
        TriggerDefinition b = (await Recording.RecordAsync(target, "btnB", "b")).PinToWindow(target).WatchName();

        await using var session = new UiaSession();
        await using var kept = new MonitorHarness(session);
        await using var discarded = new MonitorHarness(session);

        await kept.StartAsync(a);
        await discarded.StartAsync(b);
        kept.WaitForResolution(resolved: true);
        discarded.WaitForResolution(resolved: true);
        await discarded.DisposeMonitorAsync();

        target.Send("set-text btnB changed");

        discarded.AssertNoFire(Settle);
    }

    /// <summary>
    /// C1: 動かしたままトリガーを足せて、足した分がそのまま発火すること
    /// (1 件足すのに全停止 → 全再開を要しない)。
    /// </summary>
    [Fact]
    public async Task AddAsync_WhileRunning_TheNewTriggerFires()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnA");
        target.Send("add-button btnB");

        TriggerDefinition a = (await Recording.RecordAsync(target, "btnA", "a")).PinToWindow(target).WatchName();
        TriggerDefinition b = (await Recording.RecordAsync(target, "btnB", "b")).PinToWindow(target).WatchName();

        await using var harness = new MonitorHarness();
        await harness.StartAsync(a);
        harness.WaitForResolution(resolved: true);

        await harness.Monitor.AddAsync(b, TestContext.Current.CancellationToken);

        target.Send("set-text btnB changed");
        Assert.Equal("changed", harness.WaitForFire("b").NewValue.Value);

        // 先にあったトリガーも生きていること (足しただけで他が壊れない)
        target.Send("set-text btnA also");
        Assert.Equal("also", harness.WaitForFire("a").NewValue.Value);
    }

    /// <summary>
    /// C1: 1 件だけ外せて、外した分だけが止まること。
    /// </summary>
    [Fact]
    public async Task RemoveAsync_WhileRunning_StopsOnlyThatTrigger()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnA");
        target.Send("add-button btnB");

        TriggerDefinition a = (await Recording.RecordAsync(target, "btnA", "a")).PinToWindow(target).WatchName();
        TriggerDefinition b = (await Recording.RecordAsync(target, "btnB", "b")).PinToWindow(target).WatchName();

        await using var harness = new MonitorHarness();
        await harness.StartAsync(a, b);
        harness.WaitForResolution(resolved: true);
        harness.WaitForResolution(resolved: true);

        Assert.True(await harness.Monitor.RemoveAsync("b", TestContext.Current.CancellationToken));

        // 残したトリガーは動く
        target.Send("set-text btnA changed");
        Assert.Equal("changed", harness.WaitForFire("a").NewValue.Value);

        // 外したトリガーは鳴らない
        target.Send("set-text btnB changed");
        harness.AssertNoFire(Settle);
    }
}
