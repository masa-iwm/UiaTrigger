// 対象アプリ + サンプルホスト + n 枚のピッカーを組み立て、オーバーレイを外から観測する
// 舞台 (docs/DESIGN.md §10)。
//
// <b>T4 と T5 の両方が使う。</b>T5 (合成入力) は同じ舞台の上でキーを撃ち、枠が動くかを見る
// (docs/TESTING.md §3)。写しを作らないのは意図である — 片方だけ直された状態ができると、
// 「T4 では通るのに T5 では通らない」の原因がハーネスの差になる。
//
// 座標はすべて<b>物理ピクセル</b>である。3 者とも PerMonitorV2 を宣言しているので突き合わせが
// 成立する: テストプロセスは RequireCoordinateSafeProcess、サンプルホストは app.manifest、
// 対象アプリは Program.Main の SetHighDpiMode。どれか 1 つが非認識だと座標が仮想化され、
// <b>100% スケールの機械では一致して見えるのに拡大時だけ静かにずれる</b>
// (docs/DESIGN.md A19 / docs/TESTING.md §4)。
using System.Globalization;
using System.Windows.Automation;
using UiaTrigger.Models;
using UiaTrigger.Picker;
using UiaTrigger.RealUia.Tests;
using Xunit;

namespace UiaTrigger.Picker.UiTests;

