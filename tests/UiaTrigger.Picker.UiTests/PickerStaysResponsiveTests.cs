// T4: 対象アプリが固まってもピッカーの UI が凍らないこと
// (docs/MANUAL-CHECKS.md §8 (応答しないアプリに対する耐性) / docs/DESIGN.md §3)。
//
// <b>「UI が凍っていない」は目視ではなく期限付きの assert に落とせる。</b>
// しかもこの形は人より検出力が強い — 人は「なんとなく重い」を「凍っていない」と読んでしまう。
//
// <b>ここでしか見られないもの:</b> ピッカーの UI の経路が UIA の経路を待たないこと。
// 選択の反映と枠の描画はキャッシュ済みの矩形で同期に走り、プロパティの読み取りは
// fire-and-forget である。T1 の fake View にはこの 2 つを分ける実体が無い。
//
// WPF だけで回す。この性質は TriggerPickerPresenter / OverlayController = Picker.Core に在って
// 3 変種で<b>同じもの</b>なので、変種を増やしても検査対象の被覆は増えない (OverlayTests と同じ理由)。
using System.Diagnostics;
using System.Globalization;
using System.Windows.Automation;
using UiaTrigger.Models;
using UiaTrigger.Picker;
using UiaTrigger.RealUia.Tests;
using Xunit;

namespace UiaTrigger.Picker.UiTests;

public sealed class PickerStaysResponsiveTests
{
    /// <summary>対象アプリを塞ぐ時間。</summary>
    /// <remarks>
    /// <c>TestTargetProcess</c> の応答待ち (20 秒) より十分に短くとる。塞ぐ直前に送っておいた
    /// <c>ping</c> をこの後で受けるので、超えると<b>ハーネス自身が時間切れになる</b>。
    /// </remarks>
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 塞がっている最中の操作に与える期限。<b>この数字がこのテストの検出力そのものである。</b>
    /// </summary>
    /// <remarks>
    /// <see cref="Hang"/> より<b>ずっと短く</b>すること。UI スレッドが UIA の往復を待つ実装なら
    /// 反映は塞ぎ明け (10 秒) まで遅れるので、短い期限だけがその失敗を捕まえる。
    /// 実測は選択が 34〜53ms、枠は最初のポーリングで既に移動済みである。
    /// </remarks>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    private static readonly string[] Buttons = ["btn-one", "btn-two", "btn-three", "btn-four", "btn-five"];

    /// <summary>捕捉させる要素。端ではなく真ん中を狙う (TreeRealisationTests と同じ理由)。</summary>
    private const string Target = "btn-three";

    /// <summary>塞がっている最中に選び直す行。捕捉した要素の兄弟である。</summary>
    private const string Other = "btn-one";

