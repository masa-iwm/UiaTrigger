// ピッカーの振る舞いの回帰テスト (docs/TESTING.md §1 T1 / docs/DESIGN.md §12)。
//
// 振る舞いが View (WinUI3 / x64 でテストから読み込めないアセンブリ) に埋まっていると、
// 「動いているのを見た」以外の担保が無くなる。presenter に切り出してあるからここで回せる。
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Time.Testing;
using UiaTrigger.Interop;
using UiaTrigger.Models;
using UiaTrigger.Picker;
using Xunit;

namespace UiaTrigger.Tests;

public sealed class TriggerPickerPresenterTests
{
    // ---------- ホバー滞留 ----------

    /// <summary>
    /// カーソルが動いたら滞留の計測はやり直しになること。
    ///
    /// これが効かないと、マウスを画面の端から端へ動かしただけで通り道の要素を次々に捕捉する。
    /// </summary>
    [Fact]
    public async Task Hover_WhenTheCursorMoves_TheDwellClockRestarts()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Button"));
        h.Cursor.X = 100;
        h.Cursor.Y = 100;
        await h.Presenter.TickAsync(); // 1 打目: 基準点を置く

        h.Time.Advance(TimeSpan.FromMilliseconds(900));
        h.Cursor.X = 400; // 動いた
        await h.Presenter.TickAsync();
        h.Time.Advance(TimeSpan.FromMilliseconds(900)); // 合計は 1800ms だが、動いた時点でやり直し
        await h.Presenter.TickAsync();

