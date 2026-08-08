using UiaTrigger.Models;
using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.RealUia.Tests;

/// <summary>
/// 利用者が指定したポーリング (docs/DESIGN.md §5) を実 UIA で確認する。
///
/// <para>
/// T1 (<c>TriggerMonitorPollingTests</c>) が見ているのは配線の形だけで、
/// 要素を 1 つも解決しない。**「読み直して、変わっていたら鳴る / 変わっていなければ鳴らない」は
/// 実際の要素が要る**ので、こちらの担当である。
/// </para>
/// <para>
/// **この一群の主眼は「鳴りすぎないこと」である。**ポーリングを足したときに最も起きやすい
/// 壊れ方は「動かない」ではなく**毎周期鳴る**で、しかもそれは
/// 「ちゃんと動いている」ようにしか見えない。だから各テストにネガティブコントロールを付ける。
/// </para>
/// </summary>
public sealed class PollingScenarioTests
{
    /// <summary>ポーリング間隔。実時間を待つので、確かめられる範囲でいちばん短くとる。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>発火しないことを見るための観測窓。上の間隔の何周ぶんかを必ず含めること。</summary>
    private static readonly TimeSpan NoFireWindow = TimeSpan.FromSeconds(3);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// **まず測る: <c>set-accessible-name</c> は UIA の変化通知を上げないこと。**
    ///
    /// <para>
    /// これはポーリングのテストではなく、**あとの 2 件が何かを証明できるようにするための実測**である。
    /// この前提が崩れていると (= 実は通知が上がっていると)、「ポーリングが気づいた」と思っている
    /// 発火が、ただのイベント駆動の発火になる。**その状態でもテストは緑になる** —
    /// 「assert を書く前に測る」(docs/TESTING.md §3) と同じ理由でここに置く。
    /// </para>
    /// <para>
    /// 対になる肯定側も同じテストの中で見る。<c>set-text</c> は**通知を上げる**ので、
    /// 「このハーネスではそもそも発火を観測できない」という取り違えを同時に排除できる。
    /// </para>
    /// </summary>
    [Fact]
    public async Task SettingTheAccessibleName_RaisesNoUiaEvent_ButSettingTheTextDoes()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        // 否定側: 通知を上げない変え方。ポーリングしていないので、何も来てはいけない
        target.Send("set-accessible-name btnTarget silent-1");
        harness.AssertNoFire(NoFireWindow);

        // 肯定側: 同じハーネスで、通知を上げる変え方なら届く。
        // これが無いと上の「来なかった」がハーネスの故障と区別できない
        target.Send("set-text btnTarget loud-1");
        TriggerFiredEventArgs fired = harness.WaitForFire();

