// ピッカーの WPF View (docs/DESIGN.md §12)。
//
// 振る舞いは TriggerPickerPresenter (UiaTrigger.Picker.Core) が持つ。ここに残っているのは
// どれも「WPF でしかそうならない」ことだけである:
//   ・ItemContainerGenerator の実体化を待つリトライ (WinUI は TreeViewItem、
//     WinForms は仮想化が無いので不要 — 共通化すると 3 つとも間違った最小公倍数になる)
//   ・TextBox の空欄が「値なし」であること (WinUI の NumberBox は NaN で表す)
//   ・ラベルをコードで代入すること (WPF に x:Uid は無い)
//   ・BooleanToVisibilityConverter
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using UiaTrigger.Models;

namespace UiaTrigger.Picker.Wpf;

/// <summary>The picker as a WPF window: pick an element, then turn it into a trigger.</summary>
/// <remarks>
/// Holds no rules of its own — <see cref="TriggerPickerPresenter"/> does. Committed triggers are
/// announced through <see cref="TriggerCommitted"/>; storing them is up to the host.
/// </remarks>
public partial class TriggerPickerWindow : Window, IPickerView, IDisposable
{
    /// <summary>確定アイコンの字形を DataTemplate へ渡すためのリソースキー。</summary>
    private const string ConfirmNodeGlyphKey = "ConfirmNodeGlyph";

    private readonly TriggerPickerPresenter _presenter;
    private readonly IPickerStrings _strings;

    /// <summary>ツリー再構築の世代。差し替え後に古い展開/選択の遅延処理を破棄するために使う。</summary>
    private int _treeGeneration;
    private bool _suppressSelectionChanged;
    private bool _suppressCondChanged;
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
        : this(new ResxPickerStrings(), cursor, createPresenter: null)
    {
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
    /// <c>ScrollViewer</c> を置いてあることは T1 が見ていたが、**置いてあることは
    /// スクロールできることを意味しない** — 上限を渡さない限りビューポートは伸び続ける。
    /// </para>
    /// <para>
    /// Windows Forms 側は <c>SizeTheConditionPane</c> で同じことをしている。あちらは
    /// <c>SplitContainer</c> が区画を境界で切るので上限が要らず、代わりに
    /// <c>AutoScroll</c> が効く。**ここだけが上限を自分で渡す必要がある。**
    /// </para>
    /// <para>
    /// 引く 2 つ (上段の最小高さ・区切りの実寸) はどちらも XAML 側が正である。
    /// 定数で持つと、XAML を変えたときに黙ってずれる。
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
    }

    // internal だが、公開されている多重定義と名前が同じなので PublicApiDocumentationTests は
    // これも公開 API として扱う (名前だけで照合するため)。よって doc は英語で書く。
    //
    // 既定の経路は実物の UIA セッションとオーバーレイ (メッセージループを持つ実ウィンドウ) を
    // 作ってしまうので、T1 からはこちらで組み立てる。プレゼンターは View 自身を要求するため
    // インスタンスではなくファクトリを受け取る。

    /// <summary>For tests: substitutes the strings and the presenter.</summary>
    internal TriggerPickerWindow(
        IPickerStrings strings,
        Func<IPickerView, TriggerPickerPresenter>? createPresenter)
        : this(strings, new Win32CursorSource(), createPresenter)
    {
    }

