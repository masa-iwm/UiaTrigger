// WPF View (継ぎ目の向こう側) の回帰テスト (docs/DESIGN.md §12)。
//
// 「View は T1 から触れない」は**WinUI3 / x64 / WindowsAppSDK** の制約であって
// View 一般の話ではない (docs/DESIGN.md §12)。
// net10.0-windows / AnyCPU の WPF View はここで直接組み立てて検査できる。
//
// ただし**コンテナの実体化にはウィンドウの表示が要る** (実測):
//   Measure/Arrange だけ         → ItemContainerGenerator は何も返さない
//   Window.Measure/Arrange + pump → 同上
//   Window.Show()                → TreeViewItem が生成され、展開も選択も効く
// 可視のデスクトップを要する検査は T1 の担当ではない (docs/TESTING.md §2 の横断ルール) ため、
// 実体化を伴う経路 (ExpandThenSelect / IsExpanded の TwoWay) はここでは扱わず、
// T4 が引き取っている (docs/MANUAL-CHECKS.md §4.3.3)。
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Time.Testing;
using UiaTrigger.Models;
using UiaTrigger.Picker;
using UiaTrigger.Picker.Wpf;
using Xunit;

namespace UiaTrigger.Tests;

public sealed class TriggerPickerWpfWindowTests
{
    /// <summary>
    /// 実物の UIA セッションとオーバーレイを作らせずに View を組み立てる。
    /// </summary>
    private static TriggerPickerWindow CreateWindow(IPickerStrings strings)
        => new(strings, view => new TriggerPickerPresenter(
            view,
            new FakeDispatcher(),
            new FakeCursor(),
            strings,
            new FakePickerServices(),
            new FakeOverlay(),
            new FakeTimeProvider(),
            new FakeDpiSource()));

    /// <summary>
    /// <c>StopAutoSelect</c> がホバー捕捉を**プレゼンターまで**止めること
    /// (docs/DESIGN.md §12)。
    ///
    /// <para>
    /// チェックが外れたことだけを見ても足りない — **見るのはオーバーレイのフックである**。
    /// 代入だけしてハンドラーを経由しない書き方に変えると、画面は「OFF」と出るのに捕捉は生きたままになり、
    /// 症状 (2 枚目のマウスに 1 枚目が追随する) は何も変わらない。
    /// </para>
    /// <para>
    /// 開いた直後が ON であることを先に見るのは、**この検査に検出力があることを示すため**である。
    /// </para>
    /// </summary>
    [Fact]
    public void StopAutoSelect_StopsHoverCaptureAtThePresenter()
    {
        Sta.Run(() =>
        {
            var overlay = new FakeOverlay();
            var strings = new FakeStrings();
            using TriggerPickerWindow window = new(strings, view => new TriggerPickerPresenter(
                view,
                new FakeDispatcher(),
                new FakeCursor(),
                strings,
                new FakePickerServices(),
                overlay,
                new FakeTimeProvider(),
                new FakeDpiSource()));

            Assert.True(overlay.HookEnabled, "ピッカーは自動選択 ON で開くはずである (既定が変わっている)。");

            window.StopAutoSelect();

            Assert.False(
                overlay.HookEnabled,
                "StopAutoSelect がプレゼンターまで届いていません。画面は OFF でも捕捉は生きています。");
        });
    }