    /// <summary>
    /// 対象アプリが応答しなくなっても、ツリーの別の行を選べて、枠がその行へ移ること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>なぜこれが効くのか。</b><c>SelectionItemPattern.Select()</c> は
    /// <b>ピッカーの UI スレッドへの他プロセス呼び出し</b>である。UI スレッドが詰まっていれば
    /// この呼び出し自体が返らない — <b>観測点が探針そのものになっている</b>。
    /// </para>
    /// <para>
    /// ピッカーの UIA 読み取りは専用スレッド (MTA) で走り、塞がっているあいだ丸ごと止まる。
    /// 一方 <c>NotifyTreeSelectionChanged</c> → <c>RefreshCurrentNode</c> は
    /// <b>キャッシュ済みの矩形で同期に枠を出し</b>、プロパティの読み取りは fire-and-forget である。
    /// つまりこのテストは<b>「UI の経路が UIA の経路を待たない」という設計の性質</b>を直接固定する。
    /// </para>
    /// <para>
    /// <b>プロパティ一覧は見ない。</b>塞がっているあいだ埋まらないのは正しい。
    /// ただし<b>塞ぎが明けても更新されない</b>ことが実測で分かっている
    /// (<c>RefreshPropsAsync</c> が <c>COMException</c> を捕まえないため —
    /// docs/TESTING.md §5 の未修正の不具合)。
    /// <b>それは送ってある不具合であり、ここで固定してしまうと直せなくなる。</b>
    /// 代わりに <see cref="WithoutAHang_SelectingTheSameRowAlsoFillsThePropertyList"/> が
    /// 「塞いでいなければ埋まる」を押さえ、この振る舞いが hang 固有だと読めるようにしてある。
    /// </para>
    /// <para>
    /// 退行:
    /// <c>RefreshPropsAsync</c> を UI スレッドで同期待ちにする (<c>.GetAwaiter().GetResult()</c>)
    /// → 選択が動かず期限で落ちる /
    /// <c>RefreshCurrentNode</c> からキャッシュ矩形での即時描画を外し、スナップショット到着後だけ
    /// 描くようにする → 枠が動かず期限で落ちる。
    /// </para>
    /// </remarks>
    [Fact]
    public void WhileTheTargetIsUnresponsive_AnotherRowCanStillBeSelectedAndTheFrameFollows()
    {
        using var scenario = FrozenScenario.Open();

        var clock = Stopwatch.StartNew();
        scenario.HangTheTarget(Hang);

        scenario.SelectTheOtherRow();

        // 選択が実際に移ること
        _ = Ui.Until(
            () => scenario.SelectedRowName() is { } name &&
                  name.Contains($"[{Other}]", StringComparison.Ordinal) ? name : null,
            Deadline,
            $"塞がっている最中に {Other} の行へ選択が移ること",
            scenario.Describe);

        // 枠がその行の矩形へ移ること。期待は OverlayGeometry (純関数) から出す —
        // 塞がっている相手には矩形を訊けないので、塞ぐ前に読んだ値を使う
        System.Windows.Rect want = scenario.ExpectedFrameFor(Other);
        _ = Ui.Until(
            () => scenario.FrameRect() is { } now && now == want ? "ok" : null,
            Deadline,
            FormattableString.Invariant($"枠が {Other} の矩形 {want} へ移ること"),
            scenario.Describe);

        scenario.RequireTheTargetWasBlockedThroughout(clock, Hang);
    }

    /// <summary>
    /// 対照: 塞いでいなければ、同じ操作でプロパティ一覧も埋まること。
    /// </summary>
    /// <remarks>
    /// <b>これが無いと、上のテストが「塞いだから埋まらない」のか
    /// 「そもそも埋まらない」のかを区別できない。</b>塞がっている最中にプロパティ一覧が
    /// 前の要素のままであることは正しい振る舞いだが、それを言うには
    /// 「塞がなければ埋まる」を別に示さなければならない。
    /// 実測では 268ms で埋まる。
    /// </remarks>
    [Fact]
    public void WithoutAHang_SelectingTheSameRowAlsoFillsThePropertyList()
    {
        using var scenario = FrozenScenario.Open();

        scenario.SelectTheOtherRow();

        System.Windows.Rect want = scenario.ExpectedFrameFor(Other);
        _ = Ui.Until(
            () => scenario.FrameRect() is { } now && now == want ? "ok" : null,
            Deadline,
            FormattableString.Invariant($"枠が {Other} の矩形 {want} へ移ること"),
            scenario.Describe);

        string[] rows = Ui.Until(
            () => scenario.PropertyRows() is { Length: > 0 } list &&
                  list.Any(r => r.Contains(Other, StringComparison.Ordinal)) ? list : null,
            Deadline,
            $"プロパティ一覧が {Other} のものになること",
            scenario.Describe);

        Assert.Contains(rows, r => r.Contains($"AutomationId: {Other}", StringComparison.Ordinal));
    }