/// <summary>対象アプリ + ホスト + n 枚のピッカーを組み立てる。</summary>
internal sealed class OverlayScenario : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    /// <summary>捕捉させる対象アプリのボタン。<b>n 枚目のピッカーが n 番目</b>を捕捉する。</summary>
    private static readonly string[] Buttons = ["OverlayA", "OverlayB"];

    private readonly TestTargetProcess _target;
    private readonly List<(int Left, int Top, int Right, int Bottom)> _buttonRects = [];

    public PickerHostProcess Host { get; }

    /// <summary>対象アプリ。<b>塞ぐ・前へ出すなど、舞台を動かすときだけ触る。</b></summary>
    public TestTargetProcess Target => _target;

    private OverlayScenario(TestTargetProcess target, PickerHostProcess host)
    {
        _target = target;
        Host = host;
    }

    public static OverlayScenario Open(int pickers)
    {
        TestTargetProcess target = TestTargetProcess.Start(TargetProfile.WinForms);
        try
        {
            // ピッカーの窓と重ならない場所へ置く。TopMost にしてあっても UIA の
            // ヒットテストは重なった窓を返す (docs/DESIGN.md §12)。割り付けは DesktopLayout が決める
            target.Send(DesktopLayout.PlaceTargetCommand);
            foreach (string name in Buttons)
            {
                target.Send("add-button " + name);
            }
            target.Send("ping");

            var rects = new List<(int, int, int, int)>();
            var points = new List<(int X, int Y)>();
            foreach (string name in Buttons)
            {
                (int l, int t, int r, int b) = target.RectOf(name);
                rects.Add((l, t, r, b));
                points.Add(((l + r) / 2, (t + b) / 2));
            }

            PickerHostProcess host = PickerHostProcess.Start(
                PickerHostProfile.Wpf, [.. points.Take(pickers)]);
            try
            {
                host.OpenPicker();
                for (int i = 1; i < pickers; i++)
                {
                    host.OpenAnotherPicker();
                }
                var scenario = new OverlayScenario(target, host);
                scenario._buttonRects.AddRange(rects.Take(pickers));
                return scenario;
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

    /// <summary>期待される枠の矩形。<c>OverlayGeometry</c> から計算する。</summary>
    public List<System.Windows.Rect> ExpectedRects()
    {
        var expected = new List<System.Windows.Rect>();
        foreach ((int l, int t, int r, int b) in _buttonRects)
        {
            var element = new ElementRect(l, t, r, b);
            // 製品と同じで、DPI は要素が乗っているモニターから引く (docs/DESIGN.md §9)
            int dpi = NativeWindows.DpiAt(l, t);
            (int w, int h) = OverlayGeometry.FrameSize(element, dpi);
            (int x, int y) = OverlayGeometry.FrameOrigin(element);
            expected.Add(new System.Windows.Rect(x, y, w, h));
        }
        return expected;
    }

    /// <summary>期待される確定アイコンの窓の矩形 (docs/DESIGN.md §10)。</summary>
    public List<System.Windows.Rect> ExpectedIconRects()
    {
        var expected = new List<System.Windows.Rect>();
        foreach ((int l, int t, int r, int b) in _buttonRects)
        {
            int dpi = NativeWindows.DpiAt(l, t);
            (int x, int y, int size) = OverlayGeometry.IconRect(new ElementRect(l, t, r, b), dpi);
            expected.Add(new System.Windows.Rect(x, y, size, size));
        }
        return expected;
    }

    /// <summary>
    /// 枠とアイコンが <paramref name="count"/> 個ずつになるまで待ち、<b>枠の</b>矩形を返す。
    /// </summary>
    /// <remarks>
    /// <b>両方を待つ。</b>1 枚のピッカーは窓を 2 つ出す (docs/DESIGN.md §10) ので、片方だけ見て先へ進むと
    /// 「アイコンがまだ出ていない」状態を掴む。どちらが先に UIA に現れるかは決まっていない。
    /// </remarks>
    public List<System.Windows.Rect> WaitForOverlays(int count) => Ui.Until(
        () =>
        {
            IReadOnlyList<AutomationElement> overlays = Host.Overlays();
            if (overlays.Count != count || Host.IconOverlays().Count != count)
            {
                return null;
            }
            // 枠は 1 回の選択で 2 度描かれるので、期待と一致するまで待つ。
            // 1 回だけ見ると途中の矩形を掴む
            List<System.Windows.Rect> rects = [.. overlays.Select(o => o.Current.BoundingRectangle)];
            return rects.Count == count ? rects : null;
        },
        Settle,
        $"枠とアイコンが {count} 個ずつになること",
        Describe);

    /// <summary>ピッカーを 1 枚閉じる。<b>どちらが閉じるかは決められない</b>。</summary>
    public void CloseOnePicker()
    {
        AutomationElement picker = Host.PickerWindow();
        ((WindowPattern)picker.GetCurrentPattern(WindowPattern.Pattern)).Close();
    }

    /// <summary>対象アプリをトップモースト帯の先頭へ出し直す。</summary>
    public void BringTargetToTheFrontOfTheTopmostBand()
    {
        _target.Send("topmost-refresh");
        _target.Send("ping");
    }

    /// <summary>
    /// 枠を描き直させる。ツリーの別の行を選ぶと <c>ShowRect</c> が呼ばれる。
    /// </summary>
    /// <remarks>
    /// UIA のコントロールパターンだけで駆動できる。<b>枠の位置が変わるまで待つ</b> —
    /// 「選んだ」ことと「描き直された」ことは別なので、待たずに次へ進むと
    /// 直しが効いているかどうかと無関係に落ちうる。
    /// </remarks>
    public void ForceOverlayRedraw()
    {
        System.Windows.Rect before = Host.Overlays()[0].Current.BoundingRectangle;
        IReadOnlyList<AutomationElement> rows = Host.Tree().Descendants(ControlType.TreeItem);
        Assert.True(rows.Count >= 2, $"ツリーの行が {rows.Count} 個しかなく、別の行を選べません。");
        ((SelectionItemPattern)rows[^2].GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

        _ = Ui.Until(
            () => Host.Overlays() is { Count: > 0 } now &&
                  now[0].Current.BoundingRectangle != before ? "ok" : null,
            Settle,
            "別の行を選んで枠が描き直されること",
            Describe);
    }

    /// <summary>確定アイコンの窓のハンドル。</summary>
    public nint IconHwnd()
        => (nint)(Host.IconOverlays() is { Count: > 0 } found
            ? found[0].Current.NativeWindowHandle
            : throw new InvalidOperationException("確定アイコンの窓がありません。" + Describe()));

    /// <summary>
    /// 確定アイコンの点が<b>対象アプリの窓の中</b>にあること。
    /// </summary>
    /// <remarks>
    /// これが成り立っていないと「オーバーレイと対象アプリのどちらが上か」という問い自体が
    /// 立たない。ハーネス自身の検証である (docs/TESTING.md §4 の教訓 (b))。
    /// </remarks>
    public void RequireTheIconOverlapsTheTarget()
    {
        nint hwnd = IconHwnd();
        (int Left, int Top, int Right, int Bottom) r =
            NativeWindows.RectOf(hwnd) ?? throw new InvalidOperationException("アイコンの矩形が取れません。");
        (int x, int y) = IconCentre(r);

        (int wl, int wt, int wr, int wb) = _target.RectOf("root");
        Assert.True(
            x >= wl && x < wr && y >= wt && y < wb,
            FormattableString.Invariant(
                $"確定アイコンの点 ({x},{y}) が対象アプリの窓 ({wl},{wt})-({wr},{wb}) の外にあります。") +
            "この状態では前後関係を比べる相手が居らず、テストの結果に意味がありません。");
    }

    /// <summary>
    /// 確定アイコンの中心 (スクリーン座標) = <b>アイコンの窓の矩形の中心</b>。
    /// </summary>
    /// <remarks>
    /// アイコンは独立した窓である (docs/DESIGN.md §10) ので、その矩形の中心が
    /// そのまま答えになる — <c>OverlayGeometry</c> の実寸を写した式は要らず、
    /// <b>写した式が製品とずれる余地が無い</b>。
    /// 引数は <see cref="IconHwnd"/> の <c>RectOf</c> であること (枠の矩形ではない)。
    /// </remarks>
    public static (int X, int Y) IconCentre((int Left, int Top, int Right, int Bottom) icon)
        => ((icon.Left + icon.Right) / 2, (icon.Top + icon.Bottom) / 2);

    public string DescribeIconPixel()
    {
        IReadOnlyList<AutomationElement> found = Host.IconOverlays();
        if (found.Count == 0)
        {
            return "確定アイコンの窓がありません。" + Describe();
        }
        var hwnd = (nint)found[0].Current.NativeWindowHandle;
        if (NativeWindows.RectOf(hwnd) is not { } r)
        {
            return $"アイコンの矩形が取れません ({NativeWindows.Describe(hwnd)})。";
        }
        (int x, int y) = IconCentre(r);
        nint at = NativeWindows.WindowAt(x, y);
        return FormattableString.Invariant(
            $"アイコン中心 ({x},{y}) に居るのは {NativeWindows.Describe(at)}。") +
            FormattableString.Invariant($" アイコンの窓は {NativeWindows.Describe(hwnd)}。") +
            Environment.NewLine + Describe();
    }

    public string Describe()
    {
        string frames = Lines("枠", Host.Overlays(), ExpectedRects());
        string icons = Lines("アイコン", Host.IconOverlays(), ExpectedIconRects());
        return string.Join(Environment.NewLine, frames, icons, Host.Diagnostics());
    }

    private static string Lines(
        string label, IReadOnlyList<AutomationElement> windows, List<System.Windows.Rect> expected)
    {
        string seen = windows.Count == 0
            ? $"  ({label}なし)"
            : string.Join(
                Environment.NewLine,
                windows.Select(o => $"  {label} 観測: " +
                    o.Current.BoundingRectangle.ToString(CultureInfo.InvariantCulture)));
        string want = string.Join(
            Environment.NewLine,
            expected.Select(r => $"  {label} 期待: " + r.ToString(CultureInfo.InvariantCulture)));
        return string.Join(Environment.NewLine, seen, want);
    }

    public void Dispose()
    {
        Host.Dispose();
        _target.Dispose();
    }
}
