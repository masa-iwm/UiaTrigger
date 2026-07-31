// K3 / K5: フックの寿命と行き先 (docs/DESIGN.md §11 / docs/TESTING.md §3)。
// MANUAL-CHECKS §2 / §6 の一次の網。
//
// どれも合成入力でしか起こせない。低レベルフックに届くのは実際の入力だけで、
// UIA のコントロールパターンはこの経路を一度も通らない。
using System.Globalization;
using System.Windows.Automation;
using UiaTrigger.Picker.UiTests;
using Xunit;

namespace UiaTrigger.Input.Tests;

/// <summary>閉じたピッカーは ←/→ に反応せず、残ったピッカーは反応し続けること。</summary>
public sealed class HookLifetimeTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    /// <summary>「起きないこと」を確かめる窓 (<c>ArrowKeyTests</c> と同じ見積もり)。</summary>
    private static readonly TimeSpan NothingHappens = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 1 枚閉じても、**もう 1 枚は ←/→ に反応し続ける**こと
    /// (MANUAL-CHECKS §6「片方を閉じても、もう片方が動き続ける」)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **A18 の本題を入力の側から見る。**オーバーレイを static singleton にすると、
    /// 1 枚目を閉じたとき共有しているウィンドウごと消える。<c>OverlayTests</c> は
    /// 「枠が残る」ことを見ているが、**残った枠がまだ動くか**はここでしか見られない —
    /// フックはピッカーごとに別のスレッドで仕掛けられており、閉じるほうの後始末が
    /// 残るほうのフックまで外していないか、は実際にキーを撃たないと分からない。
    /// </para>
    /// <para>
    /// **閉じる前に、残るほうが動くことを確かめてはいない。**確かめようがない —
    /// どのオーバーレイがどのピッカーのものかを言う手段が無いので、
    /// 「閉じる前に動いた枠」と「閉じた後に残った枠」が同じものだと言えない。
    /// 代わりに**閉じた後の 1 枚が動くこと**だけを見る。これで狙いは果たす:
    /// 閉じる側が残る側のフックを外せば、残った 1 枚は動かなくなる。
    /// </para>
    /// <para>
    /// 退行: <c>OverlayController</c> を static singleton へ戻す → 閉じた時点で
    /// オーバーレイが 0 個になり、待ち受けの手前で落ちる。
    /// </para>
    /// </remarks>
    [Fact]
    public void ClosingOnePicker_LeavesTheOtherRespondingToArrows()
    {
        using var cursor = SyntheticInput.CursorGuard.Save();
        using var scenario = OverlayScenario.Open(pickers: 2);
        _ = scenario.WaitForOverlays(2);

        scenario.CloseOnePicker();
        List<System.Windows.Rect> remaining = scenario.WaitForOverlays(1);

        SyntheticInput.TapKey(SyntheticInput.VkLeft);
        WaitUntilMoved(scenario, remaining, expected: 1, "残った 1 枚の枠が ← で動くこと");
    }

    /// <summary>
    /// ピッカーを閉じた後は、←/→ で**枠が出ない**こと (MANUAL-CHECKS §2)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **先に「閉じる前は動く」ことを確かめる。**これを飛ばすと、
    /// 端に居て動きようがなかった場合と区別できない (<c>ArrowKeyTests</c> の
    /// ネガティブコントロールと同じ手順。実測で踏んだ形である)。
    /// </para>
    /// <para>
    /// **このテストの検出力について正直に書く。**計画は退行として
    /// 「<c>Dispose</c> から <c>UnhookWindowsHookEx</c> を落とす」を挙げていたが、
    /// **それでは落ちない** (実測 — docs/TESTING.md §3 の K3)。枠を出す経路は閉じるときに 3 つとも切れる:
    /// <c>ArrowKeyPressed</c> のハンドラーが外れ、登録表から消え
    /// (<c>Unregister()</c> は <c>_hook?.Dispose()</c> の**前**である)、
    /// ウィンドウ自体が壊される。フックの解除は**いちばん外側**にあり、
    /// 外から見える差を作らない。
    /// </para>
    /// <para>
    /// つまりここが固定しているのは「フックが解除されたこと」ではなく
    /// **「閉じたピッカーが二度と枠を出さないこと」**である。
    /// 実際に落とせる退行は <c>TriggerPickerPresenter.Dispose</c> の後始末を外す形で、
    /// それは docs/TESTING.md §3 (K3) に書いてある。**フックのハンドル自体が漏れているかは、
    /// 外からは観測できない** — そこは人の項目として残る (MANUAL-CHECKS §2)。
    /// </para>
    /// </remarks>
    [Fact]
    public void AfterThePickerIsClosed_ArrowsShowNoFrame()
    {
        using var cursor = SyntheticInput.CursorGuard.Save();
        using var scenario = OverlayScenario.Open(pickers: 1);
        List<System.Windows.Rect> before = scenario.WaitForOverlays(1);

        // 閉じる前に、同じキーで実際に動くこと
        SyntheticInput.TapKey(SyntheticInput.VkLeft);
        WaitUntilMoved(scenario, before, expected: 1, "閉じる前に、同じキーで枠が動くこと");

        scenario.CloseOnePicker();
        _ = Ui.Until(
            () => scenario.Host.Overlays().Count == 0 ? "ok" : null,
            Settle,
            "ピッカーを閉じるとオーバーレイが 0 個になること",
            scenario.Describe);

        SyntheticInput.TapKey(SyntheticInput.VkLeft);
        Ui.Never(
            () => scenario.Host.Overlays().Count > 0,
            NothingHappens,
            "閉じたピッカーが ← で枠を出すこと",
            () => FormattableString.Invariant(
                      $"オーバーレイ数: {scenario.Host.Overlays().Count}") +
                  Environment.NewLine + scenario.Host.HostLog());
    }

    /// <summary>
    /// ←/→ が**フックを有効にしているピッカーだけ**に届くこと (MANUAL-CHECKS §6)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **2 段構えである。**先に両方 ON で撃って**2 つとも動く**ことを見る —
    /// これが**計数に検出力があること**の証明で、これを示さないと
    /// 後半の「1 つだけ動いた」が「たまたま 1 つしか動かなかった」と区別できない
    /// (<c>OverlayTests.OnePicker_ShowsExactlyOneOverlay</c> と同じ役割)。
    /// </para>
    /// <para>
    /// **どちらのピッカーを OFF にしたかは言わない。**ピッカーの窓とオーバーレイを
    /// 対応づける手段が無いので、「OFF にしたほうの枠が動かなかった」は書けない。
    /// 書けるのは**動いた枠の数**だけである。それで足りる —
    /// フックがピッカーごとに切れていなければ 2 つとも動く。
    /// </para>
    /// <para>
    /// 退行: <c>SetAutoSelect</c> を <c>SetHookEnabled(true)</c> 固定にする →
    /// 片方を OFF にしても 2 つとも動いて落ちる。
    /// </para>
    /// </remarks>
    [Fact]
    public void ArrowsReachOnlyThePickersWithTheHookEnabled()
    {
        using var cursor = SyntheticInput.CursorGuard.Save();
        using var scenario = OverlayScenario.Open(pickers: 2);
        List<System.Windows.Rect> bothOn = scenario.WaitForOverlays(2);

        // (1) 計数に検出力があること — 両方 ON なら 2 つとも動く
        SyntheticInput.TapKey(SyntheticInput.VkLeft);
        List<System.Windows.Rect> afterBoth = WaitUntilMoved(
            scenario, bothOn, expected: 2, "両方 ON のとき、2 つとも枠が動くこと");

        // (2) 片方だけ OFF にすると、動くのは 1 つだけ
        DisableAutoSelectOnOnePicker(scenario);

        SyntheticInput.TapKey(SyntheticInput.VkLeft);
        _ = WaitUntilMoved(
            scenario, afterBoth, expected: 1, "片方を OFF にすると、動く枠が 1 つだけになること");
    }

    /// <summary>
    /// 枠が <paramref name="expected"/> 個だけ動くまで待ち、**それ以上動かないことも見る**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「1 つ動いた」で止めると、**まだ 2 つ目が動く途中**だったときに通ってしまう。
    /// 数が揃ってから、短い窓のあいだ増えないことまで確かめる。
    /// </para>
    /// <para>
    /// **対応付けではなく数で見る。**どのオーバーレイがどのピッカーのものかを
    /// 言う手段が無いのは <c>OverlayTests</c> と同じ理由である。
    /// </para>
    /// <para>
    /// オーバーレイの**個数**が変わったときは待ち続けない — 枠が消えたのは
    /// 「動いた」ではなく**別の失敗**であり、数え方の前提が崩れている。
    /// </para>
    /// </remarks>
    private static List<System.Windows.Rect> WaitUntilMoved(
        OverlayScenario scenario, List<System.Windows.Rect> before, int expected, string what)
    {
        List<System.Windows.Rect> settled = Ui.Until(
            () =>
            {
                List<System.Windows.Rect> now = CurrentRects(scenario);
                return now.Count == before.Count && MovedCount(before, now) == expected ? now : null;
            },
            Settle,
            what,
            () => Diagnose(scenario, before, expected));

        Ui.Never(
            () =>
            {
                List<System.Windows.Rect> now = CurrentRects(scenario);
                return now.Count == before.Count && MovedCount(before, now) > expected;
            },
            NothingHappens,
            $"{what} — 期待より多くの枠が動くこと",
            () => Diagnose(scenario, before, expected));

        return settled;
    }

    /// <summary>
    /// <paramref name="before"/> に無い矩形の数。
    /// </summary>
    /// <remarks>
    /// 2 枚が同じ要素へ移ると矩形が一致しうるので、集合ではなく**個数**で数える。
    /// **塞げていない穴を書いておく**: 動いた先が、もう一方の**元の**矩形と
    /// ちょうど一致すると 1 つ少なく数える。捕捉させているのは別々のボタンなので
    /// 実際には起きないが、原理的には起こりうる。
    /// </remarks>
    private static int MovedCount(List<System.Windows.Rect> before, List<System.Windows.Rect> after)
        => after.Count(r => !before.Contains(r));

    private static List<System.Windows.Rect> CurrentRects(OverlayScenario scenario)
        => [.. scenario.Host.Overlays().Select(o => o.Current.BoundingRectangle)];

    /// <summary>
    /// ピッカーを 1 枚選んで自動選択を OFF にする。**OFF になったことを確かめてから返る。**
    /// </summary>
    private static void DisableAutoSelectOnOnePicker(OverlayScenario scenario)
    {
        IReadOnlyList<AutomationElement> windows = scenario.Host.PickerWindows();
        Assert.True(
            windows.Count == 2,
            $"ピッカーの窓が {windows.Count} 枚しか掴めません (2 枚を別々に操作できません)。");

        ((TogglePattern)windows[0].RequireById("AutoSelectToggle")
            .GetCurrentPattern(TogglePattern.Pattern)).Toggle();

        _ = Ui.Until(
            () => scenario.Host.PickerWindows() is { Count: 2 } now &&
                  ((TogglePattern)now[0].RequireById("AutoSelectToggle")
                      .GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.Off
                ? "ok" : null,
            Settle,
            "1 枚目の自動選択が OFF になること",
            scenario.Describe);
    }

    private static string Diagnose(
        OverlayScenario scenario, List<System.Windows.Rect> before, int expected)
    {
        List<System.Windows.Rect> now = CurrentRects(scenario);
        return string.Join(
            Environment.NewLine,
            FormattableString.Invariant($"期待した「動いた枠」の数: {expected}"),
            "撃つ前: " + Describe(before),
            "いま: " + Describe(now),
            FormattableString.Invariant($"動いた数: {MovedCount(before, now)}"),
            scenario.Describe());
    }

    private static string Describe(List<System.Windows.Rect> rects)
        => rects.Count == 0
            ? "(なし)"
            : string.Join(" / ", rects.Select(r => r.ToString(CultureInfo.InvariantCulture)));
}