    // 実体。cursor は createPresenter が null のとき (= 既定の経路) だけ使う。
    // private なので XML doc は出ず、日本語版に項目を足す必要も無い
    private TriggerPickerWindow(
        IPickerStrings strings,
        ICursorSource cursor,
        Func<IPickerView, TriggerPickerPresenter>? createPresenter)
    {
        InitializeComponent();

        _strings = strings;
        ApplyStrings();

        // プレゼンターは**コントロールに初期値を入れる前に**作る。
        // ViewCombo.SelectedIndex / AutoSelectToggle.IsChecked への代入はその場で
        // SelectionChanged / Checked を上げ、ハンドラーが _presenter を触るためである
        // (逆順にすると、ピッカーを開いた瞬間に NullReferenceException になる)
        _presenter = createPresenter?.Invoke(this) ?? new TriggerPickerPresenter(
            this, new WpfDispatcher(Dispatcher), cursor, strings);
        _presenter.TriggerCommitted += (_, e) => TriggerCommitted?.Invoke(this, e);
        ElementTree.ItemsSource = _presenter.Roots;

        ViewCombo.ItemsSource = new[] { "Raw", "Control", "Content" };
        ViewCombo.SelectedIndex = 1;
        // TriggerOn は丸ごと列挙しない — StoppedMatching はイベント専用で、
        // 定義の lifecycle としては保存できない (TriggerDraftValidator.DefinitionLifecycles)
        OnCombo.ItemsSource = TriggerDraftValidator.DefinitionLifecycles.ToArray();
        CondCombo.ItemsSource = Enum.GetValues<ComparisonOp>();

        // 見るのはウィンドウのアクティブ状態ではなく**ツリーがキーボードフォーカスを
        // 持っているか**である。ホバー中はフォーカスがピッカーに残るので、
        // ウィンドウ単位だと重なり切替 (←/→) が一度他アプリをクリックするまで使えない
        ElementTree.IsKeyboardFocusWithinChanged += (_, e) =>
            _presenter.SetTreeHasFocus(e.NewValue is true);
        Closed += (_, _) => Dispose();
        // Esc で閉じる。**PreviewKeyDown ではなく KeyDown (バブリング) で受けること** —
        // 先取りすると、コンボの一覧が開いている最中の Esc まで奪って窓ごと閉じてしまう。
        // バブリングなら一覧を閉じた側が Handled にするので、ここへは来ない
        KeyDown += OnKeyDown;

        // マウス自動選択は既定で ON (Checked が OnAutoSelectToggled を呼ぶ)
        AutoSelectToggle.IsChecked = true;
    }

