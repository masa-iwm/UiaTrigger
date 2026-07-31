using UiaTrigger.Models;
using Xunit;

namespace UiaTrigger.RealUia.Tests;

/// <summary>
/// <see cref="ElementSearch"/> の実 UIA 回帰 (docs/DESIGN.md §3 の Search 方式)。
///
/// <para>
/// 擬似ツリーの <c>SearchLocatorTests</c> は「解決側が Search を使うこと」を固定するが、
/// **記録側が一意性を正しく確かめていること**は実物でしか突き合わせられない。
/// 「一意だろう」で記録した定義は、解決時に黙って別の要素を掴む。
/// </para>
///
/// WinForms は UIA の <c>AutomationId</c> にコントロールの <c>Name</c> を出すので、
/// この対象アプリは Search 方式のちょうどよい題材になる。
/// </summary>
public sealed class SearchLocatorScenarioTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    /// <summary>一意な AutomationId を持つ要素からは Search が記録されること。</summary>
    [Fact]
    public async Task Record_WithAUniqueAutomationId_RecordsASearch()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");

        TriggerDefinition definition = await Recording.RecordAsync(target, "btnTarget");

        Assert.Equal("btnTarget", definition.Locator.Search?.AutomationId);
        // 経路も残ること。Search が使えなくなったときの退避路なので、片方だけでは足りない
        Assert.NotEmpty(definition.Locator.Steps);
    }

    /// <summary>
    /// 同じ AutomationId が 2 つあるなら Search は記録しないこと。
    ///
    /// これが記録側の肝心な部分である。記録してしまうと、解決は 2 件のうち片方を掴むか
    /// (先頭を採る実装なら) 黙って間違うか、いずれにせよ静かに壊れる。
    /// </summary>
    [Fact]
    public async Task Record_WithADuplicatedAutomationId_DoesNotRecordASearch()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");
        target.Send("add-button btnTarget");   // 同名 = UIA の AutomationId が重複する

        TriggerDefinition definition = await Recording.RecordAsync(target, "btnTarget");

        Assert.Null(definition.Locator.Search);
    }

    /// <summary>
    /// Search 方式は経路が丸ごと合わなくなっても解決できること。
    /// 「途中の段の変化に影響されない」という Search の唯一の利点そのもの。
    /// </summary>
    [Fact]
    public async Task Resolve_WithABrokenPath_StillFindsTheElementThroughTheSearch()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();
        Assert.NotNull(definition.Locator.Search);
        BreakThePath(definition);

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        target.Send("set-text btnTarget changed");
        Assert.Equal("changed", harness.WaitForFire().NewValue.Value);
    }

    /// <summary>
    /// 上の対: Search を外せば、同じ壊れた経路では解決できないこと。
    /// これが無いと「経路の壊し方が甘かっただけ」と区別できない。
    /// </summary>
    [Fact]
    public async Task Resolve_WithABrokenPathAndNoSearch_NeverResolves()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName().WithoutSearch();
        BreakThePath(definition);

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);

        target.Send("set-text btnTarget changed");
        harness.AssertNoFire(Settle);
    }

    /// <summary>
    /// 記録時は一意だった id が後から重複したら、Search を使わず経路方式で
    /// **記録した側の要素**を掴み直すこと。先頭の一致を採る実装なら落ちる。
    /// </summary>
    [Fact]
    public async Task Resolve_WhenTheAutomationIdBecomesAmbiguous_StillWatchesTheRecordedElement()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");
        target.Send("add-button btnOther");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();
        Assert.NotNull(definition.Locator.Search);
        int recordedIndex = definition.LastStep().SiblingIndex;

        // 記録の後で同じ id の要素が増える。Search は一意でなくなる
        target.Send("add-button btnTarget");

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        // set-text は最初に見つかった (= 記録した) コントロールを変える
        target.Send("set-text btnTarget changed");

        Assert.Equal("changed", harness.WaitForFire().NewValue.Value);
        Assert.Equal(0, recordedIndex);
    }

    /// <summary>
    /// Search 方式でも、経路購読 (B3) は経路方式と同じ形で張られること。
    /// ここが崩れると Search を使ったトリガーだけウィンドウ全体を購読することになり、
    /// B3 で得たものを静かに失う。
    /// </summary>
    [Fact]
    public async Task Resolve_ViaTheSearch_StillSubscribesPerLevel()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");
        target.Send("add-branch offPath");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();
        Assert.NotNull(definition.Locator.Search);

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);
        await Task.Delay(TimeSpan.FromMilliseconds(1500), TestContext.Current.CancellationToken);

        Monitoring.TriggerMonitorDiagnostics diagnostics = harness.Monitor.GetDiagnostics();
        Assert.Equal(definition.Locator.Steps.Count, diagnostics.StructureSubscriptionCount);
        Assert.True(diagnostics.StructureIsPathScoped);
    }

    /// <summary>経路上のすべての手掛かりを別物にする (Search だけが残る状態を作る)。</summary>
    private static void BreakThePath(TriggerDefinition definition)
    {
        foreach (ElementPathStep step in definition.Locator.Steps)
        {
            step.ControlType = 50004;   // Edit
            step.AutomationId = "nothingLikeThis";
            step.Name = "nothingLikeThis";
            step.ClassName = "NothingLikeThis";
        }
    }
}
