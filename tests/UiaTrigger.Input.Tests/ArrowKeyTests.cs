// K1: 選択モード中の ←/→ で重なり切替が起きること (docs/TESTING.md §3)。
// MANUAL-CHECKS §2 / §4.3.1 / §6 の一次の網。
//
// <b>ここでしか見られない。</b>重なり切替の入口は WH_KEYBOARD_LL だけである
// (OverlayController.HookProc → ArrowKeyPressed → MoveStackAsync)。UIA の
// コントロールパターンはこの経路を<b>一度も通らない</b>ので、T1 も T4 も
// MoveStackAsync を直接呼ぶことしかできない — 「キーがフックに届く」ことは主張できない。
//
// <b>座標の検算は要らない</b> (docs/TESTING.md §3 の解禁条件 1)。低レベルフックはグローバルで、
// フォアグラウンドがどこであっても発火する。狙いが要るのは対象アプリへ届けるとき
// (K2) と、ピクセルを押すとき (M1/M2) である。
using System.Globalization;
using System.Windows.Automation;
using UiaTrigger.Picker.UiTests;
using Xunit;

namespace UiaTrigger.Input.Tests;

/// <summary>合成した ←/→ が重なり切替を起こすこと。</summary>
public sealed class ArrowKeyTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    /// <summary>
    /// ネガティブコントロールで「起きないこと」を確かめる窓。
    /// </summary>
    /// <remarks>
    /// 上の <see cref="Settle"/> より<b>短くてよい</b>。効いているときは実測で
    /// <b>2 秒待つ前に</b>移り終えていた。<b>正確な往復時間は測っていない</b> —
    /// 実測は 2 秒の固定待ちで観測したため、言えるのは上限だけである。
    /// その倍を待って動かなければ結論してよい、という見積もりで置いてある。
    /// 長くするとネガティブコントロールだけで数十秒かかるようになる。
    /// </remarks>
    private static readonly TimeSpan NothingHappens = TimeSpan.FromSeconds(5);

    /// <summary>
    /// ← で重なりの 1 つ外へ移り、→ で<b>戻ってくる</b>こと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>往復で見るのは意図である。</b>「押したら何かが変わった」だけだと、
    /// 変わった先が正しいかを言えない。戻ってきた矩形は
    /// <c>OverlayGeometry</c> (純関数) から計算した期待値と一致するので、
    /// <b>行きと帰りの両方が正しい</b>ことまで言える。マジックナンバーも書かない。
    /// </para>
    /// <para>
    /// <b>← から始めるのは、捕捉直後の位置がスタックの終端だからである</b>
    /// (実測では 32 個中 32 個目だった)。<c>MoveStackAsync</c> は端で clamp して
    /// <b>何もせずに返る</b>ので、→ から始めると「フックが効いていない」と
    /// 区別のつかない緑ではない赤になる。順序はこの事実に依存している。
    /// </para>
    /// <para>
    /// <b>文字列は見ない。</b>ヒント欄には重なりの位置が出る (<c>OverlapPosition</c>) が、
    /// あれはローカライズされているので、assert に使うと OS の表示言語で結果が変わる。
    /// 矩形なら言語に依らない。
    /// </para>
    /// <para>
    /// 退行: <c>OverlayController.HookProc</c> から <c>ArrowKeyPressed</c> の発火を外す →
    /// ← で何も起きず期限で落ちる。
    /// </para>
    /// </remarks>
    [Fact]
    public void ArrowKeys_StepOutThroughTheOverlappedElementsAndBack()
    {
        using var cursor = SyntheticInput.CursorGuard.Save();
        using var scenario = OverlayScenario.Open(pickers: 1);

        // 出発点は純関数から計算した期待矩形である。「いま出ている矩形」を出発点にすると、
        // 途中の描き直しを掴んだときに往復の判定ごとずれる
        System.Windows.Rect start = scenario.ExpectedRects()[0];
        _ = WaitForFrame(scenario, r => r == start, "枠が捕捉した要素に出ること");

        SyntheticInput.TapKey(SyntheticInput.VkLeft);
        System.Windows.Rect stepped = WaitForFrame(
            scenario, r => r != start, "← で枠が重なりの 1 つ外へ移ること");

        SyntheticInput.TapKey(SyntheticInput.VkRight);
        _ = WaitForFrame(scenario, r => r == start, "→ で枠が元の要素へ戻ること");

        // 移った先が<b>実体のある矩形</b>であること。「別の矩形になった」だけだと、
        // 潰れた矩形 (幅か高さが 0) も条件を満たしてしまう
        Assert.True(
            stepped.Width > 0 && stepped.Height > 0,
            FormattableString.Invariant($"← で移った先の枠が潰れています ({stepped})。"));
    }

    /// <summary>
    /// 自動選択が OFF のとき、←/→ で<b>何も起きない</b>こと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="ArrowKeys_StepOutThroughTheOverlappedElementsAndBack"/> の
    /// ネガティブコントロールである。</b><c>SetHookEnabled</c> に検出力があることを
    /// ここで示さないと、あちらの緑は「フックが効いている」ではなく
    /// 「何か別のものが枠を動かした」と区別できない。
    /// </para>
    /// <para>
    /// <b>OFF にする前に、同じキーで実際に動くことを確かめる。</b>これを飛ばすと、
    /// スタックの端に居るせいで動かなかった場合と見分けがつかない —
    /// 実測でまさにその形 (捕捉直後に → を撃つと clamp されて何も起きない) を踏んだ。
    /// 「起きなかった」と「そもそも起こしようがなかった」を分けるための手順である。
    /// </para>
    /// <para>
    /// 退行: <c>TriggerPickerPresenter.SetAutoSelect</c> から
    /// <c>_overlay.SetHookEnabled(on)</c> を外す → OFF でも枠が動いて落ちる。
    /// </para>
    /// </remarks>
    [Fact]
    public void WhenAutoSelectIsOff_ArrowKeysDoNothing()
    {
        using var cursor = SyntheticInput.CursorGuard.Save();
        using var scenario = OverlayScenario.Open(pickers: 1);

        System.Windows.Rect start = scenario.ExpectedRects()[0];
        _ = WaitForFrame(scenario, r => r == start, "枠が捕捉した要素に出ること");

        // ON のうちに 1 歩動かしておく。ここが動かなければ、以降の「動かない」に意味が無い
        SyntheticInput.TapKey(SyntheticInput.VkLeft);
        System.Windows.Rect moved = WaitForFrame(
            scenario, r => r != start, "OFF にする前に、同じキーで枠が動くこと");

        SetAutoSelect(scenario, on: false);

        SyntheticInput.TapKey(SyntheticInput.VkLeft);
        Ui.Never(
            // 枠が<b>読めなかった</b>ことを「動いた」と読まない。UIA の列挙が一時的に空を
            // 返しうるので、そこを動きとして数えると OFF でも赤くなる。
            // 消えたまま戻らない場合は下の assert が捕まえる
            () => CurrentFrame(scenario) is { } now && now != moved,
            NothingHappens,
            "自動選択が OFF なのに ← で枠が動くこと",
            () => FormattableString.Invariant($"OFF にした時点の枠: {moved} / いまの枠: ") +
                  (CurrentFrame(scenario)?.ToString(CultureInfo.InvariantCulture) ?? "(オーバーレイなし)") +
                  Environment.NewLine + scenario.Describe());

        // 上の Ui.Never は「読めなかった」を見逃す向きに倒してある。
        // 枠が消えたまま終わったのを緑にしないよう、最後に在ることまで確かめる
        Assert.Equal(moved, CurrentFrame(scenario));
    }

    /// <summary>
    /// いま出ている枠。<b>オーバーレイが無ければ null</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>「無い」を既定の矩形 (0,0,0,0) に潰してはいけない。</b>潰すと
    /// <see cref="WaitForFrame"/> の述語 <c>r != start</c> が<b>1 回の空読み</b>で成立し、
    /// 枠が一度も動いていないのに「← で移った」と結論する — このテストが主張している
    /// ことそのものに、偽の緑の経路ができる。
    /// </para>
    /// <para>
    /// <c>Ui.Until</c> が握り潰すのは <c>ElementNotAvailableException</c> であって、
    /// <b>空のコレクションは例外ではない</b>ので素通りする。だから型で分ける。
    /// </para>
    /// <para>
    /// オーバーレイは捕捉が起きるまで<b>可視にならない</b>ので、存在の有無だけで
    /// 合否を決めないこと (docs/DESIGN.md §12)。要素はキャッシュせず取り直す。
    /// </para>
    /// </remarks>
    private static System.Windows.Rect? CurrentFrame(OverlayScenario scenario)
        => scenario.Host.Overlays() is { Count: > 0 } overlays
            ? overlays[0].Current.BoundingRectangle
            : null;

    /// <summary>
    /// 枠が <paramref name="predicate"/> を満たすまで待つ。
    /// </summary>
    /// <remarks>
    /// <b>述語を評価するのは枠が読めたときだけ</b>である。読めないあいだは
    /// 「まだ成立していない」として待ち続ける (最終的には期限で落ちるので、
    /// 本当に出てこない場合を見逃すことはない)。
    /// </remarks>
    private static System.Windows.Rect WaitForFrame(
        OverlayScenario scenario, Func<System.Windows.Rect, bool> predicate, string what)
        => (System.Windows.Rect)Ui.Until(
            () => CurrentFrame(scenario) is { } r && predicate(r) ? (object)r : null,
            Settle,
            what,
            scenario.Describe);

    /// <summary>
    /// 自動選択のトグルを切り替える。<b>切り替わったことを確かめてから返る。</b>
    /// </summary>
    /// <remarks>
    /// <c>Toggle()</c> が返ることと、プレゼンターが <c>SetAutoSelect</c> を受け取ったことは
    /// 別である。確かめずに次へ進むと、ネガティブコントロールが
    /// 「まだ ON のまま撃った」ぶんの緑を拾いうる。
    /// </remarks>
    private static void SetAutoSelect(OverlayScenario scenario, bool on)
    {
        ToggleState want = on ? ToggleState.On : ToggleState.Off;
        AutomationElement toggle = scenario.Host.PickerWindow().RequireById("AutoSelectToggle");
        var pattern = (TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern);
        if (pattern.Current.ToggleState != want)
        {
            pattern.Toggle();
        }

        _ = Ui.Until(
            () => ((TogglePattern)scenario.Host.PickerWindow()
                       .RequireById("AutoSelectToggle")
                       .GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == want
                ? "ok" : null,
            Settle,
            $"自動選択のトグルが {want} になること",
            scenario.Describe);
    }
}