    /// <summary>
    /// ラベルをリソースから代入する。WPF に <c>x:Uid</c> の実行時解決は無いので、
    /// XAML 側は空のままにしてここで 1 行ずつ埋める (docs/DESIGN.md L3)。
    /// </summary>
    private void ApplyStrings()
    {
        Title = _strings.GetString(PickerStringKeys.WindowTitle);
        AutoSelectLabel.Text = _strings.GetString(PickerStringKeys.AutoSelectToggleHeader);
        // 行の確定ボタンは DataTemplate の中にあり、コードから掴めない。
        // DynamicResource 越しに渡すことで XAML には文字列リテラルを置かずに済む
        Resources[PickerStringKeys.ConfirmNodeButtonToolTip] =
            _strings.GetString(PickerStringKeys.ConfirmNodeButtonToolTip);
        // 確定アイコンの字形。翻訳の対象ではないが、XAML の Content に直接書くと
        // 「表示文字列の直書き」として検出される (XamlLocalizationTests)。
        // 検査に例外を足すより、字形をこちら側に置くほうが網を緩めずに済む
        Resources[ConfirmNodeGlyphKey] = "✓";
        // 区切りには表示文字列が無いので、読み上げに出るのはこの名前だけである。
        // 入れないと WPF の GridSplitter は Thumb としか名乗らない
        AutomationProperties.SetName(
            TreeSplitter, _strings.GetString(PickerStringKeys.TreeSplitterAutomationName));
        AutomationProperties.SetName(
            ConditionSplitter, _strings.GetString(PickerStringKeys.ConditionSplitterAutomationName));
        ViewComboLabel.Text = _strings.GetString(PickerStringKeys.ViewComboHeader);
        SearchBoxLabel.Text = _strings.GetString(PickerStringKeys.SearchBoxHeader);
        SearchNextButton.Content = _strings.GetString(PickerStringKeys.SearchNextButtonContent);
        HintText.Text = _strings.GetString(PickerStringKeys.HintTextText);
        ConditionHeading.Text = _strings.GetString(PickerStringKeys.ConditionHeadingText);
        ConfirmedText.Text = _strings.GetString(PickerStringKeys.ConfirmedTextText);
        KeyBoxLabel.Text = _strings.GetString(PickerStringKeys.KeyBoxHeader);
        DisplayNameBoxLabel.Text = _strings.GetString(PickerStringKeys.DisplayNameBoxHeader);
        OnComboLabel.Text = _strings.GetString(PickerStringKeys.OnComboHeader);
        PropComboLabel.Text = _strings.GetString(PickerStringKeys.PropComboHeader);
        CondComboLabel.Text = _strings.GetString(PickerStringKeys.CondComboHeader);
        TextOperandLabel.Text = _strings.GetString(PickerStringKeys.TextOperandHeader);
        ValueOperandLabel.Text = _strings.GetString(PickerStringKeys.ValueOperandHeader);
        LowOperandLabel.Text = _strings.GetString(PickerStringKeys.LowOperandHeader);
        HighOperandLabel.Text = _strings.GetString(PickerStringKeys.HighOperandHeader);
        ToleranceOperandLabel.Text = _strings.GetString(PickerStringKeys.ToleranceOperandHeader);
        MinIntervalOperandLabel.Text = _strings.GetString(PickerStringKeys.MinIntervalOperandHeader);
        PollIntervalOperandLabel.Text = _strings.GetString(PickerStringKeys.PollIntervalOperandHeader);
        StoppedMatchingCheck.Content = _strings.GetString(PickerStringKeys.StoppedMatchingCheckContent);
        CommitButton.Content = _strings.GetString(PickerStringKeys.CommitButtonContent);
        // 初期状態 (未チェック) の文字。以後は OnAutoSelectToggled が入れ替える
        AutoSelectToggle.Content = _strings.GetString(PickerStringKeys.AutoSelectToggleOffContent);
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
        AutoSelectToggle.IsChecked = false;
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
    public void StopAutoSelect() => AutoSelectToggle.IsChecked = false;

    /// <summary>Releases the presenter: its timer, the overlay and the UI Automation session.</summary>
    /// <remarks>
    /// Closing the window does this already, so callers rarely need to. Safe to call more than once.
    /// </remarks>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the presenter and everything it owns.</summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (disposing)
        {
            _presenter.Dispose();
        }
    }

    // ---------- ユーザー操作 → プレゼンター ----------

    private void OnAutoSelectToggled(object sender, RoutedEventArgs e)
    {
        bool on = AutoSelectToggle.IsChecked == true;
        AutoSelectToggle.Content = _strings.GetString(
            on ? PickerStringKeys.AutoSelectToggleOnContent : PickerStringKeys.AutoSelectToggleOffContent);
        _presenter.SetAutoSelect(on);
    }