    /// <summary>
    /// View が**すべての**コントロール用キーを要求すること。
    ///
    /// <para>
    /// コントロールを 1 つ足してラベルの代入を忘れる、というのがこの層で最も起きやすい壊れ方で、
    /// しかも例外にならない — その欄が空のまま表示されるだけである
    /// (WinUI 側は T4 の <c>PublishedResourceTests</c> が発行レイアウトで見ている項目そのもの —
    /// docs/MANUAL-CHECKS.md §4.1)。
    /// WPF View は T1 から組み立てられるので、こちらはウィンドウを出さずに固定できる。
    /// </para>
    /// </summary>
    [Fact]
    public void TheViewAsksForEveryLabelInTheKeyTable()
    {
        Sta.Run(() =>
        {
            var strings = new FakeStrings();
            using TriggerPickerWindow window = CreateWindow(strings);

            // '.' を含むキー = 特定のコントロールのラベル。加えてウィンドウタイトル
            string[] expected = [.. PickerStringKeys.All
                .Where(k => k.Contains('.', StringComparison.Ordinal) || k == PickerStringKeys.WindowTitle)];
            string[] missing = [.. expected
                .Except(strings.Requested, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];

            Assert.True(
                missing.Length == 0,
                $"WPF View が要求しなかったラベル: {string.Join(", ", missing)}。" +
                "そのコントロールは空欄のまま表示されます。");
        });
    }

    /// <summary>
    /// 要求するだけでなく、引いた文字列が実際にコントロールへ載っていること。
    ///
    /// 「GetString は呼んだが代入先を間違えた」を上のテストは見抜けない。
    /// 実物のリソースで組み立てて、代表的なコントロールの表示文字列を突き合わせる。
    /// </summary>
    [Fact]
    public void TheResolvedStringsLandOnTheControls()
    {
        Sta.Run(() =>
        {
            CultureInfo original = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = new CultureInfo("en-US");
                using TriggerPickerWindow window = CreateWindow(new ResxPickerStrings());

                Assert.Equal("Pick an element — UiaTrigger", window.Title);
                Assert.Equal("Add trigger", window.CommitButton.Content);
                Assert.Equal("Follow the mouse", window.AutoSelectLabel.Text);
                Assert.Equal("Tree view", window.ViewComboLabel.Text);
                // DataTemplate の中のボタンには掴めないので DynamicResource 経由で渡している
                Assert.Equal(
                    "Confirm this element",
                    window.Resources[PickerStringKeys.ConfirmNodeButtonToolTip]);
            }
            finally
            {
                CultureInfo.CurrentUICulture = original;
            }
        });
    }

    /// <summary>
    /// 数値欄が空なら「値なし」であること。
    ///
    /// WinUI の <c>NumberBox</c> は空欄を <see cref="double.NaN"/> で表すが、
    /// <c>TextBox</c> は空文字である。この違いは継ぎ目に出さず View が吸収する約束になっている
    /// (<see cref="IPickerView.ReadDraft"/>)。
    /// </summary>
    [Fact]
    public void ReadDraft_WithEmptyNumberBoxes_LeavesTheOperandsUnset()
    {
        Sta.Run(() =>
        {
            using TriggerPickerWindow window = CreateWindow(new FakeStrings());
            var view = (IPickerView)window;
            view.ShowTriggerShape([TriggerProperty.Name], TriggerOn.PropertyChanged, ComparisonOp.Equals);

            TriggerDraft? draft = view.ReadDraft();

            Assert.NotNull(draft);
            Assert.Null(draft.Value);
            Assert.Null(draft.Low);
            Assert.Null(draft.High);
            Assert.Null(draft.Tolerance);
            Assert.Null(draft.MinIntervalSeconds);
        });
    }

    /// <summary>
    /// 読めない入力は 0 ではなく「値なし」になること。
    ///
    /// <para>
    /// 0 は**正当な値**である。打ち間違いを 0 に丸めると「0 という条件」が黙って保存され、
    /// 監視は誰も書いていない条件で動き続ける。null なら <c>TriggerDraftValidator</c> が
    /// 欠落として弾き、ユーザーにメッセージが出る。
    /// </para>
    /// </summary>
    [Fact]
    public void ReadDraft_WithUnparsableText_LeavesTheOperandUnsetRatherThanZero()
    {
        Sta.Run(() =>
        {
            using TriggerPickerWindow window = CreateWindow(new FakeStrings());
            var view = (IPickerView)window;
            view.ShowTriggerShape([TriggerProperty.Name], TriggerOn.PropertyChanged, ComparisonOp.Equals);
            window.ValueOperand.Text = "abc";

            Assert.Null(view.ReadDraft()!.Value);
        });
    }

