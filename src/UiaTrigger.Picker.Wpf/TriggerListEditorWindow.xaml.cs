// トリガ一覧エディタの WPF View (docs/DESIGN.md §4)。
//
// 振る舞いは TriggerListEditorPresenter (UiaTrigger.Picker.Core) が持つ。ここに残るのは
// どれも「WPF でしかそうならない」ことだけである:
//   ・ShowDialog による本物のモーダル (WinUI3 には窓単位のモーダルが無い)
//   ・子ピッカーの所有者付けと後始末
//   ・ラベルをコードで代入すること (WPF に x:Uid は無い)
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using UiaTrigger.Models;

namespace UiaTrigger.Picker.Wpf;

/// <summary>The trigger-list editor as a WPF dialog: add, edit, remove, combine and take apart.</summary>
/// <remarks>
/// Holds no rules of its own — <see cref="TriggerListEditorPresenter"/> does. Works on a copy: the
/// list handed in is never modified, and the edited list comes back from
/// <see cref="EditAsync(Window, IReadOnlyList{TriggerDefinition})"/>. Where the triggers are stored,
/// and whether a monitor is running, stays the host's business.
/// </remarks>
public partial class TriggerListEditorWindow : Window, ITriggerListEditorView
{
    private readonly IPickerStrings _strings;
    private readonly TriggerListEditorPresenter _presenter;
    private readonly Func<TriggerPickerWindow> _createPicker;

    /// <summary>開いている子ピッカー。同時に 1 枚だけ開く。</summary>
    private TriggerPickerWindow? _picker;

    /// <summary>[OK] で確定した結果。null = キャンセル (窓を閉じただけ)。</summary>
    private IReadOnlyList<TriggerDefinition>? _result;

    /// <summary>一覧を差し替えている最中か (その間の選択変化はユーザー操作ではない)。</summary>
    private bool _suppressSelectionChanged;

    /// <summary>Shows the editor modally and returns the edited list, or null when cancelled.</summary>
    /// <param name="owner">The window to own the dialog, or null for none.</param>
    /// <param name="triggers">The triggers to edit. Neither the list nor its items are modified.</param>
    /// <returns>The edited triggers, or null when the user cancelled.</returns>
    /// <remarks>
    /// Call it on the thread that owns <paramref name="owner"/>. The returned task is already
    /// complete when it comes back — the dialog runs a nested message loop, so this returns once the
    /// user is done. The asynchronous shape is what the three variants have in common: WinUI 3 has
    /// no window-modal dialog, so its editor cannot offer a blocking call at all.
    /// </remarks>
    public static Task<IReadOnlyList<TriggerDefinition>?> EditAsync(
        Window? owner, IReadOnlyList<TriggerDefinition> triggers)
        => EditAsync(owner, triggers, new Win32CursorSource());

    /// <summary>
    /// Same, with the child picker taking the pointer position from <paramref name="cursor"/>.
    /// </summary>
    /// <param name="owner">The window to own the dialog, or null for none.</param>
    /// <param name="triggers">The triggers to edit. Neither the list nor its items are modified.</param>
    /// <param name="cursor">Where the child picker's hover dwell reads the pointer position from.</param>
    /// <returns>The edited triggers, or null when the user cancelled.</returns>
    /// <remarks><inheritdoc cref="EditAsync(Window, IReadOnlyList{TriggerDefinition})" path="/remarks"/></remarks>
    public static Task<IReadOnlyList<TriggerDefinition>?> EditAsync(
        Window? owner, IReadOnlyList<TriggerDefinition> triggers, ICursorSource cursor)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        ArgumentNullException.ThrowIfNull(cursor);

