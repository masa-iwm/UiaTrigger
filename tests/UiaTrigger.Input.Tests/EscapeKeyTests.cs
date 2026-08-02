// Esc でピッカーの窓が閉じること (docs/TESTING.md §3)。
// MANUAL-CHECKS §4.3.1 の一次の網。
//
// **ここでしか見られない。**Esc は View のキー処理 (WPF は KeyDown、Windows Forms は
// ProcessDialogKey、WinUI は中身の KeyDown) を通るので、UIA のコントロールパターンでは
// 一度も通らない経路である。T1 は Close() を直に呼べるだけで、「キーが窓に届いて閉じる」
// ことは主張できない。
//
// **←/→ と違って、これはフックではなく通常のフォーカス経路である。**
// だから窓に明示的にフォーカスを与えてから撃つ (UIA の SetFocus — 合成入力ではない)。
using System.Windows.Automation;
using UiaTrigger.Picker.UiTests;
using Xunit;

namespace UiaTrigger.Input.Tests;

/// <summary>合成した Esc がピッカーの窓を閉じること。</summary>
public sealed class EscapeKeyTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    /// <summary>関係の無いキーで閉じないことを見る窓。</summary>
    private static readonly TimeSpan NothingHappens = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Esc でピッカーの窓が閉じること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **ネガティブコントロールを先に置く。**← を撃っても閉じないことを確かめてから Esc を撃つ。
    /// これが無いと、Esc の緑が「Esc で閉じた」ではなく「何であれキーを撃つと閉じる」と
    /// 区別できない。← が実際に窓へ届くことは <see cref="ArrowKeyTests"/> が別に示している。
    /// </para>
    /// <para>
    /// 閉じたことを窓の数で見るのは、**フックとセッションの後始末まで含めて**確かめたいからである。
    /// 3 変種とも <c>Closed</c> で <c>Dispose</c> する契約なので、窓が消えれば低レベル
    /// キーボードフックも UIA セッションも畳まれている。
    /// </para>
    /// <para>
    /// 退行: <c>TriggerPickerWindow</c> の <c>KeyDown</c> の配線を外す → Esc で閉じず期限で落ちる。
    /// </para>
    /// </remarks>
    [Fact]
    public void Escape_ClosesThePickerWindow()
    {
        using var cursor = SyntheticInput.CursorGuard.Save();
        using var scenario = OverlayScenario.Open(pickers: 1);

        AutomationElement picker = scenario.Host.PickerWindow();
        // 通常のフォーカス経路を通るので、狙いを定めてから撃つ。
        // ここを省くと、ランナーや対象アプリが前面のときに間欠で落ちる
        picker.SetFocus();

        SyntheticInput.TapKey(SyntheticInput.VkLeft);
        Ui.Never(
            () => scenario.Host.PickerWindows().Count == 0,
            NothingHappens,
            "← でピッカーの窓が閉じてしまう",
            scenario.Host.Diagnostics);

        SyntheticInput.TapKey(SyntheticInput.VkEscape);

        _ = Ui.Until(
            () => scenario.Host.PickerWindows().Count == 0 ? "ok" : null,
            Settle,
            "Esc でピッカーの窓が閉じること",
            scenario.Host.Diagnostics);
    }

    /// <summary>
    /// **行のダブルクリックで開いた子ピッカーでも Enter が効くこと。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// ボタン ([条件を編集]) 経由とは**フォーカスの行き先が違う**。ダブルクリックは
    /// エディタの一覧にキーボードフォーカスを置いたままにするので、子ピッカーの中の
    /// 要素は誰もフォーカスを持たない。WinUI の <c>KeyDown</c> は中の要素が持っていないと
    /// 発火しないため、**キーがどちらの窓にも届かない**。
    /// </para>
    /// <para>
    /// ここだけ合成マウスが要る (解禁条件は docs/TESTING.md §3)。UIA の
    /// <c>Invoke</c> ではダブルクリックの入力系列が起きず、この経路を一度も通らない。
    /// </para>
    /// </remarks>
    [Fact]
    public void EnterCommitsOnAChildPickerOpenedByDoubleClick()
    {
        using var cursor = SyntheticInput.CursorGuard.Save();
        using var scenario = EditorScenario.Open();

        scenario.OpenEditor();
        scenario.RecordOneThroughTheChildPicker(out _);
        scenario.Accept();

        scenario.OpenEditor();
        // 行の中心でダブルクリック。2 発を続けて撃つ (既定のダブルクリック時間は 500ms で、
        // SendInput の往復はそれよりずっと短い)
        (int x, int y) = scenario.FirstRowCenter();
        SyntheticInput.CursorGuard.MoveTo(x, y);
        SyntheticInput.TapLeftButton();
        SyntheticInput.TapLeftButton();
        _ = Ui.Until(
            () => scenario.PickerWindowCount() == 1 ? "ok" : null,
            EditorScenario.Settle,
            "ダブルクリックで子ピッカーが開くこと",
            scenario.Diagnostics);

        // **Enter のほうを見る。**ダブルクリックで開いた窓は編集セッションなので、
        // Enter は「トリガーを更新」して窓を閉じるはずである
        SyntheticInput.TapKey(SyntheticInput.VkEnter);

        _ = Ui.Until(
            () => scenario.PickerWindowCount() == 0 ? "ok" : null,
            EditorScenario.Settle,
            "ダブルクリックで開いた子ピッカーが Enter で確定して閉じること",
            scenario.Diagnostics);
        Assert.True(scenario.EditorIsShowing(), "エディタは残ること");
    }

    /// <summary>
    /// **開いた直後の子ピッカーが、そのまま Esc で閉じること。**
    /// </summary>
    /// <remarks>
    /// フォーカスを与え直さずに撃つ。WinUI の <c>KeyDown</c> は**中の要素**が
    /// キーボードフォーカスを持っていないと発火しないので、窓を出しただけでは
    /// キーがどこにも届かないことがありうる。
    /// </remarks>
    [Fact]
    public void EscapeWorksOnAFreshlyOpenedChildPicker()
    {
        using var cursor = SyntheticInput.CursorGuard.Save();
        using var scenario = EditorScenario.Open();

        scenario.OpenEditor();
        scenario.RecordOneThroughTheChildPicker(out _);
        scenario.Accept();

        scenario.OpenEditor();
        scenario.SelectTheFirstRow();
        _ = scenario.EditTheSelectedRow();

        // **フォーカスに触らない。**開いた直後の状態のまま撃つ
        SyntheticInput.TapKey(SyntheticInput.VkEscape);

        _ = Ui.Until(
            () => scenario.PickerWindowCount() == 0 ? "ok" : null,
            EditorScenario.Settle,
            "開いた直後の子ピッカーが Esc で閉じること",
            scenario.Diagnostics);
        Assert.True(scenario.EditorIsShowing(), "エディタは残ること");
    }

    /// <summary>
    /// エディタから開いた子ピッカーの Esc が、**子ピッカーだけ**を閉じること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **WinUI ホストで見る。**あの変種にだけ Enter/Esc の配線が要る (WPF の
    /// <c>IsCancel</c> / Windows Forms の <c>CancelButton</c> に相当するものが無い) ため、
    /// エディタと子ピッカーの両方が Esc を見にいく形になる。
    /// </para>
    /// <para>
    /// **狙いはフォーカスがエディタ側に在るときである。**あちらが Esc を拾うと
    /// エディタが閉じ、閉じたエディタは契約どおり子ピッカーも閉じる (<c>OnClosed</c>) ので、
    /// **1 回の Esc で両方消える**。子ピッカーへフォーカスを与えてから撃つと、
    /// エディタは端からキーを見ないので**この壊れ方は再現しない** — テストの狙いを
    /// そこへ置くと、緑が何も意味しなくなる。
    /// </para>
    /// <para>
    /// 退行: <c>TriggerListEditorWindow.OnKeyDown</c> の「子ピッカーが開いている間は
    /// 何もしない」を外す → エディタが閉じて落ちる。
    /// </para>
    /// </remarks>
    [Fact]
    public void EscapeInTheChildPicker_ClosesOnlyThePicker()
    {
        using var cursor = SyntheticInput.CursorGuard.Save();
        using var scenario = EditorScenario.Open();

        // 1 件録って確定し、**エディタを開き直してから** [条件を編集] で新しい子ピッカーを出す。
        // 録った直後の子ピッカーは開いたままで、そのまま [条件を編集] を押すと presenter が
        // 同じ窓を使い回すため「新しい窓が出る」を待つ組み立てが成立しない
        // (EditingThroughTheEditor_ShowsUpdateAndClosesThePickerOnCommit と同じ順)
        scenario.OpenEditor();
        scenario.RecordOneThroughTheChildPicker(out _);
        scenario.Accept();

        scenario.OpenEditor();
        scenario.SelectTheFirstRow();
        AutomationElement picker = scenario.EditTheSelectedRow();
        Assert.True(scenario.EditorIsShowing(), "撃つ前はエディタも開いていること");

        // **本題。**エディタ側にフォーカスがある状態で撃つ。行のダブルクリックで開いたときの
        // 実際の状態がこれで、そのまま拾うとエディタが閉じ (閉じたエディタは子も閉じる)、
        // 握り潰すと**どちらも動かない**。正しくは子ピッカーが閉じ、エディタが残る
        _ = picker;
        scenario.FocusEditor();
        SyntheticInput.TapKey(SyntheticInput.VkEscape);

        _ = Ui.Until(
            () => scenario.PickerWindowCount() == 0 ? "ok" : null,
            EditorScenario.Settle,
            "エディタにフォーカスがあっても Esc で子ピッカーが閉じること",
            scenario.Diagnostics);
        Ui.Never(
            () => !scenario.EditorIsShowing(),
            NothingHappens,
            "子ピッカーの Esc でエディタまで閉じてしまう",
            scenario.Diagnostics);
    }
}
