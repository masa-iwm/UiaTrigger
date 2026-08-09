// S1: オーバーレイを**外から**観測する層 (docs/DESIGN.md §10)。
//
// ここでしか見られないものが 2 つある:
//   - 2 枚のピッカーが**それぞれ独立に**枠を出すこと。A18 で static singleton を
//     やめた効果は、T1 の fake View では見えない (オーバーレイは実ウィンドウである)
//   - 確定アイコンのピクセルの上にオーバーレイが居ること。他のトップモーストウィンドウが
//     帯の順位を主張すると、枠は見えたまま**アイコンだけが押せなくなる**
//
// WPF だけで回す。OverlayController は Picker.Core に在って 3 変種で**同じもの**なので、
// 変種を増やしても検査対象の被覆は増えない。
//
// 「WinUI は捕捉そのものが不安定」という理由は当てにしない — 不安定さの正体は
// ピッカーの窓がカスケードで pick 点を覆うかどうかが実行ごとに変わることで、
// いまは構造から消してある (docs/TESTING.md §1)。上の理由だけが残る。
//
// 舞台の組み立てと座標系の前提は OverlayScenario.cs にある (T5 と共有している)。
using System.Windows.Automation;
using UiaTrigger.Picker;
using Xunit;

namespace UiaTrigger.Picker.UiTests;

