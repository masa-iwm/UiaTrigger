// T4: 深い階層になったときのツリーの見え方 (docs/TESTING.md §1)。
//
// 「深い階層で水平スクロールする」と「確定アイコンが要素の行にだけ出る」の一次の網。
// どちらも<b>ウィンドウを画面に出さないと成立しない</b>ので T1 の担当ではない
// (docs/TESTING.md §2 / docs/MANUAL-CHECKS.md §4.3.2)。
//
// 2 変種とも回す。水平スクロールは<b>変種で機構が違う</b> — WinUI の TreeView は
// 自分に付いた ScrollViewer.* をテンプレート内へ中継しないのでコードビハインドで
// 内側の ListControl に付けており、WPF は既定テンプレートが中継する。
// 「同じ添付プロパティが片方では効いて片方では黙って無視される」という差そのものが
// 検査対象なので、ここは 3 変種へ機械的に広げないのとは逆に、2 つとも要る。
using System.Globalization;
using System.Windows.Automation;
using UiaTrigger.RealUia.Tests;
using Xunit;

namespace UiaTrigger.Picker.UiTests;

public sealed class TreePresentationTests
{
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 水平スクロールが要るようになるまで試す深さ。<b>1 つの固定値にはしない。</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 12 段では 2 変種とも <c>HorizontallyScrollable</c> が false だった —
    /// 行が字下げされても内容が表示域に収まっていたためである。
    /// つまり「深いチェーンを作れば水平スクロールが要る」は自明ではなく、
    /// <b>要るところまで深くして初めて検査になる</b>。
    /// </para>
    /// <para>
    /// <b>固定値 1 つにすると、それは較正であって、測った機械でしか成り立たない</b>
    /// (docs/DESIGN.md §9)。ホストの窓はデスクトップの大きさから決まるので、
    /// 表示域の幅は画面の解像度と表示スケールで変わる。実際 3840x2160 / 175% では
    /// <b>30 段が収まってしまい</b> (実測: ツリー幅 1838px に対し水平表示率 100.0%)、
    /// 「スクロールできない」ではなく「要らない」で赤くなる。
    /// <b>検出力を失ったまま赤い</b>という、いちばん質の悪い壊れ方である。
    /// </para>
    /// <para>
    /// <b>直しの本体は深さではなく表示域のほうである</b> —
    /// <see cref="DesktopLayout.NarrowHost"/> で<b>ホストの窓を固定幅にした</b>ので、
    /// 「収まるかどうか」がデスクトップの広さで変わらなくなった。
    /// <b>深さを増やす方向では直せない</b>ことも実測で分かっている:
    /// 対象アプリは 1 段ごとに HWND を作るので <b>55 段でウィンドウハンドルの作成に失敗する</b>し、
    /// そもそも行は字下げに応じて<b>縮んで収まる</b> (実測: 深さ 30 → 40 で
    /// 行の幅が 741px → 409px になり、右端は 1868px のまま動かなかった)。
    /// </para>
    /// <para>
    /// 深さを 2 段構えにしてあるのは保険である。文字の大きさやテーマが違う機械で
    /// 30 段が収まってしまっても、45 段まで試してから落ちる。
    /// 全部の深さで駄目なら実測を添えて落とす。
    /// </para>
    /// </remarks>
    private static readonly int[] DepthsToTry = [30, 45];

    /// <summary>深さを問わない検査で使う深さ (行が数段あればよい)。</summary>
    private const int ShallowDepth = 30;

    /// <summary>1 つの深さで「まだ収まっている」と見切るまでの待ち。</summary>
    /// <remarks>葉の行が選ばれたあとなので、レイアウトは既に落ち着いている。</remarks>
    private static readonly TimeSpan ScrollSettleTimeout = TimeSpan.FromSeconds(5);

    private const string Leaf = "deep-leaf";

