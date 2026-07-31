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
