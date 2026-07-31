// T5: 枠のクリックスルー (M1 — docs/TESTING.md §3。窓の構造は docs/DESIGN.md §10)。
//
// 枠線の上を合成クリックし、**下のアプリまで届く**ことを見る。実マウスでも
// 同じ点 (枠の下辺) で「届かない」が再現した実績があるので、ここが赤なら
// 合成入力の癖ではなく製品の欠陥である。枠とアイコンは別のウィンドウで、
// 枠には WS_EX_TRANSPARENT が付いている約束 (docs/DESIGN.md §10) — それが外れると落ちる。
//
// 観測点は「ボタンが Click を上げたか」ではなく「**クリックが対象アプリまで届いたか**」。
// 前面化に食われた場合も**前面が変わる**ので、どちらでも「届いた」と数えられる。
// オーバーレイは WS_EX_NOACTIVATE なので、それ自身を押しても前面は変わらない —
// つまり前面が変わったなら、クリックはオーバーレイを通り越している。
// Click だけを観測点にすると**押す順で答えが変わる** (本題を 1 発目にすると
// 前面化に食われ、対照を先にするとオーバーレイが下へ回る) ので、この形にしてある。
// また、対照 (アルファ 0 の内側) が無いと「届かなかった」と「枠が塞いだ」を分けられない。
//
// **Skip にしないこと。**「走らないから緑」はこの repo が各所で塞いでいる形である。
using System.Globalization;
using System.Windows.Automation;
using UiaTrigger.Models;
using UiaTrigger.Picker;
using UiaTrigger.Picker.UiTests;
using Xunit;

namespace UiaTrigger.Input.Tests;

