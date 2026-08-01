// Windows Forms View (継ぎ目の向こう側) の回帰テスト (docs/DESIGN.md §12)。
//
// WPF 版と違い、こちらは**ツリーの実体化にもウィンドウの表示が要らない**。
// Windows Forms の TreeView に仮想化が無く、TreeNode は追加した時点で存在するためである。
// その代わりモデルとの同期を自前で書く必要があり (TreeMirror)、3 変種のうち
// ここにしか無いコードなのでいちばん厚く見る。
using System.Globalization;
using System.Windows.Forms;
using Microsoft.Extensions.Time.Testing;
using UiaTrigger.Models;
using UiaTrigger.Picker;
using UiaTrigger.Picker.WinForms;
using Xunit;

namespace UiaTrigger.Tests;

public sealed class TriggerPickerWinFormsTests
{
    private static TriggerPickerForm CreateForm(IPickerStrings strings)
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
    /// 代入だけして <c>CheckedChanged</c> を経由しない書き方に変えると、画面は「OFF」と出るのに
    /// 捕捉は生きたままになり、症状 (2 枚目のマウスに 1 枚目が追随する) は何も変わらない。
    /// </para>
    /// <para>
    /// 開いた直後が ON であることを先に見るのは、**この検査に検出力があることを示すため**である。
    /// 既定が OFF に変わったら、下の assert は何も確かめないまま緑になる。
    /// </para>
    /// </summary>
    [Fact]
    public void StopAutoSelect_StopsHoverCaptureAtThePresenter()
    {
        var overlay = new FakeOverlay();
        var strings = new FakeStrings();
        using TriggerPickerForm form = new(strings, view => new TriggerPickerPresenter(
            view,
            new FakeDispatcher(),
            new FakeCursor(),
            strings,
            new FakePickerServices(),
            overlay,
            new FakeTimeProvider(),
            new FakeDpiSource()));

        Assert.True(overlay.HookEnabled, "ピッカーは自動選択 ON で開くはずである (既定が変わっている)。");

        form.StopAutoSelect();

        Assert.False(
            overlay.HookEnabled,
            "StopAutoSelect がプレゼンターまで届いていません。画面は OFF でも捕捉は生きています。");
    }

