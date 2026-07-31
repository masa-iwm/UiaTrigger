// T4 が使うデスクトップの割り付け (docs/TESTING.md §1)。
//
// 対象アプリの窓とホストの窓が**重ならない**ことを、ここ 1 箇所で決める。
// 「ウィンドウは左上からカスケードして開くので右下は空いている」のような前提には
// 頼らない — カスケードの位置はセッション中に作られた窓の数で動くので、
// その前提は実行ごとに崩れる。
using System.Globalization;
using System.Windows.Automation;
using Xunit;

namespace UiaTrigger.Picker.UiTests;

/// <summary>
/// 対象アプリとホストの窓を置く場所。
/// </summary>
/// <remarks>
/// <para>
/// **ここが T4 の非決定性の出どころである。**実測 (1600x900):
/// 対象アプリ (1228,471)-(1552,692) に対して pick 点は (1350,560)、
/// WPF のピッカーは (52,52)-(1152,752) で**届かない**が、
/// WinUI のピッカーは (156,156)-(1356,774) で**pick 点を 6px 覆っていた**。
/// つまり「WPF は安定していて WinUI は不安定」ではなく、
/// **WPF の窓が 1100 幅で偶然届かなかっただけ**である。
/// </para>
/// <para>
/// 覆われるとピッカーは自プロセスを掴んで捕捉を飛ばし、しかも
/// <c>TriggerPickerPresenter.TickAsync</c> は <c>_lastCapturedPoint</c> で
/// 「同じ点は再捕捉しない」ので**二度と捕捉しない**。
/// 症状は 20 秒のタイムアウトで、原因は「実体化しなかった」に見える。
/// </para>
/// <para>
/// 1600x900 の画面に 1200x618 の窓が任意のカスケード位置で出る以上、
/// **静的に安全な領域は存在しない**。だから当てにするのをやめ、
/// ホストの窓を**出た瞬間に退かす** (<see cref="PickerHostProcess"/>)。
/// </para>
/// </remarks>
internal static class DesktopLayout
{
    /// <summary>対象アプリとホストの窓のあいだに空ける幅。</summary>
    private const int Gap = 24;

    /// <summary>デスクトップの矩形。失敗したときの説明に使う。</summary>
    public static System.Windows.Rect Desktop { get; }

    /// <summary>対象アプリの窓。**右下**に置く。</summary>
    public static (int Left, int Top, int Width, int Height) Target { get; }

    /// <summary>ホストの窓 (メインもピッカーも)。**左に寄せる**。</summary>
    public static (int Left, int Top, int Width, int Height) Host { get; }

    /// <summary>
    /// **幅を固定した**ホストの窓。表示域の広さそのものを検査対象にするテスト用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Host"/> の幅は <c>Target.Left - Gap</c> で、**デスクトップの広さで変わる**。
    /// 「深いチェーンで水平スクロールが要る」のような**内容が表示域に収まるかどうか**を
    /// 見るテストは、これに乗ると機械ごとに結果が変わる — 実際 3840x2160 では
    /// ホスト幅が 3436px になり、30 段の字下げが収まってしまって
    /// **検出力を失ったまま赤い**状態になっていた (docs/DESIGN.md §9)。
    /// </para>
    /// <para>
    /// **幅には上と下の両方から制約がある。**狭すぎても成立しない —
    /// このテストの対照は「畳めば水平スクロールが要らなくなる」なので、
    /// **畳んだときに残る 1 行が収まる**幅は要る。
    /// </para>
    /// <para>
    /// 実測 (175%、ツリーはホスト幅の約 52%):
    /// </para>
    /// <list type="table">
    /// <item><description>ホスト 3436px → ツリー 1838px: 30 段でも**表示率 100%** (収まってしまう = 検査にならない)</description></item>
    /// <item><description>ホスト 700px → ツリー 346px: 30 段で表示率 70.9% だが、**畳んでも水平スクロールが残る** (対照が壊れる)</description></item>
    /// </list>
    /// <para>
    /// なので中間を取る。字下げは 1 段あたり 96 DPI で約 19px・175% で約 33px、
    /// 行の文字が要る幅は 96 DPI で約 234px・175% で約 409px なので、
    /// 30 段が溢れるには ツリー &lt; 約 800px (96 DPI) / 約 1400px (175%) であればよい。
    /// 畳んだ 1 行は 346px には収まらなかったので、それより広く取る。
    /// **1100px (ツリー約 570px) は、どちらの表示スケールでも両方の条件を満たす。**
    /// </para>
    /// </remarks>
    public static (int Left, int Top, int Width, int Height) NarrowHost =>
        (Host.Left, Host.Top, 1100, Host.Height);