/// <summary>枠の上のクリックが下のアプリまで届くかどうかを、2 つの観測点で見る。</summary>
public sealed class OverlayClickTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    /// <summary>枠を出させる対象アプリのボタン。<c>OverlayScenario</c> が 1 枚目に使う名前。</summary>
    private const string Button = "OverlayA";

    /// <summary>
    /// 枠線の上のクリックが**下のアプリまで届く**こと。届かなければ製品の欠陥である。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **観測点は 2 つで、どちらか一方でも動けば「届いた」とする。**
    /// (a) ボタンの <c>Click</c> が上がる (b) **前面が対象アプリに変わる**。
    /// 前面でない窓を押した 1 発目は前面化に食われて (a) が上がらないことがあるが、
    /// **そのときも (b) は動く** — 前面化が起きた = クリックはその窓まで届いた、である。
    /// この 2 つを両方見ることで**押す順に依存しなくなる**。
    /// </para>
    /// <para>
    /// **撃つ前に狙いを検算する** (docs/TESTING.md §3 の解禁条件)。枠線の点で
    /// <c>WindowFromPoint</c> がオーバーレイを返すこと = そこでオーバーレイが対象アプリより
    /// **上に居る**ことを確かめる。<c>WindowFromPoint</c> が <c>HTTRANSPARENT</c> を
    /// 尊重しないのは実測済み (docs/DESIGN.md §10) で、不透明な枠の上ではこれは
    /// **Z オーダーだけを見ている** — ここではそれがちょうど欲しい情報である。
    /// </para>
    /// <para>
    /// 押す点は**枠の下辺**にする。利用者が実マウスで押したのがそこだからである。
    /// アイコンは右上なので、下辺は当たり判定から最も遠い。
    /// </para>
    /// <para>
    /// **対照は枠の内側 (アルファ 0) である。**あそこは Z オーダーに関係なく下へ抜けるので、
    /// 「そもそもこのハーネスはこのボタンを押せるのか」を切り分けられる。**本題を先に押す** —
    /// 対照を先にすると対象アプリが前面へ出てオーバーレイが下へ回り、
    /// 枠が塞いでいても素通りして偽の合格になる (Z オーダーの性質は docs/DESIGN.md §10)。
    /// </para>
    /// </remarks>
    [Fact]
    public void AClickOnTheFrame_ReachesTheApplicationBelow()
    {
        using var scenario = OverlayScenario.Open(pickers: 1);
        _ = scenario.WaitForOverlays(1);

        AutomationElement overlay = scenario.Host.Overlays()[0];
        nint overlayHwnd = (nint)overlay.Current.NativeWindowHandle;
        nint targetHwnd = (nint)scenario.Target.WindowHandle();

        (int left, int top, int right, int bottom) = scenario.Target.RectOf(Button);
        int dpi = NativeWindows.DpiAt(left, top);
        OverlayGeometry.Metrics m = OverlayGeometry.MetricsFor(dpi);
        (_, int frameHeight) =
            OverlayGeometry.FrameSize(new ElementRect(left, top, right, bottom), dpi);

        // 枠の下辺の中央。線は [底 - 太さ, 底) を占めるので、その真ん中を突く
        (int X, int Y) onTheLine = ((left + right) / 2, top + frameHeight - 1 - (m.FrameThickness / 2));

        // 枠の内側 = ボタンの中心。アルファ 0 なので Z オーダーに関係なく下へ抜ける
        (int X, int Y) inside = ((left + right) / 2, (top + bottom) / 2);

        using var cursor = SyntheticInput.CursorGuard.Save();

        // --- 検算: 狙った点が枠の窓の中にあること ---------------------------------
        // 枠の外を押していたら、抜けて当たり前である
        (int Left, int Top, int Right, int Bottom) frame =
            NativeWindows.RectOf(overlayHwnd) ?? throw new InvalidOperationException("枠の矩形が取れません。");
        Assert.True(
            onTheLine.X >= frame.Left && onTheLine.X < frame.Right &&
            onTheLine.Y >= frame.Top && onTheLine.Y < frame.Bottom,
            FormattableString.Invariant(
                $"狙った点 {onTheLine} が枠の窓 ({frame.Left},{frame.Top})-({frame.Right},{frame.Bottom}) の") +
            "外にあります。この点で「枠が塞ぐか」は問えません。" + Environment.NewLine + scenario.Describe());

        // --- 検算: その点で枠が対象アプリより上に居ること -------------------------
        // **WindowFromPoint では確かめられない** (docs/DESIGN.md §10)。枠は WS_EX_TRANSPARENT で
        // ヒットテストから外れているので、上に居てもあの関数は下のアプリを返す —
        // それはまさにこのテストが確かめたい性質であって、前提条件にはできない。
        // ヒットテストを通さない Z オーダーの順位で見る
        int frameZ = NativeWindows.ZOrderIndexOf(overlayHwnd);
        int targetZ = NativeWindows.ZOrderIndexOf(targetHwnd);
        Assert.True(
            frameZ >= 0 && targetZ >= 0 && frameZ < targetZ,
            FormattableString.Invariant(
                $"枠が対象アプリより上に居ません (枠の Z={frameZ} / 対象アプリの Z={targetZ}、0 が最前面)。") +
            "下に回っていると、枠が塞いでいても素通りして**偽の合格**になります (docs/DESIGN.md §10)。" +
            Environment.NewLine + scenario.Describe());

        // --- 検算: 対象アプリがまだ前面でないこと ---------------------------------
        // 前面が変わったかどうかを観測点にするので、始点が「前面ではない」でなければ成立しない
        Assert.NotEqual(targetHwnd, NativeWindows.ForegroundWindow());

        // --- (1) 本題: 枠線を押す ------------------------------------------------
        Reach line = ClickAndSee(scenario, targetHwnd, onTheLine);

        // --- (2) 対照: 枠の内側を押す --------------------------------------------
        Reach insideReach = ClickAndSee(scenario, targetHwnd, inside);

        string measured = string.Create(
            CultureInfo.InvariantCulture,
            $"枠線 {onTheLine}: {line} / 内側 {inside}: {insideReach}。" +
            $"ボタン=({left},{top})-({right},{bottom}) 枠の高さ={frameHeight} " +
            $"枠の太さ={m.FrameThickness} DPI={dpi}");

        // **対照を先に主張する。**ここが動いていなければ枠線の結果は解釈できない
        Assert.True(
            insideReach.Reached,
            "対照が成立しませんでした — 枠の内側 (アルファ 0) を押しても対象アプリに何も起きません。" +
            "これは「枠が塞いだ」ではなく**ハーネスがこのボタンを押せていない**ということです。" +
            Environment.NewLine + measured + Environment.NewLine + scenario.Describe());

        Assert.True(
            line.Reached,
            "**枠線の上のクリックが下のアプリまで届いていません。**" +
            "同じボタンを枠の内側から押すと届く (対照が成立している) ので、" +
            "「押せなかった」ではなく**枠が塞いでいる**ということです。" +
            "枠の窓は WS_EX_TRANSPARENT で**窓ごと**ヒットテストから外れている約束です " +
            "(docs/DESIGN.md §10)。枠とアイコンを 1 つの窓に戻すとここが落ちます — " +
            "レイヤードウィンドウでは不透明なピクセルが必ずその窓のものになり、" +
            "WM_NCHITTEST で HTTRANSPARENT を返してもクリックは下へ渡らず落ちます (docs/DESIGN.md §10)。" +
            Environment.NewLine + measured + Environment.NewLine + scenario.Describe());
    }

    /// <summary>クリックが対象アプリまで届いたかどうか。</summary>
    /// <param name="Clicked">ボタンが <c>Click</c> を上げた。</param>
    /// <param name="Activated">前面が対象アプリに変わった (前面化に食われた場合もこちらが動く)。</param>
    private readonly record struct Reach(bool Clicked, bool Activated)
    {
        public bool Reached => Clicked || Activated;

        public override string ToString() =>
            $"届いた={Reached} (Click={Clicked} 前面化={Activated})";
    }

    /// <summary>1 点を押して、2 つの観測点を見る。</summary>
    private static Reach ClickAndSee(
        OverlayScenario scenario,
        nint targetHwnd,
        (int X, int Y) point)
    {
        int before = scenario.Target.ClicksOn(Button);
        SyntheticInput.CursorGuard.MoveTo(point.X, point.Y);
        SyntheticInput.TapLeftButton();

        // どちらかが動くまで待つ。両方動かないまま期限が来たら「届かなかった」である
        DateTime deadline = DateTime.UtcNow + Settle;
        bool clicked = false;
        bool activated = false;
        while (DateTime.UtcNow < deadline && !clicked && !activated)
        {
            clicked = scenario.Target.ClicksOn(Button) > before;
            activated = NativeWindows.ForegroundWindow() == targetHwnd;
        }
        return new Reach(clicked, activated);
    }
}
