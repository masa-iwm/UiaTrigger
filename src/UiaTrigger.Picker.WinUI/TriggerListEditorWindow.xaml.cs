// トリガ一覧エディタの WinUI 3 View (docs/DESIGN.md §4)。
//
// 振る舞いは TriggerListEditorPresenter が持つ。ここに残るのは「WinUI3 でしかそうならない」
// ことだけである:
//   ・窓単位のモーダルが無いので、完了は TaskCompletionSource + Closed で伝える
//   ・MRT Core からの文字列解決 (ラベルは x:Uid、Title だけコードで)
//   ・ListView の SelectedRanges から選択行を組み立てること
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using UiaTrigger.Models;

namespace UiaTrigger.Picker.WinUI;

/// <summary>The trigger-list editor as a WinUI window: add, edit, remove, combine, take apart.</summary>
/// <remarks>
/// Holds no rules of its own — <see cref="TriggerListEditorPresenter"/> does. Works on a copy: the
/// list handed in is never modified, and the edited list comes back from
/// <see cref="EditAsync(IReadOnlyList{TriggerDefinition})"/>.
/// </remarks>
public sealed partial class TriggerListEditorWindow : Window, ITriggerListEditorView
{
    /// <summary>
    /// 表示領域の既定サイズ (96 DPI 基準)。**WPF / Windows Forms のエディタと同じ値である**
    /// (<c>PickerWindowDefaultSizeTests</c>)。
    /// </summary>
    private const int DefaultWidth = 900;
    private const int DefaultHeight = 560;

    private readonly TriggerListEditorPresenter _presenter;
    private readonly ICursorSource _cursor;

    private readonly TaskCompletionSource<IReadOnlyList<TriggerDefinition>?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>開いている子ピッカー。同時に 1 枚だけ開く。</summary>
    private TriggerPickerWindow? _picker;

    private IReadOnlyList<TriggerDefinition>? _result;

    /// <summary>一覧を差し替えている最中か (その間の選択変化はユーザー操作ではない)。</summary>
    private bool _suppressSelectionChanged;

    /// <summary>Shows the editor and completes with the edited list, or null when it is closed.</summary>
    /// <param name="triggers">The triggers to edit. Neither the list nor its items are modified.</param>
    /// <returns>The edited triggers, or null when the user closed the window without accepting.</returns>
    /// <remarks>
    /// **The window is modeless.** WinUI has no window-modal dialog, so the caller stays usable
    /// while the editor is open; keep the user from opening a second one — disabling whatever opened
    /// it until the task completes is enough. The asynchronous shape is what the three variants have
    /// in common, and this is the variant that forces it.
    /// </remarks>
    public static Task<IReadOnlyList<TriggerDefinition>?> EditAsync(
        IReadOnlyList<TriggerDefinition> triggers)
        => EditAsync(triggers, new Win32CursorSource());

    /// <summary>
    /// Same, with the child picker taking the pointer position from <paramref name="cursor"/>.
    /// </summary>
    /// <param name="triggers">The triggers to edit. Neither the list nor its items are modified.</param>
    /// <param name="cursor">Where the child picker's hover dwell reads the pointer position from.</param>
    /// <returns>The edited triggers, or null when the user closed the window without accepting.</returns>
    /// <remarks><inheritdoc cref="EditAsync(IReadOnlyList{TriggerDefinition})" path="/remarks"/></remarks>
    public static Task<IReadOnlyList<TriggerDefinition>?> EditAsync(
        IReadOnlyList<TriggerDefinition> triggers, ICursorSource cursor)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        ArgumentNullException.ThrowIfNull(cursor);