    static DesktopLayout()
    {
        System.Windows.Rect desktop = AutomationElement.RootElement.Current.BoundingRectangle;
        Desktop = desktop;

        // 下端はタスクバーを避ける。対象アプリの窓が隠れると RectOf と画面が食い違う
        const int TargetWidth = 340;
        const int TargetHeight = 260;
        Target = (
            (int)desktop.Right - TargetWidth - 40,
            (int)desktop.Bottom - TargetHeight - 200,
            TargetWidth,
            TargetHeight);

        Host = (0, 0, Target.Left - Gap, (int)desktop.Bottom - 100);
    }

    /// <summary>対象アプリへ送る <c>place</c> コマンド。</summary>
    public static string PlaceTargetCommand => string.Create(
        CultureInfo.InvariantCulture,
        $"place {Target.Left} {Target.Top} {Target.Width} {Target.Height}");

    /// <summary>
    /// 割り付けそのものが成立していること。**画面が狭いと成立しない。**
    /// </summary>
    /// <remarks>
    /// ハーネス自身の検証である (docs/TESTING.md §4 の教訓 (b))。ここが成立していなければ
    /// 「ホストの窓を退かした」あとも pick 点が覆われうるので、
    /// 落ちるべきは個々のテストではなく**割り付け**である。
    /// </remarks>
    /// <summary>
    /// **2 つ目の**対象アプリの窓。<see cref="Target"/> の**真上**に積む。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 複合条件 (docs/DESIGN.md §4) は**別プロセスを 2 つ**立てないと何も確かめられない —
    /// 同一ウィンドウ内の 2 要素だと、スロットが割れていても素通りするからである。
    /// </para>
    /// <para>
    /// **横ではなく縦に積む。**横に並べるとホストの幅 (<c>Target.Left - Gap</c>) を削ることになり、
    /// <see cref="NarrowHost"/> を含む既存の割り付けが全部動く。
    /// 縦なら <see cref="Host"/> も <see cref="Target"/> も 1 ピクセルも変わらない。
    /// </para>
    /// <para>
    /// **重ならないことが要点である。**記録は座標 (<c>ElementFromPoint</c>) を通るので、
    /// 重なっていると一方の記録がもう一方の窓を掴む
    /// (docs/MANUAL-CHECKS.md §9 が手順として同じ注意を書いている)。
    /// </para>
    /// </remarks>
    public static (int Left, int Top, int Width, int Height) SecondTarget =>
        (Target.Left, Target.Top - Target.Height - Gap, Target.Width, Target.Height);

    /// <summary>2 つ目の対象アプリへ送る <c>place</c> コマンド。</summary>
    public static string PlaceSecondTargetCommand => string.Create(
        CultureInfo.InvariantCulture,
        $"place {SecondTarget.Left} {SecondTarget.Top} {SecondTarget.Width} {SecondTarget.Height}");