        Assert.Equal(definition.Id, fired.TriggerId);
    }

    /// <summary>
    /// **同じことを WPF (ネイティブ UIA プロバイダー) でも測る。**
    ///
    /// <para>
    /// 上の実測は WinForms (MSAA ブリッジ) のものである。**そのまま WPF に当てはめてはいけない** —
    /// 2 つのプロバイダー経路は非対称である (A21 — docs/DESIGN.md §6)。ネイティブプロバイダーは
    /// peer のプロパティを自動で突き合わせて通知を上げる仕組みを持つので、
    /// **WPF では「無通知の変化」を作れない可能性がある**。
    /// </para>
    /// <para>
    /// **どちらに転んでもこのテストは意味を持つ。**通知が上がらないなら WinForms と同じで、
    /// ポーリングの価値がプロバイダーによらないことが言える。上がるなら
    /// 「無通知の変化を構成できるのは MSAA ブリッジ側だけ」という知見であり、
    /// それ自体がプロバイダー差の記録 (docs/DESIGN.md §6) に連なる。**推測で書かず、実測を assert にする。**
    /// </para>
    /// </summary>
    [Fact]
    public async Task OnANativeProvider_SettingTheNameWithoutTouchingLayout_IsMeasured()
    {
        using var target = TestTargetProcess.Start(TargetProfile.Wpf);
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        // ポーリングなし。ここで鳴れば「WPF は通知を上げる」である
        target.Send("set-accessible-name btnTarget silent-on-wpf");

        bool raisedAnEvent = harness.Next(NoFireWindow) is TriggerFiredEventArgs;

        // 肯定側: 通知を上げる変え方なら必ず届く。上が false のときに
        // 「ハーネスが壊れていた」を排除する
        target.Send("set-text btnTarget loud-on-wpf");
        TriggerFiredEventArgs fired = harness.WaitForFire();
        Assert.Equal("loud-on-wpf", fired.NewValue.Value);

        // 実測の結果をそのまま固定する。docs/DESIGN.md §5 が
        // 「無通知の変化はどちらのプロバイダーでも構成できる」の根拠として引く実測である
        Assert.False(
            raisedAnEvent,
            "WPF (ネイティブ UIA プロバイダー) が AutomationProperties.Name の差し替えで " +
            "PropertyChanged を上げた。これが変わったなら docs/DESIGN.md §5 の記述を直すこと — " +
            "『無通知の変化はどちらのプロバイダーでも構成できる』が前提を失う。");
    }

    /// <summary>
    /// **ポーリングが、通知の上がらない変化に気づくこと** (この機能の存在理由)。
    ///
    /// 上の実測により <c>set-accessible-name</c> は通知を上げない。したがってここで鳴ったなら、
    /// 気づいたのはポーリング以外にありえない。
    /// </summary>
    [Fact]
    public async Task WithPolling_AChangeThatRaisesNoEvent_IsStillNoticed()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();
        definition.PollInterval = PollInterval;

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        target.Send("set-accessible-name btnTarget noticed-by-polling");

        TriggerFiredEventArgs fired = harness.WaitForFire();
        Assert.Equal("noticed-by-polling", fired.NewValue.Value);

        TriggerMonitorDiagnostics diagnostics = harness.Monitor.GetDiagnostics();
        Assert.True(diagnostics.PollCount > 0, $"ポーリングが 1 周も回っていません: {harness.History()}");
        Assert.True(
            diagnostics.PolledReadCount > 0,
            $"ポーリングが 1 度も要素を読んでいません: {harness.History()}");
    }

    /// <summary>
    /// 上の実測で「WPF でも無通知の変化を作れる」ことが分かったので、
    /// **ポーリングがそれに気づくことも両プロバイダーで確かめる。**
    ///
    /// ポーリング自体はスナップショットを読み直すだけでプロバイダーに依存しないが、
    /// A21 (docs/DESIGN.md §8) が禁じているのは**まさにその「プロバイダーに依存しない」という推論**である。
    /// 1 本足しておく。
    /// </summary>
    [Fact]
    public async Task OnANativeProvider_PollingNoticesTheSilentChangeToo()
    {
        using var target = TestTargetProcess.Start(TargetProfile.Wpf);
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();
        definition.PollInterval = PollInterval;

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        target.Send("set-accessible-name btnTarget noticed-on-wpf");

        TriggerFiredEventArgs fired = harness.WaitForFire();
        Assert.Equal("noticed-on-wpf", fired.NewValue.Value);
    }

    /// <summary>
    /// **何も変わっていない間は、何周回っても鳴らないこと。**
    ///
    /// <para>
    /// これがポーリングの検査の中核である。<c>WhileMatching</c> の立ち上がり判定と
    /// 「監視値が実際に変わったか」の判定は、反復して読み直しても冪等でなければならない。
    /// ここが崩れると <c>MinInterval</c> でしか抑えられない発火の洪水になる。
    /// </para>
    /// <para>
    /// 観測窓のあとに**ポーリングが実際に回っていたこと**を診断で確かめる。
    /// これが無いと「鳴らなかった」が「そもそも回っていなかった」と区別できない —
    /// 「『鳴らなかったこと』を根拠にしない」の規律である。
    /// </para>
    /// </summary>
    [Fact]
    public async Task WithPolling_WhileNothingChanges_NothingEverFires()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();
        definition.PollInterval = PollInterval;

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        // 最初の 1 件だけは通す (FireOnInitialMatch と、値を初めて見た周)。
        // ここで見たいのは「そのあと静かであること」である
        harness.AssertNoFire(NoFireWindow);

        TriggerMonitorDiagnostics diagnostics = harness.Monitor.GetDiagnostics();
        Assert.True(
            diagnostics.PollCount >= 2,
            $"観測窓の間にポーリングが 2 周も回っていないので、静かだったことに意味がありません " +
            $"(PollCount={diagnostics.PollCount}): {harness.History()}");
        Assert.True(
            diagnostics.PolledReadCount >= 2,
            $"読み直しが起きていないので、静かだったことに意味がありません " +
            $"(PolledReadCount={diagnostics.PolledReadCount}): {harness.History()}");
    }

    /// <summary>
    /// **通知が上がる変化では、ポーリングを足しても発火が二重にならないこと。**
    ///
    /// イベントで 1 回鳴り、次の周でもう 1 回鳴る、という形になっていないことを見る。
    /// 値が同じであれば周は何も起こさない、という性質がここに効いている。
    /// </summary>
    [Fact]
    public async Task WithPolling_AChangeThatRaisesAnEvent_FiresOnlyOnce()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();
        definition.PollInterval = PollInterval;

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);
        harness.AssertNoFire(NoFireWindow);

        target.Send("set-text btnTarget once");
        TriggerFiredEventArgs fired = harness.WaitForFire();
        Assert.Equal("once", fired.NewValue.Value);

        // 値はこれ以上変わらない。周は回り続けるが、2 度目は来ないはずである
        harness.AssertNoFire(NoFireWindow);
    }

    /// <summary>
    /// **Custom 句を持つトリガーが、ポーリングで毎周期鳴らないこと。**
    ///
    /// <para>
    /// <see cref="TriggerProperty.Custom"/> はスナップショットに入らないので、
    /// <c>WatchedValuesChanged</c> は前回値を持たず**無条件に「変化あり」と答えていた**。
    /// イベント経路ではそれでよい (UIA が何かの変化を伝えてきている) が、
    /// ポーリング経路には手掛かりが無いため、そのままだと**これだけが毎周期鳴る** —
    /// ポーリングが新しく作る唯一の失敗形である。
    /// </para>
    /// <para>
    /// 埋め合わせが <c>SlotObservations</c> の直前値であり、それが効いていることを
    /// ここで固定する。<c>ClassName</c> を Custom として読む — 名前付きでも読める値だが、
    /// **通るのは Custom の経路**であり、しかも実 UIA で安定している。
    /// </para>
    /// </summary>
    [Fact]
    public async Task WithPolling_ACustomPropertyClause_DoesNotFireEveryRound()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget")).PinToWindow(target);
        definition.On = TriggerOn.PropertyChanged;
        definition.PollInterval = PollInterval;
        definition.Clauses.Clear();
        definition.Clauses.Add(new PropertyClause
        {
            Property = TriggerProperty.Custom,
            CustomPropertyId = UiaTrigger.Interop.UiaIds.ClassNameProperty,
            Op = ComparisonOp.Always,
        });

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        // ClassName は変わらない。周は回り続けるが、1 件も来てはいけない
        harness.AssertNoFire(NoFireWindow);

        TriggerMonitorDiagnostics diagnostics = harness.Monitor.GetDiagnostics();
        Assert.True(
            diagnostics.PolledReadCount >= 2,
            $"読み直しが起きていないので、静かだったことに意味がありません " +
            $"(PolledReadCount={diagnostics.PolledReadCount}): {harness.History()}");
    }

    /// <summary>
    /// **式の短絡で一度も読まれない Custom 句が、ポーリングの発火源にならないこと。**
    ///
    /// <para>
    /// 式 <c>a || c</c> で a (presence) が成立し続ける構成では、c (Custom) は毎周
    /// 短絡されて読まれず、ストアに直前値を持てない。「片方でも欠けていれば通す」の
    /// 既定に毎周落ちると、**matched の間ポーリングのたびに鳴る** — 変化判定は
    /// 「この周に実際に評価された句」だけを理由にできる (LastEvaluated)。
    /// </para>
    /// </summary>
    [Fact]
    public async Task WithPolling_AShortCircuitedCustomClause_DoesNotFireEveryRound()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget")).PinToWindow(target);
        definition.On = TriggerOn.PropertyChanged;
        definition.PollInterval = PollInterval;
        definition.Clauses.Clear();
        definition.Clauses.Add(new PropertyClause
        {
            Name = "a", Property = TriggerProperty.Name, Op = ComparisonOp.Always,
        });
        definition.Clauses.Add(new PropertyClause
        {
            Name = "c",
            Property = TriggerProperty.Custom,
            CustomPropertyId = UiaTrigger.Interop.UiaIds.ClassNameProperty,
            Op = ComparisonOp.Always,
        });
        definition.Expression = "a || c";

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        // 何も変わらない。a が成立し続け c は常に短絡される — 1 件も来てはいけない
        harness.AssertNoFire(NoFireWindow);

        TriggerMonitorDiagnostics diagnostics = harness.Monitor.GetDiagnostics();
        Assert.True(
            diagnostics.PollCount >= 2,
            $"観測窓の間にポーリングが 2 周も回っていないので、静かだったことに意味がありません " +
            $"(PollCount={diagnostics.PollCount}): {harness.History()}");
    }

    /// <summary>
    /// ポーリングを頼んでいないトリガーでは、周が 1 度も回らないこと (実 UIA でも)。
    ///
    /// T1 に同じ主張の検査があるが、あちらは要素を解決しない。
    /// **解決済みの要素を抱えていても回らない**ことは、ここでしか言えない。
    /// </summary>
    [Fact]
    public async Task WithoutPolling_AResolvedTriggerIsNeverPolled()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        await Task.Delay(NoFireWindow, Ct);

        TriggerMonitorDiagnostics diagnostics = harness.Monitor.GetDiagnostics();
        Assert.Equal(1, diagnostics.ResolvedElementCount);
        Assert.Equal(0, diagnostics.PollCount);
        Assert.Equal(0, diagnostics.PolledReadCount);
    }
}