/// <summary>オーバーレイのウィンドウを外から数え、位置と前後関係を見る (S1)。</summary>
public sealed class OverlayTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 枠が 1 枚のとき、オーバーレイは**枠 1 つとアイコン 1 つ**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 2 枚の対照実験である。**数えることに検出力があること**をここで示しておかないと、
    /// 「2 つある」という主張が「たまたま 2 つ見えた」と区別できない。
    /// </para>
    /// <para>
    /// **1 枚のピッカーは窓を 2 つ出す** (docs/DESIGN.md §10)。枠とアイコンを別の窓に
    /// 割るのがクリックスルーの設計そのものである。クラス名が違うので別々に数えられる —
    /// 数えるものを 1 つにまとめると「ピッカーが 2 枚」と「窓が 2 種類」が区別できなくなり、
    /// このテストの検出力が消える。
    /// </para>
    /// </remarks>
    [Fact]
    public void OnePicker_ShowsExactlyOneFrameAndOneIcon()
    {
        using var scenario = OverlayScenario.Open(pickers: 1);
        List<System.Windows.Rect> rects = scenario.WaitForOverlays(1);
        Assert.Single(rects);
        Assert.Single(scenario.Host.IconOverlays());
    }

    /// <summary>
    /// 2 枚のピッカーが、**それぞれ自分の pick 点の要素**に枠を出すこと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **assert は集合の一致で書く。**オーバーレイにはタイトルも AutomationId も無く
    /// クラス名は共通なので、どの枠がどのピッカーのものかを言うテストは書けない。
    /// 期待側は <c>OverlayGeometry</c> (純関数) から計算する — マジックナンバーを書くと
    /// 幾何が変わったときに**テストだけが古い正解を守り続ける**。
    /// </para>
    /// <para>
    /// **1 回のサンプルでは見ない。**枠は 1 回の選択で 2 度描かれる
    /// (要素のスナップショット矩形で出したあと、現在値で描き直す) ので、
    /// 途中の状態を掴むと落ちる。
    /// </para>
    /// <para>
    /// 退行: <c>OverlayController</c> を static singleton へ戻すと 2 つが 1 つに潰れ、
    /// 集合の一致が落ちる。
    /// </para>
    /// </remarks>
    [Fact]
    public void TwoPickers_ShowTwoOverlaysAtTheirOwnElements()
    {
        using var scenario = OverlayScenario.Open(pickers: 2);
        List<System.Windows.Rect> rects = scenario.WaitForOverlays(2);

        Assert.Equal(
            scenario.ExpectedRects().OrderBy(r => r.Left).ThenBy(r => r.Top),
            rects.OrderBy(r => r.Left).ThenBy(r => r.Top));

        // アイコンの窓も同じ形で見る (docs/DESIGN.md §10)。枠だけ合っていてアイコンが取り残される
        // 壊れ方 — 位置の計算を 1 か所しか直さなかった場合 — はここでしか捕まらない
        Assert.Equal(
            scenario.ExpectedIconRects().OrderBy(r => r.Left).ThenBy(r => r.Top),
            scenario.Host.IconOverlays()
                .Select(o => o.Current.BoundingRectangle)
                .OrderBy(r => r.Left).ThenBy(r => r.Top));
    }

    /// <summary>
    /// 1 枚目を閉じても、2 枚目の枠は**元の場所に生き残る**こと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// A18 の本題である。static singleton に戻すと、1 枚目を閉じたとき
    /// 共有していたウィンドウごと消える。
    /// </para>
    /// <para>
    /// **「どちらが残ったか」は言わない。**ピッカーの窓とオーバーレイを対応づける手段が
    /// 無いのと同じ理由で、閉じたのがどちらの窓なのかもテストからは決められない
    /// (窓を探す条件は「要素ツリーを持っていること」で、2 枚とも当てはまる)。
    /// 言えるのは「1 つ残り、それは**2 つの期待位置のどちらか**である」まで。
    /// </para>
    /// <para>
    /// これでも狙いは果たす: 共有ウィンドウへ戻せば**0 個**になって落ちるし、
    /// 残ったほうが位置を失えば集合に属さなくなって落ちる。
    /// 「1 つ残った」だけを見るより強い。
    /// </para>
    /// </remarks>
    [Fact]
    public void ClosingOnePicker_LeavesTheOtherOverlayWhereItWas()
    {
        using var scenario = OverlayScenario.Open(pickers: 2);
        _ = scenario.WaitForOverlays(2);

        List<System.Windows.Rect> expected = scenario.ExpectedRects();
        scenario.CloseOnePicker();

        List<System.Windows.Rect> rects = scenario.WaitForOverlays(1);
        Assert.Contains(rects[0], expected);
    }

    /// <summary>
    /// 別のトップモーストウィンドウが帯の順位を主張しても、**描き直せば**
    /// 確定アイコンのピクセルはオーバーレイのものであること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>WS_EX_TOPMOST</c> は「帯に入る」だけで、帯の中の順位は最後に主張したものが勝つ。
    /// 対象アプリも <c>TopMost</c> なので、それが順位を主張し直すとオーバーレイは下へ回る。
    /// 枠は見えたままで、**確定アイコンだけが押せなくなる** — 例外は出ない。
    /// </para>
    /// <para>
    /// **「描き直せば」が付くのは正直に書いた制限である。**直しは
    /// <c>Redraw()</c> で最前面を主張し直すもので、再描画が起きるまでは下に居たままになる。
    /// 実際の操作ではホバーのたびに描き直すので実用上は戻るが、
    /// **瞬時に戻るわけではない**。ここではツリーの別の行を選んで描き直させている。
    /// </para>
    /// <para>
    /// 見る点は**アイコンの内側**である。レイヤードウィンドウのヒットテストは
    /// ピクセルごとのアルファを尊重するので、枠の透明な内側では Z オーダーに関係なく
    /// 下のウィンドウが返る。
    /// </para>
    /// <para>
    /// 退行: <c>Redraw()</c> の <c>SetWindowPos</c> を消すと落ちる (実測で確認済み)。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheConfirmIconStaysOnTop_AfterAnotherTopmostWindowClaimsTheBand()
    {
        using var scenario = OverlayScenario.Open(pickers: 1);
        _ = scenario.WaitForOverlays(1);

        scenario.BringTargetToTheFrontOfTheTopmostBand();
        scenario.ForceOverlayRedraw();

        // 前後関係を見る前に、**そもそも競争が起きていること**を確かめる。
        // 描き直しでアイコンがどこへ移るかは選んだ行の要素しだいで、
        // 対象アプリの窓から外れていれば WindowFromPoint はデスクトップを返す —
        // オーバーレイが最前面かどうかと無関係に落ちる (あるいは無関係に通る)。
        // TreeRealisationTests の RequireThePointBelongsTo と同じ規律である
        scenario.RequireTheIconOverlapsTheTarget();

        Ui.Until(
            () =>
            {
                AutomationElement icon = scenario.Host.IconOverlays() is { Count: > 0 } list ? list[0]
                    : throw new InvalidOperationException("確定アイコンの窓が消えました。");
                nint hwnd = (nint)icon.Current.NativeWindowHandle;
                if (NativeWindows.RectOf(hwnd) is not { } r)
                {
                    return null;
                }
                (int iconX, int iconY) = OverlayScenario.IconCentre(r);
                return NativeWindows.WindowAt(iconX, iconY) == hwnd ? "ok" : null;
            },
            Settle,
            "確定アイコンのピクセルがオーバーレイのものであること",
            scenario.DescribeIconPixel);
    }

    /// <summary>
    /// 枠の 3 点の上に**誰が居るか**。アイコンと内側だけを固定する
    /// (docs/DESIGN.md §10)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 実機 (100%) で「**枠の上のクリックが下のアプリへ抜けていない**」という報告がある。
    /// 「まず実マウスで同じ点を押して比べる」(docs/TESTING.md §3) の最初のデータ点がこの測定である。
    /// </para>
    /// <para>
    /// **3 点を測った (100% / DPI 96)**。枠線 → **オーバーレイ** /
    /// アイコン → **オーバーレイ** / 枠の内側 → **対象アプリのボタン**。
    /// </para>
    /// <para>
    /// **枠線の結果からは何も結論できない。**較正して確かめた:
    /// <c>WM_NCHITTEST</c> が**全点で** <c>HTTRANSPARENT</c> を返すようにしても、
    /// アイコンの点はオーバーレイを返し続けた。つまり
    /// **<c>WindowFromPoint</c> は <c>HTTRANSPARENT</c> を尊重しない** —
    /// <c>NativeWindows.WindowAt</c> の doc が書いていたとおりで、これは測定器の性質である。
    /// 枠線に assert を書くと**製品ではなく測定器を固定する**ことになるので書かない。
    /// </para>
    /// <para>
    /// 固定するのは 2 つだけである: アイコンはオーバーレイのもの (押せること)、
    /// 枠の内側は下のアプリのもの (アルファのヒットテストが効いていること)。
    /// 枠線の値は**失敗時の診断として**残す。
    /// </para>
    /// <para>
    /// **クリックスルーは依然として手動確認のままである** (docs/MANUAL-CHECKS.md §3)。
    /// 冒頭の報告を裏付けも否定もできていない。
    /// </para>
    /// <para>
    /// **ホバー追随が固定されている点に注意。**<c>--pick-at</c> は <c>ICursorSource</c> を
    /// 固定するので、<c>IsInIconRect</c> が読む <c>_pendingRect</c> は動かない。
    /// **カーソルが動いている間だけ起きる取り違えは、この測定では原理的に出ない。**
    /// </para>
    /// </remarks>
    [Fact]
    public void TheOverlayOwnsTheIconPixel_ButNotTheTransparentInside()
    {
        using var scenario = OverlayScenario.Open(pickers: 1);
        _ = scenario.WaitForOverlays(1);

        nint frameHwnd = (nint)scenario.Host.Overlays()[0].Current.NativeWindowHandle;
        nint iconHwnd = scenario.IconHwnd();
        (int Left, int Top, int Right, int Bottom) iconRect =
            NativeWindows.RectOf(iconHwnd) ?? throw new InvalidOperationException("アイコンの矩形が取れません。");

        (int bl, int bt, int br, int bb) = scenario.Target.RectOf("OverlayA");
        int dpi = NativeWindows.DpiAt(bl, bt);
        OverlayGeometry.Metrics m = OverlayGeometry.MetricsFor(dpi);
        (_, int frameHeight) = OverlayGeometry.FrameSize(new Models.ElementRect(bl, bt, br, bb), dpi);

        // 枠の**左辺**の中央。不透明な赤線の上で、アイコン (右上) からは最も遠い。
        // 左辺は要素の左端から内側へ FrameThickness ぶんなので、点は要素の中にもある
        (int X, int Y) onTheLine = (bl + (m.FrameThickness / 2), bt + (frameHeight / 2));
        (int X, int Y) icon = OverlayScenario.IconCentre(iconRect);
        (int X, int Y) inside = ((bl + br) / 2, (bt + bb) / 2);

        nint atLine = NativeWindows.WindowAt(onTheLine.X, onTheLine.Y);
        nint atIcon = NativeWindows.WindowAt(icon.X, icon.Y);
        nint atInside = NativeWindows.WindowAt(inside.X, inside.Y);

        string measured = string.Join(
            Environment.NewLine,
            FormattableString.Invariant($"  枠線     ({onTheLine.X},{onTheLine.Y}): {NativeWindows.Describe(atLine)}"),
            FormattableString.Invariant($"  アイコン ({icon.X},{icon.Y}): {NativeWindows.Describe(atIcon)}"),
            FormattableString.Invariant($"  枠の内側 ({inside.X},{inside.Y}): {NativeWindows.Describe(atInside)}"),
            FormattableString.Invariant($"  枠の窓:     {NativeWindows.Describe(frameHwnd)}"),
            FormattableString.Invariant($"  アイコンの窓: {NativeWindows.Describe(iconHwnd)}"),
            FormattableString.Invariant($"  枠の太さ {m.FrameThickness}px / DPI {dpi} / 枠の高さ {frameHeight}px"));

        Assert.True(
            atIcon == iconHwnd,
            "確定アイコンの点がアイコンの窓のものではありません = **押せない**側の欠陥です。" +
            Environment.NewLine + measured + Environment.NewLine + scenario.Describe());
        Assert.True(
            atInside != frameHwnd && atInside != iconHwnd,
            "枠の内側 (アルファ 0) がオーバーレイのものになっています = アルファのヒットテストが" +
            "効いていません。" + Environment.NewLine + measured + Environment.NewLine + scenario.Describe());

        // 枠線については**何も主張しない**。較正で、WindowFromPoint が
        // HTTRANSPARENT を尊重しないことを実測しているためである (docs/DESIGN.md §10):
        // 全点で HTTRANSPARENT を返すようにしても、アイコンの点はオーバーレイを返し続けた。
        // ここで atLine に assert を書くと、**製品ではなく測定器の性質を固定する**ことになる。
        //
        // **2 窓構成 (docs/DESIGN.md §10) でも主張しない。**枠は WS_EX_TRANSPARENT で
        // 「窓ごとヒットテストから外れている」ので WindowFromPoint が枠を返さない
        // 可能性はある。だが**それは測定器の話であって、クリックが下へ届くかどうかとは別である** —
        // 届くかどうかを言えるのは実際に押す T5 の OverlayClickTests だけである (docs/TESTING.md §3 の M1)。
        // 値は measured の中に残してあり、失敗時の診断としてだけ出る。
        Assert.True(atLine != 0, "枠線の点にウィンドウが居ません = 舞台が崩れています。" + measured);
    }

    /// <summary>
    /// **枠だけがヒットテストから外れていて、アイコンは外れていないこと** (docs/DESIGN.md §10)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// クリックスルーの直しの本体は <c>WS_EX_TRANSPARENT</c> という**拡張スタイル 1 つ**で、
    /// 付け外しは 1 語の変更で済む。<c>OverlayClickTests</c> (T5) が実際に押して確かめているが、
    /// あれが見ているのは**枠の側だけ**である。
    /// </para>
    /// <para>
    /// **逆向きの壊れ方 — アイコンにも付けてしまい、確定アイコンが押せなくなる — を
    /// 機械で捕まえるのはここだけである。**合成入力で確定を押す検査は無く、
    /// 目視 (MANUAL-CHECKS §3 の「見えているアイコンは全面が押せる」) が一次の網である。
    /// ここで**原因のほう**を固定しておく。<c>TheIconWindowClass_RegistersACursor</c> が
    /// カーソルの見た目ではなくクラスの登録を見ているのと同じ規律である。
    /// </para>
    /// <para>
    /// 退行: 枠から <c>WS_EX_TRANSPARENT</c> を外す → 前半が落ちる (T5 も落ちる)。
    /// アイコンに付ける → 後半が落ちる (**T5 は緑のままである**)。
    /// </para>
    /// </remarks>
    [Fact]
    public void OnlyTheFrame_IsTransparentToHitTesting()
    {
        using var scenario = OverlayScenario.Open(pickers: 1);
        _ = scenario.WaitForOverlays(1);

        nint frameHwnd = (nint)scenario.Host.Overlays()[0].Current.NativeWindowHandle;
        nint iconHwnd = scenario.IconHwnd();

        Assert.True(
            (NativeWindows.ExStyleOf(frameHwnd) & NativeWindows.WS_EX_TRANSPARENT) != 0,
            $"枠の窓に WS_EX_TRANSPARENT がありません ({NativeWindows.Describe(frameHwnd)})。" +
            "この状態では枠線の上のクリックが下のアプリへ抜けません (docs/DESIGN.md §10)。");
        Assert.True(
            (NativeWindows.ExStyleOf(iconHwnd) & NativeWindows.WS_EX_TRANSPARENT) == 0,
            $"確定アイコンの窓に WS_EX_TRANSPARENT が付いています ({NativeWindows.Describe(iconHwnd)})。" +
            "この状態では**確定アイコンが押せません** — 窓ごとヒットテストから外れるためです。");
    }

    /// <summary>
    /// オーバーレイのウィンドウクラスが**カーソルを持っている**こと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// MANUAL-CHECKS §4.3.1 の「確定アイコンにカーソルを重ねても砂時計にならない」を引き取る。
    /// <c>WNDCLASSEXW.hCursor</c> が <c>default</c> のままだと、そのクラスのウィンドウの上で
    /// カーソルが**直前の状態を引きずる** — ピッカーが読み取り中なら砂時計のままになる
    /// (実際に起きた不具合である)。
    /// </para>
    /// <para>
    /// **これは「カーソルの見た目」ではなく「クラスがカーソルを登録しているか」の検査である。**
    /// 実際にどう描かれるかは実マウスを動かさないと見られないので、
    /// MANUAL-CHECKS の項目は**補助として残す**。ここで捕まえるのは、
    /// 登録が落ちるという**原因のほう**である。
    /// </para>
    /// <para>
    /// **見るのはアイコンの窓のクラスである** (docs/DESIGN.md §10)。カーソルが実際に「乗る」のは
    /// そちらだけで、枠の窓は <c>WS_EX_TRANSPARENT</c> でヒットテストから外れている。
    /// 枠のクラスを見ていると、アイコン側の登録が落ちても緑のままになる。
    /// </para>
    /// <para>
    /// 変種に依らないので 1 件だけ回す — オーバーレイは <c>Picker.Core</c> に在って
    /// 3 変種で同じものである。
    /// </para>
    /// <para>
    /// 退行: <c>WNDCLASSEXW.hCursor</c> を <c>default</c> に戻す → 0 になる。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheIconWindowClass_RegistersACursor()
    {
        using var scenario = OverlayScenario.Open(pickers: 1);
        _ = scenario.WaitForOverlays(1);

        nint hwnd = scenario.IconHwnd();
        nint cursor = NativeWindows.ClassCursorOf(hwnd);

        Assert.True(
            cursor != 0,
            $"確定アイコンのウィンドウクラスにカーソルが登録されていません ({NativeWindows.Describe(hwnd)})。" +
            "この状態では確定アイコンの上でカーソルが直前の状態 (読み取り中なら砂時計) を引きずります。");
    }

}
