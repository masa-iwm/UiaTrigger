// トリガ一覧エディタの Windows Forms View (docs/DESIGN.md §4)。
//
// 振る舞いは TriggerListEditorPresenter が持つ。ここに残るのは Windows Forms の事実だけである:
//   ・ShowDialog による本物のモーダル (WinUI3 には窓単位のモーダルが無い)
//   ・子ピッカーの所有者付けと後始末
//   ・ラベルをコードで代入すること
//
// デザイナーは使わずコードだけで組む (ピッカーの View と同じ)。
using UiaTrigger.Models;

namespace UiaTrigger.Picker.WinForms;

/// <summary>The trigger-list editor as a Windows Forms dialog: add, edit, remove, combine, take apart.</summary>
/// <remarks>
/// Holds no rules of its own — <see cref="TriggerListEditorPresenter"/> does. Works on a copy: the
/// list handed in is never modified, and the edited list comes back from
/// <see cref="EditAsync(IWin32Window, IReadOnlyList{TriggerDefinition})"/>. Where the triggers are
/// stored, and whether a monitor is running, stays the host's business.
/// </remarks>
public sealed class TriggerListEditorForm : Form, ITriggerListEditorView
{
    // Control.Name は UIA プロバイダーがそのまま AutomationId として出す。3 変種で同じ綴りに
    // 揃えてあるので、T4 のハーネスは変種ごとに別の探し方をしなくてよい (docs/TESTING.md §1)。
    private readonly Button _add = new() { Name = "AddTriggerButton", AutoSize = true };
    private readonly Button _edit = new() { Name = "EditTriggerButton", AutoSize = true };
    private readonly Button _delete = new() { Name = "DeleteTriggerButton", AutoSize = true };
    private readonly ListBox _list = new()
    {
        Name = "EditorTriggerList",
        Dock = DockStyle.Fill,
        SelectionMode = SelectionMode.MultiExtended,
        HorizontalScrollbar = true,
        IntegralHeight = false,
    };
    private readonly Label _expressionLabel = new() { Name = "ExpressionBoxLabel", AutoSize = true };
    private readonly TextBox _expression = new() { Name = "ExpressionBox", Width = 260 };
    private readonly Label _unwatchedLabel = new() { Name = "UnwatchedBoxLabel", AutoSize = true };
    private readonly TextBox _unwatched = new() { Name = "UnwatchedBox", Width = 200 };
    private readonly Label _combinePollIntervalLabel = new() { Name = "CombinePollIntervalBoxLabel", AutoSize = true };
    private readonly TextBox _combinePollInterval = new() { Name = "CombinePollIntervalBox", Width = 90 };
    private readonly Button _combine = new() { Name = "CombineTriggersButton", AutoSize = true };
    private readonly Button _decompose = new() { Name = "DecomposeTriggerButton", AutoSize = true };
    private readonly Label _status = new()
    {
        Name = "EditorStatus", AutoSize = true, MaximumSize = new Size(820, 0),
    };
    private readonly Button _accept = new() { Name = "AcceptButton", AutoSize = true };
    private readonly Button _cancel = new() { Name = "CancelButton", AutoSize = true };

    private readonly IPickerStrings _strings;
    private readonly TriggerListEditorPresenter _presenter;
    private readonly Func<TriggerPickerForm> _createPicker;

    /// <summary>開いている子ピッカー。同時に 1 枚だけ開く。</summary>
    private TriggerPickerForm? _picker;

    private IReadOnlyList<TriggerDefinition>? _result;

    /// <summary>Shows the editor modally and returns the edited list, or null when cancelled.</summary>
    /// <param name="owner">The window to own the dialog, or null for none.</param>
    /// <param name="triggers">The triggers to edit. Neither the list nor its items are modified.</param>
    /// <returns>The edited triggers, or null when the user cancelled.</returns>
    /// <remarks>
    /// Call it on the UI thread. The returned task is already complete when it comes back — the
    /// dialog runs a nested message loop, so this returns once the user is done. The asynchronous
    /// shape is what the three variants have in common: WinUI 3 has no window-modal dialog, so its
    /// editor cannot offer a blocking call at all.
    /// </remarks>
    public static Task<IReadOnlyList<TriggerDefinition>?> EditAsync(
        IWin32Window? owner, IReadOnlyList<TriggerDefinition> triggers)
        => EditAsync(owner, triggers, new Win32CursorSource());

