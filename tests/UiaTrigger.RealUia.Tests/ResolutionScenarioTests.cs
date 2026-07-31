using UiaTrigger.Models;
using UiaTrigger.Monitoring;
using UiaTrigger.Resources;
using Xunit;

namespace UiaTrigger.RealUia.Tests;

/// <summary>
/// 実 UIA での経路解決の回帰 (docs/TESTING.md §1 の T3)。
///
/// 擬似ツリーに対する <c>BeamSearchTests</c> と違い、ここでは
/// **記録側 (DefinitionBuilder) と解決側 (ElementResolver) を実物で突き合わせる**。
/// 片方だけが正しい状態は擬似ツリーでは検出できない。
///
/// 各シナリオには「わざと壊した条件では解決できないこと」を示す対を置く
/// (検出力を証明できないハーネスは信頼しない — docs/TESTING.md §2)。
///
/// <para>
/// **[Theory] にしてあるのは「プロバイダーの挙動が論点」のものだけ**である
/// (型変化・ゾンビ検出・ウィンドウ張り替え)。A3 の兄弟挿入と A4 のクラス名 token は
/// **探索アルゴリズムが論点**であり、対象アプリを変えても同じ道を通るだけなので
/// WinForms 側にとどめてある — 実行時間と flake を倍にするだけの [Theory] は入れない。
/// A4 に至っては WinForms 固有の <c>_ad1</c> token が題材そのものである。
/// </para>
/// </summary>
public sealed class ResolutionScenarioTests
{
    private const int ButtonType = 50000;
    private const int TextType = 50020;
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 「要素を作り直さずに ControlType だけを変える」手段があるプロファイルでだけ走らせる。
    ///
    /// 黙って落とさず**理由付きでスキップ**する。代替機構 (要素を作り直す等) で
    /// 誤魔化すと「要素消滅」の経路を通ってしまい、A5/A8 とは別のものを確認したまま
    /// **間違った理由で緑になる** (docs/TESTING.md §1)。
    /// </summary>
    private static void RequireRoleSwitch(string profile) => Assert.SkipUnless(
        TargetProfile.ByName(profile).SupportsRoleSwitch,
        $"{profile}: 要素を作り直さずに ControlType だけを変える手段が無いため、" +
        "A5/A8 の前提 (docs/TESTING.md §1) を作れない。");

    // ---- A3: 兄弟が 1 個増えても解決できること (経路探索が論点。WinForms のみ) ----

    [Fact]
    public async Task Resolve_AfterASiblingIsInsertedBefore_StillFindsTheElement()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btn0");
        target.Send("add-button btnTarget");
        target.Send("add-button btn2");

        TriggerDefinition definition = await Recording.RecordAsync(target, "btnTarget");
        Assert.Equal(1, definition.LastStep().SiblingIndex);
        // 経路方式そのものの回帰なので Search 方式は外す。WinForms は AutomationId に
        // コントロールの Name を出すため、外さないと Search 1 発で通ってしまう
        definition.PinToWindow(target).WatchName().WithoutSearch();

        // 記録の後で兄弟が 1 個増える。段ごとのスコア閾値がこれだけで割れる形だと
        // 恒久的に解決不能になる (docs/DESIGN.md A3)
        target.Send("insert-sibling-before btn0 btnNew");

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        target.Send("set-text btnTarget changed");