    /// <summary>
    /// 数値は現在のカルチャで解釈すること (docs/DESIGN.md L7)。
    ///
    /// 表示側がカルチャに従う以上、入力も同じでなければ
    /// 「画面に出ている値をそのまま打ち直すと通らない」ことになる。
    /// </summary>
    [Fact]
    public void ReadDraft_ReadsNumbersInTheCurrentCulture()
    {
        Sta.Run(() =>
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            try
            {
                using TriggerPickerWindow window = CreateWindow(new FakeStrings());
                var view = (IPickerView)window;
                view.ShowTriggerShape([TriggerProperty.Name], TriggerOn.PropertyChanged, ComparisonOp.Equals);

                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                window.ValueOperand.Text = "1,5";
                Assert.Equal(1.5, view.ReadDraft()!.Value);

                CultureInfo.CurrentCulture = new CultureInfo("en-US");
                window.ValueOperand.Text = "1.5";
                Assert.Equal(1.5, view.ReadDraft()!.Value);
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        });
    }

    /// <summary>
    /// <c>ShowDraft</c> → <c>ReadDraft</c> の往復で値が変わらないこと (docs/DESIGN.md §4)。
    ///
    /// これが崩れると**読み込んだ条件をそのまま確定するだけで条件が変わる**。
    /// 書式の差なので例外にはならず、保存されたファイルを見るまで分からない。
    /// </summary>
    [Fact]
    public void ShowDraftThenReadDraft_RoundTripsEveryField()
    {
        Sta.Run(() =>
        {
            using TriggerPickerWindow window = CreateWindow(new FakeStrings());
            var view = (IPickerView)window;
            view.ShowTriggerShape(
                [TriggerProperty.Name, TriggerProperty.Value], TriggerOn.PropertyChanged, ComparisonOp.Always);
            var original = new TriggerDraft
            {
                Id = "edited",
                DisplayName = "Save button",
                On = TriggerOn.WhileMatching,
                Property = TriggerProperty.Value,
                Op = ComparisonOp.Between,
                Text = "text",
                Value = 1.5,
                Low = -2.25,
                High = 10,
                Tolerance = 0.125,
                MinIntervalSeconds = 3,
                PollIntervalSeconds = 7.5,
            };

            view.ShowDraft(original);
            TriggerDraft? read = view.ReadDraft();

            Assert.NotNull(read);
            Assert.Equal(original.Id, read.Id);
            Assert.Equal(original.DisplayName, read.DisplayName);
            Assert.Equal(original.On, read.On);
            Assert.Equal(original.Property, read.Property);
            Assert.Equal(original.Op, read.Op);
            Assert.Equal(original.Text, read.Text);
            Assert.Equal(original.Value, read.Value);
            Assert.Equal(original.Low, read.Low);
            Assert.Equal(original.High, read.High);
            Assert.Equal(original.Tolerance, read.Tolerance);
            Assert.Equal(original.MinIntervalSeconds, read.MinIntervalSeconds);
            Assert.Equal(original.PollIntervalSeconds, read.PollIntervalSeconds);
        });
    }

    /// <summary>
    /// 往復がカルチャを跨いでも崩れないこと。
    ///
    /// <c>ReadNumber</c> が現在のカルチャで読む以上、書くほうも同じでなければならない。
    /// invariant で書いて de-DE で読むと <c>1.5</c> が <c>15</c> になる — 例外は出ない。
    /// </summary>
    [Fact]
    public void ShowDraftThenReadDraft_RoundTripsInTheCurrentCulture()
    {
        Sta.Run(() =>
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            try
            {
                using TriggerPickerWindow window = CreateWindow(new FakeStrings());
                var view = (IPickerView)window;
                view.ShowTriggerShape([TriggerProperty.Name], TriggerOn.PropertyChanged, ComparisonOp.Always);

                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                view.ShowDraft(new TriggerDraft { Id = "x", Value = 1.5 });

                Assert.Equal("1,5", window.ValueOperand.Text);
                Assert.Equal(1.5, view.ReadDraft()!.Value);
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        });
    }

    /// <summary>値なし (null) は空欄になること — 0 を書かない。</summary>
    [Fact]
    public void ShowDraft_WithNoValues_LeavesTheNumberBoxesEmpty()
    {
        Sta.Run(() =>
        {
            using TriggerPickerWindow window = CreateWindow(new FakeStrings());
            var view = (IPickerView)window;
            view.ShowTriggerShape([TriggerProperty.Name], TriggerOn.PropertyChanged, ComparisonOp.Always);
            window.ValueOperand.Text = "9";

            view.ShowDraft(new TriggerDraft { Id = "x" });

            Assert.Equal(string.Empty, window.ValueOperand.Text);
            Assert.Null(view.ReadDraft()!.Value);
        });
    }

