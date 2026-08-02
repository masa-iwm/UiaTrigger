// トリガ一覧エディタの E2E (docs/DESIGN.md §4 / docs/TESTING.md §1 T4)。
//
// ここでしか見られないものが 2 つある:
//
//   (1) **子ピッカーを開く経路** (ITriggerListEditorView.ShowPicker)。Show() を呼ぶので
//       窓の表示が要り、T1 の担当ではない (WPF のコンテナ実体化を外したのと同じ線)。
//   (2) **WinUI の ShowDraft**。WinUI View は x64 / WindowsAppSDK のため T1 から
//       実体化できない。既存トリガーを読み込んだときに条件欄が埋まることは、
//       実物を起こす以外に見る手立てが無い。
//
// **WinUI ホストだけを対象にする。**WPF / Windows Forms のエディタは ShowDialog による
// 本物のモーダルなので、UIA の Invoke() がダイアログが閉じるまで返らないおそれがある。
// あちらは MANUAL-CHECKS §4.3.2 で人が見る。
//
// 真偽の根拠は**トリガーファイル**に置く。画面の文字列で見ると、
// 「一覧に出ているが保存されていない」を通してしまう (M2 の教訓)。
using System.Windows.Automation;
using UiaTrigger.Models;
using UiaTrigger.RealUia.Tests;
using Xunit;

namespace UiaTrigger.Picker.UiTests;

public sealed class EditorShowcaseTests
{
    private const string Target = "btn-edit";
    private const string Culture = "en-US";

    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 追加の経路: エディタから子ピッカーを開いて録り、[OK] でファイルに入ること。
    /// </summary>
    /// <remarks>
    /// **[OK] を押すまでファイルが変わらないことも見る。**エディタは写しの上で動く
    /// (docs/DESIGN.md §4 の継ぎ目の絶対条件) ので、途中で書かれていたらその約束が破れている。
    /// </remarks>
    [Fact]
    public void RecordingThroughTheEditor_WritesTheTriggerOnlyWhenAccepted()
    {
        using var scenario = EditorScenario.Open();

        scenario.OpenEditor();
        scenario.RecordOneThroughTheChildPicker(out string id);

        // ここまででファイルは空のまま (エディタは写しの上で動く)
        Assert.Empty(scenario.SavedTriggers());

        scenario.Accept();

        TriggerDefinition saved = Assert.Single(Ui.Until(
            () => scenario.SavedTriggers() is { Count: 1 } list ? list : null,
            Settle,
            "[OK] で 1 件がファイルに入ること",
            scenario.Diagnostics));
        Assert.Equal(id, saved.Id);
    }

    /// <summary>
    /// 編集の経路: [条件を編集] で子ピッカーが**既存の値で埋まり**、
    /// 変えて確定すると同じ id のままファイルの値が変わること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **これが WinUI の <c>ShowDraft</c> を見る唯一の場所である** (docs/DESIGN.md §4)。
    /// 埋まっていなければ <c>KeyBox</c> が空になるので、そこで落ちる。
    /// </para>
    /// <para>
    /// **要素を捕まえ直していない**ことが要点である。「しきい値を 1 つ変えるにも
    /// 要素をホバーで捕まえ直す必要がある」という穴 (docs/DESIGN.md §4) が、これで塞がっている。
    /// </para>
    /// </remarks>
    [Fact]
    public void EditingThroughTheEditor_LoadsTheConditionAndWritesTheChange()
    {
        using var scenario = EditorScenario.Open();

        // まず 1 件録って確定させる (これが編集の対象になる)
        scenario.OpenEditor();
        scenario.RecordOneThroughTheChildPicker(out string id);
        scenario.Accept();
        _ = Ui.Until(
            () => scenario.SavedTriggers().Count == 1 ? "ok" : null,
            Settle, "1 件がファイルに入ること", scenario.Diagnostics);

        // 開き直して編集する
        scenario.OpenEditor();
        scenario.SelectTheFirstRow();
        AutomationElement picker = scenario.EditTheSelectedRow();

        // 既存の値が入っていること (ShowDraft が効いていること)
        Assert.Equal(id, picker.RequireByIdEventually("KeyBox", scenario.Diagnostics).ValueOf());
        AutomationElement operand = Ui.Until(
            () => picker.ById("TextOperand"),
            Settle,
            "条件の値の欄が既存の演算子で出ていること",
            scenario.Diagnostics);
        Assert.Equal(Target, operand.ValueOf());

        operand.SetText("edited-value");
        picker.RequireByIdEventually("CommitButton", scenario.Diagnostics).Invoke();
        scenario.Accept();

        TriggerDefinition saved = Assert.Single(Ui.Until(
            () => scenario.SavedTriggers() is { Count: 1 } list &&
                  list[0].Clauses.Count == 1 &&
                  string.Equals(list[0].Clauses[0].Text, "edited-value", StringComparison.Ordinal)
                ? list : null,
            Settle,
            "編集した値がファイルに入ること",
            scenario.Diagnostics));
        // **増えていないこと。**id を保つのが編集であって、増えるなら追加になっている
        Assert.Equal(id, saved.Id);
    }

