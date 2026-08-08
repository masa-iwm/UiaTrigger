// ピッカーの WinUI 3 View。
//
// 振る舞いは TriggerPickerPresenter (UiaTrigger.Picker.Core) が持つ。ここに残っているのは
// どれも「WinUI3 でしかそうならない」ことだけである:
//   ・TreeViewItem のコンテナ実体化を待つリトライ (WPF は ItemContainerGenerator、
//     WinForms は仮想化が無いので不要 — 共通化すると 3 つとも間違った最小公倍数になる)
//   ・NumberBox の空欄が NaN であること
//   ・bool → Visibility の変換 (x:Bind に暗黙変換が無い)
//   ・MRT Core からの文字列解決
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using UiaTrigger.Models;

namespace UiaTrigger.Picker.WinUI;

/// <summary>The picker as a WinUI 3 window: pick an element, then turn it into a trigger.</summary>
/// <remarks>
/// Holds no rules of its own — <see cref="TriggerPickerPresenter"/> does. Committed triggers are
/// announced through <see cref="TriggerCommitted"/>; storing them is up to the host.
/// </remarks>
public sealed partial class TriggerPickerWindow : Window, IPickerView, IDisposable
{
    /// <summary>
    /// 表示領域の既定サイズ (96 DPI 基準)。**WPF の <c>Width</c>/<c>Height</c>、
    /// Windows Forms の <c>ClientSize</c> と同じ値である** (<c>PickerWindowDefaultSizeTests</c>)。
    /// </summary>
    private const int DefaultWidth = 1100;
    private const int DefaultHeight = 700;

    private readonly TriggerPickerPresenter _presenter;

    /// <summary>ツリー再構築の世代。差し替え後に古い展開/選択の遅延処理を破棄するために使う。</summary>
    private int _treeGeneration;
    private bool _suppressSelectionChanged;
    private bool _suppressCondChanged;
    /// <summary>編集セッションで開いているか (Enter を確定にしてよいか)。</summary>
    private bool _editSession;

    private bool _disposed;

    /// <summary>Raised when the user commits a trigger. Persisting it is up to the host.</summary>
    public event EventHandler<TriggerCommittedEventArgs>? TriggerCommitted;

    /// <summary>Creates the window and the presenter that drives it.</summary>
    public TriggerPickerWindow()
        : this(new Win32CursorSource())
    {
    }