    /// <summary>
    /// 深いチェーンを捕捉すると、ツリーが<b>水平にスクロールできる</b>ようになること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 推測で直して外すことを 3 回繰り返した箇所である。
    /// WinUI の <c>TreeView</c> は自分に付いた <c>ScrollViewer.HorizontalScrollMode</c> を
    /// テンプレート内の <c>ScrollViewer</c> へ中継しないので、コードビハインドで
    /// 実物を掴んで設定している。<b>中継しないことは例外にならない</b> —
    /// 黙って無視されて、行が右で切れるだけである。
    /// </para>
    /// <para>
    /// <b>対照として、チェーンを畳んだら false に戻ることも見る。</b>
    /// これが無いと「常に true を返す」実装 (スクロールバーを出しっぱなしにする等) でも通る。
    /// </para>
    /// <para>
    /// 退行: WinUI のコードビハインドの付け替えを消して <c>TreeView</c> に直接付ける → false になる。
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(PickerHostProfile.AllNames), MemberType = typeof(PickerHostProfile))]
    public void ADeepChain_MakesTheTreeScrollHorizontally(string profileName)
    {
        var tried = new List<string>();
        foreach (int depth in DepthsToTry)
        {
            DeepScenario? opened;
            try
            {
                opened = DeepScenario.Open(profileName, depth);
            }
            catch (InvalidOperationException ex)
            {
                // 入れ子の上限に当たった (WinForms の対象アプリは HWND を 1 段ごとに作るので
                // 深くしすぎるとウィンドウハンドルの作成に失敗する)。
                // <b>これ以上は深くできない</b>ので、ここまでの実測を添えて落とす
                tried.Add($"深さ={depth}: 対象アプリが作れませんでした ({ex.Message})");
                break;
            }

            using DeepScenario scenario = opened;
            scenario.WaitForTheLeafToBeSelected();

            if (!scenario.BecomesHorizontallyScrollable(ScrollSettleTimeout))
            {
                // まだ表示域に収まっている。<b>これは失敗ではない</b> — もっと深くする
                tried.Add(scenario.Measurements());
                continue;
            }

            // 対照: 畳めば内容が収まり、スクロールは要らなくなる
            scenario.CollapseTheWholeChain();
            _ = Ui.Until(
                () => scenario.Tree().CanScrollHorizontally() == false ? "ok" : null,
                CaptureTimeout,
                "チェーンを畳むと水平スクロールが要らなくなること (計測に検出力があること)",
                scenario.DescribeScrolling);
            return;
        }

        Assert.Fail(
            $"深さ {string.Join(" / ", DepthsToTry)} のどれでもツリーが水平にスクロールできませんでした。" +
            Environment.NewLine +
            "どの深さでも表示率が 100% のままなら表示域が広すぎるだけなので、深さの候補を増やしてください。" +
            "100% でないのにスクロールできないなら、それは製品側の退行です。" +
            Environment.NewLine + string.Join(Environment.NewLine, tried));
    }

    /// <summary>
    /// 確定アイコンが<b>要素の行にだけ</b>出ること (プロセスルートの行には出ない)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// WinUI では <c>Visibility="{x:Bind local:TriggerPickerWindow.ToVisibility(CanConfirm)}"</c> の
    /// <b>関数束縛</b>である。関数束縛が黙って解決に失敗すると
    /// <b>全行に出る / 全行から消える</b>の形で壊れ、例外は出ない。
    /// WPF は <c>BooleanToVisibilityConverter</c> なので機構は違うが、壊れ方の形は同じである。
    /// </para>
    /// <para>
    /// <b>「行ごとに 1 つ」まで見る。</b>合計だけを見ると、
    /// 1 行に 2 つ出て別の 1 行から消える壊れ方が素通りする。
    /// 数え方は <see cref="Ui.ConfirmButtonsOfItsOwn"/> を参照 (WPF は行が入れ子なので引き算が要る)。
    /// </para>
    /// <para>
    /// 退行: 束縛を壊す → 全行が 1 になるか全行が 0 になり、どちらの向きも落ちる。
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(PickerHostProfile.AllNames), MemberType = typeof(PickerHostProfile))]
    public void TheConfirmIcon_IsOnEveryRowExceptTheProcessRoot(string profileName)
    {
        using var scenario = DeepScenario.Open(profileName, ShallowDepth);
        scenario.WaitForTheLeafToBeSelected();

        IReadOnlyList<AutomationElement> rows = Ui.Until(
            () => scenario.Tree().Descendants(ControlType.TreeItem) is { Count: > 2 } found ? found : null,
            CaptureTimeout,
            "チェーンの行が出そろうこと",
            scenario.Diagnostics);

        int[] counts = [.. rows.Select(r => r.ConfirmButtonsOfItsOwn())];
        string detail = string.Join(
            Environment.NewLine,
            rows.Select((r, i) => string.Create(
                CultureInfo.InvariantCulture, $"  [{i}] 確定ボタン {counts[i]} 個: {r.NameOf()}")));

        Assert.True(
            counts[0] == 0,
            $"プロセスルートの行に確定ボタンが {counts[0]} 個あります。" +
            $"プロセスは確定できないので 0 であるべきです:{Environment.NewLine}{detail}");
        Assert.True(
            counts.Skip(1).All(c => c == 1),
            $"プロセスルート以外の行には確定ボタンがちょうど 1 つずつ在るべきです:" +
            $"{Environment.NewLine}{detail}");
    }

    /// <summary>対象アプリ 1 つ + ホスト 1 つ + 深いチェーンを捕捉した状態のピッカー。</summary>
    private sealed class DeepScenario : IDisposable
    {
        private readonly TestTargetProcess _target;
        private readonly PickerHostProcess _host;
        private readonly int _depth;

        private DeepScenario(TestTargetProcess target, PickerHostProcess host, int depth)
        {
            _target = target;
            _host = host;
            _depth = depth;
        }

        public static DeepScenario Open(string profileName, int depth)
        {
            PickerHostProfile profile = PickerHostProfile.ByName(profileName);
            // 幅を固定したホストは画面が狭いと pick 点を覆う。先に言っておかないと、
            // 症状が「30 秒待っても退かない」という原因を名指ししないタイムアウトになる
            DesktopLayout.RequireTheNarrowHostFitsOnThisScreen();
            TestTargetProcess target = TestTargetProcess.Start(TargetProfile.WinForms);
            try
            {
                target.Send(DesktopLayout.PlaceTargetCommand);
                target.Send(string.Create(CultureInfo.InvariantCulture, $"add-depth {depth} {Leaf}"));
                target.Send("ping");

                (int x, int y) = target.CenterOf(Leaf);
                // 表示域の広さそのものが検査対象なので、ホストの窓は<b>幅を固定した</b>ほうへ置く。
                // 既定の DesktopLayout.Host はデスクトップの広さで幅が変わる (docs/DESIGN.md §9)
                PickerHostProcess host = PickerHostProcess.Start(
                    profile, x, y, DesktopLayout.NarrowHost);
                try
                {
                    host.OpenPicker();
                    return new DeepScenario(target, host, depth);
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

        public AutomationElement Tree() => _host.Tree();

        /// <summary>
        /// 期限内に水平スクロールが要るようになったか。<b>ならなくても例外にしない</b>。
        /// </summary>
        /// <remarks>
        /// <see cref="Ui.Until"/> ではなく専用に書いてあるのは、ここでの false が
        /// <b>失敗ではなく「まだ浅い」</b>だからである。例外にすると、呼ぶ側が
        /// 深さを増やして試し直せない。
        /// </remarks>
        public bool BecomesHorizontallyScrollable(TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                try
                {
                    if (Tree().CanScrollHorizontally() == true)
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
                {
                    // 行が入れ替わっている最中。次の周回で読み直す
                }
                if (DateTime.UtcNow >= deadline)
                {
                    return false;
                }
                Thread.Sleep(200);
            }
        }

        public void WaitForTheLeafToBeSelected() => Ui.Until(
            () => Tree().SelectedRow()?.NameOf() is { } name &&
                  name.Contains($"[{Leaf}]", StringComparison.Ordinal)
                ? name
                : null,
            CaptureTimeout,
            $"ホバー捕捉で {Leaf} の行が選択されること",
            Diagnostics);

        /// <summary>
        /// いちばん外の行を畳む。中身がすべて隠れるので、横幅の要求も消える。
        /// </summary>
        public void CollapseTheWholeChain()
        {
            IReadOnlyList<AutomationElement> rows = Tree().Descendants(ControlType.TreeItem);
            Assert.True(rows.Count > 0, "ツリーに行がありません。");
            rows[0].Expander().Collapse();
        }

        public string Diagnostics() => _host.Diagnostics();

        public string DescribeScrolling()
        {
            string state;
            try
            {
                state = Tree().CanScrollHorizontally() switch
                {
                    true => "水平にスクロールできる",
                    false => "水平にはスクロールできない",
                    null => "ScrollPattern を出していない (検査対象を取り違えている合図)",
                };
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
            {
                state = $"ツリーの状態を読めませんでした: {ex.Message}";
            }
            return $"ツリー: {state}" + Environment.NewLine + Measurements() + Environment.NewLine + Diagnostics();
        }

        /// <summary>
        /// 幅の実測。<b>「収まっているので要らない」と「スクロールが壊れている」を分けるために要る。</b>
        /// </summary>
        /// <remarks>
        /// どちらも <c>HorizontallyScrollable == false</c> になるので、状態だけ見ても区別できない。
        /// 水平表示率が 100% なら前者である。この区別が付かないと、
        /// 較正が合わなくなっただけの赤を製品の不具合と読んでしまう (実際に読みかけた)。
        /// </remarks>
        public string Measurements()
        {
            try
            {
                AutomationElement tree = Tree();
                System.Windows.Rect treeRect = tree.Current.BoundingRectangle;
                string view = tree.TryGetCurrentPattern(ScrollPattern.Pattern, out object? pattern)
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"{((ScrollPattern)pattern).Current.HorizontalViewSize:F1}%")
                    : "(ScrollPattern 無し)";
                System.Windows.Rect? row = tree.SelectedRow()?.Current.BoundingRectangle;
                string rowText = row is { } r
                    ? string.Create(CultureInfo.InvariantCulture, $"x{r.Left:F0}..{r.Right:F0}")
                    : "(無し)";
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"実測: 深さ={_depth} ツリー幅={treeRect.Width:F0}px 水平表示率={view} 選択行={rowText}");
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
            {
                return $"実測: 読めませんでした ({ex.Message})";
            }
        }

        public void Dispose()
        {
            _host.Dispose();
            _target.Dispose();
        }
    }
}