    /// <summary>
    /// 編集セッションの UX: [条件を編集] で開いた子ピッカーは確定ボタンが**「更新」を名乗り**、
    /// コミット 1 回で**窓が閉じる**こと。対照として、追加の経路はコミット後も
    /// 「追加」のまま開いたままであることを同じテストで見る。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「トリガーを追加」のままだと、編集しているのに追加されるように読める —
    /// preview.3 の組み込み評価で実際に出た指摘である。文言はリソースから読む
    /// (<c>PickerResources</c>) ので、翻訳を変えてもテストは追随する。
    /// </para>
    /// <para>
    /// 閉じる/閉じないの判定はピッカー窓の有無で行い、真偽の根拠 (行が増えず更新される) は
    /// 既存の <see cref="EditingThroughTheEditor_LoadsTheConditionAndWritesTheChange"/> が持つ。
    /// </para>
    /// </remarks>
    [Fact]
    public void EditingThroughTheEditor_ShowsUpdateAndClosesThePickerOnCommit()
    {
        Dictionary<string, string> expected =
            PickerResources.For(PickerHostProfile.ByName("WinUI"), Culture);
        using var scenario = EditorScenario.Open();

        scenario.OpenEditor();
        scenario.RecordOneThroughTheChildPicker(out string id);

        // 対照 (追加の経路): コミットの後もピッカーは開いたまま、ボタンは「追加」のまま。
        // 「開いたまま何件でもコミットできる」が新規追加の明文化されたワークフローである
        Assert.Equal(1, scenario.PickerWindowCount());
        Assert.Equal(
            expected["CommitButton.Content"],
            scenario.PickerWindow().RequireByIdEventually("CommitButton", scenario.Diagnostics).NameOf());

        scenario.Accept();
        _ = Ui.Until(
            () => scenario.SavedTriggers().Count == 1 ? "ok" : null,
            Settle, "1 件がファイルに入ること", scenario.Diagnostics);

        // 編集の経路: ボタンが「更新」を名乗る
        scenario.OpenEditor();
        scenario.SelectTheFirstRow();
        AutomationElement picker = scenario.EditTheSelectedRow();
        AutomationElement commit = Ui.Until(
            () => picker.ById("CommitButton") is { } button &&
                  string.Equals(button.NameOf(), expected["CommitButtonUpdate"], StringComparison.Ordinal)
                ? button
                : null,
            Settle,
            $"確定ボタンが '{expected["CommitButtonUpdate"]}' を名乗ること",
            scenario.Diagnostics);

        // コミット 1 回で窓が閉じる (編集はその 1 回で終わる)
        commit.Invoke();
        Ui.Never(
            () => scenario.PickerWindowCount() > 0,
            TimeSpan.FromSeconds(5),
            "編集セッションのコミット後もピッカーの窓が残る",
            scenario.Diagnostics);

        scenario.Accept();
        TriggerDefinition saved = Assert.Single(Ui.Until(
            () => scenario.SavedTriggers() is { Count: 1 } list ? list : null,
            Settle, "編集後も 1 件のままファイルに入ること", scenario.Diagnostics));
        Assert.Equal(id, saved.Id);
    }

