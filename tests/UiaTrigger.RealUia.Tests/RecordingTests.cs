using System.Diagnostics;
using System.Globalization;
using UiaTrigger.Models;
using Xunit;

namespace UiaTrigger.RealUia.Tests;

/// <summary>
/// 記録側 (<c>DefinitionBuilder</c>) と、深い階層の解決に要する時間 (docs/DESIGN.md B1/B9)。
/// </summary>
public sealed class RecordingTests
{
    private const int Siblings = 24;
    private const int TargetIndex = 17;
    private const int Depth = 15;

    /// <summary>
    /// B9: 兄弟が多くても正しい兄弟インデックスを記録すること。
    ///
    /// 走査は「キャッシュ済み属性で絞ってから <c>CompareElements</c>」に変えてある。
    /// 絞り込み条件が実 UIA の返す値と食い違っていれば、ここで
    /// <see cref="ElementPathStep.UnknownSiblingIndex"/> (= -1) が記録される。
    /// </summary>
    [Fact]
    public async Task Record_AmongManySiblings_CapturesTheSiblingIndex()
    {
        using var target = TestTargetProcess.Start();
        for (int i = 0; i < Siblings; i++)
        {
            target.Send(string.Create(CultureInfo.InvariantCulture, $"add-button btn{i:D2}"));
        }

        string name = string.Create(CultureInfo.InvariantCulture, $"btn{TargetIndex:D2}");
        TriggerDefinition definition = await Recording.RecordAsync(target, name);

        ElementPathStep step = definition.LastStep();
        Assert.Equal(TargetIndex, step.SiblingIndex);
        Assert.Equal(name, step.AutomationId);
        Assert.Equal(name, step.Name);
    }

    /// <summary>
    /// 記録した経路の形そのものを固定する。ここが崩れると、以後のシナリオが
    /// 「何を記録したのか分からないまま通る」ことになる。
    /// </summary>
    [Theory]
    [MemberData(nameof(TargetProfile.AllNames), MemberType = typeof(TargetProfile))]
    public async Task Record_CapturesTheWindowIdentityAndThePathDownToTheTarget(string profile)
    {
        using var target = TestTargetProcess.Start(profile);
        target.Send("add-button btnTarget");

        TriggerDefinition definition = await Recording.RecordAsync(target, "btnTarget");

        Assert.Equal(target.Profile.ProcessName, definition.Window.ProcessName);
        Assert.Equal(MatchStrength.Required, definition.Window.ProcessNameMatch);
        // タイトルは記録するが照合には使わない (docs/DESIGN.md A4)
        Assert.Equal(target.Title, definition.Window.WindowName);
        Assert.Equal(MatchStrength.Ignored, definition.Window.WindowNameMatch);
        // どちらのプロファイルもウィンドウクラス名に起動ごとに変わりうる部分を含む
        // (WinForms は _r6_ad1 の token、WPF は HwndWrapper[...] の GUID)。
        // だから Preferred でなければならない
        Assert.StartsWith(target.Profile.WindowClassNamePrefix, definition.Window.ClassName, StringComparison.Ordinal);
        Assert.Equal(MatchStrength.Preferred, definition.Window.ClassNameMatch);

        // ウィンドウ → root パネル → ボタン。
        // 段数は<b>両プロファイルで同じ</b>であることを要求する。プロファイルごとに
        // 期待段数を変えると「アプリが出した形をそのまま確認するだけ」の assert になる
        Assert.Equal(2, definition.Locator.Steps.Count);
        Assert.Equal("root", definition.Locator.Steps[0].AutomationId);
        Assert.Equal("btnTarget", definition.Locator.Steps[1].AutomationId);
    }