    /// <summary>
    /// 2 つ目の対象アプリを置く余地があること。**画面が狭いと成立しない。**
    /// </summary>
    /// <remarks>
    /// <see cref="RequireItFitsOnThisScreen"/> と同じ理由でここに置く —
    /// 収まらないときに落ちるべきは個々のテストではなく**割り付け**であり、
    /// 症状 (「捕捉されない」というタイムアウト) からは原因が読めないからである
    /// (実際にそうなった。docs/RELEASING.md §2)。
    /// </remarks>
    public static void RequireTwoTargetsFitOnThisScreen()
    {
        RequireItFitsOnThisScreen();

        (int Left, int Top, int Width, int Height) second = SecondTarget;
        Assert.True(
            second.Top > 0 && second.Top + second.Height + Gap <= Target.Top,
            string.Create(
                CultureInfo.InvariantCulture,
                $"この画面では対象アプリを 2 つ置けません。2 つ目 {Describe(second)} が画面の外へ出るか、1 つ目 {Describe(Target)} と重なります。") +
            "重なると記録が別の窓を掴むため、複合条件の検査に意味がありません。");
    }

    public static void RequireItFitsOnThisScreen()
    {
        Assert.True(
            Host.Width > 400 && Host.Height > 400 && Host.Left + Host.Width <= Target.Left - 1,
            string.Create(
                CultureInfo.InvariantCulture,
                $"この画面では T4 の割り付けが成立しません。ホストの窓 {Describe(Host)} と対象アプリの窓 {Describe(Target)} が重ならないだけの幅がありません。") +
            "この状態ではホバー捕捉が起きず、テストの結果に意味がありません。");
    }

    /// <summary>
    /// <see cref="NarrowHost"/> の割り付けが成立していること。**画面が狭いと成立しない。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RequireItFitsOnThisScreen"/> は <see cref="Host"/> しか見ないので、
    /// これとは別に要る。<see cref="NarrowHost"/> は幅が固定 (1100px) なので、
    /// 画面が狭いと対象アプリの窓の上まで伸びて **pick 点を覆う**。
    /// 覆われるとピッカーは自プロセスを掴んで捕捉を飛ばし、二度と捕捉しない。
    /// </para>
    /// <para>
    /// **CI のランナー (1024x768 — docs/RELEASING.md §2) では実際に起きる。**
    /// 素通しすると症状は「ホストのメインウィンドウを pick 点から退かす が
    /// 30 秒以内に成立しませんでした」という**原因を名指ししないタイムアウト**になり、
    /// 画面の狭さは窓の矩形から逆算しないと見えない。
    /// </para>
    /// <para>
    /// **この画面では成立しない、と言えるだけでよい。**直す方法 (解像度を上げる) は
    /// テストの側では決められないので、必要な幅を数字で示して落とす。
    /// </para>
    /// </remarks>
    public static void RequireTheNarrowHostFitsOnThisScreen()
    {
        RequireItFitsOnThisScreen();

        (int Left, int Top, int Width, int Height) narrow = NarrowHost;
        int required = narrow.Width + (Target.Width + 40) + 1;

        Assert.True(
            narrow.Left + narrow.Width <= Target.Left - 1,
            string.Create(
                CultureInfo.InvariantCulture,
                $"この画面 ({Desktop.Width}x{Desktop.Height}) では幅を固定したホストの割り付けが成立しません。") +
            string.Create(
                CultureInfo.InvariantCulture,
                $"ホストの窓 {Describe(narrow)} が対象アプリの窓 {Describe(Target)} に掛かるため、pick 点が覆われます。") +
            string.Create(
                CultureInfo.InvariantCulture,
                $"幅が {required}px 以上の画面が要ります (いまは {Desktop.Width}px)。") +
            "表示解像度を上げてください (CI では picker-ui ジョブが設定します。docs/RELEASING.md §2)。");
    }

    private static string Describe((int Left, int Top, int Width, int Height) r) => string.Create(
        CultureInfo.InvariantCulture, $"({r.Left},{r.Top})-({r.Left + r.Width},{r.Top + r.Height})");
}