        var window = new TriggerListEditorWindow(
            new ResxPickerStrings(), triggers, createPresenter: null, () => new TriggerPickerWindow(cursor))
        {
            Owner = owner,
        };
        window.ShowDialog();
        return Task.FromResult(window._result);
    }

    // internal だが、公開されている多重定義と名前が同じなので PublicApiDocumentationTests は
    // これも公開 API として扱う (名前だけで照合するため)。よって doc は英語で書く。

    /// <summary>For tests: substitutes the strings, the presenter and the child picker.</summary>
    internal TriggerListEditorWindow(
        IPickerStrings strings,
        IReadOnlyList<TriggerDefinition> triggers,
        Func<ITriggerListEditorView, TriggerListEditorPresenter>? createPresenter,
        Func<TriggerPickerWindow>? createPicker)
    {
        InitializeComponent();

        _strings = strings;
        ApplyStrings();
        _createPicker = createPicker ?? (() => new TriggerPickerWindow());

        // presenter はコンストラクターの中で最初の ShowRows を呼ぶので、
        // 一覧のコントロールが出来上がった後に作る
        _presenter = createPresenter?.Invoke(this)
            ?? new TriggerListEditorPresenter(this, strings, triggers);

        // 閉じるときに子ピッカーを取り残さない。オーバーレイの低レベルキーボードフックが
        // 張られたまま残るので、これは見えない副作用になる
        Closed += (_, _) => CloseThePicker();
    }

    /// <summary>ラベルをリソースから代入する (WPF に <c>x:Uid</c> は無い)。</summary>
    private void ApplyStrings()
    {
        Title = _strings.GetString(EditorStringKeys.WindowTitle);
        AddTriggerButton.Content = _strings.GetString(EditorStringKeys.AddButtonContent);
        EditTriggerButton.Content = _strings.GetString(EditorStringKeys.EditButtonContent);
        DeleteTriggerButton.Content = _strings.GetString(EditorStringKeys.DeleteButtonContent);
        CombineTriggersButton.Content = _strings.GetString(EditorStringKeys.CombineButtonContent);
        DecomposeTriggerButton.Content = _strings.GetString(EditorStringKeys.DecomposeButtonContent);
        AcceptButton.Content = _strings.GetString(EditorStringKeys.AcceptButtonContent);
        CancelButton.Content = _strings.GetString(EditorStringKeys.CancelButtonContent);
        ExpressionBoxLabel.Text = _strings.GetString(EditorStringKeys.ExpressionBoxHeader);
        UnwatchedBoxLabel.Text = _strings.GetString(EditorStringKeys.UnwatchedBoxHeader);
        CombinePollIntervalBoxLabel.Text =
            _strings.GetString(EditorStringKeys.CombinePollIntervalBoxHeader);
        CombineStoppedMatchingCheck.Content =
            _strings.GetString(EditorStringKeys.CombineStoppedMatchingCheckContent);
        // 一覧は見出しを持たないので、読み上げに出るのはこの名前だけである
        AutomationProperties.SetName(
            EditorTriggerList, _strings.GetString(EditorStringKeys.TriggerListAutomationName));
    }

    /// <summary>
    /// 下段 (まとめる / 状態 / 確定) に「使える高さ」を上限として渡す。
    /// </summary>
    /// <remarks>
    /// ピッカーの <c>OnMainPaneSizeChanged</c> と同じ理由である (docs/DESIGN.md §12):
    /// <c>Auto</c> の行は子に「欲しいだけ」与えるので、上限を渡さない限り
    /// <c>LowerPane</c> のビューポートは中身と同じ高さになり、**スクロールが一生起きない**。
    /// 窓を縮めると Grid が下段を窓の外へ黙って切り落とすだけになる。
    /// 引くもの (上段の実寸と余白・一覧の最小高さ) は XAML 側が正である。
    /// </remarks>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double reserved = TopBar.ActualHeight + TopBar.Margin.Bottom + Root.RowDefinitions[1].MinHeight;
        double available = Math.Max(80, e.NewSize.Height - reserved);

        // 変わっていないときに代入しない (レイアウトを無用に回さないため)
        if (Math.Abs(LowerPane.MaxHeight - available) > 0.5)
        {
            LowerPane.MaxHeight = available;
        }
    }

    // ---------- ユーザー操作 → プレゼンター ----------

    private void OnAdd(object sender, RoutedEventArgs e) => _presenter.NotifyAddRequested();

    private void OnEdit(object sender, RoutedEventArgs e) => _presenter.NotifyEditRequested();

    /// <summary>
    /// 行のダブルクリック = [条件を編集]。編集できない選択 (複合・複数選択) は presenter が
    /// ボタンと同じ理由をステータスへ出す。
    /// </summary>
    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 行の上だけを編集にする。一覧の空白部分ではコンテナが引けない —
        // 無視しないと、選択済みの行が空白のダブルクリックで編集され始める
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(EditorTriggerList, source) is not ListBoxItem)
        {
            return;
        }
        // ハンドラの中で直接開かない。ダブルクリックの入力系列が残ったまま子ピッカーを
        // 開くと、残りの入力処理がエディタへフォーカスを戻す (実測: 所有関係があるので
        // 重なりは上のままだが、フォーカスがピッカーに来ない。WinUI は所有関係が無いので
        // 窓ごと後ろに出る)。入力が掃けた後 (Background) に回す
        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            () => _presenter.NotifyEditRequested());
    }

    /// <summary>
    /// 選択が変わった。**自分で一覧を差し替えている最中は報告しない** —
    /// <c>ItemsSource</c> の代入は選択を落として <c>SelectionChanged</c> を鳴らすが、
    /// あれはユーザーが選択を変えたのではない。
    /// </summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressSelectionChanged)
        {
            _presenter.NotifySelectionChanged();
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e) => _presenter.NotifyDeleteRequested();

    private void OnCombine(object sender, RoutedEventArgs e) => _presenter.NotifyCombineRequested();

    private void OnDecompose(object sender, RoutedEventArgs e) => _presenter.NotifyDecomposeRequested();

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        Accept();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    /// <summary>編集結果を確定する。テストは <c>ShowDialog</c> を使えないのでここを直に呼ぶ。</summary>
    internal void Accept() => _result = _presenter.Snapshot();

    /// <summary>いまの結果 (null = 未確定 = キャンセル)。</summary>
    internal IReadOnlyList<TriggerDefinition>? Result => _result;

    // ---------- ITriggerListEditorView ----------

    string ITriggerListEditorView.Status { set => EditorStatus.Text = value; }

    string ITriggerListEditorView.ExpressionText
    {
        get => ExpressionBox.Text ?? string.Empty;
        set => ExpressionBox.Text = value;
    }

    string ITriggerListEditorView.UnwatchedText
    {
        get => UnwatchedBox.Text ?? string.Empty;
        set => UnwatchedBox.Text = value;
    }

    // 空欄も読めない入力も「値なし」。ピッカーの数値欄と同じ規則 (TriggerPickerWindow.ReadNumber)。
    // **書くほうも同じ組で使うこと** — 書式が読みとずれると、読み込んだ値をそのまま確定するだけで
    // 設定が変わる (例外にもならない)
    double? ITriggerListEditorView.CombinePollIntervalSeconds
    {
        get => TriggerPickerWindow.ReadNumber(CombinePollIntervalBox);
        set => TriggerPickerWindow.WriteNumber(CombinePollIntervalBox, value);
    }

    bool ITriggerListEditorView.CombineNotifyOnStoppedMatching
    {
        get => CombineStoppedMatchingCheck.IsChecked == true;
        set => CombineStoppedMatchingCheck.IsChecked = value;
    }

    string ITriggerListEditorView.CombineCaption { set => CombineTriggersButton.Content = value; }

    /// <summary>選択されている行の index。</summary>
    /// <remarks>
    /// <para>
    /// WPF の <c>SelectedItems</c> は**項目**を返すので、index は引き戻すしかない。素朴に
    /// <c>Items.IndexOf</c> を使うと、同じ文字列に描画される 2 行が同じ index に潰れて
    /// **選択の件数が減る** — presenter は「複合ちょうど 1 件」で書き換えに入るので、
    /// 2 行選んだつもりが 1 件と見なされ、単独指定していない複合を書き換えうる。
    /// </para>
    /// <para>
    /// そこで一致した項目を 1 つずつ消し込みながら前から拾う。行がすべて異なる通常の場合は
    /// <c>IndexOf</c> と同じ答えになり、重複したときも件数だけは合う。
    /// </para>
    /// </remarks>
    IReadOnlyList<int> ITriggerListEditorView.SelectedIndices
    {
        get
        {
            List<object> remaining = [.. EditorTriggerList.SelectedItems.Cast<object>()];
            var indices = new List<int>(remaining.Count);
            for (int i = 0; i < EditorTriggerList.Items.Count && remaining.Count > 0; i++)
            {
                int at = remaining.IndexOf(EditorTriggerList.Items[i]);
                if (at >= 0)
                {
                    remaining.RemoveAt(at);
                    indices.Add(i);
                }
            }
            return indices;
        }
    }

    void ITriggerListEditorView.SelectRow(int index)
    {
        // presenter が駆動しているので報告し返さない (欄も文言も presenter が直後に書く)
        _suppressSelectionChanged = true;
        try
        {
            EditorTriggerList.SelectedIndex = index;
        }
        finally
        {
            _suppressSelectionChanged = false;
        }
    }

    // 継ぎ目が渡すリストは配列にしてから ItemsSource へ渡す (プレゼンター側のリストは
    // 更新され続けないため、コントロールに参照を持たせたままにしない)
    void ITriggerListEditorView.ShowRows(IReadOnlyList<string> rows)
    {
        // 差し替えは選択を落として SelectionChanged を鳴らす。**コンストラクターの最初の
        // ShowRows もここを通る** — ハンドラーは InitializeComponent で結ばれるので、
        // _presenter の代入より前に鳴りうる。この try/finally がその窓もふさぐ
        _suppressSelectionChanged = true;
        try
        {
            EditorTriggerList.ItemsSource = rows.ToArray();
        }
        finally
        {
            _suppressSelectionChanged = false;
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

        TriggerPickerWindow picker = _createPicker();
        _picker = picker;
        picker.TriggerCommitted += (_, e) => _presenter.NotifyPickerCommitted(e.Definition);
        picker.Closed += (_, _) =>
        {
            _picker = null;
            _presenter.NotifyPickerClosed();
        };
        // エディタのほうが親である。ピッカーを開いたままエディタを閉じられると、
        // 結果を返した後もフックが残る
        picker.Owner = this;
        picker.Show();
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