    /// <summary>
    /// View が**すべての**コントロール用キーを要求すること。
    /// コントロールを足してラベルの代入を忘れると、例外にならず欄が空のまま出る。
    /// </summary>
    [Fact]
    public void TheViewAsksForEveryLabelInTheKeyTable()
    {
        Sta.Run(() =>
        {
            var strings = new FakeStrings();
            using TriggerPickerForm form = CreateForm(strings);

            string[] expected = [.. PickerStringKeys.All
                .Where(k => k.Contains('.', StringComparison.Ordinal) || k == PickerStringKeys.WindowTitle)];
            string[] missing = [.. expected
                .Except(strings.Requested, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];

            Assert.True(
                missing.Length == 0,
                $"Windows Forms View が要求しなかったラベル: {string.Join(", ", missing)}。");
        });
    }

    /// <summary>引いた文字列が実際にコントロールへ載っていること。</summary>
    [Fact]
    public void TheResolvedStringsLandOnTheControls()
    {
        Sta.Run(() =>
        {
            CultureInfo original = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = new CultureInfo("en-US");
                using TriggerPickerForm form = CreateForm(new ResxPickerStrings());

                Assert.Equal("Pick an element — UiaTrigger", form.Text);
                Assert.Contains(
                    form.Controls.Cast<Control>().SelectMany(Descendants),
                    c => c is Button { Text: "Add trigger" });
            }
            finally
            {
                CultureInfo.CurrentUICulture = original;
            }
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

    /// <summary>空欄は「値なし」であること。</summary>
    [Fact]
    public void ReadDraft_WithEmptyNumberBoxes_LeavesTheOperandsUnset()
    {
        Sta.Run(() =>
        {
            using TriggerPickerForm form = CreateForm(new FakeStrings());
            var view = (IPickerView)form;
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
    /// 0 は正当な値なので、打ち間違いを 0 に丸めると誰も書いていない条件が保存される。
    /// </summary>
    [Fact]
    public void ReadNumber_WithUnparsableText_IsUnsetRatherThanZero()
        => Sta.Run(() =>
        {
            using var box = new TextBox { Text = "abc" };
            Assert.Null(TriggerPickerForm.ReadNumber(box));
        });

    /// <summary>数値は現在のカルチャで解釈すること (docs/DESIGN.md L7)。</summary>
    [Fact]
    public void ReadNumber_ReadsNumbersInTheCurrentCulture()
    {
        Sta.Run(() =>
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            try
            {
                using var box = new TextBox();

                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                box.Text = "1,5";
                Assert.Equal(1.5, TriggerPickerForm.ReadNumber(box));

                CultureInfo.CurrentCulture = new CultureInfo("en-US");
                box.Text = "1.5";
                Assert.Equal(1.5, TriggerPickerForm.ReadNumber(box));
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
    /// </summary>
    [Fact]
    public void ShowDraftThenReadDraft_RoundTripsEveryField()
    {
        Sta.Run(() =>
        {
            using TriggerPickerForm form = CreateForm(new FakeStrings());
            var view = (IPickerView)form;
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
    /// 出す欄と出さない欄が <see cref="OperandVisibility"/> のとおりであること。
    ///
    /// ここがずれると**ユーザーに見えない欄の値が条件に入る**。出し分けの規則そのものは
    /// プレゼンター側 (T1 で別途固定) の担当で、ここで見るのは「言われたとおりに
    /// 出し入れしているか」である (WPF 版と同じ検査)。
    /// </summary>
    /// <remarks>
    /// Windows Forms の <c>Control.Visible</c> は**実効値**で、親のフォームが表示されて
    /// いないと常に false を返す (WPF の <c>Visibility</c> が設定値をそのまま返すのと違う)。
    /// なのでここだけはフォームを**画面外に**出す — 出さないと、隠す側の assert が
    /// 全部素通りして検出力が無くなる。
    /// </remarks>
    [Fact]
    public void ShowOperands_ShowsExactlyTheFieldsItIsToldTo()
    {
        Sta.Run(() =>
        {
            using TriggerPickerForm form = CreateForm(new FakeStrings());
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-32000, -32000);
            form.Show();
            var view = (IPickerView)form;
            Control text = form.Controls.Find("TextOperand", searchAllChildren: true).Single();
            Control value = form.Controls.Find("ValueOperand", searchAllChildren: true).Single();
            Control poll = form.Controls.Find("PollIntervalOperand", searchAllChildren: true).Single();
            Control pollLabel = form.Controls.Find("PollIntervalOperandLabel", searchAllChildren: true).Single();
            Control stopped = form.Controls.Find("StoppedMatchingCheck", searchAllChildren: true).Single();

            view.ShowOperands(new OperandVisibility(
                Text: true, Value: false, Range: false, Tolerance: false, PollInterval: true,
                PropertyChoiceEnabled: true, StoppedMatching: true));
            Assert.True(text.Visible);
            Assert.False(value.Visible);
            Assert.True(poll.Visible);
            Assert.True(pollLabel.Visible);
            Assert.True(stopped.Visible);

            view.ShowOperands(new OperandVisibility(
                Text: false, Value: true, Range: false, Tolerance: false, PollInterval: false,
                PropertyChoiceEnabled: true, StoppedMatching: false));
            Assert.False(text.Visible);
            Assert.True(value.Visible);
            Assert.False(poll.Visible);
            Assert.False(pollLabel.Visible);
            Assert.False(stopped.Visible);
        });
    }

    /// <summary>
    /// 編集セッションのコミットで Form が閉じて Dispose されること。
    ///
    /// Form.Close は Show で出した Form を Dispose する — これが「Close は View への
    /// 全書き込みの後」という継ぎ目の契約の理由である。ただし**順序の退行はこのテストでは
    /// 落ちない** (実測: Dispose 済み Label への Text 代入はハンドルを作り直さず例外に
    /// ならない)。順序の網は presenter 側の
    /// <c>Commit_ForAnEditSession_ClosesTheViewLast</c> (Calls の並び) が持つ。
    /// </summary>
    [Fact]
    public void AnEditSessionCommit_ClosesTheFormWithoutTouchingItAfterwards()
    {
        Sta.Run(() =>
        {
            using TriggerPickerForm form = CreateForm(new FakeStrings());
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-32000, -32000);
            form.Show();
            form.LoadDefinition(Editable(), editSession: true);

            var commit = (Button)form.Controls.Find("CommitButton", searchAllChildren: true).Single();
            // FakeStrings はキーをそのまま返す — 文言が「更新」側へ差し替わった証拠
            Assert.Equal(PickerStringKeys.CommitButtonUpdate, commit.Text);

            commit.PerformClick();

            Assert.True(form.IsDisposed);
        });
    }

    /// <summary>プリフィル (編集セッションでない) のコミットでは Form が開いたままなこと。</summary>
    [Fact]
    public void APrefillCommit_LeavesTheFormOpen()
    {
        Sta.Run(() =>
        {
            using TriggerPickerForm form = CreateForm(new FakeStrings());
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-32000, -32000);
            form.Show();
            form.LoadDefinition(Editable());

            var commit = (Button)form.Controls.Find("CommitButton", searchAllChildren: true).Single();
            commit.PerformClick();

            Assert.False(form.IsDisposed);
            Assert.True(form.Visible);
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
    /// 往復がカルチャを跨いでも崩れないこと。
    /// invariant で書いて de-DE で読むと <c>1.5</c> が <c>15</c> になる — 例外は出ない。
    /// </summary>
    [Fact]
    public void WriteNumber_WritesInTheCurrentCulture()
    {
        Sta.Run(() =>
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            try
            {
                using var box = new TextBox();

                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                TriggerPickerForm.WriteNumber(box, 1.5);
                Assert.Equal("1,5", box.Text);
                Assert.Equal(1.5, TriggerPickerForm.ReadNumber(box));

                // null は空欄 (= 値なし)。0 を書かない
                TriggerPickerForm.WriteNumber(box, null);
                Assert.Equal(string.Empty, box.Text);
                Assert.Null(TriggerPickerForm.ReadNumber(box));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        });
    }

    /// <summary>
    /// 既存トリガーの読み込みでホバー捕捉が止まること。
    /// 止めないと、マウスが静止しているだけで**編集対象が別の要素へ差し替わる**。
    /// </summary>
    [Fact]
    public void LoadDefinition_TurnsHoverCaptureOff()
    {
        Sta.Run(() =>
        {
            using TriggerPickerForm form = CreateForm(new FakeStrings());
            CheckBox toggle = Assert.Single(
                form.Controls.Cast<Control>().SelectMany(Descendants).OfType<CheckBox>(),
                c => c.Name == "AutoSelectToggle");
            Assert.True(toggle.Checked);

            form.LoadDefinition(new TriggerDefinition
            {
                Id = "recorded",
                Clauses = [new PropertyClause { Property = TriggerProperty.Name, Op = ComparisonOp.Equals, Text = "a" }],
            });

            Assert.False(toggle.Checked);
            TextBox keyBox = Assert.Single(
                form.Controls.Cast<Control>().SelectMany(Descendants).OfType<TextBox>(),
                c => c.Name == "KeyBox");
            Assert.Equal("recorded", keyBox.Text);
        });
    }

    /// <summary>トリガーの形が未選択なら null を返すこと (継ぎ目の契約)。</summary>
    [Fact]
    public void ReadDraft_BeforeAnyShapeIsChosen_ReturnsNull()
        => Sta.Run(() =>
        {
            using TriggerPickerForm form = CreateForm(new FakeStrings());
            Assert.Null(((IPickerView)form).ReadDraft());
        });

    /// <summary>確定後にプロパティのコンボが埋まり、先頭が選ばれること。</summary>
    [Fact]
    public void ShowTriggerShape_FillsTheCombosAndPreselects()
    {
        Sta.Run(() =>
        {
            using TriggerPickerForm form = CreateForm(new FakeStrings());
            var view = (IPickerView)form;

            view.ShowTriggerShape(
                [TriggerProperty.Name, TriggerProperty.AutomationId],
                TriggerOn.ElementAppeared,
                ComparisonOp.RegexMatch);

            TriggerDraft? draft = view.ReadDraft();
            Assert.NotNull(draft);
            Assert.Equal(TriggerProperty.Name, draft.Property);
            Assert.Equal(TriggerOn.ElementAppeared, draft.On);
            Assert.Equal(ComparisonOp.RegexMatch, draft.Op);
        });
    }

    /// <summary>
    /// <see cref="IPickerView.Expand"/> が「表示のための展開」であること。
    /// 逆にすると、ホバーのたびに経路上の全段が兄弟を取りに行く。
    /// </summary>
    [Fact]
    public void Expand_OpensTheRowWithoutCountingAsAUserExpansion()
    {
        Sta.Run(() =>
        {
            using TriggerPickerForm form = CreateForm(new FakeStrings());
            var node = new PickerTreeNode("row", new FakePickerElement("row"));
            int userExpansions = 0;
            node.ExpandedByUser += _ => userExpansions++;

            ((IPickerView)form).Expand([node]);

            Assert.True(node.IsExpanded);
            Assert.Equal(0, userExpansions);
        });
    }

    /// <summary>
    /// ツリーと右側の境界が、ユーザーに動かせて**他の 2 変種と同じところから始まる**こと。
    ///
    /// <para>
    /// 3 変種のうち Windows Forms だけが <c>SplitContainer</c> を使う。
    /// <c>SplitterDistance</c> を初期化子で入れると、その時点の幅 (既定の 150px) に
    /// 合わせて**黙って 121 まで詰められ**、比率だけが残って Fill 後は 887 —
    /// **ツリーが幅の 80.6%** になる (実測)。他の 2 変種は 6:5 (54.5%) なので、
    /// 同じ画面が変種ごとに違って見える。例外も警告も出ない壊れ方なので、値のほうを固定する
    /// (docs/DESIGN.md §12: SplitterDistance は比率で、ドッキングが済んでから)。
    /// </para>
    /// <para>
    /// 最小幅は**片側を 0 まで引き切れないようにする**ためで、他の 2 変種の
    /// <c>ColumnDefinition.MinWidth</c> と同じ値である。
    /// </para>
    /// <para>
    /// **開始位置は絶対値で見ない** (docs/DESIGN.md §12)。絶対値 (例: 600px) を見ると、
    /// 窓が要求した 1100px を貰えるかどうかは**画面の広さで決まる**ため、
    /// CI の狭いデスクトップ (804px に詰められる) で落ちる。
    /// 見るべきものは他の 2 変種と揃っているかどうか、つまり**比率**である。
    /// </para>
    /// </summary>
    [Fact]
    public void TheSplitterIsDraggable_AndStartsWhereTheOtherViewsDo()
    {
        Sta.Run(() =>
        {
            var strings = new FakeStrings();
            strings.Values[PickerStringKeys.TreeSplitterAutomationName] = "区切り";
            using TriggerPickerForm form = CreateForm(strings);

            SplitContainer split = Splitter(form, "TreeSplitter");

            Assert.False(split.IsSplitterFixed);
            Assert.Equal(240, split.Panel1MinSize);
            Assert.Equal(240, split.Panel2MinSize);

            int usable = split.Width - split.SplitterWidth;
            string measured =
                $"SplitterDistance={split.SplitterDistance} / 使える幅={usable} / " +
                $"ClientSize={form.ClientSize.Width}x{form.ClientSize.Height}";

            // 最小幅の合計を下回ると setter が黙って詰めるので、比率の主張はそこで成立しなくなる。
            // その状況を「緑」にせず、画面の広さとして名指しで落とす
            Assert.True(
                usable >= split.Panel1MinSize + split.Panel2MinSize,
                $"窓が最小幅の合計 ({split.Panel1MinSize + split.Panel2MinSize}px) を下回っています。{measured}");

            double ratio = split.SplitterDistance / (double)usable;
            Assert.True(
                Math.Abs(ratio - (6.0 / 11.0)) < 0.01,
                $"ツリー側が他の 2 変種の 6:5 (54.5%) から外れています: {ratio:P1}。{measured}");

            // 区切りには表示文字列が無いので、読み上げに出るのはこの名前だけである
            Assert.Equal("区切り", split.AccessibleName);
        });
    }

    /// <summary>
    /// プロパティ一覧と条件欄の境界も動かせて、**既定では条件欄が切れていない**こと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 条件欄の高さは**折り返しの結果でしか決まらない**ので、開始位置を定数では書けない。
    /// 実装はレイアウト後の実測値から出しており、ここで見るのは
    /// **その結果として中身が収まっていること**である — 数字そのものは環境で動く。
    /// </para>
    /// <para>
    /// 収まらない状態は例外にならず、条件欄の下のほう ([トリガーを追加] ボタン) が
    /// 見えなくなるだけである。<c>AutoScroll</c> はそれでも辿り着けるようにするためで、
    /// 縮めたときのための保険でもある。
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePropertiesAndTheConditionFields_AreSeparatedByAWorkingSplitter()
    {
        Sta.Run(() =>
        {
            var strings = new FakeStrings();
            strings.Values[PickerStringKeys.ConditionSplitterAutomationName] = "もう 1 つの区切り";
            using TriggerPickerForm form = CreateForm(strings);

            SplitContainer split = Splitter(form, "ConditionSplitter");

            Assert.Equal(Orientation.Horizontal, split.Orientation);
            Assert.False(split.IsSplitterFixed);
            Assert.True(split.Panel1MinSize > 0);
            Assert.True(split.Panel2MinSize > 0);
            Assert.True(split.Panel2.AutoScroll, "縮めたときに中身へ辿り着けなくなります。");
            Assert.Equal("もう 1 つの区切り", split.AccessibleName);

            // 既定では条件欄が収まっていること (中身の高さぶんを Panel2 に割り当てている)
            Control conditions = Assert.Single(split.Panel2.Controls.Cast<Control>());
            Assert.True(
                split.Panel2.Height >= conditions.Height,
                $"条件欄が既定で切れています: 区画 {split.Panel2.Height}px < 中身 {conditions.Height}px");
        });
    }

    private static SplitContainer Splitter(TriggerPickerForm form, string name)
        => form.Controls.Cast<Control>()
            .SelectMany(Descendants)
            .OfType<SplitContainer>()
            .Single(c => c.Name == name);
}