    /// <summary>
    /// 既存トリガーの読み込みでホバー捕捉が止まること。
    ///
    /// 止めないと、マウスがどこかの要素の上に静止しているだけで
    /// **編集対象が別の要素へ差し替わる**。
    /// </summary>
    [Fact]
    public void LoadDefinition_TurnsHoverCaptureOff()
    {
        Sta.Run(() =>
        {
            using TriggerPickerWindow window = CreateWindow(new FakeStrings());
            Assert.True(window.AutoSelectToggle.IsChecked);

            window.LoadDefinition(new TriggerDefinition
            {
                Id = "recorded",
                Clauses = [new PropertyClause { Property = TriggerProperty.Name, Op = ComparisonOp.Equals, Text = "a" }],
            });

            Assert.False(window.AutoSelectToggle.IsChecked);
            Assert.Equal("recorded", window.KeyBox.Text);
            Assert.Equal("a", window.TextOperand.Text);
        });
    }

    /// <summary>
    /// トリガーの形が未選択なら null を返すこと (継ぎ目の契約)。
    /// プレゼンターはこれを見て「形を選んでください」と出す。
    /// </summary>
    [Fact]
    public void ReadDraft_BeforeAnyShapeIsChosen_ReturnsNull()
        => Sta.Run(() =>
        {
            using TriggerPickerWindow window = CreateWindow(new FakeStrings());
            Assert.Null(((IPickerView)window).ReadDraft());
        });

    /// <summary>
    /// 出す欄と出さない欄が <see cref="OperandVisibility"/> のとおりであること。
    ///
    /// ここがずれると**ユーザーに見えない欄の値が条件に入る**。
    /// 出し分けの規則そのものはプレゼンター側 (T1 で別途固定) の担当で、
    /// ここで見るのは「言われたとおりに出し入れしているか」である。
    /// </summary>
    [Fact]
    public void ShowOperands_ShowsExactlyTheFieldsItIsToldTo()
    {
        Sta.Run(() =>
        {
            using TriggerPickerWindow window = CreateWindow(new FakeStrings());
            var view = (IPickerView)window;

            view.ShowOperands(new OperandVisibility(
                Text: true, Value: false, Range: false, Tolerance: false, PollInterval: true,
                PropertyChoiceEnabled: true, StoppedMatching: true));
            Assert.Equal(Visibility.Visible, window.TextOperandPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, window.ValueOperandPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, window.LowOperandPanel.Visibility);
            Assert.Equal(Visibility.Visible, window.PollIntervalOperandPanel.Visibility);
            Assert.Equal(Visibility.Visible, window.StoppedMatchingCheck.Visibility);
            Assert.True(window.PropCombo.IsEnabled);

            view.ShowOperands(new OperandVisibility(
                Text: false, Value: false, Range: true, Tolerance: true, PollInterval: false,
                PropertyChoiceEnabled: false, StoppedMatching: false));
            Assert.Equal(Visibility.Collapsed, window.TextOperandPanel.Visibility);
            Assert.Equal(Visibility.Visible, window.LowOperandPanel.Visibility);
            Assert.Equal(Visibility.Visible, window.HighOperandPanel.Visibility);
            Assert.Equal(Visibility.Visible, window.ToleranceOperandPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, window.PollIntervalOperandPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, window.StoppedMatchingCheck.Visibility);
            Assert.False(window.PropCombo.IsEnabled);
        });
    }

    /// <summary>
    /// 編集セッションのコミットで窓が閉じること (WinForms 側の
    /// <c>AnEditSessionCommit_ClosesTheFormWithoutTouchingItAfterwards</c> の WPF 対)。
    /// </summary>
    [Fact]
    public void AnEditSessionCommit_ClosesTheWindow()
    {
        Sta.Run(() =>
        {
            using TriggerPickerWindow window = CreateWindow(new FakeStrings());
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.Show();
            window.LoadDefinition(Editable(), editSession: true);
            // FakeStrings はキーをそのまま返す — 文言が「更新」側へ差し替わった証拠
            Assert.Equal(PickerStringKeys.CommitButtonUpdate, window.CommitButton.Content);

            window.CommitButton.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.False(window.IsVisible);
        });
    }