    /// <summary>
    /// 取り消しの経路: エディタで削除してから窓を閉じると、ファイルが変わらないこと。
    /// </summary>
    /// <remarks>
    /// **「変わらない」は待ち切って確かめる** — 閉じた直後に数えると、
    /// まだ書かれていないだけで通ってしまう。
    /// </remarks>
    [Fact]
    public void ClosingTheEditorWithoutAccepting_LeavesTheFileAlone()
    {
        using var scenario = EditorScenario.Open();
        scenario.OpenEditor();
        scenario.RecordOneThroughTheChildPicker(out _);
        scenario.Accept();
        _ = Ui.Until(
            () => scenario.SavedTriggers().Count == 1 ? "ok" : null,
            Settle, "1 件がファイルに入ること", scenario.Diagnostics);

        scenario.OpenEditor();
        scenario.SelectTheFirstRow();
        scenario.DeleteTheSelectedRow();
        scenario.CloseTheEditor();

        Ui.Never(
            () => scenario.SavedTriggers().Count != 1,
            TimeSpan.FromSeconds(3),
            "取り消したのにファイルが変わる",
            scenario.Diagnostics);
    }

    /// <summary>対象アプリ + WinUI ホスト。ピッカーはエディタから開く。</summary>
    /// <summary>
    /// 複合の経路を丸ごと 1 回のホスト起動で見る (docs/MANUAL-CHECKS.md §4.3.5 の自動化)。
    ///
    /// <para>
    /// ここでしか見られないものが 3 つある。**どれも T1 は緑のまま通す:**
    /// </para>
    /// <list type="number">
    /// <item>
    /// ボタンの文言が**解決済みの文字**であること。プレゼンターが実行時に引くキーは
    /// ドット無しでなければならず、ドット付きだと MRT が解決できず
    /// 画面に <c>CombineTriggersButton.Content</c> と出る (T6 で実際に出た)。
    /// resx 経路 (WPF / Windows Forms) はドットでも引けるので T1 では出ない。
    /// </item>
    /// <item>
    /// **選び直したときに画面が保存値を映すこと。**チェックの状態は
    /// <c>TogglePattern</c> で読めるので、ファイルを見ずに UI で確かめられる。
    /// </item>
    /// <item>WinUI の View 全体 (T1 から実体化できない)。</item>
    /// </list>
    /// </summary>
    [Fact]
    public void CombiningWithTheFallingEdge_ShowsUpInTheUiAndInTheFile_AndStaysUpdatable()
    {
        Dictionary<string, string> expected =
            PickerResources.For(PickerHostProfile.ByName("WinUI"), Culture);
        using var scenario = EditorScenario.Open();

        scenario.OpenEditor();
        scenario.RecordOneThroughTheChildPicker(out string first);
        scenario.RecordAnotherWithId("second-trigger");

        // (1) 文言が解決済みであること。キーが漏れていれば "CombineTriggersButton.Content" になる
        Assert.Equal(expected["CombineButtonCombine"], scenario.CombineCaption());

        // **選んでからチェックを入れる。**行を選ぶと下段は空へ戻る (下段は選択に従う)
        scenario.SelectEveryRow();
        scenario.CheckTheFallingEdge();
        scenario.CombineButton().Invoke();

        AutomationElement compositeRow = Ui.Until(
            () => scenario.CompositeRow(),
            Settle,
            "まとめた行が一覧に出ること",
            scenario.Diagnostics);
        string compositeId = compositeRow.NameOf();

        // (2) 選び直すと、画面がまとめた値を映すこと (ファイルを見ずに UI で確かめられる)
        scenario.SelectTheRowContaining("composite-1");
        Assert.Equal(ToggleState.On, scenario.FallingEdgeState());
        Assert.Equal(expected["CombineButtonUpdate"], scenario.CombineCaption());

        // 何も直さずに更新しても壊れず、そのまま選ばれたままで続けて更新できること
        scenario.CombineButton().Invoke();
        Assert.Equal(expected["CombineButtonUpdate"], scenario.CombineCaption());
        Assert.Equal(ToggleState.On, scenario.FallingEdgeState());

        // 素の行へ移ると下段が空に戻ること (仕様: 下段は常に「いま押したら何が起きるか」)
        scenario.SelectTheRowContaining(first);
        Assert.Equal(expected["CombineButtonCombine"], scenario.CombineCaption());
        Assert.Equal(ToggleState.Off, scenario.FallingEdgeState());
        Assert.Equal(string.Empty, scenario.ExpressionText());

        // 真偽の根拠はファイル (画面だけだと「出ているが保存されていない」を通す)
        scenario.Accept();
        IReadOnlyList<TriggerDefinition> saved = Ui.Until(
            () => scenario.SavedTriggers() is { Count: 3 } list ? list : null,
            Settle,
            "元 2 件 + 複合 1 件がファイルに入ること",
            scenario.Diagnostics);
        TriggerDefinition composite = Assert.Single(
            saved, t => t.Expression is not null || t.Clauses.Count > 1);
        Assert.True(
            composite.NotifyOnStoppedMatching,
            $"複合 '{compositeId}' に立ち下がり通知が乗っていません。");
    }

