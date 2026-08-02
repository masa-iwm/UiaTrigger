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
            EditorScenario.Settle,
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
            EditorScenario.Settle, "1 件がファイルに入ること", scenario.Diagnostics);

        // 開き直して編集する
        scenario.OpenEditor();
        scenario.SelectTheFirstRow();
        AutomationElement picker = scenario.EditTheSelectedRow();

        // 既存の値が入っていること (ShowDraft が効いていること)
        Assert.Equal(id, picker.RequireByIdEventually("KeyBox", scenario.Diagnostics).ValueOf());
        AutomationElement operand = Ui.Until(
            () => picker.ById("TextOperand"),
            EditorScenario.Settle,
            "条件の値の欄が既存の演算子で出ていること",
            scenario.Diagnostics);
        Assert.Equal(EditorScenario.Target, operand.ValueOf());

        operand.SetText("edited-value");
        picker.RequireByIdEventually("CommitButton", scenario.Diagnostics).Invoke();
        scenario.Accept();

        TriggerDefinition saved = Assert.Single(Ui.Until(
            () => scenario.SavedTriggers() is { Count: 1 } list &&
                  list[0].Clauses.Count == 1 &&
                  string.Equals(list[0].Clauses[0].Text, "edited-value", StringComparison.Ordinal)
                ? list : null,
            EditorScenario.Settle,
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
            PickerResources.For(PickerHostProfile.ByName("WinUI"), EditorScenario.Culture);
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
            EditorScenario.Settle, "1 件がファイルに入ること", scenario.Diagnostics);

        // 編集の経路: ボタンが「更新」を名乗る
        scenario.OpenEditor();
        scenario.SelectTheFirstRow();
        AutomationElement picker = scenario.EditTheSelectedRow();
        AutomationElement commit = Ui.Until(
            () => picker.ById("CommitButton") is { } button &&
                  string.Equals(button.NameOf(), expected["CommitButtonUpdate"], StringComparison.Ordinal)
                ? button
                : null,
            EditorScenario.Settle,
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
            EditorScenario.Settle, "編集後も 1 件のままファイルに入ること", scenario.Diagnostics));
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
            EditorScenario.Settle, "1 件がファイルに入ること", scenario.Diagnostics);

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
            PickerResources.For(PickerHostProfile.ByName("WinUI"), EditorScenario.Culture);
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
            EditorScenario.Settle,
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
            EditorScenario.Settle,
            "元 2 件 + 複合 1 件がファイルに入ること",
            scenario.Diagnostics);
        TriggerDefinition composite = Assert.Single(
            saved, t => t.Expression is not null || t.Clauses.Count > 1);
        Assert.True(
            composite.NotifyOnStoppedMatching,
            $"複合 '{compositeId}' に立ち下がり通知が乗っていません。");
    }

}