    private void OnViewChanged(object sender, SelectionChangedEventArgs e)
        => _presenter.SetViewMode(ViewCombo.SelectedIndex switch
        {
            0 => TreeViewMode.Raw,
            2 => TreeViewMode.Content,
            _ => TreeViewMode.Control,
        });

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnSearchNext(sender, e);
        }
    }

    private void OnSearchNext(object sender, RoutedEventArgs e)
        => _presenter.SearchNext(SearchBox.Text ?? string.Empty);

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressSelectionChanged)
        {
            return; // プレゼンターがツリーを差し替えている最中 = ユーザー操作ではない
        }
        if (e.NewValue is PickerTreeNode node)
        {
            _presenter.NotifyTreeSelectionChanged(node);
        }
    }

    /// <summary>
    /// 行のダブルクリック = WinUI の <c>ItemInvoked</c>。子の全列挙を伴う。
    /// </summary>
    /// <remarks>
    /// <c>MouseDoubleClick</c> はバブルするので、内側の行から順に同じハンドラーが呼ばれる。
    /// 最初の 1 つ (実際にクリックされた行) だけを処理して止める。
    /// </remarks>
    private void OnTreeItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || sender is not TreeViewItem { DataContext: PickerTreeNode node })
        {
            return;
        }
        e.Handled = true;
        _presenter.NotifyTreeItemInvoked(node);
    }

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

    /// <summary>Esc = 取り消して閉じる。閉じれば <c>Closed</c> がフックとセッションを畳む。</summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

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

    // 継ぎ目が渡すリストは配列にしてから ItemsSource へ渡す。プレゼンター側のリストは
    // 更新され続けないため、コントロールに参照を持たせたままにしない (継ぎ目の契約どおり)。
    // WinUI のような CCW の制約は WPF には無いので、理由は寿命だけである

    void IPickerView.ShowProperties(IReadOnlyList<string> rows)
        => PropsList.ItemsSource = rows.Count == 0 ? null : rows.ToArray();

    void IPickerView.ShowTriggerShape(IReadOnlyList<TriggerProperty> properties, TriggerOn on, ComparisonOp op)
    {
        // 3 つのコンボを続けて書き換えると SelectionChanged が都度飛ぶ。途中の中途半端な
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
        TextOperandPanel.Visibility = ToVisibility(visibility.Text);
        ValueOperandPanel.Visibility = ToVisibility(visibility.Value);
        LowOperandPanel.Visibility = ToVisibility(visibility.Range);
        HighOperandPanel.Visibility = ToVisibility(visibility.Range);
        ToleranceOperandPanel.Visibility = ToVisibility(visibility.Tolerance);
        PollIntervalOperandPanel.Visibility = ToVisibility(visibility.PollInterval);
        StoppedMatchingCheck.Visibility = ToVisibility(visibility.StoppedMatching);
    }

    private static Visibility ToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    void IPickerView.SetCommitEnabled(bool enabled) => CommitButton.IsEnabled = enabled;

    TriggerDraft? IPickerView.ReadDraft()
    {
        if (PropCombo.SelectedItem is not TriggerProperty property ||
            CondCombo.SelectedItem is not ComparisonOp op ||
            OnCombo.SelectedItem is not TriggerOn on)
        {
            return null;
        }
        return new TriggerDraft
        {
            Id = KeyBox.Text,
            DisplayName = DisplayNameBox.Text,
            On = on,
            Property = property,
            Op = op,
            Text = TextOperand.Text,
            Value = ReadNumber(ValueOperand),
            Low = ReadNumber(LowOperand),
            High = ReadNumber(HighOperand),
            Tolerance = ReadNumber(ToleranceOperand),
            // 発火レート制限 (docs/DESIGN.md C11)
            MinIntervalSeconds = ReadNumber(MinIntervalOperand),
            PollIntervalSeconds = ReadNumber(PollIntervalOperand),
            NotifyOnStoppedMatching = StoppedMatchingCheck.IsChecked == true,
        };
    }

    void IPickerView.ShowDraft(TriggerDraft draft)
    {
        // ShowTriggerShape と同じ理由で抑止する。ここは 4 つのコントロールを続けて書き換えるので、
        // 途中の組み合わせで欄の出し分けをやり直させない (プレゼンターが直後に一度だけ指示する)
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
        WriteNumber(ValueOperand, draft.Value);
        WriteNumber(LowOperand, draft.Low);
        WriteNumber(HighOperand, draft.High);
        WriteNumber(ToleranceOperand, draft.Tolerance);
        WriteNumber(MinIntervalOperand, draft.MinIntervalSeconds);
        WriteNumber(PollIntervalOperand, draft.PollIntervalSeconds);
        StoppedMatchingCheck.IsChecked = draft.NotifyOnStoppedMatching;
    }

    /// <summary>数値欄に書く。null は空欄 (= 値なし)。</summary>
    /// <remarks>
    /// <see cref="ReadNumber"/> と同じカルチャで書くこと。ずれると**読み込んだ値を
    /// そのまま確定するだけで条件が変わる** — 書式の差なので例外にもならない。
    /// </remarks>
    internal static void WriteNumber(TextBox box, double? value)
        => box.Text = value?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

    /// <summary>
    /// 数値欄を読む。空欄も読めない入力も「値なし」(null) とする。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 現在のカルチャで解釈する — 画面に出す数値がカルチャに従う以上、入力も同じでなければ
    /// 「表示された値をそのまま打ち直すと通らない」ことになる (docs/DESIGN.md L7)。
    /// </para>
    /// <para>
    /// 読めないときに 0 を返さないこと。0 は**正当な値**なので、打ち間違いが
    /// 「0 という条件」として静かに保存されてしまう。null なら検証が欠落として弾く。
    /// </para>
    /// </remarks>
    internal static double? ReadNumber(TextBox box)
        => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value)
            ? value
            : null;

    void IPickerView.DiscardDeferredWork() => _treeGeneration++;

    void IPickerView.BeginNodeUpdate() => _suppressSelectionChanged = true;

    void IPickerView.EndNodeUpdate() => _suppressSelectionChanged = false;

    void IPickerView.Expand(IReadOnlyList<PickerTreeNode> nodes)
    {
        foreach (PickerTreeNode node in nodes)
        {
            // 表示のための展開なので子の全列挙は起こさない。TreeViewItem 側は
            // IsExpanded の TwoWay 束縛が追随する
            node.ExpandForDisplay();
        }
    }

    /// <summary>チェーンノードを上から順に (実体化を待って) 展開し、最後に対象を選択する。</summary>
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
            expandOrder[index].ExpandForDisplay();
            if (FindContainer(expandOrder[index]) is not null)
            {
                Post(() => Step(index + 1, 0));
            }
            else if (attempt < 40)
            {
                Post(() => Step(index, attempt + 1));
            }
        }
        Post(() => Step(0, 0));
    }

    void IPickerView.SelectDeferred(PickerTreeNode node) => SelectDeferred(node, _treeGeneration);

    /// <summary>コンテナの実体化を待って選択・スクロールする。</summary>
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
            if (FindContainer(node) is { } container)
            {
                // プログラム選択で SelectedItemChanged が走り、ユーザー操作として
                // 報告されてしまわないよう抑止する
                _suppressSelectionChanged = true;
                try
                {
                    container.IsSelected = true;
                }
                finally
                {
                    _suppressSelectionChanged = false;
                }
                container.BringIntoView();
                return;
            }
            if (attempt < 20)
            {
                Post(() => TrySelect(attempt + 1));
            }
        }
        Post(() => TrySelect(0));
    }

    /// <summary>UI スレッドへ、レイアウトより後の優先度で戻す。</summary>
    private void Post(Action action)
        => Dispatcher.BeginInvoke(DispatcherPriority.Background, action);

    /// <summary>
    /// ノードに対応する <see cref="TreeViewItem"/> を探す。無ければ null (まだ実体化していない)。
    /// </summary>
    /// <remarks>
    /// <see cref="PickerTreeNode"/> は親を持たないので経路を辿れない。生成済みのコンテナだけを
    /// 深さ優先で降りて同一性で突き合わせる。閉じた段のコンテナは生成されていないため、
    /// 呼び出し側は**先に祖先を開いてから**ここへ来る必要がある。
    /// </remarks>
    private TreeViewItem? FindContainer(PickerTreeNode node)
    {
        static TreeViewItem? Search(ItemsControl parent, PickerTreeNode target)
        {
            foreach (object? item in parent.Items)
            {
                if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container)
                {
                    continue;
                }
                if (ReferenceEquals(item, target))
                {
                    return container;
                }
                if (Search(container, target) is { } nested)
                {
                    return nested;
                }
            }
            return null;
        }
        return Search(ElementTree, node);
    }
}

/// <summary>WPF の <see cref="System.Windows.Threading.Dispatcher"/> を継ぎ目に合わせる。</summary>
internal sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action) => dispatcher.BeginInvoke(action);

    public IUiTimer CreateTimer(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = interval };
        timer.Tick += (_, _) => tick();
        return new WpfTimer(timer);
    }

    private sealed class WpfTimer(DispatcherTimer timer) : IUiTimer
    {
        public void SetRunning(bool running) => timer.IsEnabled = running;

        // DispatcherTimer は IDisposable ではない。止めれば以後 Tick は来ない
        public void Dispose() => timer.Stop();
    }
}