    /// <summary>プリフィル (編集セッションでない) のコミットでは窓が開いたままなこと。</summary>
    [Fact]
    public void APrefillCommit_LeavesTheWindowOpen()
    {
        Sta.Run(() =>
        {
            using TriggerPickerWindow window = CreateWindow(new FakeStrings());
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.Show();
            window.LoadDefinition(Editable());

            window.CommitButton.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.True(window.IsVisible);
            window.Close();
        });
    }

    private static TriggerDefinition Editable() => new()
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

    /// <summary>
    /// 確定後に、監視できるプロパティが実際にコンボへ入り、先頭が選ばれること。
    ///
    /// WinUI ではここが <c>E_INVALIDARG</c> で落ちる罠が実機で出た (docs/DESIGN.md §12 の ABI の罠)。
    /// WPF に CCW の制約は無いが、**同じ経路が同じように働くこと**は変種ごとに確かめる。
    /// </summary>
    [Fact]
    public void ShowTriggerShape_FillsTheCombosAndPreselects()
    {
        Sta.Run(() =>
        {
            using TriggerPickerWindow window = CreateWindow(new FakeStrings());
            var view = (IPickerView)window;

            view.ShowTriggerShape(
                [TriggerProperty.Name, TriggerProperty.AutomationId],
                TriggerOn.ElementAppeared,
                ComparisonOp.RegexMatch);

            Assert.Equal(TriggerProperty.Name, window.PropCombo.SelectedItem);
            Assert.Equal(TriggerOn.ElementAppeared, window.OnCombo.SelectedItem);
            Assert.Equal(ComparisonOp.RegexMatch, window.CondCombo.SelectedItem);
            Assert.Equal(2, window.PropCombo.Items.Count);
        });
    }

    /// <summary>
    /// プロパティ一覧が空リストで消えること (継ぎ目の「空なら消す」契約)。
    /// </summary>
    [Fact]
    public void ShowProperties_WithNoRows_ClearsTheList()
    {
        Sta.Run(() =>
        {
            using TriggerPickerWindow window = CreateWindow(new FakeStrings());
            var view = (IPickerView)window;

            view.ShowProperties(["Name: a", "AutomationId: b"]);
            Assert.Equal(2, window.PropsList.Items.Count);

            view.ShowProperties([]);
            Assert.Empty(window.PropsList.Items);
        });
    }

    /// <summary>
    /// <see cref="IPickerView.Expand"/> が「表示のための展開」であること。
    ///
    /// <para>
    /// これがこの継ぎ目の中心的な不変条件である (docs/DESIGN.md §12)。ユーザーが開いた段だけが子を全列挙し、
    /// ピッカーが経路を見せるために開いた段は列挙しない。逆にすると、ホバーのたびに
    /// 経路上の全段が兄弟を取りに行く (深さ 17 段の Chromium なら 17 往復)。
    /// </para>
    /// <para>
    /// プレゼンター側では固定済みだが、**View が本当に ExpandForDisplay を呼んでいるか**は
    /// View ごとに確かめないと分からない。IsExpanded = true と書くだけの実装でも
    /// 見た目は同じだからである。
    /// </para>
    /// </summary>
    [Fact]
    public void Expand_OpensTheRowWithoutCountingAsAUserExpansion()
    {
        Sta.Run(() =>
        {
            using TriggerPickerWindow window = CreateWindow(new FakeStrings());
            var node = new PickerTreeNode("row", new FakePickerElement("row"));
            int userExpansions = 0;
            node.ExpandedByUser += _ => userExpansions++;

            ((IPickerView)window).Expand([node]);

            Assert.True(node.IsExpanded);
            Assert.Equal(0, userExpansions);
        });
    }