    /// <summary>
    /// 対象アプリ + ホスト + 捕捉済みのピッカー。<b>塞ぐ前に見るものを全部掴んでおく。</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>この「先に掴む」規律がこのファイルの要である (docs/TESTING.md §1)。</b>
    /// <c>PickerHostProcess.PickerWindow()</c> / <c>Overlays()</c> /
    /// <c>AutomationElement.FromPoint</c> / <c>Diagnostics()</c> は
    /// どれも <c>AutomationElement.RootElement</c> の子を列挙するか、対象アプリを直接読む。
    /// <b>応答しない窓が 1 つでも在ると、それらは検査対象と一緒に詰まる</b>
    /// (実測: <c>Overlays()</c> 12052ms / <c>PickerWindow()</c> 14912ms /
    /// <c>FromPoint</c> は 10 秒で <c>TimeoutException</c> / <c>Diagnostics()</c> は
    /// <c>Win32Exception</c> を投げる)。
    /// </para>
    /// <para>
    /// <b>それを「ピッカーが凍った」と読むと、docs/TESTING.md §4 の円環がそのまま 1 層上で再現する</b> —
    /// 探針が詰まったことを検査対象の不具合として報告することになる。
    /// だから塞いでいるあいだは <c>AutomationElement.FromHandle</c> と
    /// <c>GetWindowRect</c> しか使わない。
    /// </para>
    /// <para>
    /// <b>ここを <c>host.Overlays()</c> 等へ「単純化」しないこと。</b>
    /// 12 秒の停止が、ちょうどピッカーの停止に見える形で戻ってくる。
    /// </para>
    /// <para>
    /// <b>WPF 固定なので <c>PickerHostProfile</c> を引数に取らない</b> —
    /// ツリーの AutomationId をここで直接書いているのはそのためである。
    /// 変種を横断する <c>[Theory]</c> へ広げるなら、まずそこを引数にすること。
    /// </para>
    /// </remarks>
    private sealed class FrozenScenario : IDisposable
    {
        private readonly TestTargetProcess _target;
        private readonly PickerHostProcess _host;
        private readonly Dictionary<string, ElementRect> _buttonRects;
        private readonly nint _pickerHwnd;
        private readonly nint _overlayHwnd;
        private readonly IReadOnlyList<AutomationElement> _rows;

        private FrozenScenario(
            TestTargetProcess target,
            PickerHostProcess host,
            Dictionary<string, ElementRect> buttonRects,
            nint pickerHwnd,
            nint overlayHwnd,
            IReadOnlyList<AutomationElement> rows)
        {
            _target = target;
            _host = host;
            _buttonRects = buttonRects;
            _pickerHwnd = pickerHwnd;
            _overlayHwnd = overlayHwnd;
            _rows = rows;
        }

        public static FrozenScenario Open()
        {
            TestTargetProcess target = TestTargetProcess.Start(TargetProfile.WinForms);
            try
            {
                target.Send(DesktopLayout.PlaceTargetCommand);
                foreach (string button in Buttons)
                {
                    target.Send("add-button " + button);
                }
                target.Send("ping");

                var rects = new Dictionary<string, ElementRect>(StringComparer.Ordinal);
                foreach (string button in Buttons)
                {
                    (int l, int t, int r, int b) = target.RectOf(button);
                    rects[button] = new ElementRect(l, t, r, b);
                }

                (int x, int y) = target.CenterOf(Target);
                PickerHostProcess host = PickerHostProcess.Start(PickerHostProfile.Wpf, x, y);
                try
                {
                    host.OpenPicker();

                    // 捕捉を待つ。ここまでは対象アプリが応答しているので通常の道具でよい
                    _ = Ui.Until(
                        () => host.Tree().SelectedRow()?.NameOf() is { } n &&
                              n.Contains($"[{Target}]", StringComparison.Ordinal) ? n : null,
                        TimeSpan.FromSeconds(20),
                        $"ホバー捕捉で {Target} の行が選択されること",
                        host.Diagnostics);

                    // <b>塞ぐ前に</b>兄弟を実体化させる。塞いでいる最中の全列挙は相手の
                    // 読み取りを要するので正当に遅い。そこを論点にしない
                    RevealTheSiblings(host);

                    // 塞いでいるあいだに使うものを全部ここで掴む (このクラスの remarks を参照)
                    var pickerHwnd = (nint)host.PickerWindow().Current.NativeWindowHandle;
                    // オーバーレイは<b>捕捉が枠を描くまで存在しない</b>ので、待ってから掴む。
                    // Overlays()[0] を直接書くと、まだ 1 つも無い実行で
                    // ArgumentOutOfRangeException になり、診断の付かない失敗になる
                    nint overlayHwnd = Ui.Until(
                        () => host.Overlays() is { Count: > 0 } found
                            ? (object)(nint)found[0].Current.NativeWindowHandle
                            : null,
                        TimeSpan.FromSeconds(20),
                        "オーバーレイのウィンドウが出ること",
                        host.Diagnostics) is nint h ? h : 0;
                    IReadOnlyList<AutomationElement> rows =
                        host.Tree().Descendants(ControlType.TreeItem);

                    return new FrozenScenario(target, host, rects, pickerHwnd, overlayHwnd, rows);
                }
                catch
                {
                    host.Dispose();
                    throw;
                }
            }
            catch
            {
                target.Dispose();
                throw;
            }
        }