        Assert.Empty(h.Services.CaptureRequests);
    }

    /// <summary>静止が続けば捕捉すること (上のテストが「何も捕捉しない」で通らないための対)。</summary>
    [Fact]
    public async Task Hover_OnceTheCursorHasRested_ItCaptures()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Button"));
        h.Cursor.X = 100;
        h.Cursor.Y = 100;
        await h.Presenter.TickAsync();

        h.Time.Advance(TimeSpan.FromMilliseconds(1001));
        await h.Presenter.TickAsync();

        (int x, int y, TreeViewMode view) = Assert.Single(h.Services.CaptureRequests);
        Assert.Equal((100, 100, TreeViewMode.Control), (x, y, view));
    }

    /// <summary>
    /// 同じ場所は捕捉し直さないこと。
    ///
    /// 100ms ごとに打つタイマーなので、これが無ければ静止しているあいだ毎打ごとに
    /// UIA へ問い合わせ続ける。
    /// </summary>
    [Fact]
    public async Task Hover_AtTheSamePlaceTwice_DoesNotCaptureAgain()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Button"));
        h.Cursor.X = 100;
        h.Cursor.Y = 100;
        await h.Presenter.TickAsync();
        h.Time.Advance(TimeSpan.FromMilliseconds(1001));
        await h.Presenter.TickAsync();

        h.Time.Advance(TimeSpan.FromMilliseconds(1001));
        await h.Presenter.TickAsync();

        Assert.Single(h.Services.CaptureRequests);
    }

    /// <summary>
    /// 確定アイコンの上では捕捉しないこと。
    ///
    /// 押しに行った先で選択が別の要素へ移ってしまい、「押したのに違うものが確定する」になる。
    /// </summary>
    [Fact]
    public async Task Hover_OverTheConfirmIcon_DoesNotCapture()
    {
        using var h = new Harness();
        var target = new FakePickerElement("Button") { BoundingRectangle = new ElementRect(0, 0, 200, 100) };
        h.Services.NextCapture = Capture(target);
        h.Cursor.X = 10;
        h.Cursor.Y = 50;
        await h.Presenter.TickAsync();
        h.Time.Advance(TimeSpan.FromMilliseconds(1001));
        await h.Presenter.TickAsync(); // ここで target が現在の選択になる
        Assert.Single(h.Services.CaptureRequests);

        // 選択枠の右上、確定アイコンの真ん中へ移す
        h.Cursor.X = target.BoundingRectangle.Right - h.Metrics.IconInset;
        h.Cursor.Y = target.BoundingRectangle.Top + h.Metrics.IconInset;
        await h.Presenter.TickAsync();
        h.Time.Advance(TimeSpan.FromMilliseconds(1001));
        await h.Presenter.TickAsync();

        Assert.Single(h.Services.CaptureRequests);
    }

    /// <summary>カーソル位置が読めないときは何もしないこと (基準点を 0,0 に落とさない)。</summary>
    [Fact]
    public async Task Hover_WhenTheCursorCannotBeRead_DoesNothing()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Button"));
        h.Cursor.Available = false;
        h.Cursor.X = 100;
        h.Cursor.Y = 100;

        await h.Presenter.TickAsync();
        h.Time.Advance(TimeSpan.FromMilliseconds(1001));
        await h.Presenter.TickAsync();

        Assert.Empty(h.Services.CaptureRequests);
    }

    /// <summary>自動選択の切替がタイマーと ←/→ フックの両方に効くこと。</summary>
    [Fact]
    public void Hover_TheAutoSelectToggleDrivesBothTheTimerAndTheHook()
    {
        using var h = new Harness();

        h.Presenter.SetAutoSelect(true);
        Assert.True(h.Timer.IsRunning);
        Assert.True(h.Overlay.HookEnabled);

        h.Presenter.SetAutoSelect(false);
        Assert.False(h.Timer.IsRunning);
        Assert.False(h.Overlay.HookEnabled);
    }

    /// <summary>
    /// 自動選択を入れ直したら滞留の計測もやり直しになること。
    ///
    /// でないと「切ってしばらく置いてから入れた」瞬間に、その場の要素を即座に捕捉する。
    /// </summary>
    [Fact]
    public async Task Hover_TurningAutoSelectOn_RestartsTheDwellClock()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Button"));
        h.Cursor.X = 100;
        h.Cursor.Y = 100;
        await h.Presenter.TickAsync(); // 基準点を置く

        h.Time.Advance(TimeSpan.FromMinutes(5)); // 自動選択が切れているあいだの時間
        h.Presenter.SetAutoSelect(true);
        await h.Presenter.TickAsync();

        Assert.Empty(h.Services.CaptureRequests);
    }

    /// <summary>
    /// タイマーは止まった状態で作ること。
    ///
    /// 作った瞬間から回ると、View が自動選択を ON にする前 (= ←/→ フックがまだ無効な間) に
    /// 捕捉が始まる。
    /// </summary>
    [Fact]
    public void Hover_TheTimerIsCreatedStopped()
    {
        using var h = new Harness();

        Assert.False(h.Timer.IsRunning);
        Assert.False(h.Overlay.HookEnabled);
    }

    // ---------- 捕捉したチェーンの形 ----------

    /// <summary>プロセスルート → チェーンの順で、直系 1 本の木になること。</summary>
    [Fact]
    public async Task Capture_BuildsTheProcessRootThenTheChainInOrder()
    {
        using var h = new Harness();
        var window = new FakePickerElement("Window");
        var pane = new FakePickerElement("Pane");
        var button = new FakePickerElement("Button");
        h.Services.NextCapture = Capture("notepad.exe (PID 7)", window, pane, button);

        await h.CaptureOnceAsync();

        PickerTreeNode root = Assert.Single(h.Presenter.Roots);
        Assert.Equal("notepad.exe (PID 7)", root.Display);
        Assert.Null(root.Element);
        Assert.Equal(["Window", "Pane", "Button"], Descend(root));
    }

    /// <summary>
    /// 末端 (選択された要素) は展開しないこと。
    ///
    /// 展開すると、ホバーしただけでその要素の子を全部取りに行く。
    /// </summary>
    [Fact]
    public async Task Capture_LeavesTheTargetRowUnexpanded()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), new FakePickerElement("Button"));

        await h.CaptureOnceAsync();

        (IReadOnlyList<PickerTreeNode> order, PickerTreeNode target) = Assert.Single(h.View.ExpandThenSelectCalls);
        Assert.DoesNotContain(target, order);
        Assert.Equal("Button", target.Display);
        Assert.False(target.IsExpanded);
    }

    /// <summary>
    /// チェーンの展開では子を全列挙しないこと。
    ///
    /// presenter は IsExpanded を書かない (docs/DESIGN.md §12)。プレゼンター側が IsExpanded を書き、
    /// その反響を View のフラグで抑える形は、抑止を取り違えるとホバーするたびに
    /// 経路上の全段が兄弟を取りに行く (深さ 17 段の Chromium で顕著になる)。
    /// </summary>
    [Fact]
    public async Task Capture_ExpandingTheChainDoesNotEnumerateAnyChildren()
    {
        using var h = new Harness();
        h.Services.NextCapture =
            Capture(new FakePickerElement("Window"), new FakePickerElement("Pane"), new FakePickerElement("Button"));
        h.Services.NextChildren = new ChildrenResult { Children = [new FakePickerElement("Other")], ChainChildIndex = -1 };

        await h.CaptureOnceAsync();

        Assert.NotEmpty(h.View.Expanded); // 展開自体は起きている
        Assert.Empty(h.Services.ChildrenRequests);
    }

    /// <summary>中間のチェーン段には「未取得の子がある」印を付けないこと (自動全列挙を避ける)。</summary>
    [Fact]
    public async Task Capture_MarksIntermediateChainRowsAsAlreadyRealized()
    {
        using var h = new Harness();
        h.Services.NextCapture =
            Capture(new FakePickerElement("Window"), new FakePickerElement("Pane"), new FakePickerElement("Button"));

        await h.CaptureOnceAsync();

        PickerTreeNode window = h.Presenter.Roots[0].Children[0];
        PickerTreeNode pane = window.Children[0];
        PickerTreeNode button = pane.Children[0];
        Assert.False(window.HasUnrealizedChildren);
        Assert.False(pane.HasUnrealizedChildren);
        Assert.True(button.HasUnrealizedChildren); // 末端は「子があるかもしれない」ままでよい
    }

    /// <summary>
    /// ツリーを差し替える前に、保留中の遅延処理を破棄させること。
    ///
    /// これを飛ばすと、前のツリーに対する遅延展開・遅延選択が新しいツリーの上で走る。
    /// </summary>
    [Fact]
    public async Task Capture_DiscardsDeferredWorkBeforeReplacingTheTree()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Button"));
        int discardsWhenTheTreeChanged = -1;
        h.Presenter.Roots.CollectionChanged += (_, _) => discardsWhenTheTreeChanged = h.View.DiscardCount;

        await h.CaptureOnceAsync();

        Assert.Equal(1, discardsWhenTheTreeChanged);
    }

    /// <summary>
    /// ツリーの差し替えは「View に伝えた更新中」の内側で行うこと。
    ///
    /// 外に出ると、差し替えに伴う選択変化がユーザー操作として跳ね返り、
    /// 復元の起点 (_selectionOrigin) を正規化後の要素で上書きしてしまう。
    /// </summary>
    [Fact]
    public async Task Capture_ChangesTheTreeInsideANodeUpdate()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Button"));
        // 「最後の変化が内側だった」では足りない — Clear だけが外に出ていても通ってしまう
        bool everyChangeWasInsideAnUpdate = true;
        int changes = 0;
        h.Presenter.Roots.CollectionChanged += (_, _) =>
        {
            changes++;
            everyChangeWasInsideAnUpdate &= h.View.InNodeUpdate;
        };

        await h.CaptureOnceAsync();

        Assert.True(changes >= 2); // Clear と Add の両方が起きている
        Assert.True(everyChangeWasInsideAnUpdate);
        Assert.Equal(0, h.View.NodeUpdateBalance); // 開始と終了が対になっている
    }

    /// <summary>選択された要素の矩形にオーバーレイを出すこと。</summary>
    [Fact]
    public async Task Capture_ShowsTheOverlayOnTheTargetRectangle()
    {
        using var h = new Harness();
        var rect = new ElementRect(10, 20, 110, 70);
        h.Services.NextCapture = Capture(new FakePickerElement("Button") { BoundingRectangle = rect });

        await h.CaptureOnceAsync();

        Assert.Equal(rect, Assert.Single(h.Overlay.ShownRects));
    }

    /// <summary>捕捉が失敗したら理由をヒント欄に出すこと (例外で落とさない)。</summary>
    [Fact]
    public async Task Capture_WhenItThrows_ShowsTheReasonAsAHint()
    {
        using var h = new Harness();
        h.Strings.Values["CaptureFailed"] = "capture failed: {0}";
        h.Services.CaptureThrows = new InvalidOperationException("element vanished");

        await h.CaptureOnceAsync();

        Assert.Equal("capture failed: element vanished", h.View.LastHint);
    }

    /// <summary>
    /// 座標が信用できない旨は、オーバーレイの生成失敗より優先して出すこと。
    ///
    /// DPI 非認識は「静かに別の要素を記録した定義」を作る (docs/DESIGN.md A19 / C13)。
    /// オーバーレイが出ないのは目に見えるが、こちらは目に見えない。
    /// </summary>
    [Fact]
    public void TheCoordinateProblemOutranksAnOverlayFailureInTheHint()
    {
        var view = new FakePickerView();
        var services = new FakePickerServices { CoordinateProblem = "host is not per-monitor DPI aware" };
        var overlay = new FakeOverlay { CreationError = "overlay window creation failed" };
        using var presenter = new TriggerPickerPresenter(
            view, new FakeDispatcher(), new FakeCursor(), new FakeStrings(), services, overlay,
            new FakeTimeProvider(), new FakeDpiSource());

        Assert.Equal("host is not per-monitor DPI aware", view.LastHint);
    }

    // ---------- 子の列挙 ----------

    /// <summary>
    /// 既にチェーン子として表示しているノードは、再列挙しても同じインスタンスのまま残ること。
    ///
    /// 作り直すと、その下に開いていた部分木ごと消える。Clear してから詰め直す実装では
    /// 一瞬空になった行が畳まれ、TwoWay バインディング経由で IsExpanded=false が書き戻る。
    /// </summary>
    [Fact]
    public async Task LoadChildren_KeepsTheChainChildInstanceAndItsSubtree()
    {
        using var h = new Harness();
        var window = new FakePickerElement("Window");
        var pane = new FakePickerElement("Pane");
        h.Services.NextCapture = Capture(window, pane);
        await h.CaptureOnceAsync();

        PickerTreeNode windowNode = h.Presenter.Roots[0].Children[0];
        PickerTreeNode chainChild = windowNode.Children[0];
        var deepMarker = new FakePickerElement("Deep");
        h.Services.NextChildren = new ChildrenResult
        {
            Children = [new FakePickerElement("Before"), pane, new FakePickerElement("After")],
            ChainChildIndex = 1,
        };
        // チェーン子の下に部分木があることにする
        chainChild.Children.Add(new PickerTreeNode("Deep", deepMarker));

        await h.Presenter.LoadChildrenAsync(windowNode);

        Assert.Equal(["Before", "Pane", "After"], windowNode.Children.Select(c => c.Display));
        Assert.Same(chainChild, windowNode.Children[1]);
        Assert.Equal("Deep", Assert.Single(windowNode.Children[1].Children).Display);
    }

    /// <summary>チェーン子が再列挙後に見つからなければ、素直に全部差し替えること。</summary>
    [Fact]
    public async Task LoadChildren_WhenTheChainChildIsGone_ReplacesEveryRow()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), new FakePickerElement("Pane"));
        await h.CaptureOnceAsync();

        PickerTreeNode windowNode = h.Presenter.Roots[0].Children[0];
        h.Services.NextChildren = new ChildrenResult
        {
            Children = [new FakePickerElement("A"), new FakePickerElement("B")],
            ChainChildIndex = -1,
        };

        await h.Presenter.LoadChildrenAsync(windowNode);

        Assert.Equal(["A", "B"], windowNode.Children.Select(c => c.Display));
    }

    /// <summary>同じノードを二度列挙しないこと (展開とクリックの両方から呼ばれる経路がある)。</summary>
    [Fact]
    public async Task LoadChildren_RunsOnlyOnceForTheSameRow()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), new FakePickerElement("Pane"));
        await h.CaptureOnceAsync();
        PickerTreeNode windowNode = h.Presenter.Roots[0].Children[0];
        h.Services.NextChildren = new ChildrenResult { Children = [], ChainChildIndex = -1 };

        await h.Presenter.LoadChildrenAsync(windowNode);
        await h.Presenter.LoadChildrenAsync(windowNode);

        Assert.Single(h.Services.ChildrenRequests);
    }

    /// <summary>列挙に失敗したノードは、次に開いたときにもう一度試せること。</summary>
    [Fact]
    public async Task LoadChildren_WhenTheEnumerationFails_TheRowCanBeTriedAgain()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), new FakePickerElement("Pane"));
        await h.CaptureOnceAsync();
        PickerTreeNode windowNode = h.Presenter.Roots[0].Children[0];
        h.Services.NextChildren = null; // 失敗

        await h.Presenter.LoadChildrenAsync(windowNode);
        Assert.False(windowNode.ChildrenLoaded);

        h.Services.NextChildren = new ChildrenResult { Children = [new FakePickerElement("A")], ChainChildIndex = -1 };
        await h.Presenter.LoadChildrenAsync(windowNode);

        Assert.Equal(2, h.Services.ChildrenRequests.Count);
        Assert.Equal(["A"], windowNode.Children.Select(c => c.Display));
    }

    /// <summary>
    /// ユーザーが行を開いたら子を全列挙すること。
    ///
    /// <see cref="Capture_ExpandingTheChainDoesNotEnumerateAnyChildren"/> と対になる。
    /// 抑止をやりすぎると、こちら側が黙って効かなくなる。
    /// </summary>
    [Fact]
    public async Task LoadChildren_WhenTheUserOpensARow_ItEnumerates()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), new FakePickerElement("Pane"));
        await h.CaptureOnceAsync();
        PickerTreeNode paneNode = h.Presenter.Roots[0].Children[0].Children[0];
        h.Services.NextChildren = new ChildrenResult { Children = [new FakePickerElement("A")], ChainChildIndex = -1 };

        paneNode.IsExpanded = true; // TwoWay バインディングでユーザー操作が届く形

        Assert.Single(h.Services.ChildrenRequests);
    }

    /// <summary>列挙中の差し替えで選択が外れたら選び直すこと。</summary>
    [Fact]
    public async Task LoadChildren_RestoresTheSelectionWhenTheTreeMovedItAway()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), new FakePickerElement("Pane"));
        await h.CaptureOnceAsync();
        PickerTreeNode windowNode = h.Presenter.Roots[0].Children[0];
        h.Services.NextChildren = new ChildrenResult { Children = [new FakePickerElement("A")], ChainChildIndex = -1 };
        h.View.SelectDeferredCalls.Clear();
        h.View.SelectedNode = null; // 差し替えで選択が外れた

        await h.Presenter.LoadChildrenAsync(windowNode);

        Assert.Equal("Pane", Assert.Single(h.View.SelectDeferredCalls).Display);
    }

    /// <summary>列挙は現在のツリービューで行うこと (Raw で開いたのに Control の子が出ない)。</summary>
    [Fact]
    public async Task LoadChildren_AsksForTheCurrentTreeView()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), new FakePickerElement("Pane"));
        await h.CaptureOnceAsync();
        h.Services.NextChain = h.Services.NextCapture;
        h.Presenter.SetViewMode(TreeViewMode.Raw);
        PickerTreeNode windowNode = h.Presenter.Roots[0].Children[0];
        h.Services.NextChildren = new ChildrenResult { Children = [], ChainChildIndex = -1 };

        await h.Presenter.LoadChildrenAsync(windowNode);

        Assert.Equal(TreeViewMode.Raw, Assert.Single(h.Services.ChildrenRequests).View);
    }

    // ---------- ビュー切替 ----------

    /// <summary>
    /// ビューを切り替えたら、まずユーザーが意図的に選んだ要素から復元を試みること。
    ///
    /// 正規化で選択が祖先へ丸められた状態から切り替えを繰り返すと、
    /// 現在の選択だけを起点にする実装では二度と元の深さへ戻れない。
    /// </summary>
    [Fact]
    public async Task ViewSwitch_TriesTheElementTheUserChoseFirst()
    {
        using var h = new Harness();
        var window = new FakePickerElement("Window");
        var origin = new FakePickerElement("Button");
        var rounded = new FakePickerElement("Pane");
        h.Services.NextCapture = Capture(window, origin);
        await h.CaptureOnceAsync();

        // 1 度目の切替で選択が祖先 (Pane) へ丸められる。起点は Button のまま残る —
        // ここを作らないと起点と現在の選択が同じ要素になり、どちらを先に試したか見分けられない
        h.Services.NextChain = Capture(window, rounded);
        await h.Presenter.RebuildChainAsync();
        h.Services.ChainRequests.Clear();

        await h.Presenter.RebuildChainAsync();

        Assert.Same(origin, h.Services.ChainRequests[0].Element);
        Assert.NotSame(rounded, h.Services.ChainRequests[0].Element);
    }

    /// <summary>起点が解決できなければ現在の選択要素で試すこと。</summary>
    [Fact]
    public async Task ViewSwitch_WhenTheChosenElementIsGone_FallsBackToTheSelectedOne()
    {
        using var h = new Harness();
        var window = new FakePickerElement("Window");
        var button = new FakePickerElement("Button");
        var pane = new FakePickerElement("Pane");
        h.Services.NextCapture = Capture(window, button);
        await h.CaptureOnceAsync(); // 起点 = 選択 = Button

        // 1 度目の切替で選択が祖先 (Pane) へ丸められる。起点は Button のまま残る —
        // これが「起点と現在の選択が食い違う」唯一の作られ方である
        h.Services.NextChain = Capture(window, pane);
        h.Services.ChainResolvableFor.Add(button);
        await h.Presenter.RebuildChainAsync();
        h.Services.ChainRequests.Clear();

        // 2 度目。今度は Button が消えており、Pane からしか辿れない
        h.Services.ChainResolvableFor.Clear();
        h.Services.ChainResolvableFor.Add(pane);

        await h.Presenter.RebuildChainAsync();

        Assert.Equal(2, h.Services.ChainRequests.Count);
        Assert.Same(button, h.Services.ChainRequests[0].Element);
        Assert.Same(pane, h.Services.ChainRequests[1].Element);
    }

    /// <summary>どちらからも解決できなければ、今の表示を保ったまま理由を出すこと。</summary>
    [Fact]
    public async Task ViewSwitch_WhenNothingResolves_KeepsTheTreeAndSaysSo()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), new FakePickerElement("Button"));
        await h.CaptureOnceAsync();
        h.Strings.Values["ViewSwitchFailed"] = "could not rebuild the chain";
        h.Services.NextChain = null;
        int rootsBefore = h.Presenter.Roots.Count;

        await h.Presenter.RebuildChainAsync();

        Assert.Equal("could not rebuild the chain", h.View.LastHint);
        Assert.Equal(rootsBefore, h.Presenter.Roots.Count);
    }

    /// <summary>切り替え先のビューでチェーンを求めること。</summary>
    [Fact]
    public async Task ViewSwitch_AsksForTheChainInTheNewView()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), new FakePickerElement("Button"));
        await h.CaptureOnceAsync();
        h.Services.NextChain = h.Services.NextCapture;

        h.Presenter.SetViewMode(TreeViewMode.Content);

        Assert.Equal(TreeViewMode.Content, h.Services.ChainRequests[0].View);
    }

    /// <summary>まだ何も選ばれていなければ、ビューを切り替えても問い合わせないこと。</summary>
    [Fact]
    public async Task ViewSwitch_BeforeAnythingIsSelected_DoesNothing()
    {
        using var h = new Harness();

        await h.Presenter.RebuildChainAsync();

        Assert.Empty(h.Services.ChainRequests);
    }

    // ---------- 検索 ----------

    /// <summary>一致が複数あれば呼ぶたびに次へ進み、末尾から先頭へ回ること。</summary>
    [Fact]
    public async Task Search_StepsThroughEveryMatchAndWrapsRound()
    {
        using var h = new Harness();
        h.Services.NextCapture =
            Capture(new FakePickerElement("Save Button"), new FakePickerElement("Pane"), new FakePickerElement("Save Icon"));
        await h.CaptureOnceAsync();
        h.View.SelectDeferredCalls.Clear();

        h.Presenter.SearchNext("save");
        h.Presenter.SearchNext("save");
        h.Presenter.SearchNext("save");

        Assert.Equal(
            ["Save Button", "Save Icon", "Save Button"],
            h.View.SelectDeferredCalls.Select(n => n.Display));
    }

    /// <summary>一致した行までの道を開けること。</summary>
    [Fact]
    public async Task Search_OpensThePathDownToTheMatch()
    {
        using var h = new Harness();
        h.Services.NextCapture =
            Capture(new FakePickerElement("Window"), new FakePickerElement("Pane"), new FakePickerElement("Save"));
        await h.CaptureOnceAsync();
        h.View.Expanded.Clear();

        h.Presenter.SearchNext("save");

        Assert.Equal(
            ["notepad.exe (PID 7)", "Window", "Pane"],
            h.View.Expanded.Select(n => n.Display));
    }

    /// <summary>
    /// 既に開いている段の子は、検索で通り抜けても列挙しないこと。
    ///
    /// 捕捉したチェーンの各段は「開いているが子は未列挙」である。ここに手を出すと
    /// **検索 1 回で経路の全段が兄弟を取りに行く** (深さ 17 段の Chromium なら 17 往復)。
    /// <see cref="Capture_ExpandingTheChainDoesNotEnumerateAnyChildren"/> が守っているものを
    /// 検索経路から破らないための対である。
    /// </summary>
    [Fact]
    public async Task Search_DoesNotEnumerateChildrenOfRowsThatWereAlreadyOpen()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(
            new FakePickerElement("Window"), new FakePickerElement("Pane"), new FakePickerElement("Save"));
        await h.CaptureOnceAsync();
        h.Services.NextChildren = new ChildrenResult { Children = [new FakePickerElement("Sibling")], ChainChildIndex = -1 };

        h.Presenter.SearchNext("save");

        Assert.Empty(h.Services.ChildrenRequests);
    }

    /// <summary>
    /// 逆に、検索で初めて開いた段の子は列挙すること。
    ///
    /// 上のテストが「検索は何も列挙しない」で通ってしまわないための対。
    /// </summary>
    [Fact]
    public async Task Search_EnumeratesChildrenOfRowsItOpensForTheFirstTime()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), new FakePickerElement("Pane"));
        await h.CaptureOnceAsync();

        // Pane の下に、まだ一度も開かれていない段を作る (子を全列挙した結果として現れる形)
        PickerTreeNode pane = h.Presenter.Roots[0].Children[0].Children[0];
        var groupElement = new FakePickerElement("Group");
        h.Services.NextChildren = new ChildrenResult { Children = [groupElement], ChainChildIndex = -1 };
        await h.Presenter.LoadChildrenAsync(pane);
        PickerTreeNode group = pane.Children[0];
        group.Children.Add(new PickerTreeNode("Save", new FakePickerElement("Save")));
        Assert.False(group.IsExpanded);
        h.Services.ChildrenRequests.Clear();

        h.Presenter.SearchNext("save");

        Assert.Same(groupElement, Assert.Single(h.Services.ChildrenRequests).Parent);
    }

    /// <summary>一致が無ければ理由だけ出し、選択は動かさないこと。</summary>
    [Fact]
    public async Task Search_WhenNothingMatches_SaysSoAndLeavesTheSelectionAlone()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), new FakePickerElement("Button"));
        await h.CaptureOnceAsync();
        h.Strings.Values["SearchNoMatch"] = "no match for '{0}'";
        h.View.SelectDeferredCalls.Clear();

        h.Presenter.SearchNext("nothing here");

        Assert.Equal("no match for 'nothing here'", h.View.LastHint);
        Assert.Empty(h.View.SelectDeferredCalls);
    }

    /// <summary>空の検索語では何もしないこと (空白だけの入力を含む)。</summary>
    [Fact]
    public async Task Search_WithABlankQuery_DoesNothing()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), new FakePickerElement("Button"));
        await h.CaptureOnceAsync();
        h.View.SelectDeferredCalls.Clear();
        h.View.LastHint = null;

        h.Presenter.SearchNext("   ");

        Assert.Null(h.View.LastHint);
        Assert.Empty(h.View.SelectDeferredCalls);
    }

    // ---------- 重なり要素の切替 ----------

    /// <summary>
    /// 重なり切替では**正規化しない**チェーン構築を使うこと (docs/DESIGN.md §2)。
    ///
    /// 正規化すると、表示ビューの条件を満たさない重なり要素が同じ近傍祖先へ丸められ、
    /// 「別の要素へ切り替えたつもりが同じ要素のまま」になる。実際に一度これで壊れている。
    /// </summary>
    [Fact]
    public async Task Overlap_BuildsTheChainWithoutNormalizingIt()
    {
        using var h = new Harness();
        var behind = new FakePickerElement("Behind");
        var front = new FakePickerElement("Front");
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), behind);
        await h.CaptureOnceAsync();
        h.Services.NextStack = new ElementStack { Nodes = [behind, front], CurrentIndex = 0 };
        h.Services.NextOverlapChain = Capture(new FakePickerElement("Window"), front);

        await h.Presenter.MoveStackAsync(right: true);

        Assert.Same(front, Assert.Single(h.Services.OverlapChainRequests).Element);
        Assert.Empty(h.Services.ChainRequests); // 正規化する方は使わない
    }

    /// <summary>スタックの端では動かないこと。</summary>
    [Fact]
    public async Task Overlap_AtTheEndOfTheStack_StaysWhereItIs()
    {
        using var h = new Harness();
        var front = new FakePickerElement("Front");
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), front);
        await h.CaptureOnceAsync();
        h.Services.NextStack = new ElementStack { Nodes = [new FakePickerElement("Behind"), front], CurrentIndex = 1 };

        await h.Presenter.MoveStackAsync(right: true);

        Assert.Empty(h.Services.OverlapChainRequests);
    }

    /// <summary>まだ一度も捕捉していなければ、重なりスタックは作らないこと (座標が無い)。</summary>
    [Fact]
    public async Task Overlap_BeforeAnythingHasBeenCaptured_DoesNothing()
    {
        using var h = new Harness();

        await h.Presenter.MoveStackAsync(right: true);

        Assert.Empty(h.Services.StackRequests);
    }

    /// <summary>
    /// **ツリーに**キーボードフォーカスがあるあいだだけ ←/→ を重なり切替に使わないこと。
    ///
    /// ツリーのキーボード操作を奪ってしまうため。逆に、ホバーしているだけのときは
    /// フォーカスはツリーに無いので切り替えられる — ここをウィンドウ単位 (アクティブか) で見ると、
    /// ホバー中は常にアクティブなので**機能そのものが使えなくなる** (docs/DESIGN.md §11)。
    /// </summary>
    [Fact]
    public async Task Overlap_WhileTheTreeHasTheKeyboardFocus_TheArrowKeysAreLeftToTheTree()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), new FakePickerElement("Button"));
        await h.CaptureOnceAsync();

        h.Presenter.SetTreeHasFocus(true);
        h.Overlay.RaiseArrowKey(right: true);
        Assert.Empty(h.Services.StackRequests);

        h.Presenter.SetTreeHasFocus(false);
        h.Overlay.RaiseArrowKey(right: true);
        Assert.Single(h.Services.StackRequests);
    }

    /// <summary>オーバーレイからの通知は UI スレッドへ渡してから処理すること。</summary>
    [Fact]
    public void Overlay_CallbacksAreMarshalledOntoTheUiThread()
    {
        using var h = new Harness();

        h.Overlay.RaiseConfirmClicked();
        h.Overlay.RaiseArrowKey(right: false);

        Assert.Equal(2, h.Dispatcher.PostCount);
    }

    // ---------- 確定 → 条件設定 ----------

    /// <summary>既定の id はプロセス名と automation id から作ること。</summary>
    [Fact]
    public async Task Confirm_SuggestsAnIdBuiltFromTheProcessAndTheAutomationId()
    {
        using var h = new Harness();
        var element = new FakePickerElement("Button") { AutomationId = "SaveBtn" };
        h.Services.NextCapture = Capture(element);
        await h.CaptureOnceAsync();
        h.Services.NextDefinition = Definition("notepad.exe");

        await h.Presenter.ConfirmNodeAsync(h.Presenter.Roots[0].Children[0]);

        Assert.Equal("notepad-savebtn", h.View.KeyText);
    }

    /// <summary>automation id が無ければコントロール型の安定名で代用すること。</summary>
    [Fact]
    public async Task Confirm_WithoutAnAutomationId_UsesTheStableControlTypeName()
    {
        using var h = new Harness();
        var element = new FakePickerElement("ボタン") { ControlTypeName = "Button" };
        h.Services.NextCapture = Capture(element);
        await h.CaptureOnceAsync();
        h.Services.NextDefinition = Definition("notepad.exe");

        await h.Presenter.ConfirmNodeAsync(h.Presenter.Roots[0].Children[0]);

        // 表示名 (相手アプリのロケール) ではなく安定名を使う — docs/DESIGN.md L6
        Assert.Equal("notepad-button", h.View.KeyText);
    }

    /// <summary>ユーザーが入れた id は上書きしないこと。</summary>
    [Fact]
    public async Task Confirm_DoesNotOverwriteAnIdTheUserTyped()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Button") { AutomationId = "SaveBtn" });
        await h.CaptureOnceAsync();
        h.Services.NextDefinition = Definition("notepad.exe");
        h.View.KeyText = "my-own-key";

        await h.Presenter.ConfirmNodeAsync(h.Presenter.Roots[0].Children[0]);

        Assert.Equal("my-own-key", h.View.KeyText);
    }

    /// <summary>
    /// 続けて別の要素を確定したら、既定 id もその要素のものに作り直すこと。
    ///
    /// 「空のときだけ入れる」にすると前の id が残り、コミットで**前のトリガーを
    /// 黙って置き換える** (ホストは Id で上書きするため)。追加したつもりが差し替えになる、
    /// 例外も警告も出ない壊れ方である。
    /// </summary>
    [Fact]
    public async Task Confirm_WhenAnotherElementIsConfirmed_TheSuggestedIdFollowsIt()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Button") { AutomationId = "SaveBtn" });
        await h.CaptureOnceAsync();
        h.Services.NextDefinition = Definition("notepad.exe");
        await h.Presenter.ConfirmNodeAsync(h.Presenter.Roots[0].Children[0]);
        Assert.Equal("notepad-savebtn", h.View.KeyText);

        h.Services.NextCapture = Capture(new FakePickerElement("Button") { AutomationId = "CancelBtn" });
        await h.CaptureOnceAtAsync(400, 400);
        await h.Presenter.ConfirmNodeAsync(h.Presenter.Roots[0].Children[0]);

        Assert.Equal("notepad-cancelbtn", h.View.KeyText);
    }

    /// <summary>
    /// ユーザーが書いた id は、別の要素を確定しても書き換えないこと。
    ///
    /// 上のテストが「常に上書きする」で通ってしまわないための対。
    /// </summary>
    [Fact]
    public async Task Confirm_AfterTheUserTypedAnId_LaterConfirmationsLeaveItAlone()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Button") { AutomationId = "SaveBtn" });
        await h.CaptureOnceAsync();
        h.Services.NextDefinition = Definition("notepad.exe");
        await h.Presenter.ConfirmNodeAsync(h.Presenter.Roots[0].Children[0]);
        h.View.KeyText = "my-own-key"; // ユーザーが書き換えた

        h.Services.NextCapture = Capture(new FakePickerElement("Button") { AutomationId = "CancelBtn" });
        await h.CaptureOnceAtAsync(400, 400);
        await h.Presenter.ConfirmNodeAsync(h.Presenter.Roots[0].Children[0]);

        Assert.Equal("my-own-key", h.View.KeyText);
    }

    /// <summary>確定したら、記録された表示名を提案として欄に入れること。</summary>
    [Fact]
    public async Task Confirm_SuggestsTheRecordedDisplayName()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Button") { AutomationId = "SaveBtn" });
        await h.CaptureOnceAsync();
        h.Services.NextDefinition = Definition("notepad.exe");

        await h.Presenter.ConfirmNodeAsync(h.Presenter.Roots[0].Children[0]);

        Assert.Equal("Button \"Save\"", h.View.DisplayNameText);
    }

    /// <summary>ユーザーが入れた表示名は上書きしないこと。</summary>
    [Fact]
    public async Task Confirm_DoesNotOverwriteADisplayNameTheUserTyped()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Button") { AutomationId = "SaveBtn" });
        await h.CaptureOnceAsync();
        h.Services.NextDefinition = Definition("notepad.exe");
        h.View.DisplayNameText = "my own name";

        await h.Presenter.ConfirmNodeAsync(h.Presenter.Roots[0].Children[0]);

        Assert.Equal("my own name", h.View.DisplayNameText);
    }

    /// <summary>
    /// 続けて別の要素を確定したら、提案の表示名もその要素のものに作り直すこと (id と同じ規則)。
    /// </summary>
    [Fact]
    public async Task Confirm_WhenAnotherElementIsConfirmed_TheSuggestedDisplayNameFollowsIt()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Button") { AutomationId = "SaveBtn" });
        await h.CaptureOnceAsync();
        h.Services.NextDefinition = Definition("notepad.exe");
        await h.Presenter.ConfirmNodeAsync(h.Presenter.Roots[0].Children[0]);
        Assert.Equal("Button \"Save\"", h.View.DisplayNameText);

        h.Services.NextCapture = Capture(new FakePickerElement("Button") { AutomationId = "CancelBtn" });
        await h.CaptureOnceAtAsync(400, 400);
        h.Services.NextDefinition = new TriggerDefinition
        {
            Id = "recorded",
            DisplayName = "Button \"Cancel\"",
            Window = new WindowIdentity { ProcessName = "notepad.exe" },
        };
        await h.Presenter.ConfirmNodeAsync(h.Presenter.Roots[0].Children[0]);

        Assert.Equal("Button \"Cancel\"", h.View.DisplayNameText);
    }

    /// <summary>
    /// ユーザーが書いた表示名は、別の要素を確定しても書き換えないこと。
    /// 上のテストが「常に上書きする」で通ってしまわないための対。
    /// </summary>
    [Fact]
    public async Task Confirm_AfterTheUserTypedADisplayName_LaterConfirmationsLeaveItAlone()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Button") { AutomationId = "SaveBtn" });
        await h.CaptureOnceAsync();
        h.Services.NextDefinition = Definition("notepad.exe");
        await h.Presenter.ConfirmNodeAsync(h.Presenter.Roots[0].Children[0]);
        h.View.DisplayNameText = "my own name"; // ユーザーが書き換えた

        h.Services.NextCapture = Capture(new FakePickerElement("Button") { AutomationId = "CancelBtn" });
        await h.CaptureOnceAtAsync(400, 400);
        await h.Presenter.ConfirmNodeAsync(h.Presenter.Roots[0].Children[0]);

        Assert.Equal("my own name", h.View.DisplayNameText);
    }

    /// <summary>要素が消えていたら理由を出し、コミットは有効にしないこと。</summary>
    [Fact]
    public async Task Confirm_WhenTheElementIsGone_SaysSoAndLeavesCommitDisabled()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Button"));
        await h.CaptureOnceAsync();
        h.Strings.Values["ConfirmFailedElementGone"] = "that element is gone";
        h.Services.NextDefinition = null;

        await h.Presenter.ConfirmNodeAsync(h.Presenter.Roots[0].Children[0]);

        Assert.Equal("that element is gone", h.View.LastConfirmedText);
        Assert.Null(h.View.CommitEnabled);
    }

    /// <summary>パターンを持たない要素に、その値のプロパティを条件として出さないこと。</summary>
    [Fact]
    public async Task Confirm_OffersPatternPropertiesOnlyWhenTheElementSupportsThem()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Button"));
        await h.CaptureOnceAsync();
        h.Services.NextDefinition = Definition("notepad.exe");
        h.Services.NextSnapshot = new ElementPropertySnapshot { SupportsValuePattern = false };

        await h.Presenter.ConfirmNodeAsync(h.Presenter.Roots[0].Children[0]);
        Assert.DoesNotContain(TriggerProperty.Value, h.View.ShapeProperties);

        h.Services.NextSnapshot = new ElementPropertySnapshot { SupportsValuePattern = true };
        await h.Presenter.ConfirmNodeAsync(h.Presenter.Roots[0].Children[0]);
        Assert.Contains(TriggerProperty.Value, h.View.ShapeProperties);
    }

    /// <summary>確定できたらコミットを有効にすること。</summary>
    [Fact]
    public async Task Confirm_OnSuccess_EnablesTheCommitButton()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Button"));
        await h.CaptureOnceAsync();
        h.Services.NextDefinition = Definition("notepad.exe");

        await h.Presenter.ConfirmNodeAsync(h.Presenter.Roots[0].Children[0]);

        Assert.True(h.View.CommitEnabled);
    }

    /// <summary>プロセスルートの行は確定できないこと。</summary>
    [Fact]
    public async Task Confirm_OnTheProcessRoot_DoesNothing()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Button"));
        await h.CaptureOnceAsync();

        await h.Presenter.ConfirmNodeAsync(h.Presenter.Roots[0]);

        Assert.Empty(h.Services.DefinitionRequests);
        Assert.False(h.Presenter.Roots[0].CanConfirm);
    }

    // ---------- 条件欄の出し分け ----------

    /// <summary>
    /// 出す欄が検証器と 1 つも食い違わないこと。
    ///
    /// ここがずれると「画面に出ていない欄の値が条件に載る」。全比較演算子を表で回す。
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryComparison))]
    public void Operands_MatchTheValidatorForEveryComparison(ComparisonOp op)
    {
        OperandVisibility visibility = TriggerPickerPresenter.DescribeOperands(TriggerOn.PropertyChanged, op);

        Assert.Equal(TriggerDraftValidator.UsesText(op), visibility.Text);
        Assert.Equal(TriggerDraftValidator.UsesValue(op), visibility.Value);
        Assert.Equal(TriggerDraftValidator.UsesRange(op), visibility.Range);
        Assert.Equal(TriggerDraftValidator.UsesTolerance(op), visibility.Tolerance);
        Assert.Equal(TriggerDraftValidator.UsesPollInterval(TriggerOn.PropertyChanged), visibility.PollInterval);
    }

    public static TheoryData<ComparisonOp> EveryComparison => [.. Enum.GetValues<ComparisonOp>()];

    /// <summary>
    /// ポーリング間隔の欄は、ポーリングできるライフサイクルでだけ出すこと。
    /// 出現・削除では Core (CreateRuntime) が拒否するので、欄を出すと
    /// 「入力できたのに確定でエラー」になる。
    /// </summary>
    [Theory]
    [InlineData(TriggerOn.ElementAppeared, false)]
    [InlineData(TriggerOn.ElementRemoved, false)]
    [InlineData(TriggerOn.PropertyChanged, true)]
    [InlineData(TriggerOn.WhileMatching, true)]
    public void Operands_ThePollIntervalOnlyAppearsForLifecyclesThatCanPoll(TriggerOn lifecycle, bool expected)
    {
        OperandVisibility visibility = TriggerPickerPresenter.DescribeOperands(lifecycle, ComparisonOp.Always);

        Assert.Equal(expected, visibility.PollInterval);
        Assert.Equal(TriggerDraftValidator.UsesPollInterval(lifecycle), visibility.PollInterval);
    }

    /// <summary>
    /// 「成立しなくなった時も通知」は WhileMatching でだけ出すこと。
    /// 他のライフサイクルでは Core (CreateRuntime) がフラグを拒否するので、
    /// 出すと「チェックできたのに確定でエラー」になる (ポーリング欄と同じ形)。
    /// </summary>
    [Theory]
    [InlineData(TriggerOn.ElementAppeared, false)]
    [InlineData(TriggerOn.ElementRemoved, false)]
    [InlineData(TriggerOn.PropertyChanged, false)]
    [InlineData(TriggerOn.WhileMatching, true)]
    public void Operands_TheStoppedMatchingChoiceOnlyAppearsForWhileMatching(TriggerOn lifecycle, bool expected)
    {
        OperandVisibility visibility = TriggerPickerPresenter.DescribeOperands(lifecycle, ComparisonOp.Always);

        Assert.Equal(expected, visibility.StoppedMatching);
        Assert.Equal(TriggerDraftValidator.UsesNotifyOnStoppedMatching(lifecycle), visibility.StoppedMatching);
    }

    /// <summary>
    /// 出現・削除だけを見るトリガーで条件が無いときだけ、プロパティの選択を伏せること。
    ///
    /// ライフサイクルと述語が同じ列挙にあると、この組み合わせは作れない。
    /// </summary>
    [Theory]
    [InlineData(TriggerOn.ElementAppeared, ComparisonOp.Always, false)]
    [InlineData(TriggerOn.ElementRemoved, ComparisonOp.Always, false)]
    [InlineData(TriggerOn.ElementAppeared, ComparisonOp.Equals, true)]
    [InlineData(TriggerOn.PropertyChanged, ComparisonOp.Always, true)]
    public void Operands_ThePropertyChoiceIsOnlyHiddenForAnUnconditionalLifecycleTrigger(
        TriggerOn lifecycle, ComparisonOp op, bool expected)
    {
        OperandVisibility visibility = TriggerPickerPresenter.DescribeOperands(lifecycle, op);

        Assert.Equal(expected, visibility.PropertyChoiceEnabled);
    }

    /// <summary>条件の形が変わったら、View にそのぶんの欄を出し直させること。</summary>
    [Fact]
    public void Operands_AreRefreshedWheneverTheConditionShapeChanges()
    {
        using var h = new Harness();

        h.Presenter.ConditionShapeChanged(TriggerOn.PropertyChanged, ComparisonOp.Between);

        Assert.NotNull(h.View.LastOperands);
        OperandVisibility visibility = h.View.LastOperands.Value;
        Assert.True(visibility.Range);
        Assert.False(visibility.Text);
    }

    // ---------- コミット ----------

    /// <summary>要素を確定していなければコミットしないこと。</summary>
    [Fact]
    public void Commit_WithoutAConfirmedElement_DoesNothing()
    {
        using var h = new Harness();
        h.View.Draft = new TriggerDraft { Id = "x", On = TriggerOn.ElementAppeared, Op = ComparisonOp.Always };
        bool raised = false;
        h.Presenter.TriggerCommitted += (_, _) => raised = true;

        h.Presenter.Commit();

        Assert.False(raised);
        Assert.Null(h.View.LastCommitStatus);
    }

    /// <summary>トリガーの形が選ばれていなければ、その旨を出すこと。</summary>
    [Fact]
    public async Task Commit_WhenTheShapeIsIncomplete_SaysSo()
    {
        using var h = new Harness();
        await h.ConfirmSomethingAsync();
        h.Strings.Values["SelectTriggerShape"] = "choose a trigger shape first";
        h.View.Draft = null;

        h.Presenter.Commit();

        Assert.Equal("choose a trigger shape first", h.View.LastCommitStatus);
    }

    /// <summary>検証に落ちたら、検証器が返した理由をそのまま出すこと。</summary>
    [Fact]
    public async Task Commit_WhenTheDraftIsInvalid_ReportsTheValidatorsOwnReason()
    {
        using var h = new Harness();
        await h.ConfirmSomethingAsync();
        // Equals は比較する文字列を要求する
        var draft = new TriggerDraft
        {
            Id = "trig",
            On = TriggerOn.PropertyChanged,
            Property = TriggerProperty.Name,
            Op = ComparisonOp.Equals,
            Text = null,
        };
        h.View.Draft = draft;
        bool raised = false;
        h.Presenter.TriggerCommitted += (_, _) => raised = true;

        h.Presenter.Commit();

        Assert.False(raised);
        TriggerDraftResult expected = TriggerDraftValidator.Validate(draft, TimeSpan.FromSeconds(1));
        Assert.False(expected.IsValid);
        Assert.Equal(expected.Error, h.View.LastCommitStatus);
    }

    /// <summary>検証を通ったら定義に反映してホストへ渡すこと。</summary>
    [Fact]
    public async Task Commit_WhenTheDraftIsValid_AppliesItAndRaisesTriggerCommitted()
    {
        using var h = new Harness();
        await h.ConfirmSomethingAsync();
        h.View.Draft = new TriggerDraft
        {
            Id = "save-watch",
            On = TriggerOn.PropertyChanged,
            Property = TriggerProperty.Name,
            Op = ComparisonOp.Equals,
            Text = "Saved",
        };
        TriggerDefinition? committed = null;
        h.Presenter.TriggerCommitted += (_, e) => committed = e.Definition;

        h.Presenter.Commit();

        Assert.NotNull(committed);
        Assert.Equal("save-watch", committed.Id);
        Assert.Equal(TriggerOn.PropertyChanged, committed.On);
        PropertyClause clause = Assert.Single(committed.Clauses);
        Assert.Equal(TriggerProperty.Name, clause.Property);
    }

    // ---------- 既存トリガーの編集 (docs/DESIGN.md §4) ----------

    /// <summary>
    /// 読み込みが「形 → 中身 → 欄の出し分け」の順であること。
    ///
    /// 順番が要る。<c>ShowTriggerShape</c> がプロパティの一覧を入れ替えるので、
    /// その前に <c>ShowDraft</c> で選ぶと選択が消える。<c>ShowOperands</c> が最後なのは、
    /// 出す欄が読み込んだ演算子で決まるからである。
    /// </summary>
    [Fact]
    public void LoadDefinition_DrivesTheViewInOrder()
    {
        using var h = new Harness();

        h.Presenter.LoadDefinition(Recorded());

        Assert.Equal(["ShowTriggerShape", "ShowDraft", "ShowOperands"], h.View.Calls);
        Assert.True(h.View.CommitEnabled);
    }

    /// <summary>定義の値がそのまま下書きへ写ること。</summary>
    [Fact]
    public void LoadDefinition_FillsTheDraftFromTheDefinition()
    {
        using var h = new Harness();
        TriggerDefinition def = Recorded();
        def.On = TriggerOn.WhileMatching;
        def.MinInterval = TimeSpan.FromSeconds(2.5);
        def.PollInterval = TimeSpan.FromSeconds(10);
        def.Clauses[0].Op = ComparisonOp.GreaterThan;
        def.Clauses[0].Property = TriggerProperty.Value;
        def.Clauses[0].Value = 42;
        def.Clauses[0].Tolerance = 0.5;

        h.Presenter.LoadDefinition(def);

        TriggerDraft draft = Assert.Single(h.View.ShownDrafts);
        Assert.Equal("recorded", draft.Id);
        Assert.Equal("Button \"Save\"", draft.DisplayName);
        Assert.Equal(TriggerOn.WhileMatching, draft.On);
        Assert.Equal(TriggerProperty.Value, draft.Property);
        Assert.Equal(ComparisonOp.GreaterThan, draft.Op);
        Assert.Equal(42, draft.Value);
        Assert.Equal(0.5, draft.Tolerance);
        Assert.Equal(2.5, draft.MinIntervalSeconds);
        Assert.Equal(10, draft.PollIntervalSeconds);
        // 形のほうにも同じ値が渡ること (コンボの初期選択)
        Assert.Equal(TriggerOn.WhileMatching, h.View.ShapeLifecycle);
        Assert.Equal(ComparisonOp.GreaterThan, h.View.ShapeComparison);
    }

    /// <summary>
    /// いま入っているプロパティが必ず選択肢に載ること。
    ///
    /// 要素を読み直さないのでパターン対応を見るものが無い。一覧に無ければコンボは
    /// 選択なしになり、確定で**別のプロパティの条件に化ける**。
    /// </summary>
    [Fact]
    public void LoadDefinition_OffersThePropertyTheConditionAlreadyUses()
    {
        using var h = new Harness();
        TriggerDefinition def = Recorded();
        def.Clauses[0].Property = TriggerProperty.RangeValueMaximum;

        h.Presenter.LoadDefinition(def);

        Assert.Contains(TriggerProperty.RangeValueMaximum, h.View.ShapeProperties);
        Assert.Equal(TriggerProperty.RangeValueMaximum, Assert.Single(h.View.ShownDrafts).Property);
    }

    /// <summary>演算子が許容差を使わないなら、許容差の欄は空にすること (0 を書かない)。</summary>
    [Fact]
    public void LoadDefinition_LeavesToleranceEmptyWhenTheOperatorIgnoresIt()
    {
        using var h = new Harness();
        TriggerDefinition def = Recorded();
        def.Clauses[0].Op = ComparisonOp.RegexMatch;
        def.Clauses[0].Text = "ab.*";

        h.Presenter.LoadDefinition(def);

        Assert.Null(Assert.Single(h.View.ShownDrafts).Tolerance);
    }

    /// <summary>句を持たない (出現だけを見る) トリガーも読み込めること。</summary>
    [Fact]
    public void LoadDefinition_WithoutAClause_LoadsAsAlways()
    {
        using var h = new Harness();
        var def = new TriggerDefinition { Id = "appear", On = TriggerOn.ElementAppeared };

        h.Presenter.LoadDefinition(def);

        TriggerDraft draft = Assert.Single(h.View.ShownDrafts);
        Assert.Equal(ComparisonOp.Always, draft.Op);
        Assert.Equal(TriggerOn.ElementAppeared, draft.On);
    }

    /// <summary>
    /// 読み込んだあと確定すると、読み込んだ定義の内容が書き戻されること。
    ///
    /// <para>
    /// 渡されるのは**写し**である (docs/DESIGN.md C19)。ピッカーは確定後も自分の定義を
    /// 持ち続ける (§4 の「開いたまま何件でもコミット」) ので、同じ実体を渡すと
    /// 次のコミットで 1 件目が書き換わる — <see cref="Commit_Twice_DoesNotRewriteTheFirstDefinition"/>
    /// がその形を固定している。ここで見たいのは「記録済みの要素を保ったまま書き戻る」
    /// ことなので、実体同値ではなく値で確かめる。
    /// </para>
    /// </summary>
    [Fact]
    public void LoadDefinition_ThenCommit_WritesBackTheLoadedDefinition()
    {
        using var h = new Harness();
        TriggerDefinition def = Recorded();
        h.Presenter.LoadDefinition(def);
        // View は読み込んだ下書きをそのまま読み返す (本物の View と同じ)
        h.View.Draft!.Text = "Saved";

        TriggerDefinition? committed = null;
        h.Presenter.TriggerCommitted += (_, e) => committed = e.Definition;
        h.Presenter.Commit();

        Assert.NotNull(committed);
        Assert.NotSame(def, committed);
        Assert.Equal(def.Id, committed.Id);
        // 記録済みの要素はそのまま。しきい値を変えるために捕まえ直さなくてよいのが要点である
        Assert.Equal(def.Window.ProcessName, committed.Window.ProcessName);
        Assert.Equal(def.Locator.Steps.Count, committed.Locator.Steps.Count);
        Assert.Equal("Saved", Assert.Single(committed.Clauses).Text);
    }

    /// <summary>
    /// **開いたまま 2 件コミットしても、1 件目としてホストへ渡した定義が書き換わらないこと**
    /// (docs/DESIGN.md C19)。
    ///
    /// <para>
    /// §4 は「要素を確定し直さずに条件だけ変えて何件でもコミットできる」を明文化している。
    /// 渡すのが写しでないと、2 件目の <c>Apply</c> が同じインスタンスを in-place で書き換え、
    /// **ホストのメモリ上から 1 件目が消える** — 同梱ホストは受け取った定義を一覧に保持して
    /// id で照合するので、症状は「保存したはずの 1 件目がファイルに無い」という
    /// 無警告のデータ消失になる。例外もログも出ない。
    /// </para>
    /// </summary>
    [Fact]
    public void Commit_Twice_DoesNotRewriteTheFirstDefinition()
    {
        using var h = new Harness();
        h.Presenter.LoadDefinition(Recorded());

        var committed = new List<TriggerDefinition>();
        h.Presenter.TriggerCommitted += (_, e) => committed.Add(e.Definition);

        h.View.Draft!.Id = "first";
        h.View.Draft!.Text = "one";
        h.Presenter.Commit();

        // 要素を確定し直さずに、id と条件だけ変えて 2 件目
        h.View.Draft!.Id = "second";
        h.View.Draft!.Text = "two";
        h.Presenter.Commit();

        Assert.Equal(2, committed.Count);
        Assert.NotSame(committed[0], committed[1]);
        Assert.Equal("first", committed[0].Id);
        Assert.Equal("second", committed[1].Id);
        Assert.Equal("one", Assert.Single(committed[0].Clauses).Text);
        Assert.Equal("two", Assert.Single(committed[1].Clauses).Text);
    }

    // ---------- 編集セッション (editSession) ----------

    /// <summary>
    /// 編集セッションで読み込むと確定ボタンの文言が「更新」に差し替わり、
    /// プリフィル (1 引数版) では差し替わらないこと。
    /// 「トリガーを追加」のままだと、編集しているのに追加されるように読める。
    /// </summary>
    [Fact]
    public void LoadDefinition_AsAnEditSession_SwapsTheCommitCaption()
    {
        using var h = new Harness();
        h.Strings.Values[PickerStringKeys.CommitButtonUpdate] = "Update trigger";

        h.Presenter.LoadDefinition(Recorded(), editSession: true);

        Assert.Equal("Update trigger", h.View.CommitCaption);
    }

    /// <summary>プリフィル (1 引数版) は文言を差し替えないこと。</summary>
    [Fact]
    public void LoadDefinition_AsAPrefill_LeavesTheCommitCaptionAlone()
    {
        using var h = new Harness();

        h.Presenter.LoadDefinition(Recorded());

        Assert.Null(h.View.CommitCaption);
    }

    /// <summary>
    /// 編集セッションのコミット成立で View を 1 回だけ閉じ、しかも**最後**に閉じること。
    ///
    /// 順序が要る。WinForms の Form.Close は Form を Dispose するので、Close の後に
    /// CommitStatus 等を書くと ObjectDisposedException になる (本物の View で再現する形は
    /// TriggerPickerWinFormsTests が持つ)。
    /// </summary>
    [Fact]
    public void Commit_ForAnEditSession_ClosesTheViewLast()
    {
        using var h = new Harness();
        TriggerDefinition def = Recorded();
        h.Presenter.LoadDefinition(def, editSession: true);
        h.Presenter.TriggerCommitted += (_, _) => h.View.Calls.Add("TriggerCommitted");

        h.Presenter.Commit();

        Assert.Equal(1, h.View.CloseCount);
        Assert.Equal(["TriggerCommitted", "CommitStatus", "Close"], h.View.Calls[^3..]);
    }

    /// <summary>
    /// 編集セッションでないコミットは View を閉じないこと。
    /// 「開いたまま何件でもコミットできる」が新規追加の明文化されたワークフローである。
    /// </summary>
    [Fact]
    public void Commit_WithoutAnEditSession_LeavesTheViewOpen()
    {
        using var h = new Harness();
        h.Presenter.LoadDefinition(Recorded());

        h.Presenter.Commit();

        Assert.NotNull(h.View.LastCommitStatus); // コミット自体は成立している
        Assert.Equal(0, h.View.CloseCount);
    }

    /// <summary>編集セッションでも、検証に落ちたコミットでは閉じないこと。</summary>
    [Fact]
    public void Commit_ForAnEditSession_WhenValidationFails_StaysOpen()
    {
        using var h = new Harness();
        h.Presenter.LoadDefinition(Recorded(), editSession: true);
        h.View.Draft!.Id = " "; // Id は必須 — 検証で必ず落ちる

        h.Presenter.Commit();

        Assert.Equal(0, h.View.CloseCount);
    }

    /// <summary>
    /// WhileMatching + 立ち下がり通知のトリガーを編集して確定し直しても、フラグが残ること。
    /// (下書き → Apply の往復で黙って欠けると、編集のたびに通知が 1 種類消える)
    /// </summary>
    [Fact]
    public void LoadDefinition_ThenCommit_KeepsNotifyOnStoppedMatching()
    {
        using var h = new Harness();
        TriggerDefinition def = Recorded();
        def.On = TriggerOn.WhileMatching;
        def.NotifyOnStoppedMatching = true;

        h.Presenter.LoadDefinition(def, editSession: true);
        Assert.True(Assert.Single(h.View.ShownDrafts).NotifyOnStoppedMatching);

        h.Presenter.Commit();

        Assert.True(def.NotifyOnStoppedMatching);
    }

    /// <summary>
    /// 読み込んだあと要素を捕まえ直しても、id が黙って提案 id に置き換わらないこと。
    ///
    /// 置き換わると**編集したつもりで別のトリガーが増える** (元のほうは残る)。
    /// </summary>
    [Fact]
    public async Task LoadDefinition_ThenReconfirming_KeepsTheId()
    {
        using var h = new Harness();
        h.Presenter.LoadDefinition(Recorded());
        Assert.Equal("recorded", h.View.KeyText);

        await h.ConfirmSomethingAsync();

        Assert.Equal("recorded", h.View.KeyText);
    }

    /// <summary>
    /// 読み込んだあと要素を捕まえ直しても、表示名が黙って提案に置き換わらないこと (id と同じ規則)。
    /// </summary>
    [Fact]
    public async Task LoadDefinition_ThenReconfirming_KeepsTheDisplayName()
    {
        using var h = new Harness();
        h.Presenter.LoadDefinition(Recorded());
        Assert.Equal("Button \"Save\"", h.View.DisplayNameText);

        h.Services.NextCapture = Capture(new FakePickerElement("Button"));
        await h.CaptureOnceAsync();
        h.Services.NextDefinition = new TriggerDefinition
        {
            Id = "recorded",
            DisplayName = "Button \"Other\"",
            Window = new WindowIdentity { ProcessName = "notepad.exe" },
        };
        await h.Presenter.ConfirmNodeAsync(h.Presenter.Roots[0].Children[0]);

        Assert.Equal("Button \"Save\"", h.View.DisplayNameText);
    }

    /// <summary>ピッカーで編集できないものは断ること。</summary>
    [Fact]
    public void LoadDefinition_WhatItCannotEdit_Throws()
    {
        using var h = new Harness();

        TriggerDefinition composite = Recorded();
        composite.Expression = "c1";
        Assert.Throws<ArgumentException>("definition", () => h.Presenter.LoadDefinition(composite));

        TriggerDefinition twoClauses = Recorded();
        twoClauses.Clauses.Add(new PropertyClause { Property = TriggerProperty.Name, Op = ComparisonOp.Always });
        Assert.Throws<ArgumentException>("definition", () => h.Presenter.LoadDefinition(twoClauses));

        // 以下 3 つは「下書きが運べないものを句が持っている」形である。確定は下書きから
        // 句を作り直すので (TriggerDraftValidator.Apply)、運べないものは黙って落ちる

        // Custom はプロパティ id を持つが、下書きには id を運ぶ場所が無い → id が 0 に落ちる
        TriggerDefinition custom = Recorded();
        custom.Clauses[0].Property = TriggerProperty.Custom;
        custom.Clauses[0].CustomPropertyId = 30045;
        Assert.Throws<ArgumentException>("definition", () => h.Presenter.LoadDefinition(custom));

        // 句が自前の要素を持つ形 (docs/DESIGN.md §4 の多要素は句 1 つでも作れる) →
        // 落ちるとトリガーの既定の要素に化け、**別の要素を監視し始める**
        TriggerDefinition ownElement = Recorded();
        ownElement.Clauses[0].Window = new WindowIdentity { ProcessName = "other.exe" };
        Assert.Throws<ArgumentException>("definition", () => h.Presenter.LoadDefinition(ownElement));

        TriggerDefinition ownLocator = Recorded();
        ownLocator.Clauses[0].Locator = new ElementLocator();
        Assert.Throws<ArgumentException>("definition", () => h.Presenter.LoadDefinition(ownLocator));

        // 「絞るだけ」の句 → Watch が既定 (true) に戻り、要求するだけのつもりが発火源になる
        TriggerDefinition unwatched = Recorded();
        unwatched.Clauses[0].Watch = false;
        Assert.Throws<ArgumentException>("definition", () => h.Presenter.LoadDefinition(unwatched));
    }

    /// <summary>
    /// 編集を通ったものは、**何も変えずに確定しても**句が変わらないこと。
    ///
    /// <para>
    /// <see cref="CanEdit_AgreesWithWhatLoadDefinitionAccepts"/> の対である。あちらは
    /// 「断るものを断る」を見るが、断り漏れは**通ってしまう側**に出る —
    /// 確定は下書きから句を作り直すので、下書きが運べないものを持った句が通ると黙って落ちる。
    /// ここでは往復そのものを突き合わせるので、<see cref="TriggerDraft"/> に運べない
    /// フィールドが将来増えても、<see cref="TriggerPickerPresenter.CanEdit"/> を
    /// 直さなければ落ちる。
    /// </para>
    /// </summary>
    [Fact]
    public void LoadDefinition_ThenCommittingUnchanged_ChangesNothing()
    {
        using var h = new Harness();
        TriggerDefinition def = Recorded();
        def.MinInterval = TimeSpan.FromSeconds(2);
        string before = Json(def);

        h.Presenter.LoadDefinition(def);
        h.Presenter.Commit();

        Assert.Equal(before, Json(def));
    }

    /// <summary>定義を丸ごと文字列で突き合わせる (フィールドを数え上げない)。</summary>
    private static string Json(TriggerDefinition definition) => System.Text.Json.JsonSerializer.Serialize(
        definition, UiaTrigger.Serialization.TriggerJsonContext.Default.TriggerDefinition);

    /// <summary>
    /// 句のフィールドが 1 つ残らず**どれかに分類されている**こと。
    ///
    /// <para>
    /// 上の 2 件は「いま分かっている形」を固定するが、危ないのは**これから増えるフィールド**である。
    /// <see cref="PropertyClause"/> に何か足して <see cref="TriggerDraft"/> に運ぶ場所を作らないと、
    /// 編集を通ったトリガーからそれが黙って落ちる — そして
    /// <see cref="TriggerPickerPresenter.CanEdit"/> は何も知らないまま true を返し続ける。
    /// ここは 3 つの表で全フィールドを覆っているので、足したフィールドを
    /// どれかに入れる判断をしない限り落ちる。
    /// </para>
    /// </summary>
    [Fact]
    public void EveryClauseFieldIsAccountedForWhenEditing()
    {
        // 下書きが運ぶ = 編集して確定しても保たれる
        string[] carriedByTheDraft =
            ["Property", "Op", "Text", "Value", "Low", "High", "Tolerance"];
        // 運べないので CanEdit が編集そのものを断る (CustomPropertyId は Property=Custom として)
        string[] refusedByCanEdit = ["Window", "Locator", "Watch", "CustomPropertyId"];
        // 落ちてよい。句が 1 つで式が無いトリガーでは、名前を参照するものが無く、
        // 表示名は照合に使われない — つまり監視の挙動は変わらない
        string[] droppedOnPurpose = ["Name", "DisplayName"];

        string[] unaccounted = [.. typeof(PropertyClause).GetProperties()
            .Select(p => p.Name)
            .Except([.. carriedByTheDraft, .. refusedByCanEdit, .. droppedOnPurpose], StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        Assert.True(
            unaccounted.Length == 0,
            $"句の新しいフィールドが分類されていません: {string.Join(", ", unaccounted)}。" +
            "下書きが運ぶのか、CanEdit が編集を断るのか、落としてよいのかを決めてください。" +
            "決めないと、編集して確定するだけでそのフィールドが黙って消えます。");
    }

    /// <summary><see cref="TriggerPickerPresenter.CanEdit"/> が上の 3 つと同じ答えを返すこと。</summary>
    /// <remarks>
    /// 呼び出し側 (エディタ) はこれで先に訊く。例外を捕まえて分岐する形にすると、
    /// 「編集できないもの」に [編集] を出したままになる。
    /// </remarks>
    [Fact]
    public void CanEdit_AgreesWithWhatLoadDefinitionAccepts()
    {
        Assert.True(TriggerPickerPresenter.CanEdit(Recorded()));
        Assert.True(TriggerPickerPresenter.CanEdit(new TriggerDefinition { Id = "appear" }));

        TriggerDefinition composite = Recorded();
        composite.Expression = "c1";
        Assert.False(TriggerPickerPresenter.CanEdit(composite));

        TriggerDefinition custom = Recorded();
        custom.Clauses[0].Property = TriggerProperty.Custom;
        Assert.False(TriggerPickerPresenter.CanEdit(custom));

        TriggerDefinition ownElement = Recorded();
        ownElement.Clauses[0].Window = new WindowIdentity { ProcessName = "other.exe" };
        Assert.False(TriggerPickerPresenter.CanEdit(ownElement));

        TriggerDefinition unwatched = Recorded();
        unwatched.Clauses[0].Watch = false;
        Assert.False(TriggerPickerPresenter.CanEdit(unwatched));
    }

    /// <summary>ホストが保存していそうな、句 1 つの録り済みトリガー。</summary>
    private static TriggerDefinition Recorded() => new()
    {
        Id = "recorded",
        DisplayName = "Button \"Save\"",
        Window = new WindowIdentity { ProcessName = "notepad.exe" },
        On = TriggerOn.PropertyChanged,
        Clauses =
        [
            new PropertyClause { Property = TriggerProperty.Name, Op = ComparisonOp.Equals, Text = "Save" },
        ],
    };

    // ---------- 解放 ----------

    /// <summary>解放でタイマー・オーバーレイ・UIA セッションを全部畳むこと。</summary>
    [Fact]
    public void Dispose_StopsTheTimerAndReleasesTheOverlayAndTheSession()
    {
        var h = new Harness();
        h.Presenter.SetAutoSelect(true);

        h.Presenter.Dispose();

        Assert.False(h.Timer.IsRunning);
        Assert.True(h.Timer.IsDisposed);
        Assert.True(h.Overlay.IsDisposed);
        Assert.True(h.Services.DisposeAsyncCalled);
    }

    /// <summary>二度解放しても安全であること (ウィンドウの Closed と明示的な Dispose が重なる)。</summary>
    [Fact]
    public void Dispose_IsSafeToCallTwice()
    {
        var h = new Harness();

        h.Presenter.Dispose();
        h.Presenter.Dispose();

        Assert.True(h.Overlay.IsDisposed);
    }

    // ---------- プロパティ一覧 ----------

    /// <summary>
    /// コントロール型は表示名と安定名の両方を出すこと (docs/DESIGN.md L6)。
    ///
    /// 表示名だけだと、画面に出ている名前で Equals を書いた条件が一致しない。
    /// </summary>
    [Fact]
    public async Task Properties_ShowBothNamesForTheControlType()
    {
        using var h = new Harness();
        h.Strings.Values["PropertyRow"] = "{0}: {1}";
        h.Strings.Values["PropertyRowControlType"] = "ControlType: {0} (stable: {1}, id {2})";
        h.Services.NextSnapshot = new ElementPropertySnapshot
        {
            LocalizedControlType = "ボタン",
            ControlTypeName = "Button",
            ControlType = 50000,
        };
        h.Services.NextCapture = Capture(new FakePickerElement("Button"));

        await h.CaptureOnceAsync();

        Assert.Contains("ControlType: ボタン (stable: Button, id 50000)", h.View.PropertyRows);
    }

    /// <summary>
    /// 枠はスナップショットの矩形へ追随すること。
    ///
    /// <see cref="IPickerElement.BoundingRectangle"/> は**ハンドルを作った時点**の値なので、
    /// 対象ウィンドウを動かしたあとに別の行を選ぶと、枠だけが前の位置に残る。
    /// プロパティ一覧のためにスナップショットは既に読んでいるので、往復は増えない。
    /// </summary>
    [Fact]
    public async Task Overlay_FollowsTheRectangleFromTheSnapshotNotTheStaleHandle()
    {
        using var h = new Harness();
        var moved = new ElementRect(500, 600, 700, 700);
        h.Services.NextSnapshot = new ElementPropertySnapshot { BoundingRectangle = moved };
        h.Services.NextCapture = Capture(
            new FakePickerElement("Button") { BoundingRectangle = new ElementRect(0, 0, 200, 100) });

        await h.CaptureOnceAsync();

        Assert.Equal(moved, h.Overlay.ShownRects[^1]);
    }

    /// <summary>
    /// 確定アイコンの除外領域も、画面に出ている枠に合わせること。
    ///
    /// 古い矩形で判定すると、対象ウィンドウが動いたあとに
    /// 「何も無い場所で捕捉できない」「アイコンの上で捕捉してしまう」が同時に起きる。
    /// </summary>
    [Fact]
    public async Task Hover_TheConfirmIconZoneFollowsTheRectangleOnScreen()
    {
        using var h = new Harness();
        var stale = new ElementRect(0, 0, 200, 100);
        var moved = new ElementRect(500, 600, 700, 700);
        h.Services.NextSnapshot = new ElementPropertySnapshot { BoundingRectangle = moved };
        h.Services.NextCapture = Capture(new FakePickerElement("Button") { BoundingRectangle = stale });
        await h.CaptureOnceAsync();
        Assert.Single(h.Services.CaptureRequests);

        // 動いたあとのアイコン位置 → 捕捉しない
        await h.HoverAtAsync(moved.Right - h.Metrics.IconInset, moved.Top + h.Metrics.IconInset);
        Assert.Single(h.Services.CaptureRequests);

        // 古い矩形のアイコン位置は、もう何も無い場所 → 普通に捕捉する
        await h.HoverAtAsync(stale.Right - h.Metrics.IconInset, stale.Top + h.Metrics.IconInset);
        Assert.Equal(2, h.Services.CaptureRequests.Count);
    }

    /// <summary>
    /// ホバーの除外領域も表示スケールで広がること (docs/DESIGN.md §9)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **プレゼンターが DPI を無視していないことの検査である。**
    /// <c>OverlayGeometry</c> 側をいくら固めても、呼ぶ側が 96 を渡していれば
    /// 175% では「絵は大きいのに、除外領域だけ 96 のまま」になる —
    /// アイコンの上にカーソルを置いたのに捕捉が走り、選択が変わってしまう。
    /// </para>
    /// <para>
    /// 同じ 1 点を 2 つのスケールで撃つ。96 では領域の外、175% では中である。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Hover_TheConfirmIconZoneGrowsWithTheDisplayScale()
    {
        var rect = new ElementRect(0, 0, 200, 100);
        // 96 のアイコン領域の外、175% のアイコン領域の内側にある点
        int x = rect.Right - 25;
        int y = rect.Top + 20;
        Assert.False(OverlayGeometry.IsInIconZone(rect, 96, x, y));
        Assert.True(OverlayGeometry.IsInIconZone(rect, 168, x, y));

        // 96: アイコンの外なので普通に捕捉する
        using (var at96 = new Harness())
        {
            at96.Services.NextSnapshot = new ElementPropertySnapshot { BoundingRectangle = rect };
            at96.Services.NextCapture = Capture(new FakePickerElement("Button") { BoundingRectangle = rect });
            await at96.CaptureOnceAsync();
            await at96.HoverAtAsync(x, y);

            Assert.Equal(2, at96.Services.CaptureRequests.Count);
        }

        // 175%: 同じ点がアイコンの上になるので捕捉しない
        using var at175 = new Harness();
        at175.Dpi.Dpi = 168;
        at175.Services.NextSnapshot = new ElementPropertySnapshot { BoundingRectangle = rect };
        at175.Services.NextCapture = Capture(new FakePickerElement("Button") { BoundingRectangle = rect });
        await at175.CaptureOnceAsync();
        await at175.HoverAtAsync(x, y);

        Assert.Single(at175.Services.CaptureRequests);
    }

    /// <summary>選択が要素でなくなったらオーバーレイを消し、一覧も空にすること。</summary>
    [Fact]
    public async Task Properties_AreClearedWhenTheSelectionIsNotAnElement()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Button"));
        await h.CaptureOnceAsync();

        h.Presenter.NotifyTreeSelectionChanged(h.Presenter.Roots[0]); // プロセスルート

        Assert.Equal(1, h.Overlay.HideCount);
        Assert.Empty(h.View.PropertyRows);
    }

    // ---------- 要素ハンドルの所有 (docs/DESIGN.md §7) ----------
    //
    // ここだけは Harness.Dispose の台帳では見られない。台帳が見るのは
    // 「終わったときに漏れていないか」であり、終了時にすべて手放すので
    // **途中の掃き出しが 1 つも無くても緑になる**。世代の切り替わりで解放されることは
    // 個別に見るしかない。
    //
    // 偽の要素は**世代ごとに別インスタンス**で作る。本番の UIA は同じ要素に対しても
    // 毎回別のハンドルを返すので、同じインスタンスを使い回すと
    // 「エイリアスだから生き残った」のか「世代の判定が効いた」のか区別できない。

    /// <summary>置き換えた木のハンドルを手放すこと。手放さないとホバーのたびに漏れる分である。</summary>
    [Fact]
    public async Task Capture_ReleasesTheHandlesOfTheTreeItReplaced()
    {
        using var h = new Harness();
        var oldWindow = new FakePickerElement("Window1");
        var oldLeaf = new FakePickerElement("Leaf1");
        h.Services.NextCapture = Capture(oldWindow, oldLeaf);
        await h.CaptureOnceAtAsync(100, 100);

        var newWindow = new FakePickerElement("Window2");
        var newLeaf = new FakePickerElement("Leaf2");
        h.Services.NextCapture = Capture(newWindow, newLeaf);
        await h.CaptureOnceAtAsync(400, 400);

        Assert.True(oldWindow.IsDisposed, "置き換えた木のハンドルが残っています");
        Assert.True(oldLeaf.IsDisposed, "置き換えた木のハンドルが残っています");
        Assert.False(newWindow.IsDisposed, "いま表示している木のハンドルを解放しています");
        Assert.False(newLeaf.IsDisposed, "いま表示している木のハンドルを解放しています");
    }

    /// <summary>
    /// 新しい木が古い木と共有しているハンドルは、解放しないこと。
    ///
    /// 掃き出しを「新しい木を入れる**前**」に動かすとここが壊れる。共有しているハンドルが
    /// いったん到達不能になるためで、そのあと新しい木から参照されても解放済みである。
    /// </summary>
    [Fact]
    public async Task Capture_KeepsTheHandlesTheNewTreeSharesWithTheOldOne()
    {
        using var h = new Harness();
        var shared = new FakePickerElement("Window");
        var oldLeaf = new FakePickerElement("Leaf1");
        h.Services.NextCapture = Capture(shared, oldLeaf);
        await h.CaptureOnceAtAsync(100, 100);

        var newLeaf = new FakePickerElement("Leaf2");
        h.Services.NextCapture = Capture(shared, newLeaf);
        await h.CaptureOnceAtAsync(400, 400);

        Assert.False(shared.IsDisposed, "2 世代が共有しているハンドルを解放しています");
        Assert.True(oldLeaf.IsDisposed);

        // 生きているだけでなく、継ぎ目へ渡し返せること (解放済みなら例外になる)
        h.Services.NextChildren = new ChildrenResult { Children = [], ChainChildIndex = -1 };
        await h.Presenter.LoadChildrenAsync(h.Presenter.Roots[0].Children[0]);
        Assert.Empty(h.Services.UseAfterDispose);
    }

    /// <summary>
    /// ビュー切替の起点は、木から消えても解放しないこと。
    ///
    /// <c>_selectionOrigin</c> は木のエイリアスではなく**独立した所有根**である。
    /// 所有根から外すと、正規化で丸められた次の切替でここを渡した瞬間に例外になる。
    /// </summary>
    [Fact]
    public async Task ViewSwitch_KeepsTheElementItMayStillNeedToRebuildFrom()
    {
        using var h = new Harness();
        var origin = new FakePickerElement("Button");
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), origin);
        await h.CaptureOnceAsync();

        // 切替で選択が祖先へ丸められ、起点は木から消える
        h.Services.NextChain = Capture(new FakePickerElement("Window2"), new FakePickerElement("Pane"));
        await h.Presenter.RebuildChainAsync();

        Assert.False(origin.IsDisposed, "木から消えただけの起点を解放しています");

        // もう一度切り替えると起点から試される。解放済みならここで落ちる
        await h.Presenter.RebuildChainAsync();
        Assert.Same(origin, h.Services.ChainRequests[^1].Element);
        Assert.Empty(h.Services.UseAfterDispose);
    }

    /// <summary>
    /// 再列挙がチェーン子の位置に返したハンドルを手放すこと。
    ///
    /// 差し込みループはその位置を**意図的に飛ばす** (木にある同一要素のノードを
    /// 部分木ごと残すため)。飛ばされたハンドルはどのノードにも包まれないので、
    /// 所有下に置いていないと列挙のたびに 1 個ずつ漏れる。
    /// </summary>
    [Fact]
    public async Task LoadChildren_ReleasesTheChainChildHandleItDidNotUse()
    {
        using var h = new Harness();
        var pane = new FakePickerElement("Pane");
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), pane);
        await h.CaptureOnceAsync();
        PickerTreeNode windowNode = h.Presenter.Roots[0].Children[0];

        // 再列挙は同じ UIA 要素に対しても別のハンドルを返す
        var paneAgain = new FakePickerElement("Pane");
        var before = new FakePickerElement("Before");
        var after = new FakePickerElement("After");
        h.Services.NextChildren = new ChildrenResult
        {
            Children = [before, paneAgain, after],
            ChainChildIndex = 1,
        };

        await h.Presenter.LoadChildrenAsync(windowNode);

        Assert.True(paneAgain.IsDisposed, "使わなかったチェーン子のハンドルが残っています");
        Assert.False(pane.IsDisposed, "木に残っているチェーン子を解放しています");
        Assert.False(before.IsDisposed);
        Assert.False(after.IsDisposed);
    }

    /// <summary>チェーン子が見つからず全行を差し替えたときも、消えた行のハンドルを手放すこと。</summary>
    [Fact]
    public async Task LoadChildren_ReleasesTheRowsItReplaced()
    {
        using var h = new Harness();
        var window = new FakePickerElement("Window");
        var pane = new FakePickerElement("Pane");
        h.Services.NextCapture = Capture(window, pane);
        await h.CaptureOnceAsync();
        PickerTreeNode windowNode = h.Presenter.Roots[0].Children[0];

        // 選択を親へ移す。pane を所有根から外さないと、消えても解放されない (それが正しい)
        h.Presenter.NotifyTreeSelectionChanged(windowNode);
        Assert.False(pane.IsDisposed);

        var replacement = new FakePickerElement("A");
        h.Services.NextChildren = new ChildrenResult { Children = [replacement], ChainChildIndex = -1 };

        await h.Presenter.LoadChildrenAsync(windowNode);

        Assert.True(pane.IsDisposed, "差し替えで消えた行のハンドルが残っています");
        Assert.False(replacement.IsDisposed);
    }

    /// <summary>重なりスタックのうち、移動しなかったぶんを手放すこと (←/→ 1 回ごとに作り直す)。</summary>
    [Fact]
    public async Task Overlap_ReleasesTheStackItDidNotMoveTo()
    {
        using var h = new Harness();
        var behind = new FakePickerElement("Behind");
        var front = new FakePickerElement("Front");
        var third = new FakePickerElement("Third");
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), behind);
        await h.CaptureOnceAsync();
        h.Services.NextStack = new ElementStack { Nodes = [behind, front, third], CurrentIndex = 0 };
        h.Services.NextOverlapChain = Capture(new FakePickerElement("Window2"), new FakePickerElement("FrontAgain"));

        await h.Presenter.MoveStackAsync(right: true);

        Assert.False(front.IsDisposed, "移動先のハンドルを解放しています (次の切替で使えなくなります)");
        Assert.True(third.IsDisposed, "使わなかったスタックのハンドルが残っています");
    }

    /// <summary>スタックの端で動かなかったときは、作ったスタックを丸ごと手放すこと (早期 return の経路)。</summary>
    [Fact]
    public async Task Overlap_AtTheEndOfTheStack_ReleasesTheWholeStack()
    {
        using var h = new Harness();
        var front = new FakePickerElement("Front");
        var behind = new FakePickerElement("Behind");
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), front);
        await h.CaptureOnceAsync();
        h.Services.NextStack = new ElementStack { Nodes = [behind, front], CurrentIndex = 1 };

        await h.Presenter.MoveStackAsync(right: true);

        Assert.True(behind.IsDisposed, "動かなかったときにスタックのハンドルが残っています");
        Assert.False(front.IsDisposed, "木に居る要素を解放しています");
    }

    /// <summary>
    /// 起点が別の要素へ移ったら、**その場で**古い起点を手放すこと。
    ///
    /// <para>
    /// 重なり切替の移動先は、切替のあと<c>_selectionOrigin</c> からしか到達できない
    /// (<c>ApplyCapture</c> が入れ替えた木には別のハンドルが並ぶため)。
    /// だから起点が動いた瞬間に掃かないと、そのハンドルは終了まで残る。
    /// </para>
    /// <para>
    /// **掃き出しを消しても、他のテストはすべて緑のままである** —
    /// 終了時の一括解放 (<c>ReleaseAllElements</c>) が「終わったときには漏れていない」を
    /// 成立させてしまうためで、**途中で解放されること**を名指しで見るのはここだけである。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("selection")]
    [InlineData("invoked")]
    [InlineData("search")]
    public async Task WhenTheOriginMoves_TheOverlapTargetItNoLongerNeedsIsReleased(string how)
    {
        using var h = new Harness();
        var behind = new FakePickerElement("Behind");
        var front = new FakePickerElement("Front");
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), behind);
        await h.CaptureOnceAsync();

        // 重なり切替: 移動先 front が起点になるが、入れ替わった木には別のハンドルが並ぶ
        h.Services.NextStack = new ElementStack { Nodes = [behind, front], CurrentIndex = 0 };
        h.Services.NextOverlapChain = Capture(new FakePickerElement("Window2"), new FakePickerElement("FrontAgain"));
        await h.Presenter.MoveStackAsync(right: true);
        Assert.False(front.IsDisposed, "移動直後は起点として要る");

        PickerTreeNode windowNode = h.Presenter.Roots[0].Children[0];
        switch (how)
        {
            case "selection":
                h.Presenter.NotifyTreeSelectionChanged(windowNode);
                break;
            case "invoked":
                h.Services.NextChildren = null; // 列挙はさせない (この経路の論点ではない)
                h.Presenter.NotifyTreeItemInvoked(windowNode);
                break;
            default:
                h.Presenter.SearchNext("Window2");
                break;
        }

        Assert.True(
            front.IsDisposed,
            $"起点が動いた ({how}) のに、到達できなくなった重なり切替の移動先が残っています");
    }

    /// <summary>
    /// 終了時に、持っているハンドルをすべて手放すこと。
    ///
    /// <c>using var h</c> ではなく素の <c>var h</c> を使っている — 台帳の assert を通さず、
    /// 「終了時に解放する」ことそのものを名指しで見るためである。
    /// </summary>
    [Fact]
    public async Task Dispose_ReleasesEveryHandleItStillHolds()
    {
        var h = new Harness();
        var window = new FakePickerElement("Window");
        var leaf = new FakePickerElement("Leaf");
        h.Services.NextCapture = Capture(window, leaf);
        await h.CaptureOnceAsync();
        Assert.False(window.IsDisposed);

        h.Presenter.Dispose();

        Assert.True(window.IsDisposed, "終了時に木のハンドルが残っています");
        Assert.True(leaf.IsDisposed, "終了時に選択中のハンドルが残っています");
    }

    /// <summary>
    /// プロパティを読んでいる在庫中に木が入れ替わっても、生き延びること。
    ///
    /// この経路は fire-and-forget で <c>try</c> が無い。掃き出しがハンドルを解放すると
    /// 借用スコープが <see cref="ObjectDisposedException"/> を投げ、**誰も観測しない
    /// faulted Task** になる。この失敗形は掃き出し (起点が動いた瞬間にハンドルを
    /// 解放すること) が作るものなので、ここで閉じる。
    /// </summary>
    [Fact]
    public async Task Refresh_SurvivesTheTreeBeingReplacedWhileItReadsAProperty()
    {
        using var h = new Harness();
        var oldLeaf = new FakePickerElement("Leaf1");
        h.Services.NextCapture = Capture(new FakePickerElement("Window1"), oldLeaf);
        await h.CaptureOnceAtAsync(100, 100);
        PickerTreeNode oldNode = h.Presenter.Roots[0].Children[0].Children[0];

        var gate = new TaskCompletionSource();
        h.Services.SnapshotGate = gate;
        Task reading = h.Presenter.RefreshPropsAsync(oldNode);

        // 読んでいる途中に別の場所を捕捉する → 古い木のハンドルが解放される
        h.Services.NextCapture = Capture(new FakePickerElement("Window2"), new FakePickerElement("Leaf2"));
        await h.CaptureOnceAtAsync(400, 400);
        Assert.True(oldLeaf.IsDisposed);

        gate.SetResult();
        await reading; // ここで投げたら、本番では観測されない faulted Task になっていた
    }

    /// <summary>
    /// **相手から読めなくなったら、プロパティ一覧が空になること** (docs/DESIGN.md §12 の決定)。
    ///
    /// 塞がれたアプリへの読み取りは <see cref="System.Runtime.InteropServices.COMException"/>
    /// (「Operation timed out」— 実測) になる。この経路が fire-and-forget の faulted Task に
    /// なると、一覧は**古い値のまま**残る — hang が明けても更新されず、
    /// 「読めている」ようにしか見えない。
    /// </summary>
    [Fact]
    public async Task Refresh_WhenTheTargetCannotBeRead_EmptiesThePropertyList()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), new FakePickerElement("Leaf"));
        h.Services.NextSnapshot = new ElementPropertySnapshot();
        await h.CaptureOnceAtAsync(100, 100);
        Assert.NotEmpty(h.View.PropertyRows);
        PickerTreeNode current = h.Presenter.Roots[0].Children[0].Children[0];

        h.Services.SnapshotFails = true;
        await h.Presenter.RefreshPropsAsync(current);

        Assert.Empty(h.View.PropertyRows);
    }

    /// <summary>
    /// **読めるようになったら一覧が追いつくこと** (空のまま行き止まりにならないこと)。
    /// 実機では「次の選択・捕捉・重なり切替」が新しい読み取りを起こす。
    /// </summary>
    [Fact]
    public async Task Refresh_AfterTheTargetRecovers_ShowsThePropertiesAgain()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), new FakePickerElement("Leaf"));
        h.Services.NextSnapshot = new ElementPropertySnapshot();
        await h.CaptureOnceAtAsync(100, 100);
        PickerTreeNode current = h.Presenter.Roots[0].Children[0].Children[0];
        h.Services.SnapshotFails = true;
        await h.Presenter.RefreshPropsAsync(current);
        Assert.Empty(h.View.PropertyRows);

        h.Services.SnapshotFails = false;
        await h.Presenter.RefreshPropsAsync(current);

        Assert.NotEmpty(h.View.PropertyRows);
    }

    /// <summary>
    /// **現在の行でない読み取りの失敗は、いま出ている一覧に触らないこと。**
    /// 在庫中に木が入れ替わった古い読み取りが、新しい行の一覧を消してはいけない。
    /// </summary>
    [Fact]
    public async Task Refresh_WhenAStaleReadFails_LeavesTheCurrentListAlone()
    {
        using var h = new Harness();
        h.Services.NextCapture = Capture(new FakePickerElement("Window"), new FakePickerElement("Leaf"));
        h.Services.NextSnapshot = new ElementPropertySnapshot();
        await h.CaptureOnceAtAsync(100, 100);
        PickerTreeNode stale = h.Presenter.Roots[0].Children[0].Children[0];

        // **同じ木の中で**選択を移して current を入れ替える。木を差し替えると
        // 古いハンドルが解放され、失敗が ObjectDisposedException 側 (別のテストが担当) に化ける
        h.Presenter.NotifyTreeSelectionChanged(h.Presenter.Roots[0].Children[0]);
        Assert.NotEmpty(h.View.PropertyRows);

        h.Services.SnapshotFails = true;
        await h.Presenter.RefreshPropsAsync(stale);

        Assert.NotEmpty(h.View.PropertyRows);
    }

    /// <summary>子を列挙している在庫中に木が入れ替わっても、生き延びること。</summary>
    [Fact]
    public async Task LoadChildren_SurvivesTheTreeBeingReplacedWhileItEnumerates()
    {
        using var h = new Harness();
        var oldWindow = new FakePickerElement("Window1");
        h.Services.NextCapture = Capture(oldWindow, new FakePickerElement("Leaf1"));
        await h.CaptureOnceAtAsync(100, 100);
        PickerTreeNode oldNode = h.Presenter.Roots[0].Children[0];

        var gate = new TaskCompletionSource();
        h.Services.ChildrenGate = gate;
        h.Services.NextChildren = new ChildrenResult { Children = [], ChainChildIndex = -1 };
        Task enumerating = h.Presenter.LoadChildrenAsync(oldNode);

        h.Services.NextCapture = Capture(new FakePickerElement("Window2"), new FakePickerElement("Leaf2"));
        await h.CaptureOnceAtAsync(400, 400);
        Assert.True(oldWindow.IsDisposed);

        gate.SetResult();
        await enumerating;
    }

    // ---------- 補助 ----------

    private static HoverCapture Capture(params IPickerElement[] chain)
        => Capture("notepad.exe (PID 7)", chain);

    private static HoverCapture Capture(string processLabel, params IPickerElement[] chain)
        => new() { ProcessLabel = processLabel, ProcessId = 7, Chain = chain };

    private static TriggerDefinition Definition(string processName) => new()
    {
        Id = "recorded",
        DisplayName = "Button \"Save\"",
        Window = new WindowIdentity { ProcessName = processName },
    };

    private static IEnumerable<string> Descend(PickerTreeNode root)
    {
        PickerTreeNode? node = root.Children.Count > 0 ? root.Children[0] : null;
        while (node is not null)
        {
            yield return node.Display;
            node = node.Children.Count > 0 ? node.Children[0] : null;
        }
    }

    private sealed class Harness : IDisposable
    {
        public FakePickerView View { get; } = new();

        public FakeDispatcher Dispatcher { get; } = new();

        public FakeCursor Cursor { get; } = new();

        public FakeStrings Strings { get; } = new();

        public FakePickerServices Services { get; } = new();

        public FakeOverlay Overlay { get; } = new();

        public FakeTimeProvider Time { get; } = new();

        /// <summary>既定は 96。スケールを見たいテストだけが <c>Dpi.Dpi</c> を立てる (docs/DESIGN.md §9)。</summary>
        public FakeDpiSource Dpi { get; } = new();

        public TriggerPickerPresenter Presenter { get; }

        public FakeTimer Timer => Dispatcher.Timers[0];

        /// <summary>いまの表示スケールでの実寸。確定アイコンを狙うテストが使う。</summary>
        public OverlayGeometry.Metrics Metrics => OverlayGeometry.MetricsFor(Dpi.Dpi);

        public Harness() =>
            Presenter = new TriggerPickerPresenter(View, Dispatcher, Cursor, Strings, Services, Overlay, Time, Dpi);

        /// <summary>仕込んだ <see cref="FakePickerServices.NextCapture"/> を 1 回だけ捕捉させる。</summary>
        public Task CaptureOnceAsync() => CaptureOnceAtAsync(100, 100);

        /// <summary>指定座標で滞留させる。</summary>
        public async Task CaptureOnceAtAsync(int x, int y)
        {
            Cursor.X = x;
            Cursor.Y = y;
            await Presenter.TickAsync();
            Time.Advance(TimeSpan.FromMilliseconds(1001));
            await Presenter.TickAsync();
        }

        /// <summary>指定座標へ動かして滞留させる (捕捉されるかどうかはテストが見る)。</summary>
        public async Task HoverAtAsync(int x, int y)
        {
            Cursor.X = x;
            Cursor.Y = y;
            await Presenter.TickAsync();
            Time.Advance(TimeSpan.FromMilliseconds(1001));
            await Presenter.TickAsync();
        }

        /// <summary>コミットのテスト向けに「要素を確定した」状態を作る。</summary>
        public async Task ConfirmSomethingAsync()
        {
            Services.NextCapture = Capture(new FakePickerElement("Button"));
            await CaptureOnceAsync();
            Services.NextDefinition = Definition("notepad.exe");
            await Presenter.ConfirmNodeAsync(Presenter.Roots[0].Children[0]);
        }

        /// <summary>
        /// プレゼンターを畳み、**そのうえで**ハンドルの台帳を検める。
        /// </summary>
        /// <remarks>
        /// <para>
        /// これがあるおかげで、要素を扱う既存テスト 40 件以上が**本文を 1 行も変えずに**
        /// 「ハンドルを漏らしていないか」「解放済みを使い回していないか」の検査を兼ねる。
        /// 掃き出しは <see cref="FakePickerServices"/> が配ったものだけを対象にできる —
        /// プレゼンターが要素を受け取る先はそこしか無いからである。
        /// </para>
        /// <para>
        /// <see cref="TriggerPickerPresenter.Dispose"/> の**後**に見るのは、
        /// 「選択したまま終わる」テストでも成立させるためである (終了時にすべて手放すので、
        /// 選択状態に関係なく <see cref="FakePickerServices.Retained"/> は空になる)。
        /// </para>
        /// <para>
        /// **注意**: unwinding 中の <c>Dispose</c> で投げると、元の失敗が置き換わる。
        /// メッセージは台帳のものだと一目で分かる文言にしてある。
        /// </para>
        /// </remarks>
        public void Dispose()
        {
            Presenter.Dispose();

            Assert.True(
                Services.UseAfterDispose.Count == 0,
                "【ハンドルの台帳】解放済みの要素を継ぎ目へ渡しました (解放が早すぎます): " +
                string.Join(", ", Services.UseAfterDispose));
            Assert.True(
                !Services.Retained.Any(),
                "【ハンドルの台帳】配ったハンドルが解放されていません (漏れています): " +
                string.Join(", ", Services.Retained));
        }
    }
}
