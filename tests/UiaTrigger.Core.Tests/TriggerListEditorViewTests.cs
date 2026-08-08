// トリガ一覧エディタの View (継ぎ目の向こう側) の回帰テスト (docs/DESIGN.md §4)。
//
// WPF と Windows Forms の View は net10.0-windows / AnyCPU なので、ピッカーの View と同じく
// ここで直接組み立てられる (docs/DESIGN.md §12 —「View はテストできない」は誤り)。
// WinUI 版だけは x64 / WindowsAppSDK のため T1 から実体化できず、T4 の担当である。
//
// ShowDialog は使わない。入れ子のメッセージループを回すのでテストが返らなくなる。
// 確定の経路は internal な Accept / Result を直に叩いて見る。
using System.Globalization;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Extensions.Time.Testing;
using UiaTrigger.Models;
using UiaTrigger.Picker;
using UiaTrigger.Picker.WinForms;
using UiaTrigger.Picker.Wpf;
using Xunit;

namespace UiaTrigger.Tests;

public sealed class TriggerListEditorViewTests
{
    /// <summary>エディタが使うラベルのキー ('.' を含むもの) + ウィンドウタイトル。</summary>
    private static string[] LabelKeys => [.. EditorStringKeys.All
        .Where(k => k.Contains('.', StringComparison.Ordinal) || k == EditorStringKeys.WindowTitle)];

    private static TriggerDefinition Simple(string id) => new()
    {
        Id = id,
        DisplayName = id.ToUpperInvariant(),
        Window = new WindowIdentity { ProcessName = id + ".exe" },
        On = TriggerOn.PropertyChanged,
        Clauses = [new PropertyClause { Property = TriggerProperty.Name, Op = ComparisonOp.Equals, Text = id }],
    };

    /// <summary>実物の UIA セッションとオーバーレイを作らせずに子ピッカーを組み立てる。</summary>
    private static TriggerPickerWindow FakeWpfPicker()
    {
        var strings = new FakeStrings();
        return new TriggerPickerWindow(strings, view => new TriggerPickerPresenter(
            view, new FakeDispatcher(), new FakeCursor(), strings,
            new FakePickerServices(), new FakeOverlay(), new FakeTimeProvider(), new FakeDpiSource()));
    }

    private static TriggerPickerForm FakeWinFormsPicker()
    {
        var strings = new FakeStrings();
        return new TriggerPickerForm(strings, view => new TriggerPickerPresenter(
            view, new FakeDispatcher(), new FakeCursor(), strings,
            new FakePickerServices(), new FakeOverlay(), new FakeTimeProvider(), new FakeDpiSource()));
    }

    // ---------- WPF ----------

