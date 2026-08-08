using UiaTrigger.Models;
using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.RealUia.Tests;

/// <summary>
/// 立ち下がり通知 (<see cref="TriggerDefinition.NotifyOnStoppedMatching"/> — docs/DESIGN.md C14)
/// を実 UIA で確認する。
///
/// <para>
/// T1 はこの挙動を縛れない — エッジ判定は要素が解決して初めて動き、T1 の
/// <c>TriggerMonitor*Tests</c> は要素を 1 つも解決しない。立ち上がり・立ち下がりの
/// **発火回数**を見られるのはここだけである。
/// </para>
/// <para>
/// 対象は WinForms のみ。エッジ判定はモニターの純ロジックで、プロバイダー差
/// (docs/DESIGN.md A21) が論点になるのは検知経路のほうであり、それは既存の
/// <c>PollingScenarioTests</c> / <c>EventDeliveryTests</c> が両プロバイダーで押さえている。
/// </para>
/// </summary>
public sealed class StoppedMatchingScenarioTests
{
    /// <summary>発火しないことを見るための観測窓。</summary>
    private static readonly TimeSpan NoFireWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 「Name が "on" のあいだ成立」の WhileMatching トリガー。
    /// ボタンの初期 Name は "btnTarget" なので、開始時点では不成立から始まる。
    /// </summary>
    private static async Task<TriggerDefinition> WhileNameIsOnAsync(
        TestTargetProcess target, bool notifyOnStopped)
    {
        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget")).PinToWindow(target);
        definition.On = TriggerOn.WhileMatching;
        definition.Clauses.Clear();
        definition.Clauses.Add(new PropertyClause
        {
            Property = TriggerProperty.Name, Op = ComparisonOp.Equals, Text = "on",
        });
        definition.NotifyOnStoppedMatching = notifyOnStopped;
        return definition;
    }

    /// <summary>
    /// **条件が成立しなくなったら、その旨がホストへ届くこと** (この機能の存在理由)。
    /// イベントの <c>On</c> が <c>StoppedMatching</c> なので、立ち上がりと見分けられる。
    /// </summary>
    [Fact]
    public async Task WhenTheConditionStopsHolding_TheHostIsTold()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");
        TriggerDefinition definition = await WhileNameIsOnAsync(target, notifyOnStopped: true);

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        target.Send("set-text btnTarget on");
        TriggerFiredEventArgs rising = harness.WaitForFire();
        Assert.Equal(TriggerOn.WhileMatching, rising.On);

        target.Send("set-text btnTarget off");
        TriggerFiredEventArgs falling = harness.WaitForFire();

        Assert.Equal(TriggerOn.StoppedMatching, falling.On);
        Assert.Equal(definition.Id, falling.TriggerId);
        Assert.Equal("off", falling.NewValue.Value);