    /// <summary>
    /// <c>AutomationId</c> を持たない要素は、Search 方式ではなく経路方式で記録・解決されること。
    ///
    /// <para>
    /// WinForms は必ず <c>Control.Name</c> を UIA の <c>AutomationId</c> に出すため、
    /// <c>Recording.WithoutSearch()</c> で作れるのは<b>擬似的な</b>「Search の無い定義」だけである。
    /// 「記録側が本当に Search を諦められるか」(= 一意性を数えた結果 0 件だったときの経路) は
    /// AutomationId を持たない実物の要素が要る — ネイティブプロバイダーなら素直に作れる。
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(TargetProfile.AllNames), MemberType = typeof(TargetProfile))]
    public async Task Record_WithoutAnyAutomationId_FallsBackToThePathMethod(string profile)
    {
        Assert.SkipUnless(
            TargetProfile.ByName(profile).IsNativeProvider,
            "WinForms は Control.Name をそのまま AutomationId に出すため、AutomationId の無い要素を作れない。");

        using var target = TestTargetProcess.Start(profile);
        target.Send("add-anonymous lonely");

        TriggerDefinition anonymous = await Recording.RecordAnonymousAsync(target, "lonely");

        Assert.Null(anonymous.Locator.Search);
        Assert.Equal(2, anonymous.Locator.Steps.Count);

        // 記録した経路で実際に解決でき、発火すること。
        // 「Search が付かなかった」だけでは、経路が使い物になるかは分からない
        anonymous.PinToWindow(target).WatchName();
        await using var harness = new MonitorHarness();
        await harness.StartAsync(anonymous);
        harness.WaitForResolution(resolved: true);
        target.Send("set-text lonely changed");
        Assert.Equal("changed", harness.WaitForFire().NewValue.Value);

        // ネガティブコントロール: 同じ形の要素に AutomationId を付ければ Search が記録される。
        // これが無いと「このアプリでは常に Search が付かないだけ」と区別できない
        target.Send("add-button named");
        TriggerDefinition named = await Recording.RecordAsync(target, "named", triggerId: "named");
        Assert.NotNull(named.Locator.Search);
    }

    /// <summary>
    /// ハーネス自身の検証: 対象アプリが申告する矩形が、UIA の <c>BoundingRectangle</c> と一致すること。
    ///
    /// <para>
    /// T3 の定義はすべて<b>座標からの記録</b>で作る。対象アプリの座標が DIP のままだったり
    /// 係数が掛かっていたりすると、記録は例外を出さずに<b>別の要素</b>を掴み、
    /// 以後のシナリオは「何を記録したのか分からないまま」通る (docs/DESIGN.md A19 / docs/TESTING.md §4)。
    /// </para>
    /// <para>
    /// <b>100% スケールの CI ではこの種のバグは緑のまま通る</b>ため、絶対値の比較でしか
    /// 捕まえられない。WinForms は <c>RectangleToScreen</c>、WPF は <c>Visual.PointToScreen</c>
    /// (HwndSource の DPI 変換を含む) で、どちらも物理ピクセルを返す規約になっている。
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(TargetProfile.AllNames), MemberType = typeof(TargetProfile))]
    public async Task Harness_ReportedRectMatchesTheUiaBoundingRectangle(string profile)
    {
        using var target = TestTargetProcess.Start(profile);
        target.Send("add-button btnRect");

        (int left, int top, int right, int bottom) = target.RectOf("btnRect");
        Assert.True(right > left && bottom > top, $"対象アプリの矩形が空です: {left},{top},{right},{bottom}");

        await using var session = new UiaSession();
        UiaElement? element = await session.ElementFromPointAsync((left + right) / 2, (top + bottom) / 2);
        Assert.NotNull(element);
        using (element)
        {
            Assert.Equal("btnRect", element.AutomationId);
            ElementRect uia = element.BoundingRectangle;

            // 1px は丸めの余地。スケール係数の取り違えはこの桁では収まらない
            const int Tolerance = 1;
            Assert.True(
                Math.Abs(uia.Left - left) <= Tolerance && Math.Abs(uia.Top - top) <= Tolerance &&
                Math.Abs(uia.Right - right) <= Tolerance && Math.Abs(uia.Bottom - bottom) <= Tolerance,
                $"対象アプリの申告 ({left},{top})-({right},{bottom}) と UIA の BoundingRectangle " +
                $"({uia.Left},{uia.Top})-({uia.Right},{uia.Bottom}) が食い違います。" +
                "座標系 (物理ピクセル / DIP) か DPI 認識の宣言を確認すること。");
        }
    }

    /// <summary>
    /// B1: 深い階層でも解決が現実的な時間で終わること。
    ///
    /// 候補 1 件あたり最大 3 往復 × 最大 512 兄弟 × 段数を掛ける形に戻ると、
    /// この深さでは実用にならない。上限は緩くとってある — ここで見たいのは
    /// 「桁が違う」ことであって特定のミリ秒数ではない。
    /// </summary>
    [Fact]
    public async Task Resolve_AtDepth15_FinishesQuickly()
    {
        using var target = TestTargetProcess.Start();
        target.Send(string.Create(CultureInfo.InvariantCulture, $"add-depth {Depth} deepLeaf"));

        TriggerDefinition definition = (await Recording.RecordAsync(target, "deepLeaf"))
            .PinToWindow(target).WatchName();
        // root + 中間パネル + 葉
        Assert.Equal(Depth + 2, definition.Locator.Steps.Count);

        await using var harness = new MonitorHarness();
        long start = Stopwatch.GetTimestamp();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

        Assert.True(
            elapsed < TimeSpan.FromSeconds(5),
            $"深さ {definition.Locator.Steps.Count} 段の解決に {elapsed.TotalSeconds:F1} 秒かかりました。" +
            "CacheRequest 化 (docs/DESIGN.md B1) が効いていない可能性があります。");

        // 掴んだのが本当に葉であること (速いだけで別物では意味がない)
        target.Send("set-text deepLeaf changed");
        Assert.Equal("changed", harness.WaitForFire().NewValue.Value);
    }
}