    private sealed class EditorScenario : IDisposable
    {
        private readonly TestTargetProcess _target;
        private readonly PickerHostProcess _host;

        private EditorScenario(TestTargetProcess target, PickerHostProcess host)
        {
            _target = target;
            _host = host;
        }

        public static EditorScenario Open()
        {
            PickerHostProfile profile = PickerHostProfile.ByName("WinUI");
            TestTargetProcess target = TestTargetProcess.Start(TargetProfile.WinForms);
            try
            {
                target.Send(DesktopLayout.PlaceTargetCommand);
                target.Send("add-button " + Target);
                target.Send("ping");

                (int x, int y) = target.CenterOf(Target);
                // pick 点は 2 つ渡す。エディタは開くたびに子ピッカーを作り、
                // ホストは NextCursor() を**エディタ 1 つにつき 1 回**消費する
                PickerHostProcess host = PickerHostProcess.Start(profile, Culture, (x, y), (x, y));
                try
                {
                    return new EditorScenario(target, host);
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

        public IReadOnlyList<TriggerDefinition> SavedTriggers() => _host.SavedTriggers();

        public string Diagnostics() => _host.Diagnostics();

        public void OpenEditor() => _host.OpenEditor();

        public AutomationElement PickerWindow() => _host.PickerWindow();

        /// <summary>いま開いているピッカー窓の数。閉じたこと/残っていることの判定に使う。</summary>
        public int PickerWindowCount() => _host.PickerWindows().Count;

        private AutomationElement Editor() => _host.EditorWindow();

        /// <summary>[追加] を押し、出てきた子ピッカーで捕捉 → 確定 → コミットする。</summary>
        public void RecordOneThroughTheChildPicker(out string id)
        {
            // **退かしながら開くこと。**素の Invoke() だと子ピッカーがカスケードして
            // pick 点を覆い、滞留が明けた瞬間にピッカーが自分の DesktopChildSiteBridge を
            // 掴んで**二度と捕捉しなくなる** (docs/TESTING.md §1。実測で 1 度踏んだ)
            AutomationElement picker = _host.OpenPickerFromEditor("AddTriggerButton");

            AutomationElement row = Ui.Until(
                () => picker.ById(_host.Profile.TreeAutomationId)?.SelectedRow(),
                Settle,
                $"子ピッカーが {Target} を捕捉すること",
                Diagnostics);
            Assert.Contains($"[{Target}]", row.NameOf(), StringComparison.Ordinal);
            ConfirmButtonOf(row).Invoke();
            _ = Ui.Until(
                () => picker.ById("CommitButton") is { } b && b.Current.IsEnabled ? "ok" : null,
                Settle,
                "確定してコミットできるようになること",
                Diagnostics);

            // 「Name が Target と等しい」にする。編集の経路で値を書き換えられるよう、
            // 値を持つ演算子にしておく
            picker.RequireByIdEventually("CondCombo", Diagnostics).SelectComboItem("Equals");
            AutomationElement operand = Ui.Until(
                () => picker.ById("TextOperand"),
                Settle,
                "値の欄が出ること (演算子の出し分けが済むこと)",
                Diagnostics);
            operand.SetText(Target);

            id = picker.RequireByIdEventually("KeyBox", Diagnostics).ValueOf() ?? string.Empty;
            Assert.NotEmpty(id);
            picker.RequireByIdEventually("CommitButton", Diagnostics).Invoke();

            // 一覧に出るまで待つ。ここで待たないと [OK] が録る前の写しを返しうる
            string expected = id;
            _ = Ui.Until(
                () => Rows().Any(r => r.NameOf().Contains($"[{expected}]", StringComparison.Ordinal))
                    ? "ok" : null,
                Settle,
                $"'{expected}' がエディタの一覧に出ること",
                Diagnostics);
        }

        /// <summary>[条件を編集] を押し、出てきた子ピッカーを返す。</summary>
        /// <remarks>
        /// 編集の経路はホバー捕捉を使わない (それが <c>LoadDefinition</c> の要点である) が、
        /// **開き方は追加と揃える** — 覆われたままだと、この後に要素を捕まえ直す操作を
        /// 足したときだけ間欠に落ちる形になる。
        /// </remarks>
        public AutomationElement EditTheSelectedRow() =>
            _host.OpenPickerFromEditor("EditTriggerButton");

        public void DeleteTheSelectedRow()
        {
            int before = Rows().Count;
            Editor().RequireByIdEventually("DeleteTriggerButton", Diagnostics).Invoke();
            _ = Ui.Until(
                () => Rows().Count < before ? "ok" : null,
                Settle,
                "エディタの一覧から行が消えること",
                Diagnostics);
        }

        /// <summary>いま開いている子ピッカーから、id だけ変えてもう 1 件コミットする。</summary>
        /// <remarks>
        /// ピッカーは追加のとき開いたままなので、同じ要素に対して id 違いの 2 件目が録れる。
        /// まとめるには 2 行あればよく、どの要素かは問わない。
        /// </remarks>
        public void RecordAnotherWithId(string id)
        {
            AutomationElement picker = PickerWindow();
            picker.RequireByIdEventually("KeyBox", Diagnostics).SetText(id);
            picker.RequireByIdEventually("CommitButton", Diagnostics).Invoke();
            _ = Ui.Until(
                () => Rows().Any(r => r.NameOf().Contains($"[{id}]", StringComparison.Ordinal))
                    ? "ok" : null,
                Settle,
                $"'{id}' の行が一覧に出ること",
                Diagnostics);
        }

        /// <summary>まとめた行 (id が composite- で始まるもの)、まだ無ければ null。</summary>
        public AutomationElement? CompositeRow() =>
            Rows().FirstOrDefault(r => r.NameOf().Contains("[composite-", StringComparison.Ordinal));

        public void SelectEveryRow()
        {
            foreach (AutomationElement row in Rows())
            {
                ((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern))
                    .AddToSelection();
            }
        }

        /// <summary>id を含む行を単独で選ぶ。</summary>
        public void SelectTheRowContaining(string id)
        {
            AutomationElement row = Ui.Until(
                () => Rows().FirstOrDefault(
                    r => r.NameOf().Contains(id, StringComparison.Ordinal)),
                Settle,
                $"'{id}' の行が在ること",
                Diagnostics);
            ((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        }

        private AutomationElement CombineCheck() =>
            Editor().RequireByIdEventually("CombineStoppedMatchingCheck", Diagnostics);

        /// <summary>立ち下がり通知のチェックを入れる。</summary>
        public void CheckTheFallingEdge()
        {
            var toggle = (TogglePattern)CombineCheck().GetCurrentPattern(TogglePattern.Pattern);
            if (toggle.Current.ToggleState != ToggleState.On)
            {
                toggle.Toggle();
            }
        }

        /// <summary>画面に出ている立ち下がり通知の状態。</summary>
        public ToggleState FallingEdgeState() =>
            ((TogglePattern)CombineCheck().GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState;

        public AutomationElement CombineButton() =>
            Editor().RequireByIdEventually("CombineTriggersButton", Diagnostics);

        /// <summary>まとめる / 更新のボタンがいま名乗っている文字。</summary>
        public string CombineCaption() => CombineButton().NameOf();

        public string ExpressionText() =>
            Editor().RequireByIdEventually("ExpressionBox", Diagnostics).ValueOf() ?? string.Empty;

        public void SelectTheFirstRow()
        {
            AutomationElement row = Ui.Until(
                () => Rows() is { Count: > 0 } rows ? rows[0] : null,
                Settle,
                "エディタの一覧に行が在ること",
                Diagnostics);
            ((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        }

        public void Accept()
        {
            AutomationElement editor = Editor();
            editor.RequireByIdEventually("AcceptButton", Diagnostics).Invoke();
            WaitUntilTheEditorIsGone();
        }

        public void CloseTheEditor()
        {
            AutomationElement editor = Editor();
            editor.RequireByIdEventually("CancelButton", Diagnostics).Invoke();
            WaitUntilTheEditorIsGone();
        }

        /// <summary>
        /// エディタが閉じるまで待つ。
        /// </summary>
        /// <remarks>
        /// **閉じたことを待たないと次の <c>OpenEditor</c> が古い窓を掴む。**
        /// ホストは await 中ボタンを無効にしているので、押しても何も起きないまま
        /// 「一覧が出ない」で落ちる形になる。
        /// </remarks>
        private void WaitUntilTheEditorIsGone() => Ui.Never(
            () => _host.EditorWindowIsShowing(),
            TimeSpan.FromSeconds(5),
            "エディタの窓が閉じない",
            Diagnostics);

        private IReadOnlyList<AutomationElement> Rows()
        {
            AutomationElement? list = Editor().ById("EditorTriggerList");
            return list is null
                ? []
                : [.. list
                    .FindAll(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                    .Cast<AutomationElement>()];
        }

        /// <summary>
        /// 行の確定ボタン。**その行自身のもの**を取る。
        /// </summary>
        /// <remarks>
        /// AutomationId では探せない — ボタンは <c>DataTemplate</c> の中にあり、
        /// 行ごとに実体化されるので id を持たない。加えて WinUI の <c>TreeView</c> は
        /// 行を入れ子にするので、部分木をそのまま辿ると**子の行のボタンを掴みうる**。
        /// <c>MonitorShowcaseTests</c> と同じ形である。
        /// </remarks>
        private static AutomationElement ConfirmButtonOf(AutomationElement row)
        {
            AutomationElement? nestedRow = row.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem));
            foreach (AutomationElement button in row.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            {
                if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out _))
                {
                    continue;
                }
                if (nestedRow is not null &&
                    nestedRow.FindFirst(
                        TreeScope.Descendants,
                        new PropertyCondition(
                            AutomationElement.RuntimeIdProperty, button.GetRuntimeId())) is not null)
                {
                    continue;
                }
                return button;
            }
            throw new InvalidOperationException($"行 '{row.NameOf()}' に確定ボタンがありません。");
        }

        public void Dispose()
        {
            _host.Dispose();
            _target.Dispose();
        }
    }
}
