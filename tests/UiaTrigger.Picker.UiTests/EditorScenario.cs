// エディタの E2E を組み立てる土台 (docs/TESTING.md §1 T4 / §3 T5)。
//
// **T4 と T5 の両方から使う。**エディタと子ピッカーを実際に起こす手順はどちらでも同じで、
// 違うのは駆動の仕方だけである — T4 は UIA のコントロールパターン、T5 は合成入力。
// OverlayScenario と同じ位置づけで、ここに置くことで T5 側が組み立てを写経しなくて済む。
using System.Windows.Automation;
using UiaTrigger.Models;
using UiaTrigger.RealUia.Tests;
using Xunit;

namespace UiaTrigger.Picker.UiTests;

internal sealed class EditorScenario : IDisposable
{
    /// <summary>対象アプリに置くボタン。捕捉の狙いはここに定まる。</summary>
    public const string Target = "btn-edit";

    /// <summary>ホストの表示言語。文字列を assert に使うので固定する。</summary>
    public const string Culture = "en-US";

    /// <summary>UI が落ち着くまでの上限。</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

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

    /// <summary>エディタの窓がまだ出ているか。</summary>
    public bool EditorIsShowing() => _host.EditorWindowIsShowing();

    /// <summary>エディタの窓へフォーカスを移す (キーがどちらの窓へ行くかを決めるため)。</summary>
    public void FocusEditor() => Editor().SetFocus();

    /// <summary>先頭行の中心の画面座標 (合成マウスで押すため)。</summary>
    /// <remarks>
    /// 呼び出し側が <c>CursorGuard.MoveTo</c> で置いてから押す。座標と押下を 1 つに
    /// 混ぜないのは <c>SyntheticInput.TapLeftButton</c> の注記と同じ理由である。
    /// </remarks>
    public (int X, int Y) FirstRowCenter()
    {
        AutomationElement row = Ui.Until(
            () => Rows() is { Count: > 0 } rows ? rows[0] : null,
            Settle,
            "エディタの一覧に行が在ること",
            Diagnostics);
        System.Windows.Rect r = row.Current.BoundingRectangle;
        Assert.True(r.Width > 0 && r.Height > 0, $"行の矩形が潰れています ({r})。");
        return ((int)(r.Left + (r.Width / 2)), (int)(r.Top + (r.Height / 2)));
    }


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
