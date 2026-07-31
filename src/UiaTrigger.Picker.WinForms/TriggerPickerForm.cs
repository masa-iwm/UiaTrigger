// ピッカーの Windows Forms View (docs/DESIGN.md §12)。
//
// 振る舞いは TriggerPickerPresenter が持つ。ここに残るのは「Windows Forms の事実」だけである:
//   ・データバインディングが無いので TreeView を手で同期させる (TreeMirror)
//   ・行の中にボタンを置けないので、確定は「選択中の要素を確定する」ボタン 1 つにする
//   ・TextBox の空欄が「値なし」であること
//   ・仮想化が無いので**遅延処理が要らない** — WinUI / WPF の実体化待ちリトライを
//     写さないこと。写すと、要らない複雑さを 3 つ目の View にも配ることになる
//
// デザイナーは使わずコードだけで組む。
using System.Globalization;
using UiaTrigger.Models;

namespace UiaTrigger.Picker.WinForms;

/// <summary>The picker as a Windows Forms window: pick an element, then turn it into a trigger.</summary>
/// <remarks>
/// Holds no rules of its own — <see cref="TriggerPickerPresenter"/> does. Committed triggers are
/// announced through <see cref="TriggerCommitted"/>; storing them is up to the host.
/// </remarks>
public sealed class TriggerPickerForm : Form, IPickerView
{
    // Control.Name は Windows Forms の UIA プロバイダーがそのまま AutomationId として出す。
    // 他の 2 変種の x:Name / x:Uid と同じ綴りに揃えてあるので、T4 のハーネスは
    // 変種ごとに別の探し方をしなくてよい (docs/TESTING.md §1)。
    //
    // これはアクセシビリティの改善でもある。AutomationId が無いコントロールは
    // スクリーンリーダーの利用者にとっても、自動化する側にとっても**名前が無い**。
    private readonly IPickerStrings _strings;
    private readonly TriggerPickerPresenter _presenter;
    private readonly TreeMirror _mirror;

