// Enter が「目の前の欄の仕事」で止まること (docs/DESIGN.md A25 / docs/TESTING.md §3)。
//
// **ここでしか見られない。**既定ボタン (WPF の IsDefault / Windows Forms の AcceptButton /
// WinUI の自前 KeyDown) と入力欄のどちらが Enter を取るかは、キーが実際に窓へ届いて
// フレームワークの規則を通らないと決まらない。UIA のコントロールパターンは Invoke を
// 要素へ直接届けるので、この分岐を一度も通らない (T4 が来られない理由)。
//
// T1 側は Windows Forms の判断 (HandleDialogKey) を窓を出さずに縛っているが、
// **WPF / WinUI は routed event を組み立てられない**ので、あちらの担保はここである。
//
// フックではなく通常のフォーカス経路なので、撃つ前に UIA でフォーカスを与え、
// UIA で狙いを検算する (SetFocus / HasKeyboardFocus — どちらも合成入力ではない)。
using System.Windows.Automation;
using UiaTrigger.Picker.UiTests;
using Xunit;

namespace UiaTrigger.Input.Tests;

/// <summary>合成した Enter が、フォーカスのある欄の仕事で止まること。</summary>
public sealed class EnterKeyTests
{
    /// <summary>「何も起きない」ことを見る窓。</summary>
    private static readonly TimeSpan NothingHappens = TimeSpan.FromSeconds(3);

    /// <summary>
    /// **編集セッションのピッカーで、検索欄の Enter が確定にならないこと。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// エディタからの編集は常に編集セッションなので、確定が Enter に載っている。検索欄が
    /// 自分で Enter を使うと宣言しているのに <c>Handled</c> を立てないと、木を検索しようと
    /// した Enter が**書きかけごと窓を閉じる**。3 変種とも症状は同じで、Windows Forms だけは
    /// さらに重い (単一行 <c>TextBox</c> の Enter は <c>ProcessDialogKey</c> が先に取るので、
    /// 検索欄の <c>KeyDown</c> ハンドラーが一度も呼ばれない)。ここで駆動するのは WinUI である。
    /// </para>
    /// <para>
    /// **ネガティブコントロールは後ろに置く。**「閉じなかった」だけでは
    /// 「Enter がどこにも届いていない」と区別できないので、確定ボタンへフォーカスを移して
    /// 同じキーを撃ち、**そちらでは閉じる**ことを示す。順序が逆だと窓が無くなって続けられない。
    /// </para>
    /// <para>
    /// 退行: <c>TriggerPickerWindow.OnSearchKeyDown</c> から <c>e.Handled = true</c> を外す
    /// → 検索欄の Enter で窓が閉じ、最初の <c>Ui.Never</c> が落ちる。
    /// </para>
    /// </remarks>
    [Fact]
    public void EnterInTheSearchBox_DoesNotCommitTheEditSession()
    {
        using var cursor = SyntheticInput.CursorGuard.Save();
        using var scenario = EditorScenario.Open();

        scenario.OpenEditor();
        scenario.RecordOneThroughTheChildPicker(out _);
        scenario.Accept();

        scenario.OpenEditor();
        scenario.SelectTheFirstRow();
        AutomationElement picker = scenario.EditTheSelectedRow();

        // 本題。検索欄に狙いを定めて撃つ
        AutomationElement search = picker.RequireByIdEventually("SearchBox", scenario.Diagnostics);
        scenario.RequireKeyboardFocus(search, "ピッカーの検索欄");
        search.SetText("nothing-matches-this");
        SyntheticInput.TapKey(SyntheticInput.VkEnter);

        Ui.Never(
            () => scenario.PickerWindowCount() == 0,
            NothingHappens,
            "検索欄の Enter でピッカーが確定して閉じてしまう",
            scenario.Diagnostics);

        // ネガティブコントロール: 同じキーが確定ボタンでは効くこと。
        // これが無いと上の緑は「Enter が届いていない」と区別できない
        AutomationElement commit = picker.RequireByIdEventually("CommitButton", scenario.Diagnostics);
        scenario.RequireKeyboardFocus(commit, "ピッカーの確定ボタン");
        SyntheticInput.TapKey(SyntheticInput.VkEnter);

        _ = Ui.Until(
            () => scenario.PickerWindowCount() == 0 ? "ok" : null,
            EditorScenario.Settle,
            "確定ボタンにフォーカスがある Enter はピッカーを閉じること (Enter は届いている)",
            scenario.Diagnostics);
    }

    /// <summary>
    /// **エディタの式欄で、Enter が [まとめる] になり [OK] にならないこと。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// 下段の 3 欄は [まとめる/更新] を押して初めて効く — <c>Snapshot</c> は式を読まない。
    /// 既定ボタンに流すと**打ちかけの式を捨ててエディタが閉じる**。
    /// </para>
    /// <para>
    /// 見るのは 2 つで、**まとめられたこと**のほうが本体である。「閉じなかった」だけだと、
    /// キーがどこにも届かなくても緑になる。複合の行が出たなら presenter まで届いている。
    /// </para>
    /// <para>
    /// 退行: <c>TriggerListEditorWindow</c> の <c>OnCombineFieldKeyDown</c> の配線を外す
    /// → Enter が [OK] に取られてエディタが閉じ、<c>Ui.Never</c> が落ちる。
    /// </para>
    /// </remarks>
    [Fact]
    public void EnterInTheExpressionBox_CombinesInsteadOfAccepting()
    {
        using var cursor = SyntheticInput.CursorGuard.Save();
        using var scenario = EditorScenario.Open();

        scenario.OpenEditor();
        scenario.RecordOneThroughTheChildPicker(out string first);
        scenario.RecordAnotherWithId("second");
        scenario.Accept();

        scenario.OpenEditor();
        scenario.SelectEveryRow();
        // 式は既定 (全部) でよいが、**式欄に文字を置いてから撃つ**のが要点である —
        // 捨てられるのはまさにこれである
        scenario.SetExpression($"{first} || second");

        AutomationElement expression =
            scenario.EditorWindow().RequireByIdEventually("ExpressionBox", scenario.Diagnostics);
        scenario.RequireKeyboardFocus(expression, "エディタの式欄");
        SyntheticInput.TapKey(SyntheticInput.VkEnter);

        _ = Ui.Until(
            () => scenario.CompositeRow(),
            EditorScenario.Settle,
            "式欄の Enter が [まとめる] として効くこと",
            scenario.Diagnostics);
        Assert.True(scenario.EditorIsShowing(), "式欄の Enter でエディタが閉じてはいけない");

        // ネガティブコントロール: 同じキーが [OK] では効くこと。これが無いと上の緑は
        // 「Enter がエディタにまったく届いていない」と区別できない。
        // **一覧では見られない** — WinUI の ListView は SetFocus を受け取れない (実測)
        AutomationElement accept =
            scenario.EditorWindow().RequireByIdEventually("AcceptButton", scenario.Diagnostics);
        scenario.RequireKeyboardFocus(accept, "エディタの [OK]");
        SyntheticInput.TapKey(SyntheticInput.VkEnter);

        _ = Ui.Until(
            () => scenario.EditorIsShowing() ? null : "ok",
            EditorScenario.Settle,
            "[OK] にフォーカスがある Enter はエディタを閉じること (Enter は届いている)",
            scenario.Diagnostics);
    }
}