    /// <summary>
    /// ツリーと右側の境界を、ユーザーが動かせるように**正しく繋いである**こと。
    ///
    /// <para>
    /// GridSplitter は置き場所を間違えても**例外にならず、動かないだけ**である —
    /// 列を取り違えれば隣の列を押し合うし、ResizeDirection の推測が外れれば掴んでも何も起きない。
    /// 「置いたのに効かない」は目で見るまで分からない類なので、繋ぎのほうを T1 で固定する。
    /// 実際に掴んで動くかどうかは docs/MANUAL-CHECKS.md §4.3.1 に置いた (合成入力は T5 の担当である)。
    /// </para>
    /// <para>
    /// MinWidth を見るのは、これが無いと**片側を 0 まで引き切れて戻せなくなる**ためである
    /// (掴み代ごと畳めてしまう)。
    /// </para>
    /// </summary>
    [Fact]
    public void TheTreeAndTheRightHandSide_AreSeparatedByAWorkingSplitter()
    {
        Sta.Run(() =>
        {
            using TriggerPickerWindow window = CreateWindow(new FakeStrings());
            GridSplitter splitter = window.TreeSplitter;
            var grid = (Grid)splitter.Parent;

            Assert.Equal(3, grid.ColumnDefinitions.Count);
            Assert.Equal(1, Grid.GetColumn(splitter));
            Assert.Equal(GridResizeDirection.Columns, splitter.ResizeDirection);
            Assert.Equal(GridResizeBehavior.PreviousAndNext, splitter.ResizeBehavior);
            Assert.True(grid.ColumnDefinitions[0].MinWidth > 0, "ツリー側に MinWidth がありません。");
            Assert.True(grid.ColumnDefinitions[2].MinWidth > 0, "右側に MinWidth がありません。");

            // キーボードだけで寸法を変えられる唯一の手段である。行の確定ボタンとは逆に、
            // ここは停留点を**残す**のが正しい (XAML の注記を参照)
            Assert.True(splitter.Focusable);
            Assert.True(splitter.IsTabStop);
        });
    }

    /// <summary>
    /// プロパティ一覧と条件欄の境界も動かせること。**ただし既定の高さは変えないこと。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// 条件欄の行を <c>Auto</c> のままにしてあるのが要点である。掴まれるまでは今日と同じ
    /// 「内容ぶんの高さ」で、掴んだ時点で <c>GridSplitter</c> が固定 px に変える。
    /// **ここを <c>*</c> にすると既定の見た目が変わり**、T4 / S4 が条件欄の中の
    /// コントロールを AutomationId で駆動している経路にそのまま効く。
    /// </para>
    /// <para>
    /// 固定 px になった後は中身がはみ出しうる (窓を狭めると <c>WrapPanel</c> が折り返して
    /// さらに伸びる) ので、<c>ScrollViewer</c> で包んである。**無いと黙って切れる。**
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePropertiesAndTheConditionFields_AreSeparatedByAWorkingSplitter()
    {
        Sta.Run(() =>
        {
            using TriggerPickerWindow window = CreateWindow(new FakeStrings());
            GridSplitter splitter = window.ConditionSplitter;
            var grid = (Grid)splitter.Parent;

            Assert.Equal(3, grid.RowDefinitions.Count);
            Assert.Equal(1, Grid.GetRow(splitter));
            Assert.Equal(GridResizeDirection.Rows, splitter.ResizeDirection);
            Assert.Equal(GridResizeBehavior.PreviousAndNext, splitter.ResizeBehavior);
            Assert.True(grid.RowDefinitions[0].MinHeight > 0, "プロパティ一覧側に MinHeight がありません。");
            Assert.True(grid.RowDefinitions[2].MinHeight > 0, "条件欄側に MinHeight がありません。");

            // 掴まれるまでは内容ぶんの高さ = 今日と同じ見た目であること
            Assert.Equal(GridUnitType.Auto, grid.RowDefinitions[2].Height.GridUnitType);

            // 縮めたときに切れないこと。ScrollViewer を外すと黙って切れる
            Assert.IsType<ScrollViewer>(
                grid.Children.Cast<UIElement>().Single(c => Grid.GetRow(c) == 2));
        });
    }

