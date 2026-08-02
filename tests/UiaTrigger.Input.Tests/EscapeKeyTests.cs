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
}