    /// <summary>Creates the window, taking the pointer position from <paramref name="cursor"/>.</summary>
    /// <param name="cursor">Where hover dwell reads the pointer position from.</param>
    /// <remarks>
    /// For hosts that point the picker somewhere themselves rather than following the mouse. A UI
    /// test is the obvious case — hover dwell is how the picker starts, and moving a real mouse
    /// would be synthetic input (docs/TESTING.md §4) — but so is any host driven by something other
    /// than a mouse.
    /// </remarks>
    public TriggerPickerWindow(ICursorSource cursor)
    {
        InitializeComponent();
        // 他の 2 変種と同じ既定サイズ。WinUI3 の Window だけは XAML で宣言できない
        // (WindowDefaults の冒頭)。Activate される前なので、出したあとの縮みにはならない
        WindowDefaults.ApplyClientSize(this, DefaultWidth, DefaultHeight);

        var strings = new MrtPickerStrings();
        _presenter = new TriggerPickerPresenter(
            this, new WinUiDispatcher(DispatcherQueue), cursor, strings);
        _presenter.TriggerCommitted += (_, e) => TriggerCommitted?.Invoke(this, e);

        // Window は FrameworkElement ではないので x:Uid が効かない。ここだけコードで引く
        Title = strings.GetString(PickerStringKeys.WindowTitle);
        ElementTree.ItemsSource = _presenter.Roots;

        ViewCombo.ItemsSource = new[] { "Raw", "Control", "Content" };
        ViewCombo.SelectedIndex = 1;
        // TriggerOn は丸ごと列挙しない — StoppedMatching はイベント専用で、定義の lifecycle
        // としては保存できない (TriggerDraftValidator.DefinitionLifecycles)。
        // **配列に具象化してから渡す** — IReadOnlyList<素の列挙型> は CsWinRT が
        // CCW を組めず E_INVALIDARG になる (ShowProperties のコメントを参照)
        OnCombo.ItemsSource = TriggerDraftValidator.DefinitionLifecycles.ToArray();
        CondCombo.ItemsSource = Enum.GetValues<ComparisonOp>();

        // 見るのはウィンドウのアクティブ状態ではなく**ツリーがフォーカスを持っているか**である。
        // ホバー中はフォーカスがピッカーに残るので、ウィンドウ単位だと重なり切替 (←/→) が
        // 一度他アプリをクリックするまで使えない (docs/DESIGN.md §7)。
        //
        // アクティブ状態を見ないので、WinUI3 が初期化中に Activated/Deactivated を
        // 何度も上げることへの対処 (表示直後だけ通知を無視する猶予) も要らない。
        ElementTree.GotFocus += (_, _) => _presenter.SetTreeHasFocus(true);
        ElementTree.LostFocus += (_, _) => _presenter.SetTreeHasFocus(false);
        Closed += OnClosed;
        // Esc で閉じる。**バブリングの KeyDown で受けること** — 先取りすると、コンボの一覧が
        // 開いている最中の Esc まで奪って窓ごと閉じてしまう。一覧を閉じた側が Handled にするので
        // ここへは来ない。Window 自体は KeyDown を持たないので中身 (ルートの Grid) に付ける
        if (Content is UIElement root)
        {
            root.KeyDown += OnKeyDown;
        }

        // **NumberBox は Enter を自分で使う** (値の確定) ので Handled にする。素の KeyDown
        // 購読では呼ばれず、編集セッションの Enter = 更新 (下の OnKeyDown) がしきい値の欄では
        // 一度も効かない — 編集セッションの主用途がまさにその欄なので、WPF (IsDefault) /
        // WinForms (AcceptButton) と挙動が割れる (docs/DESIGN.md A25)。
        // エディタの CombinePollIntervalBox と同じ解決形を使う: 処理済みでも受け取る形で足すと、
        // クラスハンドラーが先に走るのでこちらが読む値は確定済みになる
        foreach (NumberBox box in new[]
        {
            ValueOperand, LowOperand, HighOperand, ToleranceOperand, MinIntervalOperand, PollIntervalOperand,
        })
        {
            box.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);
        }