    private readonly CheckBox _autoSelect = new() { Name = "AutoSelectToggle", AutoSize = true };
    private readonly Label _viewLabel = new() { Name = "ViewComboLabel", AutoSize = true };
    private readonly ComboBox _viewCombo = new() { Name = "ViewCombo", DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly Label _searchLabel = new() { Name = "SearchBoxLabel", AutoSize = true };
    private readonly TextBox _searchBox = new() { Name = "SearchBox", Width = 180 };
    private readonly Button _searchNext = new() { Name = "SearchNextButton", AutoSize = true };
    private readonly Label _hint = new() { Name = "HintText", AutoSize = true, MaximumSize = new Size(700, 0) };
    private readonly TreeView _tree = new() { Name = "ElementTree", Dock = DockStyle.Fill, HideSelection = false };
    private readonly Button _confirmNode = new() { Name = "ConfirmNodeButton", AutoSize = true };
    private readonly ListBox _props = new() { Name = "PropsList", Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false };
    private readonly Label _conditionHeading = new() { Name = "ConditionHeading", AutoSize = true, Font = new Font(DefaultFont, FontStyle.Bold) };
    private readonly Label _confirmed = new() { Name = "ConfirmedText", AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Label _keyLabel = new() { Name = "KeyBoxLabel", AutoSize = true };
    private readonly TextBox _keyBox = new() { Name = "KeyBox", Width = 150 };
    private readonly Label _onLabel = new() { Name = "OnComboLabel", AutoSize = true };
    private readonly ComboBox _onCombo = new() { Name = "OnCombo", DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly Label _propLabel = new() { Name = "PropComboLabel", AutoSize = true };
    private readonly ComboBox _propCombo = new() { Name = "PropCombo", DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly Label _condLabel = new() { Name = "CondComboLabel", AutoSize = true };
    private readonly ComboBox _condCombo = new() { Name = "CondCombo", DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly Label _textLabel = new() { Name = "TextOperandLabel", AutoSize = true };
    private readonly TextBox _textOperand = new() { Name = "TextOperand", Width = 180 };
    private readonly Label _valueLabel = new() { Name = "ValueOperandLabel", AutoSize = true };
    private readonly TextBox _valueOperand = new() { Name = "ValueOperand", Width = 90 };
    private readonly Label _lowLabel = new() { Name = "LowOperandLabel", AutoSize = true };
    private readonly TextBox _lowOperand = new() { Name = "LowOperand", Width = 90 };
    private readonly Label _highLabel = new() { Name = "HighOperandLabel", AutoSize = true };
    private readonly TextBox _highOperand = new() { Name = "HighOperand", Width = 90 };
    private readonly Label _toleranceLabel = new() { Name = "ToleranceOperandLabel", AutoSize = true };
    private readonly TextBox _toleranceOperand = new() { Name = "ToleranceOperand", Width = 90 };
    private readonly Label _minIntervalLabel = new() { Name = "MinIntervalOperandLabel", AutoSize = true };
    private readonly TextBox _minIntervalOperand = new() { Name = "MinIntervalOperand", Width = 90 };
    private readonly Button _commit = new() { Name = "CommitButton", AutoSize = true, Enabled = false };
    private readonly Label _commitStatus = new() { Name = "CommitStatus", AutoSize = true };

    // ツリーと右側の境界。Windows Forms は SplitContainer を標準で持つので、
    // 3 変種のうち**ここだけは元から動かせた** (WPF は GridSplitter、
    // WinUI3 だけが標準で持たず CommunityToolkit を足してある)。
    // 名前は 3 変種で揃えてあり、T4 は変種ごとに別の探し方をしなくてよい。
    private readonly SplitContainer _split = new() { Name = "TreeSplitter", Dock = DockStyle.Fill };

    // プロパティ一覧と条件欄の境界。こちらは縦の分割である。
    private readonly SplitContainer _conditionSplit = new()
    {
        Name = "ConditionSplitter",
        Dock = DockStyle.Fill,
        Orientation = Orientation.Horizontal,
    };

    // 条件欄。高さは折り返しの結果で決まるので AutoSize のままにする。
    // Bottom ではなく Top なのは、自分の区画 (Panel2) を持つようになったためである
    private readonly FlowLayoutPanel _conditions = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        WrapContents = true,
    };

    private readonly ToolTip _tips = new();

    private bool _suppressSelectionChanged;
    private bool _suppressCondChanged;
    private bool _disposedPicker;

    /// <summary>Raised when the user commits a trigger. Persisting it is up to the host.</summary>
    public event EventHandler<TriggerCommittedEventArgs>? TriggerCommitted;

    /// <summary>Creates the window and the presenter that drives it.</summary>
    public TriggerPickerForm()
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
    public TriggerPickerForm(ICursorSource cursor)
        : this(new ResxPickerStrings(), cursor, createPresenter: null)
    {
    }

    // internal だが、公開されている多重定義と名前が同じなので PublicApiDocumentationTests は
    // これも公開 API として扱う (名前だけで照合するため)。よって doc は英語で書く。
    //
    // 既定の経路は実物の UIA セッションとオーバーレイを作ってしまうので、T1 からはこちらで
    // 組み立てる。プレゼンターは View 自身を要求するのでファクトリを受け取る。

    /// <summary>For tests: substitutes the strings and the presenter.</summary>
    internal TriggerPickerForm(
        IPickerStrings strings,
        Func<IPickerView, TriggerPickerPresenter>? createPresenter)
        : this(strings, new Win32CursorSource(), createPresenter)
    {
    }

    // 実体。cursor は createPresenter が null のとき (= 既定の経路) だけ使う。
    // private なので XML doc は出ず、日本語版に項目を足す必要も無い
    private TriggerPickerForm(
        IPickerStrings strings,
        ICursorSource cursor,
        Func<IPickerView, TriggerPickerPresenter>? createPresenter)
    {
        _strings = strings;
        ClientSize = new Size(1100, 700);
        BuildLayout();
        ApplyStrings();

        // ハンドルは**ここで**作る。オーバーレイはコールバックを自分のスレッドから
        // 上げてくるので、Post は UI スレッド以外から呼ばれる。ハンドル生成前の
        // BeginInvoke は InvalidOperationException になり (実測済み)、かといって
        // Post の中で Handle に触れば別スレッドでハンドルが作られてしまう
        _ = Handle;

        // 条件欄の区画の高さは、ハンドルができて幅が確定してからでないと決められない
        // (SizeTheConditionPane の remarks を参照)
        SizeTheConditionPane();

        // プレゼンターはコントロールに初期値を入れる**前に**作る。
        // SelectedIndex への代入はその場で SelectedIndexChanged を上げ、
        // ハンドラーがプレゼンターを触るためである
        _presenter = createPresenter?.Invoke(this) ?? new TriggerPickerPresenter(
            this, new WinFormsDispatcher(this), cursor, strings);
        _presenter.TriggerCommitted += (_, e) => TriggerCommitted?.Invoke(this, e);
        _mirror = new TreeMirror(_tree, _presenter.Roots);

        _viewCombo.Items.AddRange(["Raw", "Control", "Content"]);
        _viewCombo.SelectedIndex = 1;
        _onCombo.Items.AddRange([.. Enum.GetValues<TriggerOn>().Cast<object>()]);
        _condCombo.Items.AddRange([.. Enum.GetValues<ComparisonOp>().Cast<object>()]);

        WireEvents();

        // マウス自動選択は既定で ON
        _autoSelect.Checked = true;
    }

    /// <summary>Opens this picker on an existing trigger so its condition can be edited.</summary>
    /// <param name="definition">
    /// The trigger to edit. The picker takes it over and writes the committed condition back into
    /// this instance, so pass a copy when the original has to stay as it is.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <see cref="TriggerPickerPresenter.CanEdit"/> is false for <paramref name="definition"/>.
    /// </exception>
    public void LoadDefinition(TriggerDefinition definition)
    {
        // ホバー捕捉を先に止める。開いたまま読み込むと、マウスがどこかの要素の上に
        // 静止しているだけで**編集対象が別の要素に差し替わる**
        _autoSelect.Checked = false;
        _presenter.LoadDefinition(definition);
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
    /// This is the same reason <see cref="LoadDefinition"/> switches capture off before it loads.
    /// </para>
    /// </remarks>
    public void StopAutoSelect() => _autoSelect.Checked = false;

    /// <summary>コントロールを組み立てる。デザイナーは使わない。</summary>
    private void BuildLayout()
    {
        var topBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        topBar.Controls.AddRange(
            [_autoSelect, _viewLabel, _viewCombo, _searchLabel, _searchBox, _searchNext, _confirmNode, _hint]);

        _conditions.Controls.AddRange(
        [
            _conditionHeading, _confirmed,
            _keyLabel, _keyBox, _onLabel, _onCombo, _propLabel, _propCombo, _condLabel, _condCombo,
            _textLabel, _textOperand, _valueLabel, _valueOperand,
            _lowLabel, _lowOperand, _highLabel, _highOperand,
            _toleranceLabel, _toleranceOperand, _minIntervalLabel, _minIntervalOperand,
            _commit, _commitStatus,
        ]);

        // 条件欄を縮められるようにすると中身がはみ出す。AutoScroll が無いと黙って切れる
        // (WPF / WinUI 側で ScrollViewer を挟んでいるのと同じ理由)
        _conditionSplit.Panel2.AutoScroll = true;
        _conditionSplit.Panel1.Controls.Add(_props);
        _conditionSplit.Panel2.Controls.Add(_conditions);

        _split.Panel1.Controls.Add(_tree);
        _split.Panel2.Controls.Add(_conditionSplit);

        Controls.Add(_split);
        Controls.Add(topBar);

        // 寸法は**ドッキングが済んでから**入れる。ここまでの SplitContainer は
        // まだ既定の 150px 幅で、その幅に収まらない値は例外にも警告にもならないからである。
        //
        // SplitterDistance を初期化子で入れると、その時点の幅は 150px で、setter は例外を
        // 出さずに**黙って 121 まで詰める** (実測)。SplitContainer は比率を保つので、Fill で
        // 1100px に広がると 887 — **ツリーが幅の 80.6%** になり、6:5 (54.5%) の他の 2 変種と
        // 同じ画面が変種ごとに違って見える。
        // 最小幅のほうは黙ってすらおらず、150px 幅で 240 を入れると
        // ApplyPanel2MinSize が中で SplitterDistance を動かして例外になる (T1 で実測)。
        //
        // Controls.Add の時点でレイアウトが走り、幅は ClientSize (1100) 基準になる。
        // Panel1MinSize / Panel2MinSize は WPF・WinUI 側の ColumnDefinition.MinWidth と同じ値である。
        _split.Panel1MinSize = 240;
        _split.Panel2MinSize = 240;

        // 開始位置は**比率で入れる** (docs/DESIGN.md §12)。
        //
        // 他の 2 変種は 6*/5* の star サイズなので、幅がいくつであっても 6:5 になる。
        // ここに絶対値 (600) を入れると、**窓が 1100px を貰えない環境で比率が崩れる** —
        // CI の狭いデスクトップでは要求した 1100 が黙って 804 まで詰められ、600 は
        // Panel2MinSize に引っかかって**560 (69.6%)** になる。
        // 例外も警告も出ず、狭い画面でだけツリーが太くなる形である。
        _split.SplitterDistance = (int)Math.Round((_split.Width - _split.SplitterWidth) * 6.0 / 11.0);

        _conditionSplit.Panel1MinSize = 80;
        _conditionSplit.Panel2MinSize = 80;
    }

    /// <summary>
    /// 条件欄の区画に、中身が収まるだけの高さを与える。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 高さは**折り返しの結果でしか決まらない**ので定数では書けず、折り返しは幅で決まる。
    /// **そして幅は、ハンドルができるまで確定しない** — <c>BuildLayout</c> の中で測ると
    /// 内側の区画は 856px (直前の値) のままで、<c>PerformLayout</c> を挟んでも変わらなかった
    /// (実測)。だから呼び出しはハンドル生成の後に置いてある。
    /// </para>
    /// <para>
    /// 測るのは**縦スクロールバーのぶんを引いた幅**である。いまの高さをそのまま使うと
    /// 足りない: 区画をその高さちょうどにした瞬間に <c>AutoScroll</c> がスクロールバーを出し、
    /// **幅が縮んでさらに折り返して**中身が伸びる。狭いほうの幅で測っておけば、
    /// スクロールバーが出ても出なくても収まる。
    /// </para>
    /// <para>
    /// どちらの読み違いも例外にならない。出るのは**「[トリガーを追加] が既定で見えない」**
    /// という形だけで、T1 が 106px の区画に 200px の中身を見つけて初めて分かった。
    /// </para>
    /// </remarks>
    private void SizeTheConditionPane()
    {
        int usableWidth = _conditionSplit.Panel2.ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
        if (usableWidth <= 0)
        {
            return;
        }

        int neededHeight = _conditions.GetPreferredSize(new Size(usableWidth, 0)).Height;
        _conditionSplit.SplitterDistance = Math.Max(
            _conditionSplit.Panel1MinSize,
            _conditionSplit.Height - _conditionSplit.SplitterWidth - neededHeight);
    }

    /// <summary>
    /// ラベルをリソースから代入する。Windows Forms に <c>x:Uid</c> のような仕組みは無い
    /// (docs/DESIGN.md L3)。
    /// </summary>
    private void ApplyStrings()
    {
        Text = _strings.GetString(PickerStringKeys.WindowTitle);
        _autoSelect.Text = _strings.GetString(PickerStringKeys.AutoSelectToggleHeader);
        _viewLabel.Text = _strings.GetString(PickerStringKeys.ViewComboHeader);
        _searchLabel.Text = _strings.GetString(PickerStringKeys.SearchBoxHeader);
        _searchNext.Text = _strings.GetString(PickerStringKeys.SearchNextButtonContent);
        _hint.Text = _strings.GetString(PickerStringKeys.HintTextText);
        // 行の中にボタンを置けないので、確定は「選択中の行を確定する」1 つのボタンで行う。
        // 文言は他の変種で行ボタンのツールチップに使っているものと同じキーである
        _confirmNode.Text = _strings.GetString(PickerStringKeys.ConfirmNodeButtonToolTip);
        _tips.SetToolTip(_confirmNode, _confirmNode.Text);
        // 区切りには表示文字列が無いので、読み上げに出るのはこの名前だけである。
        // Windows Forms の SplitContainer は既定では何も名乗らない
        _split.AccessibleName = _strings.GetString(PickerStringKeys.TreeSplitterAutomationName);
        _conditionSplit.AccessibleName =
            _strings.GetString(PickerStringKeys.ConditionSplitterAutomationName);
        _conditionHeading.Text = _strings.GetString(PickerStringKeys.ConditionHeadingText);
        _confirmed.Text = _strings.GetString(PickerStringKeys.ConfirmedTextText);
        _keyLabel.Text = _strings.GetString(PickerStringKeys.KeyBoxHeader);
        _onLabel.Text = _strings.GetString(PickerStringKeys.OnComboHeader);
        _propLabel.Text = _strings.GetString(PickerStringKeys.PropComboHeader);
        _condLabel.Text = _strings.GetString(PickerStringKeys.CondComboHeader);
        _textLabel.Text = _strings.GetString(PickerStringKeys.TextOperandHeader);
        _valueLabel.Text = _strings.GetString(PickerStringKeys.ValueOperandHeader);
        _lowLabel.Text = _strings.GetString(PickerStringKeys.LowOperandHeader);
        _highLabel.Text = _strings.GetString(PickerStringKeys.HighOperandHeader);
        _toleranceLabel.Text = _strings.GetString(PickerStringKeys.ToleranceOperandHeader);
        _minIntervalLabel.Text = _strings.GetString(PickerStringKeys.MinIntervalOperandHeader);
        _commit.Text = _strings.GetString(PickerStringKeys.CommitButtonContent);
        // 初期状態 (未チェック) の文字。以後は切り替えるたびに入れ替える
        _autoSelect.Text = _strings.GetString(PickerStringKeys.AutoSelectToggleHeader) +
            " " + _strings.GetString(PickerStringKeys.AutoSelectToggleOffContent);
    }

    private void WireEvents()
    {
        _autoSelect.CheckedChanged += (_, _) =>
        {
            _autoSelect.Text = _strings.GetString(PickerStringKeys.AutoSelectToggleHeader) + " " +
                _strings.GetString(_autoSelect.Checked
                    ? PickerStringKeys.AutoSelectToggleOnContent
                    : PickerStringKeys.AutoSelectToggleOffContent);
            _presenter.SetAutoSelect(_autoSelect.Checked);
        };
        _viewCombo.SelectedIndexChanged += (_, _) => _presenter.SetViewMode(_viewCombo.SelectedIndex switch
        {
            0 => TreeViewMode.Raw,
            2 => TreeViewMode.Content,
            _ => TreeViewMode.Control,
        });
        _searchNext.Click += (_, _) => _presenter.SearchNext(_searchBox.Text ?? string.Empty);
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true; // 既定の警告音を出さない
                _presenter.SearchNext(_searchBox.Text ?? string.Empty);
            }
        };
        _tree.AfterSelect += (_, e) =>
        {
            if (!_suppressSelectionChanged && e.Node?.Tag is PickerTreeNode node)
            {
                _presenter.NotifyTreeSelectionChanged(node);
            }
        };
        _tree.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Node?.Tag is PickerTreeNode node)
            {
                _presenter.NotifyTreeItemInvoked(node);
            }
        };
        _confirmNode.Click += (_, _) => _presenter.ConfirmNode(_tree.SelectedNode?.Tag as PickerTreeNode);
        foreach (ComboBox combo in new[] { _onCombo, _propCombo, _condCombo })
        {
            combo.SelectedIndexChanged += (_, _) =>
            {
                if (!_suppressCondChanged)
                {
                    _presenter.ConditionShapeChanged(
                        _onCombo.SelectedItem is TriggerOn on ? on : TriggerOn.PropertyChanged,
                        _condCombo.SelectedItem is ComparisonOp op ? op : ComparisonOp.Always);
                }
            };
        }
        _commit.Click += (_, _) => _presenter.Commit();
        // ウィンドウのアクティブ状態ではなくツリーのフォーカスで判定する (WPF と同じ理由)
        _tree.Enter += (_, _) => _presenter.SetTreeHasFocus(true);
        _tree.Leave += (_, _) => _presenter.SetTreeHasFocus(false);
    }

    /// <summary>プレゼンター (タイマー・オーバーレイ・UIA セッション) を解放する。</summary>
    /// <param name="disposing">True when called from <see cref="IDisposable.Dispose"/>.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposedPicker)
        {
            _disposedPicker = true;
            _mirror.Dispose();
            _presenter.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------- IPickerView ----------

    string IPickerView.Hint { set => _hint.Text = value; }

    string IPickerView.ConfirmedText { set => _confirmed.Text = value; }

    string IPickerView.CommitStatus { set => _commitStatus.Text = value; }

    string IPickerView.KeyText
    {
        get => _keyBox.Text ?? string.Empty;
        set => _keyBox.Text = value;
    }

    PickerTreeNode? IPickerView.SelectedNode => _tree.SelectedNode?.Tag as PickerTreeNode;

    void IPickerView.ShowProperties(IReadOnlyList<string> rows)
    {
        // DataSource は IList を要求するので配列にしてから渡す。継ぎ目の
        // IReadOnlyList<T> をそのまま渡すことはできない (WinUI とは理由が違うが結論は同じ)
        _props.BeginUpdate();
        try
        {
            _props.DataSource = rows.Count == 0 ? null : rows.ToArray();
        }
        finally
        {
            _props.EndUpdate();
        }
    }

    void IPickerView.ShowTriggerShape(IReadOnlyList<TriggerProperty> properties, TriggerOn on, ComparisonOp op)
    {
        // 3 つのコンボを続けて書き換えると SelectedIndexChanged が都度飛ぶ。途中の
        // 中途半端な組み合わせで欄の出し分けをやり直さないよう抑止する
        _suppressCondChanged = true;
        try
        {
            _propCombo.Items.Clear();
            _propCombo.Items.AddRange([.. properties.Cast<object>()]);
            if (_propCombo.Items.Count > 0)
            {
                _propCombo.SelectedIndex = 0;
            }
            _onCombo.SelectedItem = on;
            _condCombo.SelectedItem = op;
        }
        finally
        {
            _suppressCondChanged = false;
        }
    }

    void IPickerView.ShowOperands(OperandVisibility visibility)
    {
        _propCombo.Enabled = visibility.PropertyChoiceEnabled;
        SetOperandVisible(_textLabel, _textOperand, visibility.Text);
        SetOperandVisible(_valueLabel, _valueOperand, visibility.Value);
        SetOperandVisible(_lowLabel, _lowOperand, visibility.Range);
        SetOperandVisible(_highLabel, _highOperand, visibility.Range);
        SetOperandVisible(_toleranceLabel, _toleranceOperand, visibility.Tolerance);
    }

    private static void SetOperandVisible(Label label, TextBox box, bool visible)
    {
        label.Visible = visible;
        box.Visible = visible;
    }

    void IPickerView.SetCommitEnabled(bool enabled) => _commit.Enabled = enabled;

    TriggerDraft? IPickerView.ReadDraft()
    {
        if (_propCombo.SelectedItem is not TriggerProperty property ||
            _condCombo.SelectedItem is not ComparisonOp op ||
            _onCombo.SelectedItem is not TriggerOn on)
        {
            return null;
        }
        return new TriggerDraft
        {
            Id = _keyBox.Text,
            On = on,
            Property = property,
            Op = op,
            Text = _textOperand.Text,
            Value = ReadNumber(_valueOperand),
            Low = ReadNumber(_lowOperand),
            High = ReadNumber(_highOperand),
            Tolerance = ReadNumber(_toleranceOperand),
            // 発火レート制限 (docs/DESIGN.md C11)
            MinIntervalSeconds = ReadNumber(_minIntervalOperand),
        };
    }

    void IPickerView.ShowDraft(TriggerDraft draft)
    {
        // ShowTriggerShape と同じ理由で抑止する (途中の組み合わせで欄の出し分けをやり直させない)
        _suppressCondChanged = true;
        try
        {
            _keyBox.Text = draft.Id ?? string.Empty;
            _onCombo.SelectedItem = draft.On;
            _propCombo.SelectedItem = draft.Property;
            _condCombo.SelectedItem = draft.Op;
        }
        finally
        {
            _suppressCondChanged = false;
        }
        _textOperand.Text = draft.Text ?? string.Empty;
        WriteNumber(_valueOperand, draft.Value);
        WriteNumber(_lowOperand, draft.Low);
        WriteNumber(_highOperand, draft.High);
        WriteNumber(_toleranceOperand, draft.Tolerance);
        WriteNumber(_minIntervalOperand, draft.MinIntervalSeconds);
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
    /// 現在のカルチャで解釈する (docs/DESIGN.md L7)。読めないときに 0 を返さないこと —
    /// 0 は正当な値なので、打ち間違いが「0 という条件」として静かに保存されてしまう。
    /// </remarks>
    internal static double? ReadNumber(TextBox box)
        => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value)
            ? value
            : null;

    // 遅延処理は無い。Windows Forms の TreeView に仮想化が無く、ノードは追加した時点で
    // 実体を持つためである。WinUI / WPF のリトライをここへ写さないこと
    void IPickerView.DiscardDeferredWork()
    {
    }

    void IPickerView.BeginNodeUpdate()
    {
        _suppressSelectionChanged = true;
        _tree.BeginUpdate();
    }

    void IPickerView.EndNodeUpdate()
    {
        _tree.EndUpdate();
        _suppressSelectionChanged = false;
    }

    void IPickerView.Expand(IReadOnlyList<PickerTreeNode> nodes)
    {
        foreach (PickerTreeNode node in nodes)
        {
            // 表示のための展開なので子の全列挙は起こさない。写しの側は
            // IsExpanded の変化を見て TreeNode を開く
            node.ExpandForDisplay();
        }
    }

    void IPickerView.ExpandThenSelect(IReadOnlyList<PickerTreeNode> expandOrder, PickerTreeNode target)
    {
        ((IPickerView)this).Expand(expandOrder);
        ((IPickerView)this).SelectDeferred(target);
    }

    void IPickerView.SelectDeferred(PickerTreeNode node)
    {
        if (_mirror.Find(node) is not { } treeNode)
        {
            return;
        }
        _suppressSelectionChanged = true;
        try
        {
            _tree.SelectedNode = treeNode;
        }
        finally
        {
            _suppressSelectionChanged = false;
        }
        treeNode.EnsureVisible();
    }
}

/// <summary>Windows Forms の UI スレッドを継ぎ目に合わせる。</summary>
internal sealed class WinFormsDispatcher(Control control) : IUiDispatcher
{
    public void Post(Action action)
    {
        // ウィンドウが閉じたあとにオーバーレイ側から届くことがある。
        // 破棄済みへの BeginInvoke は例外になるので、そこで捨てる
        if (control.IsDisposed || !control.IsHandleCreated)
        {
            return;
        }
        control.BeginInvoke(action);
    }

    public IUiTimer CreateTimer(TimeSpan interval, Action tick)
    {
        var timer = new System.Windows.Forms.Timer { Interval = (int)interval.TotalMilliseconds };
        timer.Tick += (_, _) => tick();
        return new WinFormsTimer(timer);
    }

    private sealed class WinFormsTimer(System.Windows.Forms.Timer timer) : IUiTimer
    {
        public void SetRunning(bool running) => timer.Enabled = running;

        public void Dispose() => timer.Dispose();
    }
}