        // **両エッジとも「その値が変わった」ときは前回値を載せること**
        // (`TriggerFiredEventArgs.OldValue` の doc)。ここでのエッジはどちらも Name の変化が
        // 起こしたものなので、遷移の前後が揃って読める。前回値を落とすと、受け手は
        // 「いま何になったか」しか分からず、何から変わったかを別に覚えておく必要が出る。
        // 解決・消滅が起こしたエッジでは値の変化が無いので absent になる — そちらは
        // ElementDisappearsScenario 側の経路である
        Assert.Equal("btnTarget", rising.OldValue.Value);
        Assert.Equal("on", falling.OldValue.Value);
    }

    /// <summary>
    /// **ネガティブコントロール: フラグ無しなら立ち下がりは何も上げないこと。**
    /// 既定 false の後方互換の主張そのものである — preview.3 までのトリガーは
    /// 今までどおり立ち上がりしか鳴らさない。
    /// </summary>
    [Fact]
    public async Task WithoutTheFlag_TheFallingEdgeRaisesNothing()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");
        TriggerDefinition definition = await WhileNameIsOnAsync(target, notifyOnStopped: false);

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        // 立ち上がりが届くことを先に見る (この検査に検出力があることの証明 —
        // ハーネスの故障なら、ここで落ちて「静かだった」とは区別が付く)
        target.Send("set-text btnTarget on");
        Assert.Equal(TriggerOn.WhileMatching, harness.WaitForFire().On);

        target.Send("set-text btnTarget off");
        harness.AssertNoFire(NoFireWindow);
    }

    /// <summary>「要素が在ること」(presence) の WhileMatching トリガー — 出現/消滅レシピの形。</summary>
    private static async Task<TriggerDefinition> WhilePresentAsync(
        TestTargetProcess target, bool notifyOnStopped)
    {
        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget")).PinToWindow(target);
        definition.On = TriggerOn.WhileMatching;
        definition.Clauses.Clear();
        definition.Clauses.Add(new PropertyClause
        {
            Property = TriggerProperty.Name, Op = ComparisonOp.Always,
        });
        definition.NotifyOnStoppedMatching = notifyOnStopped;
        return definition;
    }

    /// <summary>
    /// **出現/消滅レシピ** (docs/DESIGN.md C14 / <see cref="TriggerDefinition.NotifyOnStoppedMatching"/>
    /// の remarks): WhileMatching + Always の句 + フラグで、要素の出現 (立ち上がり) と
    /// 消滅 (立ち下がり) が 1 つのトリガーで届くこと。
    ///
    /// Always の句は「その要素が在ること」で成立する (presence — docs/DESIGN.md C16)。
    /// 在否を見ない挙動に退行すると、水準が最初から true になり**立ち上がりごと消える**。
    /// </summary>
    [Fact]
    public async Task ThePresenceRecipe_ReportsAppearingAndDisappearingWithOneTrigger()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");
        TriggerDefinition definition = await WhilePresentAsync(target, notifyOnStopped: true);

        // 記録に使った要素を消してから監視を始める。不在 = 不成立から始まることが要点
        target.Send("remove btnTarget");

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);

        target.Send("add-button btnTarget");
        TriggerFiredEventArgs rising = harness.WaitForFire();
        Assert.Equal(TriggerOn.WhileMatching, rising.On);

        target.Send("remove btnTarget");
        TriggerFiredEventArgs falling = harness.WaitForFire();
        Assert.Equal(TriggerOn.StoppedMatching, falling.On);
    }

    /// <summary>ネガティブ対: フラグ無しの presence トリガーは、消滅では何も上げないこと。</summary>
    [Fact]
    public async Task WithoutTheFlag_ThePresenceTriggerIsSilentOnRemoval()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");
        TriggerDefinition definition = await WhilePresentAsync(target, notifyOnStopped: false);

        target.Send("remove btnTarget");

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);

        // 立ち上がりが届くことを先に見る (検出力の証明)
        target.Send("add-button btnTarget");
        Assert.Equal(TriggerOn.WhileMatching, harness.WaitForFire().On);

        target.Send("remove btnTarget");
        harness.AssertNoFire(NoFireWindow);
    }

    /// <summary>
    /// **値の述語の水準は、要素が消えても生き残ること** (docs/DESIGN.md C16 の裏面)。
    ///
    /// 消えたスロットの値は「最後に見えた値」で評価され続ける — こうでないと、
    /// 成立したまま要素が入れ替わっただけ (ツリー再構築) で立ち下がり + 立ち上がりが
    /// 鳴る洪水になる。消滅そのものを知りたいなら presence (上のレシピ) か
    /// ElementRemoved で書く。
    /// </summary>
    [Fact]
    public async Task AValueConditionsLevel_SurvivesTheElementsRemoval()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");
        TriggerDefinition definition = await WhileNameIsOnAsync(target, notifyOnStopped: true);

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        target.Send("set-text btnTarget on");
        Assert.Equal(TriggerOn.WhileMatching, harness.WaitForFire().On);

        // 成立したまま消える。値の述語は最後の値 ("on") で成立し続け、立ち下がりは出ない
        target.Send("remove btnTarget");
        harness.AssertNoFire(NoFireWindow);
    }

    /// <summary>
    /// **C16 は <see cref="TriggerProperty.Custom"/> の句にも適用されること。**
    ///
    /// <para>
    /// Custom はスナップショット関門を通らない第二の読み取り経路である。消滅時の評価が
    /// ここだけ生読みに落ちると、死んだ要素への読みが Unsupported に潰れ、
    /// **Name (上のテスト) では出ない立ち下がりが Custom でだけ出る** —
    /// 「成立したまま要素が入れ替わっただけで水準が揺れない」という C16 の約束が
    /// プロパティ種別で割れる。評価はストアの最終値を読む。
    /// </para>
    /// </summary>
    [Fact]
    public async Task AValueConditionsLevel_SurvivesTheElementsRemoval_WithACustomClause()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");
        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget")).PinToWindow(target);
        definition.On = TriggerOn.WhileMatching;
        definition.Clauses.Clear();
        definition.Clauses.Add(new PropertyClause
        {
            Property = TriggerProperty.Custom,
            CustomPropertyId = UiaTrigger.Interop.UiaIds.NameProperty,
            Op = ComparisonOp.Equals,
            Text = "on",
        });
        definition.NotifyOnStoppedMatching = true;

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        target.Send("set-text btnTarget on");
        Assert.Equal(TriggerOn.WhileMatching, harness.WaitForFire().On);

        // 成立したまま消える。Custom の述語もストアの最終値 ("on") で成立し続け、立ち下がりは出ない
        target.Send("remove btnTarget");
        harness.AssertNoFire(NoFireWindow);
    }

    /// <summary>
    /// **トリガーの削除は「条件が成立しなくなった」ではなく、何も上げないこと。**
    /// ReleaseTrigger は水準を代入で戻すだけで、発火経路を通らない (docs/DESIGN.md C14)。
    /// 停止・削除で鳴り始めると、ホストの後始末のたびに偽の立ち下がりが混ざる。
    /// </summary>
    [Fact]
    public async Task RemovingTheTrigger_IsNotAFallingEdge()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");
        TriggerDefinition definition = await WhileNameIsOnAsync(target, notifyOnStopped: true);

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        target.Send("set-text btnTarget on");
        Assert.Equal(TriggerOn.WhileMatching, harness.WaitForFire().On);

        Assert.True(await harness.Monitor.RemoveAsync(
            definition.Id, TestContext.Current.CancellationToken));
        harness.AssertNoFire(NoFireWindow);
    }

    /// <summary>
    /// **立ち下がりが <see cref="TriggerDefinition.MinInterval"/> に落とされないこと**
    /// (docs/DESIGN.md C14)。窓の中で落とすと、ホストは「まだ成立中」と信じ続ける。
    ///
    /// <para>
    /// 同じテストの中で検出力も証明する: 窓内の**再立ち上がり**は落とされ
    /// (<c>SuppressedFireCount</c> が進む)、ゲートが実際に武装していたことを見てから
    /// 「立ち下がりは通った」に意味を持たせる。ゲートが素通しになる退行では
    /// 再立ち上がりが届いて <c>AssertNoFire</c> が落ちる。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheFallingEdge_IsNotRateLimited()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");
        TriggerDefinition definition = await WhileNameIsOnAsync(target, notifyOnStopped: true);
        definition.MinInterval = TimeSpan.FromSeconds(30);

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        // 1 度目の立ち上がりはゲートを通る (窓はここから 30 秒)
        target.Send("set-text btnTarget on");
        Assert.Equal(TriggerOn.WhileMatching, harness.WaitForFire().On);

        // 窓の内側の立ち下がり — ゲートの対象外なので届く
        target.Send("set-text btnTarget off");
        Assert.Equal(TriggerOn.StoppedMatching, harness.WaitForFire().On);

        // 窓の内側の再立ち上がりは落とされる。これがゲートの武装の証明である
        target.Send("set-text btnTarget on");
        harness.AssertNoFire(NoFireWindow);
        Assert.True(
            harness.Monitor.GetDiagnostics().SuppressedFireCount > 0,
            $"MinInterval が 1 件も落としていないので、上の立ち下がりが「ゲートを通り抜けた」" +
            $"証明になっていません: {harness.History()}");
    }

    /// <summary>
    /// **ポーリング経路も立ち下がりを継承すること。**
    /// <c>set-accessible-name</c> は通知を上げない (PollingScenarioTests で実測済み) ので、
    /// ここで立ち下がりが届いたなら、気づいたのはポーリング以外にありえない。
    /// PollRound は OnPropertyChanged に合流するだけ (docs/DESIGN.md §5) — 並行実装が
    /// 生えたらここが落ちる。
    /// </summary>
    [Fact]
    public async Task WithPolling_TheSilentFallingEdge_IsNoticed()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");
        TriggerDefinition definition = await WhileNameIsOnAsync(target, notifyOnStopped: true);
        definition.PollInterval = TimeSpan.FromMilliseconds(250);

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        target.Send("set-text btnTarget on");
        Assert.Equal(TriggerOn.WhileMatching, harness.WaitForFire().On);

        // 通知を上げずに条件から外す。イベント経路はこれに気づけない
        target.Send("set-accessible-name btnTarget off");
        TriggerFiredEventArgs falling = harness.WaitForFire();

        Assert.Equal(TriggerOn.StoppedMatching, falling.On);
        Assert.True(
            harness.Monitor.GetDiagnostics().PolledReadCount > 0,
            $"ポーリングが 1 度も読んでいないのに立ち下がりが届いた — 前提 " +
            $"(set-accessible-name は通知を上げない) が崩れています: {harness.History()}");
    }
}
