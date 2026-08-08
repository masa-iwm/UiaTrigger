using UiaTrigger.Models;
using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.RealUia.Tests;

/// <summary>
/// ライフサイクル (出現・削除) + 値の条件の組み合わせが、**発火時に実際に条件で絞られる**こと。
///
/// <para>
/// ライフサイクルと述語の直交 (docs/DESIGN.md C3〜C6) は T1 が「句が作られ、監視に受理される」
/// まで固定しているが、**発火判定 (<c>matched &amp;&amp;</c> の絞り込み) を見るテストは
/// ここが初めてである** — これが無い間、絞り込みを消しても全テストが緑のままだった。
/// </para>
/// <para>
/// ElementRemoved の条件は**消滅直前の値**に対して評価される (docs/DESIGN.md C15)。
/// 句付き ElementRemoved が監視プロパティを購読して LastSnapshot を最新に保つのはそのためで、
/// 購読しないと「解決時の値」と比較する嘘になる — それがまさに修正された不具合である。
/// </para>
/// </summary>
public sealed class LifecycleConditionScenarioTests
{
    /// <summary>発火しないことを見るための観測窓。</summary>
    private static readonly TimeSpan NoFireWindow = TimeSpan.FromSeconds(3);

    /// <summary>「Name が <paramref name="text"/> のとき」の条件を持つライフサイクルトリガー。</summary>
    private static async Task<TriggerDefinition> WithNameConditionAsync(
        TestTargetProcess target, TriggerOn on, string text)
    {
        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget")).PinToWindow(target);
        definition.On = on;
        definition.Clauses.Clear();
        definition.Clauses.Add(new PropertyClause
        {
            Property = TriggerProperty.Name, Op = ComparisonOp.Equals, Text = text,
        });
        return definition;
    }

    /// <summary>
    /// **ElementRemoved の条件は、解決時ではなく消滅直前の値と比較されること** (C15 の本体)。
    ///
    /// <para>
    /// ボタンは Name="btnTarget" で解決され、消える前に "ready" へ変わる。条件
    /// <c>Equals "ready"</c> で鳴ったなら、比較されたのは直前の値である —
    /// 修正前 (購読が無く LastSnapshot が解決時のまま) はここが鳴らない。
    /// </para>
    /// <para>
    /// 途中の <c>AssertNoFire</c> は 2 つを兼ねる: 購読が**発火源になっていない**こと
    /// (ElementRemoved が PropertyChanged のように鳴り始めていない) の網であり、
    /// remove の前に変化通知が処理し終わることの同期でもある。
    /// </para>
    /// </summary>
    [Fact]
    public async Task ElementRemoved_ComparesTheValueJustBeforeRemoval()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");
        TriggerDefinition definition =
            await WithNameConditionAsync(target, TriggerOn.ElementRemoved, "ready");

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        target.Send("set-text btnTarget ready");
        harness.AssertNoFire(NoFireWindow);

        target.Send("remove btnTarget");
        TriggerFiredEventArgs fired = harness.WaitForFire();

        Assert.Equal(TriggerOn.ElementRemoved, fired.On);
        Assert.Equal("ready", fired.OldValue.Value);
    }

    /// <summary>
    /// **C15 は <see cref="TriggerProperty.Custom"/> の句にも適用されること。**
    ///
    /// <para>
    /// Custom はキャッシュ済みスナップショットを通らない第二の読み取り経路である。
    /// 消滅時の評価がここだけ生読みに落ちると、死んだ要素への読みは COMException →
    /// Unsupported に潰れ、**Custom で絞った ElementRemoved は例外もログも無く
    /// 一度も鳴らない** — Name (上のテスト) と Custom で意味論が割れる。
    /// 評価はストアの最終値 (購読が最新に保っている) を読む。
    /// </para>
    /// </summary>
    [Fact]
    public async Task ElementRemoved_WithACustomClause_ComparesTheValueJustBeforeRemoval()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");
        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget")).PinToWindow(target);
        definition.On = TriggerOn.ElementRemoved;
        definition.Clauses.Clear();
        definition.Clauses.Add(new PropertyClause
        {
            Property = TriggerProperty.Custom,
            CustomPropertyId = UiaTrigger.Interop.UiaIds.NameProperty,
            Op = ComparisonOp.Equals,
            Text = "ready",
        });

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        target.Send("set-text btnTarget ready");
        harness.AssertNoFire(NoFireWindow);

        target.Send("remove btnTarget");
        TriggerFiredEventArgs fired = harness.WaitForFire();

        Assert.Equal(TriggerOn.ElementRemoved, fired.On);
    }

    /// <summary>
    /// **ネガティブコントロール: 消滅直前に条件から外れていたら鳴らないこと。**
    /// <c>matched &amp;&amp;</c> の絞り込みそのものの網である — 絞り込みを消すとここが落ちる。
    /// 逆向きの鮮度も同時に言えている: 解決時の値と比較する退行では
    /// 「一度 ready になった事実」ではなく最後の値が効く、という形も崩れる。
    /// </summary>
    [Fact]
    public async Task ElementRemoved_WithAConditionThatStoppedHolding_DoesNotFire()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");
        TriggerDefinition definition =
            await WithNameConditionAsync(target, TriggerOn.ElementRemoved, "ready");

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        target.Send("set-text btnTarget ready");
        harness.AssertNoFire(NoFireWindow);
        target.Send("set-text btnTarget not-ready");
        harness.AssertNoFire(NoFireWindow);

        target.Send("remove btnTarget");
        harness.AssertNoFire(NoFireWindow);
    }

    /// <summary>
    /// **ElementAppeared + 条件は「出現し、かつその瞬間に条件が成立しているとき」だけ鳴ること。**
    /// 成立側。ボタンは "btnTarget" の名で現れるので、その名を要求する条件は出現の瞬間に成立する。
    /// 条件は出現の瞬間に 1 回だけ評価される — 後から成立しても鳴らない (それは
    /// WhileMatching の仕事) ので、出現時点で成立している形で組む。
    /// </summary>
    [Fact]
    public async Task ElementAppeared_WhenTheConditionHoldsAtAppearance_Fires()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");
        TriggerDefinition definition =
            await WithNameConditionAsync(target, TriggerOn.ElementAppeared, "btnTarget");

        // 記録に使った要素を消してから監視を始める。最初から在るものは「出現」ではない
        target.Send("remove btnTarget");

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);

        target.Send("add-button btnTarget");

        TriggerFiredEventArgs fired = harness.WaitForFire();

        Assert.Equal(TriggerOn.ElementAppeared, fired.On);
        Assert.Equal("btnTarget", fired.NewValue.Value);
    }

    /// <summary>
    /// ネガティブ対: 出現の瞬間に条件が成立していなければ鳴らないこと。
    /// ボタンは "btnTarget" の名のまま現れ、条件 <c>Equals "ready"</c> を満たさない。
    /// 解決自体は起きる (WaitForResolution) ので、「鳴らなかった」が
    /// 「見つからなかった」と区別できる。
    /// </summary>
    [Fact]
    public async Task ElementAppeared_WhenTheConditionDoesNotHold_DoesNotFire()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");
        TriggerDefinition definition =
            await WithNameConditionAsync(target, TriggerOn.ElementAppeared, "ready");

        target.Send("remove btnTarget");

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);

        target.Send("add-button btnTarget");
        harness.WaitForResolution(resolved: true);

        harness.AssertNoFire(NoFireWindow);
    }
}