    /// <summary>
    /// 条件欄に**「区画にある高さ」が上限として渡っている**こと
    /// (docs/DESIGN.md §12)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ThePropertiesAndTheConditionFields_AreSeparatedByAWorkingSplitter"/> は
    /// <c>ScrollViewer</c> が**在ること**しか見ていない。ところが条件欄の行は
    /// <c>Auto</c> で、**<c>Auto</c> の行は子に「欲しいだけ」与える** —
    /// ビューポートが中身と同じ高さになるので、包んでいても**スクロールは一生起きない**。
    /// 窓を縮めると <c>Grid</c> が下を黙って切り落とし、[トリガーを追加] へ到達できなくなる。
    /// **「在ること」の検査はこの壊れ方をまったく覆わない。**
    /// </para>
    /// <para>
    /// 見るのは**上限の値そのもの**である。「縮めたらスクロールできること」を見たかったが、
    /// **表示していない <c>Window</c> ではレイアウトを狙った高さで回せない** —
    /// <c>UpdateLayout()</c> は <c>Window</c> から**無制約で**回り直すので、
    /// <c>Measure</c> / <c>Arrange</c> に渡した高さが効かない (実測: 200px を渡しても
    /// 区画は中身ぶんの 237.72px になった)。上限が入っていれば
    /// 「中身より小さい上限 → スクロールできる」は <c>ScrollViewer</c> の仕様であり、
    /// 実際に縮めた画面は T4 と docs/MANUAL-CHECKS.md §4.3.1 が見る。
    /// </para>
    /// <para>
    /// 上限が渡っていなければ <c>MaxHeight</c> は <c>PositiveInfinity</c> のままになる。
    /// そこも別に見ておく — 引き算を間違えた場合と、配線を忘れた場合は別の壊れ方である。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheConditionFields_AreCappedToTheHeightThePaneActuallyHas()
    {
        Sta.Run(() =>
        {
            using TriggerPickerWindow window = CreateWindow(new FakeStrings());
            Layout((FrameworkElement)window.Content, 1100, 700);

            Grid pane = window.MainPane;
            ScrollViewer scroll = window.ConditionScroll;
            string measured =
                $"区画 {pane.ActualHeight}px / 区切り {window.ConditionSplitter.ActualHeight}px / " +
                $"上段の最小 {pane.RowDefinitions[0].MinHeight}px / 上限 {scroll.MaxHeight}";

            Assert.False(
                double.IsPositiveInfinity(scroll.MaxHeight),
                $"条件欄に上限が渡っていません (配線が外れています)。{measured}");

            double expected =
                pane.ActualHeight - pane.RowDefinitions[0].MinHeight - window.ConditionSplitter.ActualHeight;
            Assert.True(expected > 0, $"高さを測れていません。{measured}");
            Assert.Equal(expected, scroll.MaxHeight, tolerance: 1.0);
        });
    }

    /// <summary>窓を出さずにレイアウトを走らせる。</summary>
    /// <remarks>
    /// **2 周回す。**WPF は <c>SizeChanged</c> をレイアウト 1 周の**終わりに**上げるので、
    /// その中で寸法を変えるハンドラー (条件欄の上限) の結果は次の周でしか出ない。
    /// 実アプリではディスパッチャーが次の周を回すが、ここは手で回しているので自分で回す。
    /// </remarks>
    private static void Layout(FrameworkElement root, double width, double height)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
        }
    }

    /// <summary>
    /// 区切りが読み上げ名を持つこと。
    /// </summary>
    /// <remarks>
    /// 表示文字列を持たないコントロールなので、名前が無いと WPF の GridSplitter は
    /// 「Thumb」としか名乗らない。<see cref="TheViewAsksForEveryLabelInTheKeyTable"/> は
    /// 「キーを引いたか」までしか見ないので、**代入先**のほうをここで押さえる。
    /// </remarks>
    [Fact]
    public void TheSplitterCarriesAnAccessibleName()
    {
        Sta.Run(() =>
        {
            var strings = new FakeStrings();
            strings.Values[PickerStringKeys.TreeSplitterAutomationName] = "区切り";
            strings.Values[PickerStringKeys.ConditionSplitterAutomationName] = "もう 1 つの区切り";
            using TriggerPickerWindow window = CreateWindow(strings);

            Assert.Equal("区切り", System.Windows.Automation.AutomationProperties.GetName(window.TreeSplitter));
            Assert.Equal(
                "もう 1 つの区切り",
                System.Windows.Automation.AutomationProperties.GetName(window.ConditionSplitter));
        });
    }

    /// <summary>解放は冪等であること (ウィンドウを閉じた経路と明示呼び出しが重なる)。</summary>
    [Fact]
    public void Dispose_CanBeCalledMoreThanOnce()
        => Sta.Run(() =>
        {
            TriggerPickerWindow window = CreateWindow(new FakeStrings());
            window.Dispose();
            window.Dispose();
        });
}