        var window = new TriggerListEditorWindow(triggers, cursor);
        window.Activate();
        return window._completion.Task;
    }

    private TriggerListEditorWindow(IReadOnlyList<TriggerDefinition> triggers, ICursorSource cursor)
    {
        InitializeComponent();
        // ピッカーの窓と同じ扱い (WindowDefaults の冒頭)
        WindowDefaults.ApplyClientSize(this, DefaultWidth, DefaultHeight);

        _cursor = cursor;
        var strings = new MrtPickerStrings();
        // Window は FrameworkElement ではないので x:Uid が効かない。ここだけコードで引く
        Title = strings.GetString(EditorStringKeys.WindowTitle);

        _presenter = new TriggerListEditorPresenter(this, strings, triggers);
        Closed += OnClosed;
        // **子ピッカーが開いている間は、活性化をあちらへ返す。**
        //
        // 所有関係 (WindowOwnership) が固定するのは**重なり**だけである。子ピッカーは
        // 前面に出るが、開いた直後の入力処理がエディタを活性化し直すと**フォーカスだけが
        // こちらへ戻る** (実測: 要素選択の窓が active で出た後、表示が済んだあたりで
        // フォーカスがエディタへ移る)。そうなるとキーはどちらの窓でも効かなくなる —
        // 見えている前面の窓に打っているのに反応しない、という形になる。
        //
        // WPF の Owner / Windows Forms の Show(owner) は活性化まで面倒を見るので、
        // これが要るのはこの変種だけである
        Activated += OnActivated;
        // Enter = [OK] / Esc = [キャンセル]。**この変種にだけ配線が要る** — WPF は
        // IsDefault / IsCancel、Windows Forms は AcceptButton / CancelButton を持つが、
        // WinUI3 には相当するものが無い。**バブリングの KeyDown で受けること**:
        // 先取りすると、コンボの一覧が開いている最中の Esc まで奪って窓ごと閉じてしまう。
        // 下段の入力欄はここへ来る前に Enter を取る (OnCombineFieldKeyDown — A25)
        if (Content is UIElement root)
        {
            root.KeyDown += OnKeyDown;
        }
        // NumberBox は Enter を自分で処理して Handled にする (値の確定)。素の KeyDown 購読では
        // 呼ばれないので、**処理済みでも受け取る**形で足す (OnCombineFieldKeyDown の remarks)。
        // クラスハンドラーが先に走るので、こちらが読む Value は確定済みの値である
        CombinePollIntervalBox.AddHandler(
            UIElement.KeyDownEvent, new KeyEventHandler(OnCombineFieldKeyDown), handledEventsToo: true);
    }

    /// <summary>子ピッカーが開いている間にこちらが活性化されたら、あちらへ返す。</summary>
    /// <remarks>
    /// ピンポンにはならない — これで子が活性化されるとこちらは非活性になり、
    /// <c>Deactivated</c> ではここへ来ないためである。
    /// </remarks>
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated && _picker is { } picker)
        {
            picker.Activate();
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // 子ピッカーを取り残さない (オーバーレイの低レベルキーボードフックが残る)。
        // 結果を伝えるのはその**後**である — 呼び出し元が結果を受けて次を始めたときに、
        // まだ生きているピッカーが残っていない形にする
        if (_picker is { } picker)
        {
            _picker = null;
            picker.Close();
            picker.Dispose();
        }
        _ = _completion.TrySetResult(_result);
    }

    /// <summary>
    /// 下段 (まとめる / 状態 / 確定) に「使える高さ」を上限として渡す。
    /// </summary>
    /// <remarks>
    /// ピッカーの <c>OnMainPaneSizeChanged</c> と同じ理由である (docs/DESIGN.md §12):
    /// <c>Auto</c> の行は子に「欲しいだけ」与えるので、上限を渡さない限り
    /// <c>LowerPane</c> のビューポートは中身と同じ高さになり、**スクロールが一生起きない**。
    /// 窓を縮めると Grid が下段を黙って潰すだけになる (実測 175%: OK/キャンセルが 8px)。
    /// 引くもの (上段の実寸・一覧の最小高さ・余白) は XAML 側が正である。
    /// </remarks>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double reserved = TopBar.ActualHeight + Root.RowDefinitions[1].MinHeight
            + Root.Padding.Top + Root.Padding.Bottom + (2 * Root.RowSpacing);
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
    /// 行のダブルタップ = [条件を編集]。編集できない選択 (複合・複数選択) は presenter が
    /// ボタンと同じ理由をステータスへ出す。
    /// </summary>
    [WinRT.DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private void OnRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // 行の上だけを編集にする。一覧の空白部分では DataContext が行の文字列にならない —
        // 無視しないと、選択済みの行が空白のダブルクリックで編集され始める
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not string)
        {
            return;
        }
        // ハンドラの中で直接開かない。ダブルクリックの入力系列が残ったまま子ピッカーを
        // Activate すると、残りの入力処理がエディタを前面へ戻し、**ピッカーが後ろに出る**
        // (実測: 直接開くと picker BEHIND editor / foreground=editor)。
        // 入力が掃けた後 (Low) に回すと前面 + フォーカス付きで開く
        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => _presenter.NotifyEditRequested());
    }

    /// <summary>
    /// 選択が変わった。**自分で一覧を差し替えている最中は報告しない** —
    /// <c>ItemsSource</c> の代入は選択を落として <c>SelectionChanged</c> を鳴らすが、
    /// あれはユーザーが選択を変えたのではない。
    /// </summary>
    /// <remarks>
    /// この変種だけは通知が遅れて (抑止が解けた後に) 来ることがありうるが、そのときの選択は
    /// 空なので presenter は何も埋めず、文言を書き直すだけである — 打ちかけの式は消えない。
    /// </remarks>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressSelectionChanged)
        {
            _presenter.NotifySelectionChanged();
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e) => _presenter.NotifyDeleteRequested();

    /// <summary>
    /// 下段の入力欄での Enter は [まとめる/更新] (docs/DESIGN.md A25 / §4)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 3 欄は押して初めて効く — <c>Snapshot</c> は式を読まないので、
    /// <see cref="OnKeyDown"/> の <c>Enter</c> (= 他の 2 変種の既定ボタン) に流すと
    /// **打ちかけの式を捨てて窓が閉じる**。<c>Handled</c> を立ててルートへ行かせない。
    /// </para>
    /// <para>
    /// <c>CombinePollIntervalBox</c> は <c>NumberBox</c> で、Enter を自分で使う (値の確定) ため
    /// 素の <c>KeyDown</c> 購読では呼ばれない。あちらだけはコンストラクターで
    /// <c>handledEventsToo</c> 付きに足してある — 欄が Enter を使うこと自体は正しく、
    /// **そのうえでこちらも走らせたい**からである (値を確定してからまとめる)。
    /// </para>
    /// <para>
    /// 掛けるのは 3 欄だけである。一覧や [OK] / [キャンセル] にフォーカスがあるときの
    /// Enter は [OK] のままにする。
    /// </para>
    /// </remarks>
    private void OnCombineFieldKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _presenter.NotifyCombineRequested();
        }
    }

    private void OnCombine(object sender, RoutedEventArgs e) => _presenter.NotifyCombineRequested();

    private void OnDecompose(object sender, RoutedEventArgs e) => _presenter.NotifyDecomposeRequested();

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        _result = _presenter.Snapshot();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    /// <summary>Enter = 確定、Esc = 取り消し (他の 2 変種の既定ボタンに合わせる)。</summary>
    /// <remarks>
    /// <para>
    /// **子ピッカーが開いている間、キーはあちらのものである。**ここでそのまま拾うと、
    /// 子を閉じたつもりでエディタごと閉じる (閉じたエディタは <c>OnClosed</c> で子も閉じる)。
    /// かといって**握り潰すと今度はどちらも動かない** — 行のダブルクリックで開いたときは
    /// フォーカスが一覧に残るので、キーは子ピッカーへ届かず、ここで捨てられて終わる。
    /// </para>
    /// <para>
    /// そこで**子ピッカーへ回す**: Esc はあれを閉じ、Enter はあれに任せる (編集セッションなら
    /// 確定して閉じる)。WPF / Windows Forms は既定ボタンと所有関係で自然にそうなるので、
    /// 明示的に書く必要があるのはこの変種だけである。
    /// </para>
    /// </remarks>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_picker is { } picker)
        {
            if (e.Key is Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                picker.Close();
            }
            return;
        }
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                OnAccept(this, new RoutedEventArgs());
                break;
            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                Close();
                break;
            default:
                break;
        }
    }

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

    // NumberBox は「空欄」を NaN で表す。この変換は WinUI の事実なので View に残す。
    // **書くほうも同じ約束で** — null を 0 で書くと「値なし」が「0 秒」に化ける
    double? ITriggerListEditorView.CombinePollIntervalSeconds
    {
        get => double.IsNaN(CombinePollIntervalBox.Value) ? null : CombinePollIntervalBox.Value;
        set => CombinePollIntervalBox.Value = value ?? double.NaN;
    }

    bool ITriggerListEditorView.CombineNotifyOnStoppedMatching
    {
        get => CombineStoppedMatchingCheck.IsChecked == true;
        set => CombineStoppedMatchingCheck.IsChecked = value;
    }

    string ITriggerListEditorView.CombineCaption { set => CombineTriggersButton.Content = value; }

    // **具体型へ明示的にキャストする** (docs/DESIGN.md §12)。
    // IReadOnlyList<int> を狙ったコレクション式は具体型が決まらず、
    // WinRT 経路では trim / AOT で壊れる (CsWinRT1032)。
    IReadOnlyList<int> ITriggerListEditorView.SelectedIndices =>
        (int[])[.. EditorTriggerList.SelectedRanges
            .SelectMany(r => Enumerable.Range(r.FirstIndex, (int)r.Length))];

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

    // 継ぎ目が渡すリストは配列にしてから ItemsSource へ渡す。WinUI はインターフェース型の
    // リストをここで受け付けないうえ、プレゼンター側のリストは更新され続けない
    void ITriggerListEditorView.ShowRows(IReadOnlyList<string> rows)
    {
        // 差し替えは選択を落として SelectionChanged を鳴らす。ユーザーが選択を変えたのでは
        // ないので報告しない (3 変種で同じ規律)
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

        var picker = new TriggerPickerWindow(_cursor);
        _picker = picker;
        picker.TriggerCommitted += (_, e) => _presenter.NotifyPickerCommitted(e.Definition);
        picker.Closed += (_, _) =>
        {
            _picker = null;
            _presenter.NotifyPickerClosed();
        };
        // エディタのほうが親である (WPF の Owner / Windows Forms の Show(this) と同じ意味)。
        // 所有関係が無いと重なりが活性化の順序で決まり、ダブルクリックの入力残りが
        // エディタを前面へ戻した瞬間に子ピッカーが後ろへ隠れる (WindowOwnership の冒頭)
        WindowOwnership.SetOwner(picker, this);
        picker.Activate();
        if (definitionToEdit is not null)
        {
            picker.LoadDefinition(definitionToEdit, editSession: true);
        }
    }
}