    /// <summary>
    /// View が**すべての**ラベル用キーを要求すること。
    /// コントロールを足してラベルの代入を忘れると、例外にならず欄が空のまま出る。
    /// </summary>
    [Fact]
    public void TheWpfViewAsksForEveryLabelInTheKeyTable()
    {
        Sta.Run(() =>
        {
            var strings = new FakeStrings();
            var window = new TriggerListEditorWindow(strings, [], createPresenter: null, FakeWpfPicker);
            try
            {
                string[] missing = [.. LabelKeys
                    .Except(strings.Requested, StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)];

                Assert.True(
                    missing.Length == 0,
                    $"WPF のエディタが要求しなかったラベル: {string.Join(", ", missing)}。" +
                    "そのコントロールは空欄のまま表示されます。");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>引いた文字列が実際にコントロールへ載っていること。</summary>
    [Fact]
    public void TheResolvedStringsLandOnTheWpfControls()
    {
        Sta.Run(() =>
        {
            CultureInfo original = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = new CultureInfo("en-US");
                var window = new TriggerListEditorWindow(
                    new ResxPickerStrings(), [], createPresenter: null, FakeWpfPicker);
                try
                {
                    Assert.Equal("Edit triggers — UiaTrigger", window.Title);
                    Assert.Equal("OK", window.AcceptButton.Content);
                    Assert.Equal("Take apart", window.DecomposeTriggerButton.Content);
                    // 一覧は見出しを持たないので、読み上げ名が唯一の手がかりである
                    Assert.Equal("Triggers", AutomationProperties.GetName(window.EditorTriggerList));
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                CultureInfo.CurrentUICulture = original;
            }
        });
    }

    /// <summary>一覧に行が出ること (presenter が構築時に描いたもの)。</summary>
    [Fact]
    public void TheWpfViewShowsARowPerTrigger()
    {
        Sta.Run(() =>
        {
            var window = new TriggerListEditorWindow(
                new ResxPickerStrings(), [Simple("a"), Simple("b")], createPresenter: null, FakeWpfPicker);
            try
            {
                Assert.Equal(2, window.EditorTriggerList.Items.Count);
                Assert.Contains("[a]", (string)window.EditorTriggerList.Items[0]!, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 選択が添字として presenter に届くこと。
    ///
    /// <c>ListBox.SelectedItems</c> は行の文字列を返すので、そこから添字へ戻す配線が要る。
    /// 間違えると「別の行を消す」形で壊れ、例外は出ない。
    /// </summary>
    [Fact]
    public void TheWpfViewReportsTheSelectedIndices()
    {
        Sta.Run(() =>
        {
            var window = new TriggerListEditorWindow(
                new ResxPickerStrings(), [Simple("a"), Simple("b"), Simple("c")],
                createPresenter: null, FakeWpfPicker);
            try
            {
                window.EditorTriggerList.SelectedIndex = 2;

                Assert.Equal([2], ((ITriggerListEditorView)window).SelectedIndices);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// ポーリング間隔の欄の読み取り: 数値は値、空欄と読めない入力は null (= 値なし)。
    /// 0 を返す形に変わると、打ち間違いが「0 秒」として Compose に届く。
    /// </summary>
    [Fact]
    public void TheWpfViewReadsTheCombinePollInterval()
    {
        Sta.Run(() =>
        {
            var window = new TriggerListEditorWindow(
                new ResxPickerStrings(), [], createPresenter: null, FakeWpfPicker);
            try
            {
                var view = (ITriggerListEditorView)window;
                Assert.Null(view.CombinePollIntervalSeconds);

                window.CombinePollIntervalBox.Text = 1.5.ToString(CultureInfo.CurrentCulture);
                Assert.Equal(1.5, view.CombinePollIntervalSeconds);

                window.CombinePollIntervalBox.Text = "abc";
                Assert.Null(view.CombinePollIntervalSeconds);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 下段 (まとめる / 状態 / 確定) に「使える高さ」の上限が渡っていること。
    ///
    /// <para>
    /// ピッカーの <c>TheConditionFields_AreCappedToTheHeightThePaneActuallyHas</c> と同型である。
    /// Auto の行は子に「欲しいだけ」与えるので、上限が渡らないと ScrollViewer の
    /// ビューポートが中身と同じ高さになり**スクロールが一生起きない** — 窓を縮めると
    /// OK/キャンセルが窓の外へ黙って切れる (T6 で実際に出た)。
    /// </para>
    /// <para>
    /// 見るのは上限の値そのもの。「縮めたらスクロールできること」は表示していない
    /// Window ではレイアウトを狙った高さで回せない (ピッカー側の実測)。
    /// 上限が中身より小さければスクロールできるのは ScrollViewer の仕様であり、
    /// 実際に縮めた画面は docs/MANUAL-CHECKS.md §4.3.5 が見る。
    /// </para>
    /// </summary>
    [Fact]
    public void TheWpfEditorsLowerPane_IsCappedToTheHeightTheWindowActuallyHas()
    {
        Sta.Run(() =>
        {
            var window = new TriggerListEditorWindow(
                new ResxPickerStrings(), [Simple("a")], createPresenter: null, FakeWpfPicker);
            try
            {
                var root = (System.Windows.FrameworkElement)window.Content;
                for (int pass = 0; pass < 2; pass++)
                {
                    root.Measure(new System.Windows.Size(900, 560));
                    root.Arrange(new System.Windows.Rect(0, 0, 900, 560));
                    root.UpdateLayout();
                }

                string measured =
                    $"上段 {window.TopBar.ActualHeight}px / 一覧の最小 {window.Root.RowDefinitions[1].MinHeight}px / " +
                    $"上限 {window.LowerPane.MaxHeight}";
                Assert.False(
                    double.IsPositiveInfinity(window.LowerPane.MaxHeight),
                    $"下段に上限が渡っていません (配線が外れています)。{measured}");

                double expected = window.Root.ActualHeight - window.TopBar.ActualHeight
                    - window.TopBar.Margin.Bottom - window.Root.RowDefinitions[1].MinHeight;
                Assert.True(expected > 0, $"高さを測れていません。{measured}");
                Assert.Equal(expected, window.LowerPane.MaxHeight, tolerance: 1.0);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// [OK] が編集後のリストを返し、閉じただけなら null のままであること。
    ///
    /// null は「プロパティを設定しない」を意味する (<c>TriggerListEditor</c> の規約)。
    /// [OK] を押していないのにリストが返ると、**取り消したはずの編集が保存される**。
    /// </summary>
    [Fact]
    public void TheWpfViewReturnsTheListOnlyAfterAccepting()
    {
        Sta.Run(() =>
        {
            var window = new TriggerListEditorWindow(
                new ResxPickerStrings(), [Simple("a")], createPresenter: null, FakeWpfPicker);
            try
            {
                Assert.Null(window.Result);

                window.Accept();

                Assert.NotNull(window.Result);
                Assert.Equal(["a"], window.Result.Select(t => t.Id));
            }
            finally
            {
                window.Close();
            }
        });
    }

    // 子ピッカーを開く経路 (ShowPicker) はここでは扱わない。**窓の表示が要る**ためである —
    // Show() / Show(owner) を呼ぶことになり、可視のデスクトップを要する検査は T1 の担当ではない
    // (docs/TESTING.md §2 の横断ルール。WPF のコンテナ実体化を T1 から外したのと同じ線)。
    // 追加 → コミット → 一覧に出る、までを通すのは T4 (EditorShowcaseTests) である。
    // presenter 側の配線 (NotifyPickerCommitted の差し替え規則) は
    // TriggerListEditorPresenterTests が覆っている。

    // ---------- Windows Forms ----------

    [Fact]
    public void TheWinFormsViewAsksForEveryLabelInTheKeyTable()
    {
        Sta.Run(() =>
        {
            var strings = new FakeStrings();
            using var form = new TriggerListEditorForm(strings, [], createPresenter: null, FakeWinFormsPicker);

            string[] missing = [.. LabelKeys
                .Except(strings.Requested, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];

            Assert.True(
                missing.Length == 0,
                $"Windows Forms のエディタが要求しなかったラベル: {string.Join(", ", missing)}。");
        });
    }

    [Fact]
    public void TheResolvedStringsLandOnTheWinFormsControls()
    {
        Sta.Run(() =>
        {
            CultureInfo original = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = new CultureInfo("en-US");
                using var form = new TriggerListEditorForm(
                    new ResxPickerStrings(), [], createPresenter: null, FakeWinFormsPicker);

                Assert.Equal("Edit triggers — UiaTrigger", form.Text);
                Control[] all = [.. form.Controls.Cast<Control>().SelectMany(Descendants)];
                Assert.Contains(all, c => c is Button { Name: "AcceptButton", Text: "OK" });
                Assert.Contains(all, c => c is Button { Name: "DecomposeTriggerButton", Text: "Take apart" });
                // 一覧は見出しを持たないので、読み上げ名が唯一の手がかりである
                Assert.Contains(all, c => c is ListBox { Name: "EditorTriggerList", AccessibleName: "Triggers" });
            }
            finally
            {
                CultureInfo.CurrentUICulture = original;
            }
        });
    }

    /// <summary>
    /// 下段の入力欄に居るあいだの Enter は [まとめる/更新] で止まり、[OK] へ渡らないこと
    /// (docs/DESIGN.md A25 / §4)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 下段の 3 欄は押して初めて効く — <c>Snapshot</c> は式を読まない。既定ボタンに流すと
    /// **打ちかけの式を捨てて窓が閉じる**。しかもこの変種では単一行 <c>TextBox</c> の Enter が
    /// <c>KeyDown</c> より先に <c>ProcessDialogKey</c> を通るので、<c>KeyDown</c> ハンドラーでは
    /// そもそも受けられない (ピッカーの検索欄と同じ形)。
    /// </para>
    /// <para>
    /// 見るのは 3 つ: 止まったこと (返り値) / presenter まで届いたこと (状態行が書かれた) /
    /// 確定していないこと (<c>Result</c> が null のまま)。3 つ目が無いと、
    /// 「まとめてから閉じる」形の退行を見逃す。
    /// </para>
    /// <para>
    /// 退行: <c>HandleDialogKey</c> の <c>combineFieldHasFocus</c> の判定を外して常に false にする。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheWinFormsEditor_TakesEnterInACombineField_ForCombineNotAccept()
    {
        Sta.Run(() =>
        {
            using var form = new TriggerListEditorForm(
                new ResxPickerStrings(), [Simple("a"), Simple("b")],
                createPresenter: null, FakeWinFormsPicker);
            Control[] all = [.. form.Controls.Cast<Control>().SelectMany(Descendants)];
            var status = (Label)Assert.Single(all, c => c.Name == "EditorStatus");
            var accept = (Button)Assert.Single(all, c => c.Name == "AcceptButton");

            // 既定ボタンは立っている (この検査に検出力があることを示す)
            Assert.Same(accept, form.AcceptButton);

            status.Text = string.Empty;
            Assert.True(form.HandleDialogKey(Keys.Enter, combineFieldHasFocus: true));
            // 何も選ばずに押したので「2 件以上要る」で断られる。文言はどうあれ、
            // 状態行が書かれたことが presenter まで届いた証拠である
            Assert.NotEqual(string.Empty, status.Text);
            Assert.Null(form.Result);

            // 欄の外なら既定ボタンへ渡す
            Assert.False(form.HandleDialogKey(Keys.Enter, combineFieldHasFocus: false));
        });
    }

    [Fact]
    public void TheWinFormsViewShowsARowPerTriggerAndReportsTheSelection()
    {
        Sta.Run(() =>
        {
            using var form = new TriggerListEditorForm(
                new ResxPickerStrings(), [Simple("a"), Simple("b"), Simple("c")],
                createPresenter: null, FakeWinFormsPicker);
            ListBox list = Assert.Single(
                form.Controls.Cast<Control>().SelectMany(Descendants).OfType<ListBox>(),
                c => c.Name == "EditorTriggerList");

            Assert.Equal(3, list.Items.Count);
            list.SelectedIndex = 1;

            Assert.Equal([1], ((ITriggerListEditorView)form).SelectedIndices);
        });
    }

    /// <summary>ポーリング間隔の欄の読み取り (WPF 側と同じ規則)。</summary>
    [Fact]
    public void TheWinFormsViewReadsTheCombinePollInterval()
    {
        Sta.Run(() =>
        {
            using var form = new TriggerListEditorForm(
                new ResxPickerStrings(), [], createPresenter: null, FakeWinFormsPicker);
            TextBox box = Assert.Single(
                form.Controls.Cast<Control>().SelectMany(Descendants).OfType<TextBox>(),
                c => c.Name == "CombinePollIntervalBox");
            var view = (ITriggerListEditorView)form;

            Assert.Null(view.CombinePollIntervalSeconds);

            box.Text = 1.5.ToString(CultureInfo.CurrentCulture);
            Assert.Equal(1.5, view.CombinePollIntervalSeconds);

            box.Text = "abc";
            Assert.Null(view.CombinePollIntervalSeconds);
        });
    }

    [Fact]
    public void TheWinFormsViewReturnsTheListOnlyAfterAccepting()
    {
        Sta.Run(() =>
        {
            using var form = new TriggerListEditorForm(
                new ResxPickerStrings(), [Simple("a")], createPresenter: null, FakeWinFormsPicker);

            Assert.Null(form.Result);

            form.Accept();

            Assert.NotNull(form.Result);
            Assert.Equal(["a"], form.Result.Select(t => t.Id));
        });
    }

    // ---------- 複合の書き換え (継ぎ目の新しい口) ----------

    /// <summary>
    /// 下段の欄が本物のコントロールを通して往復すること。
    ///
    /// <para>
    /// presenter 側の恒等テストは素の代入しか通らないので、実際に壊れうる箇所 —
    /// 数値の書式 (読みと書きのカルチャの対) と「空欄 = 値なし」の約束 — はここでしか見られない。
    /// </para>
    /// <para>
    /// **小数点がカンマのカルチャで走らせる。**開発機の既定 (en-US / invariant) のままだと、
    /// 書きだけ <c>InvariantCulture</c> に変えても同じ文字列になり、対のずれを検出できない。
    /// </para>
    /// </summary>
    [Fact]
    public void TheWpfViewRoundTripsTheCombineFields()
    {
        Sta.Run(() =>
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var window = new TriggerListEditorWindow(
                new ResxPickerStrings(), [], createPresenter: null, FakeWpfPicker);
            try
            {
                var view = (ITriggerListEditorView)window;

                view.ExpressionText = "a && b";
                view.UnwatchedText = "b, c";
                view.CombinePollIntervalSeconds = 2.5;
                view.CombineNotifyOnStoppedMatching = true;
                view.CombineCaption = "更新";

                Assert.Equal("a && b", view.ExpressionText);
                Assert.Equal("b, c", view.UnwatchedText);
                Assert.Equal(2.5, view.CombinePollIntervalSeconds);
                Assert.True(view.CombineNotifyOnStoppedMatching);
                Assert.Equal("更新", window.CombineTriggersButton.Content);

                // 「値なし」は空欄へ戻ること (0 に化けると打ち間違いが 0 秒として届く)
                view.CombinePollIntervalSeconds = null;
                Assert.Null(view.CombinePollIntervalSeconds);
                Assert.Equal(string.Empty, window.CombinePollIntervalBox.Text);
            }
            finally
            {
                window.Close();
                CultureInfo.CurrentCulture = original;
            }
        });
    }

    /// <summary>
    /// 自分で一覧を差し替えたときの選択変化を presenter へ報告しないこと。
    /// 報告すると、いずれ「欄を埋め直す」経路がそこに乗った日に打ちかけの式が消える。
    /// </summary>
    [Fact]
    public void TheWpfViewDoesNotReportASelectionChangeItCausedItself()
    {
        Sta.Run(() =>
        {
            RecordingEditorView? recorder = null;
            var window = new TriggerListEditorWindow(
                new ResxPickerStrings(),
                [Simple("a"), Simple("b")],
                v => new TriggerListEditorPresenter(
                    recorder = new RecordingEditorView(v), new ResxPickerStrings(),
                    [Simple("a"), Simple("b")]),
                FakeWpfPicker);
            try
            {
                // (a) ユーザーの選択は届くこと — これが無いとこのテストは空になる
                int before = recorder!.SelectionReads;
                window.EditorTriggerList.SelectedIndex = 0;
                Assert.True(recorder.SelectionReads > before, "選択が presenter に届いていない");

                // (b) 自分で差し替えたときは届かないこと
                before = recorder.SelectionReads;
                ((ITriggerListEditorView)window).ShowRows(["x", "y"]);
                Assert.Equal(before, recorder.SelectionReads);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// **恒等を本物のコントロールで。**複合を選んで、何も直さずに押すと定義が変わらないこと。
    /// ここが崩れる原因は presenter には無く、欄の書式 (数値のカルチャ・区切り文字) にある。
    /// </summary>
    [Fact]
    public void TheWpfViewUpdatesACompositeWithoutChangingIt()
    {
        Sta.Run(() =>
        {
            // 小数点がカンマのカルチャで走らせる (往復テストと同じ理由)
            CultureInfo original = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var window = new TriggerListEditorWindow(
                new ResxPickerStrings(), [RichComposite()], createPresenter: null, FakeWpfPicker);
            try
            {
                window.Accept();
                string before = JsonList(window.Result!);

                window.EditorTriggerList.SelectedIndex = 0;
                window.CombineTriggersButton.RaiseEvent(
                    new System.Windows.RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                // 押せていることを先に確かめる (押されていなければ「変わらない」は当たり前)
                Assert.NotEmpty(window.EditorStatus.Text);

                window.Accept();
                Assert.Equal(before, JsonList(window.Result!));
            }
            finally
            {
                window.Close();
                CultureInfo.CurrentCulture = original;
            }
        });
    }

    /// <summary>WPF 側と同じ規則。カルチャを変える理由もそちらと同じ。</summary>
    [Fact]
    public void TheWinFormsViewRoundTripsTheCombineFields()
    {
        Sta.Run(() =>
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            try
            {
            using var form = new TriggerListEditorForm(
                new ResxPickerStrings(), [], createPresenter: null, FakeWinFormsPicker);
            Control[] all = [.. form.Controls.Cast<Control>().SelectMany(Descendants)];
            var view = (ITriggerListEditorView)form;

            view.ExpressionText = "a && b";
            view.UnwatchedText = "b, c";
            view.CombinePollIntervalSeconds = 2.5;
            view.CombineNotifyOnStoppedMatching = true;
            view.CombineCaption = "更新";

            Assert.Equal("a && b", view.ExpressionText);
            Assert.Equal("b, c", view.UnwatchedText);
            Assert.Equal(2.5, view.CombinePollIntervalSeconds);
            Assert.True(view.CombineNotifyOnStoppedMatching);
            Assert.Contains(all, c => c is Button { Name: "CombineTriggersButton", Text: "更新" });

            view.CombinePollIntervalSeconds = null;
            Assert.Null(view.CombinePollIntervalSeconds);
            Assert.Contains(all, c => c is TextBox { Name: "CombinePollIntervalBox", Text: "" });
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        });
    }

    /// <summary>
    /// ユーザーの選択が presenter へ届くこと。
    ///
    /// <para>
    /// WPF 側にある「自分で起こした選択変化は報告しない」の対はここに置かない —
    /// <c>ListBox.Items.Clear()</c> は <c>SelectedIndexChanged</c> を**鳴らさない**
    /// (実測: ハンドル有りでも 0 回。<c>LB_RESETCONTENT</c> は通知を親へ返さない)。
    /// 鳴らないものを見るテストは常に緑になるので置かない。View 側に抑止を残す理由は
    /// <c>TriggerListEditorForm.ShowRows</c> のコメントにある。
    /// </para>
    /// </summary>
    [Fact]
    public void TheWinFormsViewReportsTheUsersSelectionChange()
    {
        Sta.Run(() =>
        {
            RecordingEditorView? recorder = null;
            using var form = new TriggerListEditorForm(
                new ResxPickerStrings(),
                [Simple("a"), Simple("b")],
                v => new TriggerListEditorPresenter(
                    recorder = new RecordingEditorView(v), new ResxPickerStrings(),
                    [Simple("a"), Simple("b")]),
                FakeWinFormsPicker);
            ListBox list = Assert.Single(
                form.Controls.Cast<Control>().SelectMany(Descendants).OfType<ListBox>(),
                c => c.Name == "EditorTriggerList");

            int before = recorder!.SelectionReads;
            list.SelectedIndex = 0;

            Assert.True(recorder.SelectionReads > before, "選択が presenter に届いていない");
        });
    }

    [Fact]
    public void TheWinFormsViewUpdatesACompositeWithoutChangingIt()
    {
        Sta.Run(() =>
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            try
            {
                using var form = new TriggerListEditorForm(
                    new ResxPickerStrings(), [RichComposite()], createPresenter: null, FakeWinFormsPicker);
                Control[] all = [.. form.Controls.Cast<Control>().SelectMany(Descendants)];
                ListBox list = Assert.Single(all.OfType<ListBox>(), c => c.Name == "EditorTriggerList");
                Button combine = Assert.Single(all.OfType<Button>(), c => c.Name == "CombineTriggersButton");

                form.Accept();
                string before = JsonList(form.Result!);

                // PerformClick は見えていないフォームでは何もしない (CanSelect が false)。
                // ピッカーの Windows Forms テストと同じく先に出す。
                // **画面の外へ退かす** — 既定位置で出すと T4/T5 の座標をまたぐ
                form.Location = new System.Drawing.Point(-32000, -32000);
                form.Show();
                list.SelectedIndex = 0;
                combine.PerformClick();

                // 押せていることを先に確かめる。押されていなければ「変わらない」は当たり前で、
                // このテストは何も見ていないことになる
                Label status = Assert.Single(all.OfType<Label>(), c => c.Name == "EditorStatus");
                Assert.NotEmpty(status.Text);

                form.Accept();
                Assert.Equal(before, JsonList(form.Result!));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        });
    }

    /// <summary>
    /// 本物のチェックボックスを操作してまとめると、複合に立ち下がり通知が乗ること。
    /// T6 でここが「乗らない」と報告された経路をそのまま辿る。
    /// </summary>
    [Fact]
    public void TheWpfViewCombinesWithTheStoppedMatchingCheckbox()
    {
        Sta.Run(() =>
        {
            var window = new TriggerListEditorWindow(
                new ResxPickerStrings(), [Simple("a"), Simple("b")], createPresenter: null, FakeWpfPicker);
            try
            {
                // **選んでからチェックを入れる。**行を選ぶと下段は空に戻る (選択が下段の主) ので、
                // 順番が逆だとチェックは消える。実際の操作もこの順である
                window.EditorTriggerList.SelectAll();
                // 人がチェックを入れるのと同じ経路 (継ぎ目ではなくコントロールを触る)
                window.CombineStoppedMatchingCheck.IsChecked = true;
                window.CombineTriggersButton.RaiseEvent(
                    new System.Windows.RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                window.Accept();
                TriggerDefinition composite = Assert.Single(
                    window.Result!, t => t.Id.StartsWith("composite", StringComparison.Ordinal));
                Assert.True(composite.NotifyOnStoppedMatching);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheWinFormsViewCombinesWithTheStoppedMatchingCheckbox()
    {
        Sta.Run(() =>
        {
            using var form = new TriggerListEditorForm(
                new ResxPickerStrings(), [Simple("a"), Simple("b")], createPresenter: null, FakeWinFormsPicker);
            Control[] all = [.. form.Controls.Cast<Control>().SelectMany(Descendants)];
            ListBox list = Assert.Single(all.OfType<ListBox>(), c => c.Name == "EditorTriggerList");
            Button combine = Assert.Single(all.OfType<Button>(), c => c.Name == "CombineTriggersButton");
            CheckBox check = Assert.Single(
                all.OfType<CheckBox>(), c => c.Name == "CombineStoppedMatchingCheck");

            // 画面の外へ退かしてから出す (上のテストと同じ理由)
            form.Location = new System.Drawing.Point(-32000, -32000);
            form.Show();
            // 選んでからチェックを入れる (WPF 側と同じ理由)
            list.SelectedIndices.Add(0);
            list.SelectedIndices.Add(1);
            check.Checked = true;
            combine.PerformClick();

            form.Accept();
            TriggerDefinition composite = Assert.Single(
                form.Result!, t => t.Id.StartsWith("composite", StringComparison.Ordinal));
            Assert.True(composite.NotifyOnStoppedMatching);
        });
    }

    /// <summary>複数句・絞るだけ・式・ポーリング間隔・立ち下がり通知の全部入りの複合。</summary>
    private static TriggerDefinition RichComposite()
    {
        TriggerDefinition login = Simple("login");
        login.Clauses.Add(new PropertyClause
        {
            Property = TriggerProperty.Value, Op = ComparisonOp.GreaterThan, Value = 1,
        });
        TriggerCompositionResult result = TriggerComposer.Compose(
            [login, Simple("b")],
            "(login-1 && login-2) || b",
            ["login"],
            [],
            // 小数を含めるのは、欄への往復でカルチャがずれたら落ちるようにするためである
            TimeSpan.FromSeconds(2.5),
            notifyOnStoppedMatching: true);
        Assert.True(result.IsValid);
        return result.Definition!;
    }

    private static string JsonList(IReadOnlyList<TriggerDefinition> triggers) =>
        string.Join("\n", triggers.Select(t => System.Text.Json.JsonSerializer.Serialize(
            t, UiaTrigger.Serialization.TriggerJsonContext.Default.TriggerDefinition)));

    /// <summary>
    /// View と presenter の間に挟んで、presenter が選択を読んだ回数を数える。
    /// </summary>
    /// <remarks>
    /// 「報告されたか」を直に数える口が無いので、<see cref="ITriggerListEditorView.SelectedIndices"/>
    /// の読みで代用する — <c>NotifySelectionChanged</c> は必ずここを通る。
    /// </remarks>
    private sealed class RecordingEditorView(ITriggerListEditorView inner) : ITriggerListEditorView
    {
        public int SelectionReads { get; private set; }

        public string Status { set => inner.Status = value; }

        public string ExpressionText
        {
            get => inner.ExpressionText;
            set => inner.ExpressionText = value;
        }

        public string UnwatchedText
        {
            get => inner.UnwatchedText;
            set => inner.UnwatchedText = value;
        }

        public double? CombinePollIntervalSeconds
        {
            get => inner.CombinePollIntervalSeconds;
            set => inner.CombinePollIntervalSeconds = value;
        }

        public bool CombineNotifyOnStoppedMatching
        {
            get => inner.CombineNotifyOnStoppedMatching;
            set => inner.CombineNotifyOnStoppedMatching = value;
        }

        public string CombineCaption { set => inner.CombineCaption = value; }

        public IReadOnlyList<int> SelectedIndices
        {
            get
            {
                SelectionReads++;
                return inner.SelectedIndices;
            }
        }

        public void SelectRow(int index) => inner.SelectRow(index);

        public void ShowRows(IReadOnlyList<string> rows) => inner.ShowRows(rows);

        public void ShowPicker(TriggerDefinition? definitionToEdit) => inner.ShowPicker(definitionToEdit);
    }

    private static IEnumerable<Control> Descendants(Control control)
    {
        yield return control;
        foreach (Control child in control.Controls)
        {
            foreach (Control nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }
}
