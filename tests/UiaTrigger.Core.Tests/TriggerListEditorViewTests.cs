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