        /// <summary>捕捉直後はチェーンだけなので、親を閉じて開き直して兄弟を実体化させる。</summary>
        private static void RevealTheSiblings(PickerHostProcess host)
        {
            IReadOnlyList<AutomationElement> rows = host.Tree().Descendants(ControlType.TreeItem);
            Assert.True(rows.Count >= 2, "親の行がありません。");
            ExpandCollapsePattern expander = rows[^2].Expander();
            expander.Collapse();
            expander.Expand();
            _ = Ui.Until(
                () => host.Tree().RowNames().Any(
                    r => r.Contains($"[{Other}]", StringComparison.Ordinal)) ? "ok" : null,
                TimeSpan.FromSeconds(20),
                $"兄弟の行 ({Other}) が実体化すること",
                host.Diagnostics);
        }

        /// <summary>対象アプリの UI スレッドを塞ぐ。<b>応答は先に返る。</b></summary>
        public void HangTheTarget(TimeSpan duration)
        {
            _target.Hang(duration);
            // 「この窓のあいだ塞がっていた」ことを固定するために先に送っておく
            _target.Post("ping");
        }

        /// <summary>
        /// 塞がっている最中でも安全に読める、選択されている行の名前。
        /// </summary>
        /// <remarks>掴んでおいた HWND から辿る。理由はこのクラスの remarks を参照。</remarks>
        public string? SelectedRowName()
            => AutomationElement.FromHandle(_pickerHwnd)
                .RequireById(PickerHostProfile.Wpf.TreeAutomationId)
                .SelectedRow()?.NameOf();

        /// <summary>塞がっている最中でも安全に読める、枠の矩形。<c>GetWindowRect</c> だけを使う。</summary>
        public System.Windows.Rect? FrameRect()
            => NativeWindows.RectOf(_overlayHwnd) is { } r
                ? new System.Windows.Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)
                : null;

        public string[] PropertyRows()
            => [.. AutomationElement.FromHandle(_pickerHwnd).RequireById("PropsList")
                .Descendants(ControlType.ListItem).Select(Ui.NameOf)];

        /// <summary>
        /// その行を選んだときに枠が出るべき矩形。<c>OverlayGeometry</c> (純関数) から計算する。
        /// </summary>
        /// <remarks>
        /// 元になる要素の矩形は<b>塞ぐ前に</b>読んである。塞がれた相手には訊けないうえ、
        /// 枠が使うのも捕捉時のキャッシュなので、そちらが正しい情報源である。
        /// マジックナンバーを書かないので、幾何を変えたらここも一緒に動く。
        /// </remarks>
        public System.Windows.Rect ExpectedFrameFor(string button)
        {
            ElementRect element = _buttonRects[button];
            // 製品と同じで、DPI は要素が乗っているモニターから引く (docs/DESIGN.md §9)。
            // 見ているのは<b>枠の窓</b>である (確定アイコンは別の窓。docs/DESIGN.md §10)
            int dpi = NativeWindows.DpiAt(element.Left, element.Top);
            (int w, int h) = OverlayGeometry.FrameSize(element, dpi);
            (int x, int y) = OverlayGeometry.FrameOrigin(element);
            return new System.Windows.Rect(x, y, w, h);
        }

