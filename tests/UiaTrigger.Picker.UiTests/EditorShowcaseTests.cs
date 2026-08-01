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
