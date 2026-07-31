// K2: ピッカー以外のアプリへ ←/→ が届くこと (docs/TESTING.md §3)。
// MANUAL-CHECKS §2 の一次の網。
//
// これは「狙い」が要る組である (docs/TESTING.md §3 の解禁条件 1)。撃つ前に、対象アプリの TextBox が
// 本当にキーボードフォーカスを持っていることを<b>別実装 (UIA) で</b>検算する。
// 検算が通らなければ「入力が届かなかった」ではなく
// 「ハーネスが狙いを立てられなかった」として落とす。
//
// <b>M1 / M2 (クリック) はここに無い。</b>M1 は OverlayClickTests が持つ。
// M2 は検出力を示せず自動化を見送られており (docs/TESTING.md §3)、
// MANUAL-CHECKS §3 のアイコンの項目は<b>人の項目のまま</b>である。
using System.Globalization;
using System.Windows.Automation;
using UiaTrigger.Picker.UiTests;
using Xunit;

namespace UiaTrigger.Input.Tests;

/// <summary>ピッカー以外への配送 — 対象アプリのキャレットで見る。</summary>
public sealed class TargetInputTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    /// <summary>
    /// ピッカーがフックを仕掛けている最中でも、<b>他のアプリに ←/→ が普通に効く</b>こと (MANUAL-CHECKS §2)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// フックはキーを<b>吸収しない</b> — 通知だけして <c>CallNextHookEx</c> で流す。
    /// これが崩れると、ピッカーを開いているあいだ**システム全体で ←/→ が効かなくなる**。
    /// 症状がピッカーの外に出るので、ピッカーのテストでは絶対に捕まらない類である。
    /// </para>
    /// <para>
    /// <b>前面化は「実際のクリック」で行う。</b>背景のプロセスへフォーカスを移す方法は
    /// 他に無い — <c>Form.Activate()</c> はフォアグラウンドロックで拒否される。実測では、
    /// アプリの中では <c>Focused == true</c> になるのに <b>UIA のフォーカスはピッカーのまま</b>で、
    /// ←/→ はキャレットを動かさなかった。フォアグラウンド規則は<b>実入力を尊重する</b>ので、
    /// クリックだけがこの狙いを立てられる。
    /// </para>
    /// <para>
    /// <b>← を使う。</b>クリックで置いたキャレットは行末に来るので、
    /// → では clamp されて動かない (実測で 2 度踏んだ形)。
    /// </para>
    /// <para>
    /// 退行: <c>HookProc</c> の <c>CallNextHookEx</c> をやめて 1 を返す (キーを吸収する) →
    /// キャレットが動かず落ちる。
    /// </para>
    /// </remarks>
    [Fact]
    public void WhileThePickerHooksTheKeyboard_OtherAppsStillReceiveArrows()
    {
        using var cursor = SyntheticInput.CursorGuard.Save();
        using var scenario = OverlayScenario.Open(pickers: 1);
        _ = scenario.WaitForOverlays(1);

        scenario.Target.Send("add-edit TargetEdit");
        scenario.Target.Send("ping");

        // 狙いを立てる: 実クリックで対象アプリを前面へ出し、TextBox にフォーカスを置く
        (int left, int top, int right, int bottom) = scenario.Target.RectOf("TargetEdit");
        SyntheticInput.CursorGuard.MoveTo((left + right) / 2, (top + bottom) / 2);
        SyntheticInput.TapLeftButton();

        // 狙いの検算 — <b>別実装 (UIA) で</b>確かめる。アプリの自己申告 (Control.Focused) は
        // 前面化まで保証しないので、これだけを信じてはいけない
        _ = Ui.Until(
            () => HasKeyboardFocus(scenario, "TargetEdit") ? "ok" : null,
            Settle,
            "対象アプリの TextBox がキーボードフォーカスを持つこと " +
            "(ここで落ちたら、入力が届かなかったのではなくハーネスが狙いを立てられなかった)",
            () => "UIA のフォーカス: " + DescribeFocus() +
                  Environment.NewLine +
                  "アプリの中での選択: " + scenario.Target.TryFocus("TargetEdit"));

        int before = scenario.Target.CaretOf("TargetEdit");
        Assert.True(before > 0, $"キャレットが行頭 ({before}) に居ます。← で動かせません。");

        SyntheticInput.TapKey(SyntheticInput.VkLeft);

        _ = Ui.Until(
            () => scenario.Target.CaretOf("TargetEdit") == before - 1 ? "ok" : null,
            Settle,
            FormattableString.Invariant($"← で対象アプリのキャレットが {before} から {before - 1} へ動くこと"),
            () => FormattableString.Invariant($"いまのキャレット: {scenario.Target.CaretOf("TargetEdit")}") +
                  Environment.NewLine + "UIA のフォーカス: " + DescribeFocus());
    }

    private static bool HasKeyboardFocus(OverlayScenario scenario, string automationId)
    {
        try
        {
            AutomationElement root = AutomationElement.FromHandle((nint)scenario.Target.WindowHandle());
            return root.ById(automationId) is { } e && e.Current.HasKeyboardFocus;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or ArgumentException)
        {
            return false;
        }
    }

    private static string DescribeFocus()
    {
        try
        {
            AutomationElement? f = AutomationElement.FocusedElement;
            return f is null
                ? "(なし)"
                : FormattableString.Invariant($"pid={f.Current.ProcessId} ") +
                  $"id='{f.Current.AutomationId}' type={f.Current.ControlType.ProgrammaticName}";
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            return "(読めません: " + ex.Message + ")";
        }
    }
}
