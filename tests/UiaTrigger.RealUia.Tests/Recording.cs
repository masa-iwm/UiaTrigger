using UiaTrigger.Models;
using Xunit;

namespace UiaTrigger.RealUia.Tests;

/// <summary>
/// 実 UIA でのトリガー定義の記録。
///
/// 定義は <b>手で組み立てず、ユーザーと同じ経路 (座標からの記録) で作る</b>。
/// 手で組むと「解決側は正しいが記録側が壊れている」組み合わせを永久に見逃す —
/// 記録と解決の突き合わせこそが T3 の主眼である。
/// </summary>
internal static class Recording
{
    /// <summary>
    /// 対象アプリの指定コントロールを座標から記録する。
    /// 記録できたのが本当に狙ったコントロールかをここで検証する
    /// (ハーネス自身の検証 — docs/TESTING.md §2)。
    /// </summary>
    public static async Task<TriggerDefinition> RecordAsync(
        TestTargetProcess target, string controlName, string triggerId = "t")
    {
        (int x, int y) = target.CenterOf(controlName);

        // 記録もユーザーと同じ経路で行う。記録の入口は UiaSession に統合されている
        await using var session = new UiaSession();
        TriggerDefinition definition = await session.BuildDefinitionFromPointAsync(x, y);

        Assert.True(
            definition.Locator.Steps.Count > 0,
            $"({x},{y}) の記録が空の経路になりました。ウィンドウが他に覆われている可能性があります。");
        Assert.True(
            string.Equals(definition.Locator.Steps[^1].AutomationId, controlName, StringComparison.Ordinal),
            $"'{controlName}' を記録したつもりが '{definition.Locator.Steps[^1].AutomationId}' を掴んでいます " +
            $"(座標 {x},{y})。ウィンドウの重なりか DPI 認識の問題です。");

        definition.Id = triggerId;
        return definition;
    }

    /// <summary>
    /// <c>AutomationId</c> を持たない要素を座標から記録する (<c>add-anonymous</c> と対で使う)。
    ///
    /// <para>
    /// <see cref="RecordAsync"/> は「狙ったコントロールを掴めたか」を <c>AutomationId</c> で
    /// 確かめるが、AutomationId が無い要素ではそれができない。代わりに UIA の <c>Name</c> で
    /// 同じ検証を行い、<b>AutomationId が本当に空であること</b>もここで固定する —
    /// そうでないと「Search 方式に落ちなかった」ことを確かめるテストの前提が崩れる。
    /// </para>
    /// </summary>
    public static async Task<TriggerDefinition> RecordAnonymousAsync(
        TestTargetProcess target, string label, string triggerId = "t")
    {
        (int x, int y) = target.CenterOf(label);

        await using var session = new UiaSession();
        TriggerDefinition definition = await session.BuildDefinitionFromPointAsync(x, y);

        Assert.True(
            definition.Locator.Steps.Count > 0,
            $"({x},{y}) の記録が空の経路になりました。ウィンドウが他に覆われている可能性があります。");
        ElementPathStep last = definition.Locator.Steps[^1];
        Assert.True(
            string.Equals(last.Name, label, StringComparison.Ordinal),
            $"'{label}' を記録したつもりが Name='{last.Name}' の要素を掴んでいます (座標 {x},{y})。");
        Assert.True(
            string.IsNullOrEmpty(last.AutomationId),
            $"'{label}' に AutomationId '{last.AutomationId}' が付いています。" +
            "対象アプリが匿名の要素を作れていないので、このテストには検出力がありません。");

        definition.Id = triggerId;
        return definition;
    }

    /// <summary>
    /// ウィンドウをこのプロセスのウィンドウに固定する。
    /// 取り違えたまま通ったテストは、通っていないのと同じなので明示的に縛る。
    /// </summary>
    public static TriggerDefinition PinToWindow(this TriggerDefinition definition, TestTargetProcess target)
    {
        definition.Window.WindowName = target.Title;
        definition.Window.WindowNameMatch = MatchStrength.Required;
        return definition;
    }

    /// <summary>
    /// Search 方式 (一意な <c>AutomationId</c> による FindAll 1 発) を外し、経路方式だけを使わせる。
    ///
    /// <b>経路解決そのものを見るテストは必ずこれを通すこと。</b> WinForms は UIA の
    /// <c>AutomationId</c> にコントロールの <c>Name</c> を出すため、外さないと T3 のほぼ全部が
    /// Search 方式で通ってしまい、経路側の回帰 (A3 の兄弟挿入・A5 の型変化) を検出できなくなる。
    /// あわせて「記録側が Search を作れていること」もここで見る。
    /// </summary>
    public static TriggerDefinition WithoutSearch(this TriggerDefinition definition)
    {
        Assert.NotNull(definition.Locator.Search);
        definition.Locator.Search = null;
        return definition;
    }

    /// <summary>プロパティ変化で発火する句を付ける (値は問わない)。</summary>
    public static TriggerDefinition WatchName(this TriggerDefinition definition)
    {
        definition.On = TriggerOn.PropertyChanged;
        definition.Clauses.Clear();
        definition.Clauses.Add(new PropertyClause { Property = TriggerProperty.Name, Op = ComparisonOp.Always });
        return definition;
    }

    public static ElementPathStep LastStep(this TriggerDefinition definition)
        => definition.Locator.Steps[^1];
}