        TriggerFiredEventArgs fired = harness.WaitForFire();
        Assert.Equal("changed", fired.NewValue.Value);
    }

    /// <summary>ネガティブコントロール: 記録された要素が居なければ解決しないこと。</summary>
    [Fact]
    public async Task Resolve_WhenTheRecordedElementIsGone_NeverResolves()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btn0");
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();
        target.Send("remove btnTarget");

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);

        harness.AssertNoFire(Settle);
    }

    // ---- A4: WinForms のクラス名 token 変動 + タイトル変化 (この token 自体が題材。WinForms のみ) ----

    [Fact]
    public async Task Resolve_WithAChangedWindowClassTokenAndTitle_StillFindsTheWindow()
    {
        TriggerDefinition definition;
        string recordedClassName;
        using (var recordedOn = TestTargetProcess.Start())
        {
            recordedOn.Send("add-button btnTarget");
            definition = (await Recording.RecordAsync(recordedOn, "btnTarget")).WatchName();
            recordedClassName = definition.Window.ClassName!;
        }

        // 別インスタンス = 別タイトル。WinForms のクラス名に入る token も起動ごとに変わりうるが、
        // 同じ実行ファイルでは一致してしまうことがあるため、変動した状態を記録側で確実に作る
        definition.Window.ClassName = recordedClassName.Replace("_ad1", "_ad9", StringComparison.Ordinal);
        Assert.NotEqual(recordedClassName, definition.Window.ClassName);

        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");
        Assert.NotEqual(target.Title, definition.Window.WindowName);

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        target.Send("set-text btnTarget changed");
        Assert.Equal("changed", harness.WaitForFire().NewValue.Value);
    }

    /// <summary>
    /// ネガティブコントロール: クラス名を Required に上げれば、同じ条件で解決できなくなること。
    /// 上のテストが「何でも解決してしまう」わけではないことを示す。
    /// </summary>
    [Fact]
    public async Task Resolve_WithARequiredClassNameThatNoLongerMatches_NeverResolves()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget")).WatchName();
        definition.Window.ClassName = definition.Window.ClassName!.Replace("_ad1", "_ad9", StringComparison.Ordinal);
        definition.Window.ClassNameMatch = MatchStrength.Required;

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);

        harness.AssertNoFire(Settle);
    }

    // ---- A5: ControlType が変わっても追随できること (プロバイダー差が論点) ----

    [Theory]
    [MemberData(nameof(TargetProfile.AllNames), MemberType = typeof(TargetProfile))]
    public async Task Resolve_AfterTheControlTypeChanges_StillFindsTheElement(string profile)
    {
        RequireRoleSwitch(profile);
        using var target = TestTargetProcess.Start(profile);
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName().WithoutSearch();
        Assert.Equal(ButtonType, definition.LastStep().ControlType);

        // 要素は作り直さず ControlType だけを変える (Button → Text)。
        // ControlType 不一致を即除外する形だと追えない (docs/DESIGN.md A5)
        target.Send("set-role btnTarget StaticText");

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        target.Send("set-text btnTarget changed");
        Assert.Equal("changed", harness.WaitForFire().NewValue.Value);
    }

    /// <summary>
    /// ネガティブコントロール: 手掛かりが ControlType しかない段で型が変われば不採用になること。
    ///
    /// これは意図した挙動であり (docs/DESIGN.md §3 の StepAcceptScore の足切り)、上のテストが成り立つのは
    /// AutomationId 等の別の証拠が減点を上回るからだと示すために対で必要になる。
    /// </summary>
    [Theory]
    [MemberData(nameof(TargetProfile.AllNames), MemberType = typeof(TargetProfile))]
    public async Task Resolve_WhenControlTypeIsTheOnlyEvidence_DoesNotSurviveATypeChange(string profile)
    {
        RequireRoleSwitch(profile);
        using var target = TestTargetProcess.Start(profile);
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName().WithoutSearch();
        ElementPathStep step = definition.LastStep();
        step.AutomationId = null;
        step.Name = null;
        step.ClassName = null;
        Assert.Equal(ButtonType, step.ControlType);

        target.Send("set-role btnTarget StaticText");

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);

        harness.AssertNoFire(Settle);
    }

    // ---- A8: 掴んでいる要素が「生きているが別物」になったとき (プロバイダー差が論点) ----

    [Theory]
    [MemberData(nameof(TargetProfile.AllNames), MemberType = typeof(TargetProfile))]
    public async Task Monitor_WhenTheResolvedElementBecomesADifferentThing_ReResolvesOntoIt(string profile)
    {
        RequireRoleSwitch(profile);
        using var target = TestTargetProcess.Start(profile);
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        // 要素は生きたまま ControlType だけが変わる。ProcessId/AutomationId/ClassName は
        // 変わらないので、同一性の組に ControlType が入っていなければ検出できない
        target.Send("set-role btnTarget StaticText");
        // CheckAlive は sweep でしか走らない。経路上 (root の子) の構造変化で 1 回促す
        target.Send("add-button filler");
        target.Send("remove filler");

        TriggerResolutionChangedEventArgs lost = harness.WaitForResolution(resolved: false);
        Assert.Equal(Strings.Status_ElementIdentityChanged, lost.Message);

        harness.WaitForResolution(resolved: true);
        target.Send("set-text btnTarget changed");

        TriggerFiredEventArgs fired = harness.WaitForFire();
        Assert.Equal("changed", fired.NewValue.Value);
        // 記録側は Button のまま。それでも Text になった要素を掴み直せている (A5)
        Assert.Equal(ButtonType, definition.LastStep().ControlType);
        Assert.NotEqual(ButtonType, TextType);
    }

    [Theory]
    [MemberData(nameof(TargetProfile.AllNames), MemberType = typeof(TargetProfile))]
    public async Task Monitor_AfterTheTreeIsRebuilt_WatchesTheNewElementNotTheZombie(string profile)
    {
        using var target = TestTargetProcess.Start(profile);
        target.Send("add-button btn0");
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        // 同じ名前・同じ並びで作り直す。掴んでいた要素はゾンビになる
        target.Send("rebuild-tree");
        harness.WaitForResolution(resolved: false);
        harness.WaitForResolution(resolved: true);

        // ゾンビを監視し続けていれば、この変化は永久に届かない
        target.Send("set-text btnTarget changed");
        Assert.Equal("changed", harness.WaitForFire().NewValue.Value);
    }

    // ---- A9: ウィンドウの張り替え (HWND の再利用。プロバイダー差が論点) ----

    [Theory]
    [MemberData(nameof(TargetProfile.AllNames), MemberType = typeof(TargetProfile))]
    public async Task Monitor_AfterTheWindowIsRecreated_ResolvesInTheNewWindow(string profile)
    {
        using var target = TestTargetProcess.Start(profile);
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();
        // 監視開始時は未解決 = ウィンドウ全体を Subtree で購読している状態にする
        target.Send("remove btnTarget");

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: false);

        long before = target.WindowHandle();
        long after = target.RecreateWindow();
        target.Send("add-button btnTarget");

        harness.WaitForResolution(resolved: true);
        target.Send("set-text btnTarget changed");

        TriggerFiredEventArgs fired = harness.WaitForFire();
        Assert.Equal("changed", fired.NewValue.Value);
        // HWND が再利用されたかどうかは OS 次第で強制できない。再利用された回だけが
        // 「HWND 値の一致だけで購読済みと判断する」実装 (A9 が禁じる形) を落とせる
        Assert.True(before != 0 && after != 0, $"HWND before=0x{before:X} after=0x{after:X}");
    }

    /// <summary>ネガティブコントロール: 要素を足さなければ、張り替えだけでは発火しないこと。</summary>
    [Theory]
    [MemberData(nameof(TargetProfile.AllNames), MemberType = typeof(TargetProfile))]
    public async Task Monitor_AfterTheWindowIsRecreatedWithoutTheElement_NeverFires(string profile)
    {
        using var target = TestTargetProcess.Start(profile);
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();
        target.Send("remove btnTarget");

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: false);

        target.RecreateWindow();

        harness.AssertNoFire(Settle);
    }
}