        /// <summary>掴んでおいた行を選ぶ。<b>これがピッカーの UI スレッドへの探針である。</b></summary>
        public void SelectTheOtherRow()
        {
            AutomationElement row = _rows.FirstOrDefault(
                r => r.NameOf().Contains($"[{Other}]", StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"{Other} の行を塞ぐ前に掴めていません:{Environment.NewLine}" +
                    string.Join(Environment.NewLine, _rows.Select(Ui.NameOf)));
            ((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        }

        /// <summary>
        /// 観測の窓のあいだ、対象アプリが本当に応答していなかったこと。
        /// </summary>
        /// <remarks>
        /// <b>ハーネス自身の検証である</b> (docs/TESTING.md §4 の教訓 (b))。塞ぎが効いていなければ
        /// 「塞がっている最中でも選べた」は<b>何も言っていない</b> — 応答するアプリを相手に
        /// 普通に選べただけである。
        /// </remarks>
        public void RequireTheTargetWasBlockedThroughout(Stopwatch clock, TimeSpan hang)
        {
            _target.AwaitResponse();
            TimeSpan elapsed = clock.Elapsed;
            TimeSpan least = hang * 0.8;
            Assert.True(
                elapsed >= least,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"対象アプリは {elapsed.TotalMilliseconds:0}ms で応答しました ({hang.TotalMilliseconds:0}ms 塞いだはずです)。") +
                "塞ぐ前に送った ping がこれだけ早く返ったということは、観測のあいだ相手は応答していました。" +
                "この状態では「凍っていない」ことに意味がありません。");
        }

        /// <summary>
        /// 失敗したときに読む状態。<b>対象アプリにもデスクトップの列挙にも触らない。</b>
        /// </summary>
        /// <remarks>
        /// <c>PickerHostProcess.Diagnostics()</c> は使えない。あれは対象アプリを読み、
        /// デスクトップの子を列挙するので、<b>いちばん診断が要る場面で詰まるか
        /// <c>Win32Exception</c> を投げる</b> — 後者は <c>Ui.Until</c> の診断収集が捕まえないので
        /// <b>本当の失敗を置き換えて消す</b> (docs/TESTING.md §1)。
        /// </remarks>
        public string Describe()
        {
            var lines = new List<string>
            {
                FormattableString.Invariant($"枠の矩形: {FrameRect()}"),
                FormattableString.Invariant($"期待する枠 ({Other}): {ExpectedFrameFor(Other)}"),
            };
            try
            {
                AutomationElement picker = AutomationElement.FromHandle(_pickerHwnd);
                AutomationElement tree = picker.RequireById(PickerHostProfile.Wpf.TreeAutomationId);
                lines.Add("ツリーの行 — * は選択されている行:");
                lines.Add(tree.Descendants(ControlType.TreeItem)
                    .Select(r => (r.IsSelected() ? "* " : "  ") + r.NameOf())
                    .ToArray()
                    .Describe());
                lines.Add("プロパティ一覧: " + string.Join(" | ", PropertyRows()));
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
            {
                lines.Add($"  (ピッカーの状態を読めませんでした: {ex.GetType().Name}: {ex.Message})");
            }
            lines.Add(_host.HostLog());
            return string.Join(Environment.NewLine, lines);
        }

        public void Dispose()
        {
            _host.Dispose();
            _target.Dispose();
        }
    }
}