    /// <summary>
    /// Same, with the child picker taking the pointer position from <paramref name="cursor"/>.
    /// </summary>
    /// <param name="owner">The window to own the dialog, or null for none.</param>
    /// <param name="triggers">The triggers to edit. Neither the list nor its items are modified.</param>
    /// <param name="cursor">Where the child picker's hover dwell reads the pointer position from.</param>
    /// <returns>The edited triggers, or null when the user cancelled.</returns>
    /// <remarks><inheritdoc cref="EditAsync(IWin32Window, IReadOnlyList{TriggerDefinition})" path="/remarks"/></remarks>
    public static Task<IReadOnlyList<TriggerDefinition>?> EditAsync(
        IWin32Window? owner, IReadOnlyList<TriggerDefinition> triggers, ICursorSource cursor)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        ArgumentNullException.ThrowIfNull(cursor);

        using var form = new TriggerListEditorForm(
            new ResxPickerStrings(), triggers, createPresenter: null, () => new TriggerPickerForm(cursor));
        _ = form.ShowDialog(owner);
        return Task.FromResult(form._result);
    }

    // internal だが、公開されている多重定義と名前が同じなので PublicApiDocumentationTests は
    // これも公開 API として扱う (名前だけで照合するため)。よって doc は英語で書く。

    /// <summary>For tests: substitutes the strings, the presenter and the child picker.</summary>
    internal TriggerListEditorForm(
        IPickerStrings strings,
        IReadOnlyList<TriggerDefinition> triggers,
        Func<ITriggerListEditorView, TriggerListEditorPresenter>? createPresenter,
        Func<TriggerPickerForm>? createPicker)
    {
        _strings = strings;
        ClientSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterParent;
        BuildLayout();
        ApplyStrings();
        _createPicker = createPicker ?? (() => new TriggerPickerForm());

        // Enter / Esc をボタンに結び付ける (WPF の IsDefault / IsCancel に相当)
        AcceptButton = _accept;
        CancelButton = _cancel;

        // presenter は**ハンドラーを結ぶ前に**作る。コンストラクターの中で最初の ShowRows を
        // 呼ぶので、一覧のコントロールが出来上がっている必要がある (BuildLayout は済んでいる)
        _presenter = createPresenter?.Invoke(this)
            ?? new TriggerListEditorPresenter(this, strings, triggers);

        _add.Click += (_, _) => _presenter.NotifyAddRequested();
        _edit.Click += (_, _) => _presenter.NotifyEditRequested();
        // 行のダブルクリック = [条件を編集]。空白部分は IndexFromPoint が NoMatches を返すので
        // 無視する — でないと、選択済みの行が空白のダブルクリックで編集され始める。
        // 編集できない選択 (複合・複数選択) は presenter がボタンと同じ理由をステータスへ出す。
        // ハンドラの中で直接開かず、入力が掃けた後に回す — ダブルクリックの入力系列が
        // 残ったまま子ピッカーを出すと、残りの入力処理がエディタへフォーカスを戻す
        // (WPF / WinUI で実測した形と同じ。WinForms は所有関係があるので重なりは保たれる)
        _list.MouseDoubleClick += (_, e) =>
        {
            if (_list.IndexFromPoint(e.Location) != ListBox.NoMatches)
            {
                _ = BeginInvoke(_presenter.NotifyEditRequested);
            }
        };
        _delete.Click += (_, _) => _presenter.NotifyDeleteRequested();
        _combine.Click += (_, _) => _presenter.NotifyCombineRequested();
        _decompose.Click += (_, _) => _presenter.NotifyDecomposeRequested();
        _accept.Click += (_, _) =>
        {
            Accept();
            Close();
        };
        _cancel.Click += (_, _) => Close();
        // 閉じるときに子ピッカーを取り残さない (オーバーレイのフックが残る)
        FormClosed += (_, _) => CloseThePicker();
    }

    /// <summary>コントロールを組み立てる。デザイナーは使わない。</summary>
    private void BuildLayout()
    {
        var topBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        topBar.Controls.AddRange([_add, _edit, _delete]);

        var combineBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true,
        };
        combineBar.Controls.AddRange(
        [
            _expressionLabel, _expression, _unwatchedLabel, _unwatched,
            _combinePollIntervalLabel, _combinePollInterval, _combine, _decompose,
        ]);

        var bottomBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
        };
        bottomBar.Controls.AddRange([_cancel, _accept]);

        var statusBar = new Panel { Dock = DockStyle.Bottom, AutoSize = true };
        statusBar.Controls.Add(_status);

        // Dock の重なり順は追加の逆になる。Fill を最初に足すこと
        Controls.Add(_list);
        Controls.Add(topBar);
        Controls.Add(bottomBar);
        Controls.Add(statusBar);
        Controls.Add(combineBar);
    }

    /// <summary>ラベルをリソースから代入する (Windows Forms に <c>x:Uid</c> は無い)。</summary>
    private void ApplyStrings()
    {
        Text = _strings.GetString(EditorStringKeys.WindowTitle);
        _add.Text = _strings.GetString(EditorStringKeys.AddButtonContent);
        _edit.Text = _strings.GetString(EditorStringKeys.EditButtonContent);
        _delete.Text = _strings.GetString(EditorStringKeys.DeleteButtonContent);
        _combine.Text = _strings.GetString(EditorStringKeys.CombineButtonContent);
        _decompose.Text = _strings.GetString(EditorStringKeys.DecomposeButtonContent);
        _accept.Text = _strings.GetString(EditorStringKeys.AcceptButtonContent);
        _cancel.Text = _strings.GetString(EditorStringKeys.CancelButtonContent);
        _expressionLabel.Text = _strings.GetString(EditorStringKeys.ExpressionBoxHeader);
        _unwatchedLabel.Text = _strings.GetString(EditorStringKeys.UnwatchedBoxHeader);
        _combinePollIntervalLabel.Text =
            _strings.GetString(EditorStringKeys.CombinePollIntervalBoxHeader);
        // 一覧は見出しを持たないので、読み上げに出るのはこの名前だけである
        _list.AccessibleName = _strings.GetString(EditorStringKeys.TriggerListAutomationName);
    }

    /// <summary>編集結果を確定する。テストは <c>ShowDialog</c> を使えないのでここを直に呼ぶ。</summary>
    internal void Accept() => _result = _presenter.Snapshot();

    /// <summary>いまの結果 (null = 未確定 = キャンセル)。</summary>
    internal IReadOnlyList<TriggerDefinition>? Result => _result;

    // ---------- ITriggerListEditorView ----------

    string ITriggerListEditorView.Status { set => _status.Text = value; }

    string ITriggerListEditorView.ExpressionText => _expression.Text ?? string.Empty;

    string ITriggerListEditorView.UnwatchedText => _unwatched.Text ?? string.Empty;

    // 空欄も読めない入力も「値なし」。ピッカーの数値欄と同じ規則 (TriggerPickerForm.ReadNumber)
    double? ITriggerListEditorView.CombinePollIntervalSeconds =>
        TriggerPickerForm.ReadNumber(_combinePollInterval);

    IReadOnlyList<int> ITriggerListEditorView.SelectedIndices => [.. _list.SelectedIndices.Cast<int>()];

    void ITriggerListEditorView.ShowRows(IReadOnlyList<string> rows)
    {
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            _list.Items.AddRange([.. rows.Cast<object>()]);
        }
        finally
        {
            _list.EndUpdate();
        }
    }

    void ITriggerListEditorView.ShowPicker(TriggerDefinition? definitionToEdit)
    {
        // 同時に開くのは 1 枚。2 枚目を開けるようにすると「どちらのコミットがどの行なのか」が
        // 決まらなくなる (presenter は編集中の行を 1 つだけ覚えている)
        if (_picker is { } open)
        {
            open.Activate();
            if (definitionToEdit is not null)
            {
                // エディタ経由の読み込みは常に編集セッション — 確定 1 回で閉じ、
                // ボタンは「更新」を名乗る (presenter が _editingId で行を差し替える)
                open.LoadDefinition(definitionToEdit, editSession: true);
            }
            return;
        }

        TriggerPickerForm picker = _createPicker();
        _picker = picker;
        picker.TriggerCommitted += (_, e) => _presenter.NotifyPickerCommitted(e.Definition);
        picker.FormClosed += (_, _) =>
        {
            _picker = null;
            _presenter.NotifyPickerClosed();
        };
        // モーダルのエディタから非モーダルの子を出すので、所有者を明示する。
        // Show(this) にしないと、エディタの入れ子メッセージループの外側に取り残される
        picker.Show(this);
        picker.Activate();
        if (definitionToEdit is not null)
        {
            picker.LoadDefinition(definitionToEdit, editSession: true);
        }
    }

    private void CloseThePicker()
    {
        if (_picker is { } picker)
        {
            _picker = null;
            picker.Close();
            picker.Dispose();
        }
    }
}