        // マウス自動選択は既定で ON
        AutoSelectToggle.IsOn = true;
    }

    /// <summary>Opens this picker on an existing trigger so its condition can be edited.</summary>
    /// <param name="definition">
    /// The trigger to edit. The picker takes it over and writes the committed condition back into
    /// this instance, so pass a copy when the original has to stay as it is.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <see cref="TriggerPickerPresenter.CanEdit"/> is false for <paramref name="definition"/>.
    /// </exception>
    public void LoadDefinition(TriggerDefinition definition) => LoadDefinition(definition, editSession: false);

    /// <summary>
    /// Opens this picker on an existing trigger, optionally as an edit session that ends with its
    /// one commit (see
    /// <see cref="TriggerPickerPresenter.LoadDefinition(TriggerDefinition, bool)"/>).
    /// </summary>
    /// <param name="definition"><inheritdoc cref="LoadDefinition(TriggerDefinition)" path="/param[@name='definition']"/></param>
    /// <param name="editSession">
    /// True to change the commit button's caption and close the window after the successful commit.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <inheritdoc cref="LoadDefinition(TriggerDefinition)" path="/exception"/>
    /// </exception>
    public void LoadDefinition(TriggerDefinition definition, bool editSession)
    {
        // ホバー捕捉を先に止める。開いたまま読み込むと、マウスがどこかの要素の上に
        // 静止しているだけで**編集対象が別の要素に差し替わる**
        AutoSelectToggle.IsOn = false;
        // **この変種にだけ覚えておく必要がある。**WPF の IsDefault / Windows Forms の
        // AcceptButton に相当するものが WinUI3 に無いので、Enter は自分で見る (OnKeyDown)
        _editSession = editSession;
        _presenter.LoadDefinition(definition, editSession);
    }

    /// <summary>Turns hover capture off, as if the user had switched it off.</summary>
    /// <remarks>
    /// <para>
    /// Call this on the pickers that are already open before opening another one. The cursor
    /// position is global: every picker with hover capture on reads the same point and captures
    /// the same element, so the one that was already open silently follows the new one's mouse
    /// and loses the element it was showing. What it has already confirmed is unaffected.
    /// </para>
    /// <para>
    /// This is the same reason <see cref="LoadDefinition(TriggerDefinition)"/> switches capture off
    /// before it loads.
    /// </para>
    /// </remarks>
    public void StopAutoSelect() => AutoSelectToggle.IsOn = false;

    private void OnClosed(object sender, WindowEventArgs args) => Dispose();

    /// <summary>Esc = 取り消して閉じる。閉じれば <c>Closed</c> がフックとセッションを畳む。</summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                Close();
                break;
            case Windows.System.VirtualKey.Enter when _editSession:
                // **確定はこの窓を閉じる**ので、Esc と同じ理由で入力が掃けてから走らせる
                _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, _presenter.Commit);
                break;
            default:
                break;
        }
    }

    /// <summary>Releases the presenter: its timer, the overlay and the UI Automation session.</summary>
    /// <remarks>
    /// Closing the window does this already, so callers rarely need to. Safe to call more than once.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _presenter.Dispose();
    }

    // ---------- ユーザー操作 → プレゼンター ----------

    /// <summary>
    /// 要素ツリーの水平スクロールを有効にする。
    /// </summary>
    /// <remarks>
    /// <para>
    /// XAML で <c>ScrollViewer.HorizontalScrollMode</c> を <c>TreeView</c> に付けても効かない。
    /// <c>ListView</c> は自分に付いた添付プロパティをテンプレート内の <c>ScrollViewer</c> へ
    /// <c>TemplateBinding</c> で流すが、<c>TreeView</c> のテンプレートは内側の
    /// <c>TreeViewList</c> (= <c>ListView</c> 派生、部品名 <c>ListControl</c>) を置くだけで
    /// その中継をしないため、添付プロパティは**誰にも読まれず黙って無視される**。
    /// </para>
    /// <para>
    /// <c>ScrollViewer</c> を視覚ツリーから型で探す方法も使えない。発行済みバイナリでは
    /// <c>VisualTreeHelper</c> が返す RCW の型が基底 (<c>ContentControl</c> 等) に落ちており、
    /// <c>is ScrollViewer</c> が成立しないことを実測した。<c>ListControl</c> は
    /// <c>ListView</c> として解決できるので、そこへ添付プロパティを付けて中継させる。
    /// </para>
    /// </remarks>
    [WinRT.DynamicWindowsRuntimeCast(typeof(ListView))]
    private void OnElementTreeLoaded(object sender, RoutedEventArgs e)
    {
        if (FindDescendantByName(ElementTree, "ListControl") is not ListView list)
        {
            return; // テンプレート未適用。次に Loaded が来たときに掛かる
        }
        ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Enabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        // 横方向のレールが効いていると、斜めのホイール/タッチ操作で水平移動が抑制される
        ScrollViewer.SetIsHorizontalRailEnabled(list, false);
    }

    /// <summary>
    /// 条件欄に「使える高さ」を上限として渡す (docs/DESIGN.md §12)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 条件欄の行は <c>Auto</c> である — 掴まれるまでは内容ぶんの高さにするという決定
    /// (docs/DESIGN.md §12) を守るためで、そこは変えない。ところが **<c>Auto</c> の行は子に
    /// 「欲しいだけ」与える**ので、包んである <c>ScrollViewer</c> のビューポートが中身と
    /// 同じ高さになり、**スクロールが一生起きない**。窓を縮めると <c>Grid</c> が
    /// 下を黙って切り落とすだけになる。
    /// </para>
    /// <para>
    /// **この変種は T1 から組み立てられない** (docs/DESIGN.md §12) ので、退行の網は T4 側にある。
    /// WPF は同じ形の不具合を同じ形で持っており、あちらは T1 が見ている。
    /// </para>
    /// <para>
    /// 引く 2 つ (上段の最小高さ・区切りの実寸) はどちらも XAML 側が正である。
    /// </para>
    /// </remarks>
    private void OnMainPaneSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double reserved = MainPane.RowDefinitions[0].MinHeight + ConditionSplitter.ActualHeight;
        double available = Math.Max(MainPane.RowDefinitions[2].MinHeight, e.NewSize.Height - reserved);

        // 変わっていないときに代入しない (レイアウトを無用に回さないため)
        if (Math.Abs(ConditionScroll.MaxHeight - available) > 0.5)
        {
            ConditionScroll.MaxHeight = available;
        }

        // 折り返す文字列の幅は**こちらから渡す** (docs/DESIGN.md §12)。横スクロールを有効にすると
        // 測りに渡る幅が無限になり、TextWrapping="Wrap" が何もしなくなる — 1 行のまま
        // どこまでも伸び、**広い窓でも常に横バーが出る**ことになる。
        double textWidth = Math.Max(0, e.NewSize.Width - ScrollBarAllowance);
        if (Math.Abs(ConfirmedText.MaxWidth - textWidth) > 0.5)
        {
            ConfirmedText.MaxWidth = textWidth;
        }
    }

    /// <summary>
    /// 折り返し幅から引く、縦スクロールバーのぶんの余裕 (px)。
    /// </summary>
    /// <remarks>
    /// **バーが出ていなくても常に引く。**出た瞬間に折り返しが変わって背が伸び、
    /// その高さがまたバーの出入りを決める、という振動を避けるためである
    /// (Windows Forms 側が <c>SizeTheConditionPane</c> で同じ理由から
    /// 「狭いほうの幅」で測っている)。実際のバーは 12〜16px なので多めであり、
    /// **多い側に外すぶんには文字が少し早く折り返すだけ**で壊れない。
    /// </remarks>
    private const double ScrollBarAllowance = 20;

    [WinRT.DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private static FrameworkElement? FindDescendantByName(DependencyObject root, string name)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && string.Equals(element.Name, name, StringComparison.Ordinal))
            {
                return element;
            }
            if (FindDescendantByName(child, name) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }

    private void OnAutoSelectToggled(object sender, RoutedEventArgs e)
        => _presenter.SetAutoSelect(AutoSelectToggle.IsOn);

    private void OnViewChanged(object sender, SelectionChangedEventArgs e)
        => _presenter.SetViewMode(ViewCombo.SelectedIndex switch
        {
            0 => TreeViewMode.Raw,
            2 => TreeViewMode.Content,
            _ => TreeViewMode.Control,
        });

    /// <summary>
    /// 検索欄の Enter は「次を検索」。**必ず <c>Handled</c> を立てること**
    /// (docs/DESIGN.md A25)。
    /// </summary>
    /// <remarks>
    /// 立てないと KeyDown がルートまでバブルし、編集セッションでは
    /// <see cref="OnKeyDown"/> の <c>Enter</c> が確定を積む — **検索したうえで窓が閉じる**。
    /// </remarks>
    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            OnSearchNext(sender, e);
        }
    }

    private void OnSearchNext(object sender, RoutedEventArgs e)
        => _presenter.SearchNext(SearchBox.Text ?? string.Empty);

    private void OnTreeSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (_suppressSelectionChanged)
        {
            return; // プレゼンターがツリーを差し替えている最中 = ユーザー操作ではない
        }
        // ElementTree.SelectedItem はこのイベント発火時点でまだ更新前の値を返すことがあるため、
        // 確実に新しい選択を指す args.AddedItems を優先する
        PickerTreeNode? node =
            args.AddedItems.FirstOrDefault() as PickerTreeNode ?? ElementTree.SelectedItem as PickerTreeNode;
        if (node is not null)
        {
            _presenter.NotifyTreeSelectionChanged(node);
        }
    }

    private void OnTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is PickerTreeNode node)
        {
            _presenter.NotifyTreeItemInvoked(node);
        }
    }

    [WinRT.DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private void OnTreeConfirmClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PickerTreeNode node)
        {
            _presenter.ConfirmNode(node);
        }
    }

    private void OnCondUiChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressCondChanged)
        {
            _presenter.ConditionShapeChanged(
                OnCombo.SelectedItem is TriggerOn on ? on : TriggerOn.PropertyChanged,
                CondCombo.SelectedItem is ComparisonOp op ? op : ComparisonOp.Always);
        }
    }

    private void OnCommit(object sender, RoutedEventArgs e) => _presenter.Commit();

    // ---------- IPickerView ----------

    string IPickerView.Hint { set => HintText.Text = value; }

    string IPickerView.ConfirmedText { set => ConfirmedText.Text = value; }

    string IPickerView.CommitStatus { set => CommitStatus.Text = value; }

    string IPickerView.CommitCaption { set => CommitButton.Content = value; }

    // IPickerView.Close は Window.Close が暗黙に実装する (窓を閉じる以上の仕事は無い)

    string IPickerView.KeyText
    {
        get => KeyBox.Text ?? string.Empty;
        set => KeyBox.Text = value;
    }

    string IPickerView.DisplayNameText
    {
        get => DisplayNameBox.Text ?? string.Empty;
        set => DisplayNameBox.Text = value;
    }

    PickerTreeNode? IPickerView.SelectedNode => ElementTree.SelectedItem as PickerTreeNode;

    // ItemsSource へは **必ず配列にしてから**渡すこと。
    //
    // ItemsSource は WinRT の ABI 上 object であり、CsWinRT は「代入地点の静的な型」を見て
    // CCW を組む。継ぎ目が渡してくる IReadOnlyList<T> は IVectorView<T> として載せようとするが、
    // T が WinRT 射影を持たない素の .NET 型 (ここでは TriggerProperty 列挙) だと組めず
    // E_INVALIDARG になる — 「Value does not fall within the expected range.」という
    // **投げた場所が一切分からない**例外として出る。AOT でも JIT でも同じ。
    //
    // IReadOnlyList<string> がたまたま通るのは IVectorView<HSTRING> が実在の射影だからで、
    // 通っているのは偶然に近い。ここは「その UI フレームワークの事実」なので View に置く。
    // T1 は WinUI を読み込めないため、担保は docs/MANUAL-CHECKS.md §4.3 である。

    void IPickerView.ShowProperties(IReadOnlyList<string> rows)
        => PropsList.ItemsSource = rows.Count == 0 ? null : rows.ToArray();

    void IPickerView.ShowTriggerShape(IReadOnlyList<TriggerProperty> properties, TriggerOn on, ComparisonOp op)
    {
        // 4 つのコンボを続けて書き換えると SelectionChanged が都度飛ぶ。途中の中途半端な
        // 組み合わせで欄の出し分けをやり直さないよう抑止する (プレゼンターが直後に一度だけ指示する)
        _suppressCondChanged = true;
        try
        {
            PropCombo.ItemsSource = properties.ToArray();
            PropCombo.SelectedIndex = 0;
            OnCombo.SelectedItem = on;
            CondCombo.SelectedItem = op;
        }
        finally
        {
            _suppressCondChanged = false;
        }
    }

    void IPickerView.ShowOperands(OperandVisibility visibility)
    {
        PropCombo.IsEnabled = visibility.PropertyChoiceEnabled;
        TextOperand.Visibility = ToVisibility(visibility.Text);
        ValueOperand.Visibility = ToVisibility(visibility.Value);
        LowOperand.Visibility = ToVisibility(visibility.Range);
        HighOperand.Visibility = ToVisibility(visibility.Range);
        ToleranceOperand.Visibility = ToVisibility(visibility.Tolerance);
        PollIntervalOperand.Visibility = ToVisibility(visibility.PollInterval);
        StoppedMatchingCheck.Visibility = ToVisibility(visibility.StoppedMatching);
    }

    void IPickerView.SetCommitEnabled(bool enabled) => CommitButton.IsEnabled = enabled;

    TriggerDraft? IPickerView.ReadDraft()
    {
        if (PropCombo.SelectedItem is not TriggerProperty property ||
            CondCombo.SelectedItem is not ComparisonOp op ||
            OnCombo.SelectedItem is not TriggerOn on)
        {
            return null;
        }
        // NumberBox は「空欄」を NaN で表す。この変換は WinUI の事実なので View に残す
        static double? D(NumberBox box) => double.IsNaN(box.Value) ? null : box.Value;
        return new TriggerDraft
        {
            Id = KeyBox.Text,
            DisplayName = DisplayNameBox.Text,
            On = on,
            Property = property,
            Op = op,
            Text = TextOperand.Text,
            Value = D(ValueOperand),
            Low = D(LowOperand),
            High = D(HighOperand),
            Tolerance = D(ToleranceOperand),
            // 発火レート制限 (docs/DESIGN.md C11)。BoundingRectangle のようにドラッグ中は
            // 毎フレーム変わるプロパティを監視するときに要る
            MinIntervalSeconds = D(MinIntervalOperand),
            PollIntervalSeconds = D(PollIntervalOperand),
            NotifyOnStoppedMatching = StoppedMatchingCheck.IsChecked == true,
        };
    }

    void IPickerView.ShowDraft(TriggerDraft draft)
    {
        // ShowTriggerShape と同じ理由で抑止する (途中の組み合わせで欄の出し分けをやり直させない)
        _suppressCondChanged = true;
        try
        {
            KeyBox.Text = draft.Id ?? string.Empty;
            DisplayNameBox.Text = draft.DisplayName ?? string.Empty;
            OnCombo.SelectedItem = draft.On;
            PropCombo.SelectedItem = draft.Property;
            CondCombo.SelectedItem = draft.Op;
        }
        finally
        {
            _suppressCondChanged = false;
        }
        TextOperand.Text = draft.Text ?? string.Empty;
        // 空欄は NaN。ReadDraft の D() の逆であり、この変換が WinUI の事実そのものである
        static void W(NumberBox box, double? value) => box.Value = value ?? double.NaN;
        W(ValueOperand, draft.Value);
        W(LowOperand, draft.Low);
        W(HighOperand, draft.High);
        W(ToleranceOperand, draft.Tolerance);
        W(MinIntervalOperand, draft.MinIntervalSeconds);
        W(PollIntervalOperand, draft.PollIntervalSeconds);
        StoppedMatchingCheck.IsChecked = draft.NotifyOnStoppedMatching;
    }

    void IPickerView.DiscardDeferredWork() => _treeGeneration++;

    void IPickerView.BeginNodeUpdate() => _suppressSelectionChanged = true;

    void IPickerView.EndNodeUpdate() => _suppressSelectionChanged = false;

    [WinRT.DynamicWindowsRuntimeCast(typeof(TreeViewItem))]
    void IPickerView.Expand(IReadOnlyList<PickerTreeNode> nodes)
    {
        foreach (PickerTreeNode node in nodes)
        {
            node.ExpandForDisplay();
            if (ElementTree.ContainerFromItem(node) is TreeViewItem container)
            {
                container.IsExpanded = true;
            }
        }
    }

    /// <summary>チェーンノードを上から順に (実体化を待って) 展開し、最後に対象を選択する。</summary>
    [WinRT.DynamicWindowsRuntimeCast(typeof(TreeViewItem))]
    void IPickerView.ExpandThenSelect(IReadOnlyList<PickerTreeNode> expandOrder, PickerTreeNode target)
    {
        int generation = _treeGeneration;
        void Step(int index, int attempt)
        {
            if (generation != _treeGeneration)
            {
                return; // ツリーが差し替えられた
            }
            if (index >= expandOrder.Count)
            {
                SelectDeferred(target, generation);
                return;
            }
            ElementTree.UpdateLayout();
            if (ElementTree.ContainerFromItem(expandOrder[index]) is TreeViewItem container)
            {
                // 表示のための展開なので子の全列挙は起こさない (ExpandForDisplay)。
                // 全列挙を抑止するかどうかはノード自身が持っており、View 側の抑止フラグは要らない
                expandOrder[index].ExpandForDisplay();
                container.IsExpanded = true;
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Step(index + 1, 0));
            }
            else if (attempt < 40)
            {
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Step(index, attempt + 1));
            }
        }
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Step(0, 0));
    }

    void IPickerView.SelectDeferred(PickerTreeNode node) => SelectDeferred(node, _treeGeneration);

    /// <summary>コンテナの実体化を待って選択・スクロールする。</summary>
    [WinRT.DynamicWindowsRuntimeCast(typeof(TreeViewItem))]
    private void SelectDeferred(PickerTreeNode node, int generation)
    {
        void TrySelect(int attempt)
        {
            // ツリー差し替え、またはユーザーが別ノードを選択したら古い選択要求は破棄する
            if (generation != _treeGeneration || !_presenter.IsCurrent(node))
            {
                return;
            }
            ElementTree.UpdateLayout();
            if (ElementTree.ContainerFromItem(node) is TreeViewItem container)
            {
                // プログラム選択で SelectionChanged が走り、ユーザー操作として
                // 報告されてしまわないよう抑止する
                _suppressSelectionChanged = true;
                try
                {
                    ElementTree.SelectedItem = node;
                    container.IsSelected = true;
                }
                finally
                {
                    _suppressSelectionChanged = false;
                }
                container.StartBringIntoView();
                return;
            }
            if (attempt < 20)
            {
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => TrySelect(attempt + 1));
            }
        }
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => TrySelect(0));
    }

    /// <summary>Turns a bool into a <see cref="Visibility"/> for the XAML to bind to.</summary>
    /// <param name="value">Whether the element should be visible.</param>
    /// <returns>The matching <see cref="Visibility"/>.</returns>
    /// <remarks>
    /// Public because <c>x:Bind</c> has no implicit bool-to-<see cref="Visibility"/> conversion and
    /// calls this as a function binding. The WPF variant keeps the same helper private — there the
    /// converter is a XAML resource — so this one member is public API in the WinUI package only.
    /// </remarks>
    public static Visibility ToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>WinUI3 の DispatcherQueue を継ぎ目に合わせる。</summary>
// partial は**入れ子の親も**要る (docs/DESIGN.md §12)。
// CsWinRT1028 は「その型 or 親のどれかが partial でない」で出るので、
// WinUiTimer だけ partial にしても消えない
internal sealed partial class WinUiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public void Post(Action action) => queue.TryEnqueue(() => action());

    public IUiTimer CreateTimer(TimeSpan interval, Action tick)
    {
        DispatcherQueueTimer timer = queue.CreateTimer();
        timer.Interval = interval;
        timer.Tick += (_, _) => tick();
        return new WinUiTimer(timer);
    }

    private sealed partial class WinUiTimer(DispatcherQueueTimer timer) : IUiTimer
    {
        public void SetRunning(bool running)
        {
            if (running)
            {
                timer.Start();
            }
            else
            {
                timer.Stop();
            }
        }

        // DispatcherQueueTimer は IDisposable ではない。止めれば以後 Tick は来ない
        public void Dispose() => timer.Stop();
    }
}
