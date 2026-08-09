// ピッカーの振る舞い (docs/DESIGN.md §12)。
//
// WinUI3 / WindowsAppSDK のアセンブリに閉じ込めるとテストが 1 件も書けなくなるので、
// ここに在るのは「どの UI フレームワークでも同じ答えになるもの」だけである:
// ホバー滞留の規則・チェーンの組み立て・子の差し込み・ビュー切替の復元順・検索の巡回・
// 重なり切替・既定 id・条件欄の出し分け・コミット。
//
// 逆に View に残したもの (継ぎ目の向こう側) はどれも「そのフレームワークの事実」である:
// コンテナ実体化を待つリトライ / アクティベーション猶予 / NumberBox の空欄 = NaN /
// bool → Visibility の変換。共通化すると 3 つとも間違った最小公倍数になる。
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using UiaTrigger.Models;
using UiaTrigger.Serialization;

namespace UiaTrigger.Picker;

/// <summary>Drives the picker: hovering, the element tree, and turning a choice into a trigger.</summary>
/// <remarks>
/// <para>
/// Holds every rule that does not depend on a UI framework, and talks to the one it is running in
/// through <see cref="IPickerView"/>, <see cref="IUiDispatcher"/>, <see cref="ICursorSource"/> and
/// <see cref="IPickerStrings"/>. A view calls the <c>Notify…</c> and <c>Set…</c> methods when the
/// user does something, and implements the interface to be told what to show.
/// </para>
/// <para>
/// Everything here happens on the thread the dispatcher posts to. The overlay is the exception:
/// it raises its events on its own threads, and the presenter posts them across before acting.
/// </para>
/// <para>
/// The presenter owns every element handle the session hands it, and releases the ones it can no
/// longer reach from the tree, the current row, or the row the user last chose deliberately. Views
/// hold rows, never handles, so a row left over from a replaced tree is still safe to show.
/// </para>
/// </remarks>
public sealed class TriggerPickerPresenter : IDisposable
{
    /// <summary>ホバー捕捉に必要な静止時間。</summary>
    private static readonly TimeSpan StationaryTime = TimeSpan.FromMilliseconds(1000);

    /// <summary>静止とみなすカーソルのぶれ幅。</summary>
    private const int MoveTolerancePx = 4;

    /// <summary>カーソル位置を見る間隔。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>入力中の正規表現を試しにコンパイルするときの上限。監視側の既定と揃える。</summary>
    private static readonly TimeSpan RegexValidationTimeout = TimeSpan.FromSeconds(1);

    private readonly IPickerView _view;
    private readonly IUiDispatcher _dispatcher;
    private readonly ICursorSource _cursor;
    private readonly IDpiSource _dpi;
    private readonly IPickerStrings _strings;
    private readonly IPickerServices _services;
    private readonly IOverlay _overlay;
    private readonly TimeProvider _time;
    private readonly IUiTimer _timer;

    private TreeViewMode _treeView = TreeViewMode.Control;
    private System.Drawing.Point _anchorPoint;
    private DateTimeOffset _anchorTime;
    private System.Drawing.Point _lastCapturedPoint = new(int.MinValue, int.MinValue);
    private PickerTreeNode? _currentNode;

    /// <summary>
    /// ユーザーが最後に意図的に選択した要素 (ホバー捕捉/ツリークリック/重なり切替/検索)。
    /// ビュー切替の正規化で選択が祖先に丸められても、切替のたびにこの要素から復元を試みる。
    /// </summary>
    private IPickerElement? _selectionOrigin;

    /// <summary>継ぎ目から受け取って、まだ解放していないハンドル。</summary>
    /// <remarks>
    /// <para>
    /// <see cref="IPickerServices"/> が返す要素はすべてこちらのものになる
    /// (<see cref="IPickerElement"/> の remarks)。放っておくとファイナライザー任せになり、
    /// 大きなツリーを歩いたあいだ相手プロセスの provider を数千個掴んだままになる。
    /// </para>
    /// <para>
    /// 「どこで捨てるか」を数え上げる形では**漏れる**。<see cref="_selectionOrigin"/> は
    /// ツリーのエイリアスではなく**独立した所有根**で、重なり切替 (<see cref="MoveStackAsync"/>)
    /// が入れた要素はツリーに 1 度も現れないまま、次のビュー切替
    /// (<see cref="RebuildChainAsync"/>) で使われる。そこで所有根
    /// — <see cref="Roots"/> ∪ <see cref="_selectionOrigin"/> ∪ <see cref="_currentNode"/> —
    /// から到達できないものを掃き出す形にしてある。presenter の中の小さな GC である。
    /// </para>
    /// <para>
    /// **不変条件が 2 つある。**(1) 所有根が変わったら <see cref="ReleaseUnreachable"/> を呼ぶ。
    /// (2) 受け取ったものは**掃く前に** <see cref="Own"/> する — 逆順にすると、
    /// まだツリーへ入れていないチェーンを到達不能とみなして即座に解放してしまう。
    /// </para>
    /// </remarks>
    private readonly HashSet<IPickerElement> _owned = new(ReferenceEqualityComparer.Instance);

    private TriggerDefinition? _confirmedDef;

    /// <summary>
    /// 編集セッションか (<see cref="LoadDefinition(TriggerDefinition, bool)"/>)。
    /// 真なら確定 1 回で窓を閉じ、ボタンの文言も「更新」になる。
    /// 新規追加の「開いたまま何件でもコミット」とはここで分かれる
    /// </summary>
    private bool _editSession;

    /// <summary>
    /// いまオーバーレイに出している矩形。<see cref="IPickerElement.BoundingRectangle"/> は
    /// ハンドルを作った時点のスナップショットなので、対象ウィンドウが動くと古くなる。
    /// 確定アイコンの当たり判定も**画面に出ている枠**に合わせないと、
    /// アイコンの位置と「捕捉しない領域」がずれる。
    /// </summary>
    private ElementRect _shownRect;

    /// <summary>最後にこちらから入れた既定 id。ユーザーが書き換えたかどうかの判定に使う。</summary>
    private string _suggestedId = string.Empty;

    /// <summary>最後にこちらから入れた提案表示名。ユーザーが書き換えたかどうかの判定に使う。</summary>
    private string _suggestedDisplayName = string.Empty;

    private bool _treeHasFocus;
    private int _searchCursor = -1;
    private bool _disposed;

    /// <summary>Creates a presenter that talks to the real UI Automation session and overlay.</summary>
    /// <param name="view">The view to drive.</param>
    /// <param name="dispatcher">Posts work back onto the thread that owns the view.</param>
    /// <param name="cursor">Reads the mouse position for hover capture.</param>
    /// <param name="strings">Supplies the user-facing strings.</param>
    public TriggerPickerPresenter(
        IPickerView view,
        IUiDispatcher dispatcher,
        ICursorSource cursor,
        IPickerStrings strings)
        : this(view, dispatcher, cursor, strings, new PickerServices(),
               new OverlayController(SharedDpiSource), TimeProvider.System, SharedDpiSource)
    {
    }

    // オーバーレイとプレゼンターは**同じ** IDpiSource を見る。別々に作ると、
    // 差し替えたときに片方だけ変わって「絵と当たり判定が食い違う」を作り込める
    private static readonly Win32DpiSource SharedDpiSource = new();

    // internal だが、公開されている多重定義と名前が同じなので PublicApiDocumentationTests は
    // これも公開 API として扱う (名前だけで照合するため — 見落としではなく過剰検出の側)。
    // つまりここの doc は英語で書く必要があり、日本語版にも項目が要る。

    /// <summary>For tests: substitutes UI Automation, the overlay, the clock and the DPI source.</summary>
    internal TriggerPickerPresenter(
        IPickerView view,
        IUiDispatcher dispatcher,
        ICursorSource cursor,
        IPickerStrings strings,
        IPickerServices services,
        IOverlay overlay,
        TimeProvider time,
        IDpiSource dpi)
    {
        _view = view;
        _dispatcher = dispatcher;
        _cursor = cursor;
        _strings = strings;
        _services = services;
        _overlay = overlay;
        _time = time;
        _dpi = dpi;
        _anchorTime = time.GetUtcNow();

        // ホストが PerMonitorV2 を宣言していないと、ホバー捕捉は狙った要素ではなくその親を掴む。
        // 例外にはならず「静かに別の要素を記録した定義」が出来上がるので、真っ先に見せる
        // (docs/DESIGN.md A19 / C13)。オーバーレイの生成失敗より重い問題である
        if (_services.CoordinateProblem is { } dpiProblem)
        {
            _view.Hint = dpiProblem;
        }
        else if (_overlay.CreationError is { } overlayError)
        {
            _view.Hint = overlayError;
        }

        _overlay.ConfirmClicked += OnOverlayConfirmClicked;
        _overlay.ArrowKeyPressed += OnOverlayArrowKey;

        _timer = _dispatcher.CreateTimer(PollInterval, () => _ = TickAsync());
    }

    /// <summary>The rows of the element tree. A view binds to this once and never replaces it.</summary>
    public ObservableCollection<PickerTreeNode> Roots { get; } = [];

    /// <summary>Raised when the user commits a trigger. Persisting it is up to the host.</summary>
    public event EventHandler<TriggerCommittedEventArgs>? TriggerCommitted;

    // ---------- View から呼ばれる ----------

    /// <summary>Turns hover capture on or off.</summary>
    public void SetAutoSelect(bool on)
    {
        _overlay.SetHookEnabled(on);
        if (on)
        {
            _anchorTime = _time.GetUtcNow();
        }
        _timer.SetRunning(on);
    }

    /// <summary>Switches the tree view and rebuilds the current chain in it.</summary>
    public void SetViewMode(TreeViewMode mode)
    {
        _treeView = mode;
        _ = RebuildChainAsync();
    }

    /// <summary>Tells the presenter whether the element tree holds the keyboard focus.</summary>
    /// <remarks>
    /// <para>
    /// While it does, the arrow keys are the tree's: they move the selection, so they must not also
    /// step through the elements stacked under the cursor.
    /// </para>
    /// <para>
    /// This asks about the **tree**, not the window. Pointing at something in another
    /// application never moves the focus — Windows moves it on a click — so a window-level test is
    /// true for the whole time the user is hovering, which is exactly when stepping through
    /// overlapping elements is the thing they want. Gating on that made the feature unreachable
    /// until the user clicked another application first (docs/DESIGN.md §11).
    /// </para>
    /// </remarks>
    public void SetTreeHasFocus(bool focused) => _treeHasFocus = focused;

    /// <summary>Reports that the user selected a row. Never called for the presenter's own changes.</summary>
    public void NotifyTreeSelectionChanged(PickerTreeNode node)
    {
        _currentNode = node;
        if (node.Element is { } element)
        {
            _selectionOrigin = element; // ユーザーのツリー選択は復元の起点
        }
        RefreshCurrentNode();
        // 所有根が変わった。直前の origin が重なり切替の移動先だった場合、それはツリーに
        // 現れないので、ここで掃かないと到達不能なまま残る (_owned の不変条件 1)
        ReleaseUnreachable();
    }

    /// <summary>Reports that the user clicked a row, which also enumerates its children.</summary>
    public void NotifyTreeItemInvoked(PickerTreeNode node)
    {
        // SelectionChanged が発火しない選択経路もあるため、クリック時はここでも選択状態を確定する
        _currentNode = node;
        if (node.Element is { } element)
        {
            _selectionOrigin = element;
        }
        RefreshCurrentNode();
        ReleaseUnreachable(); // 所有根が変わった (NotifyTreeSelectionChanged と同じ理由)

        _ = LoadChildrenAsync(node);
        _view.Expand([node]);
    }

    /// <summary>Whether the presenter still wants <paramref name="node"/> selected.</summary>
    /// <remarks>
    /// A view that waits for a row's control to exist before selecting it must check this first:
    /// by the time the control appears the user may well have picked something else, and the stale
    /// request would drag the selection back.
    /// </remarks>
    public bool IsCurrent(PickerTreeNode node) => ReferenceEquals(node, _currentNode);

    /// <summary>Moves to the next row whose label contains <paramref name="query"/>, wrapping round.</summary>
    public void SearchNext(string query) => SearchNextCore(query.Trim());

    /// <summary>Confirms a row's element and offers the trigger shape for it.</summary>
    public void ConfirmNode(PickerTreeNode? node) => _ = ConfirmNodeAsync(node);

    /// <summary>Whether this picker can edit an existing trigger's condition.</summary>
    /// <remarks>
    /// <para>
    /// False for a composite: the picker edits one condition, and taking a composite apart is
    /// <see cref="TriggerComposer.Decompose"/>'s job.
    /// </para>
    /// <para>
    /// False, too, for a single condition carrying anything a <see cref="TriggerDraft"/> has no
    /// place for — an element of its own (<see cref="PropertyClause.Window"/> or
    /// <see cref="PropertyClause.Locator"/>), <see cref="PropertyClause.Watch"/> turned off, or
    /// <see cref="TriggerProperty.Custom"/> with its property id. Committing rebuilds the condition
    /// from the draft, so what the draft cannot carry would be dropped: the trigger would end up
    /// watching a different element, or watching one it was only meant to require. Ask before
    /// offering to edit; the alternative is an edit that silently changes something the user never
    /// saw.
    /// </para>
    /// </remarks>
    public static bool CanEdit(TriggerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Expression is not null || definition.Clauses.Count > 1)
        {
            return false;
        }
        return definition.Clauses.Count == 0
            || definition.Clauses[0] is
            {
                Property: not TriggerProperty.Custom, Window: null, Locator: null, Watch: true,
            };
    }

    /// <summary>Loads an existing trigger so its condition can be edited and committed again.</summary>
    /// <param name="definition">
    /// The trigger to edit. The presenter takes it over and writes the committed condition back into
    /// this instance, so pass a copy when the original has to stay as it is.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <see cref="CanEdit"/> is false for <paramref name="definition"/>.
    /// </exception>
    /// <remarks>
    /// The element is not re-read: the recorded window and locator are kept as they are, which is
    /// the point — changing a threshold should not need the element to be hovered again. Hovering
    /// still works, and confirming an element replaces the recorded one as usual.
    /// </remarks>
    public void LoadDefinition(TriggerDefinition definition) => LoadDefinition(definition, editSession: false);

    /// <summary>
    /// Loads an existing trigger, optionally as an edit session that ends with its one commit.
    /// </summary>
    /// <param name="definition"><inheritdoc cref="LoadDefinition(TriggerDefinition)" path="/param[@name='definition']"/></param>
    /// <param name="editSession">
    /// True when the load is an edit of a recorded trigger rather than a prefill. An edit session
    /// changes the commit button's caption to <see cref="PickerStringKeys.CommitButtonUpdate"/> and
    /// closes the view after the successful commit — editing ends with that commit, unlike adding,
    /// which keeps the picker open for the next trigger.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <inheritdoc cref="LoadDefinition(TriggerDefinition)" path="/exception"/>
    /// </exception>
    /// <remarks><inheritdoc cref="LoadDefinition(TriggerDefinition)" path="/remarks"/></remarks>
    public void LoadDefinition(TriggerDefinition definition, bool editSession)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!CanEdit(definition))
        {
            // 利用者向けの理由文字列ではなくプログラマ向けの契約。呼び出し側は CanEdit で
            // 先に訊ける (TriggerDraftValidator.Apply の InvalidOperationException と同じ線)
            throw new ArgumentException(
                "The picker edits a single condition a draft can carry; see CanEdit.",
                nameof(definition));
        }

        _confirmedDef = definition;
        _editSession = editSession;
        if (editSession)
        {
            _view.CommitCaption = _strings.GetString(PickerStringKeys.CommitButtonUpdate);
        }
        _view.ConfirmedText = Format(
            PickerStringKeys.Confirmed, definition.DisplayName, definition.Window.ProcessName);

        PropertyClause? clause = definition.Clauses.Count > 0 ? definition.Clauses[0] : null;
        var draft = new TriggerDraft
        {
            Id = definition.Id,
            DisplayName = definition.DisplayName,
            On = definition.On,
            Property = clause?.Property ?? TriggerProperty.Name,
            Op = clause?.Op ?? ComparisonOp.Always,
            Text = clause?.Text,
            Value = clause?.Value,
            Low = clause?.Low,
            High = clause?.High,
            // 許容差は 0 が既定であって「未入力」ではない。演算子が使わないなら欄に出さないので、
            // そこへ 0 を書いても混乱を招くだけである
            Tolerance = clause is { } c && TriggerDraftValidator.UsesTolerance(c.Op) ? c.Tolerance : null,
            MinIntervalSeconds = definition.MinInterval?.TotalSeconds,
            PollIntervalSeconds = definition.PollInterval?.TotalSeconds,
            NotifyOnStoppedMatching = definition.NotifyOnStoppedMatching,
        };

        _view.ShowTriggerShape(PropertiesFor(draft.Property), draft.On, draft.Op);
        _view.ShowDraft(draft);
        _view.ShowOperands(DescribeOperands(draft.On, draft.Op));

        // 既定 id を「こちらが最後に入れた値」として空にしておく。こうすると
        // ConfirmNodeAsync は KeyText を**ユーザーが書いた値**と見なすので、
        // 編集中に要素を捕まえ直しても id が黙って提案 id へ置き換わらない。表示名も同じ
        _suggestedId = string.Empty;
        _suggestedDisplayName = string.Empty;
        _view.SetCommitEnabled(true);
    }

    /// <summary>選べるプロパティの一覧。<paramref name="selected"/> は必ず含める。</summary>
    /// <remarks>
    /// 確定のときは要素のスナップショットからパターン対応を見て決めるが、既存の定義を
    /// 読み込むときは要素を読み直さないので見るものが無い。**いま入っているプロパティを
    /// 落とさないこと**を優先する — 一覧に無ければコンボは選択なしになり、
    /// コミットで別のプロパティの条件に化ける。
    /// </remarks>
    private static List<TriggerProperty> PropertiesFor(TriggerProperty selected)
    {
        List<TriggerProperty> props = [.. StandardProperties];
        if (!props.Contains(selected))
        {
            props.Add(selected);
        }
        return props;
    }

    /// <summary>どの要素でも読めるプロパティ。パターン依存のものは確定時に足す。</summary>
    private static readonly TriggerProperty[] StandardProperties =
    [
        TriggerProperty.Name, TriggerProperty.AutomationId, TriggerProperty.ClassName,
        TriggerProperty.ControlType, TriggerProperty.BoundingRectangle, TriggerProperty.AccessKey,
        TriggerProperty.AcceleratorKey, TriggerProperty.HelpText, TriggerProperty.IsEnabled,
        TriggerProperty.IsOffscreen,
    ];

    /// <summary>Reports that the user changed the trigger's shape, which decides the operand fields.</summary>
    public void ConditionShapeChanged(TriggerOn lifecycle, ComparisonOp op)
        => _view.ShowOperands(DescribeOperands(lifecycle, op));

    /// <summary>Which operand fields a trigger of this shape uses.</summary>
    /// <remarks>
    /// Read from <see cref="TriggerDraftValidator"/> rather than decided here, so that a field the
    /// user cannot see can never end up in the saved condition.
    /// </remarks>
    public static OperandVisibility DescribeOperands(TriggerOn lifecycle, ComparisonOp op)
    {
        // 出現・削除だけを見るトリガーでは、条件そのものを任意にできる (句なし = 無条件)。
        // ライフサイクル (TriggerOn) と値の述語は分離してある (docs/DESIGN.md C3〜C6) ため、
        // この 2 つは自由に組み合わせられ、条件を外すこともできる
        bool lifecycleOnly = lifecycle is TriggerOn.ElementAppeared or TriggerOn.ElementRemoved;
        return new OperandVisibility(
            Text: TriggerDraftValidator.UsesText(op),
            Value: TriggerDraftValidator.UsesValue(op),
            Range: TriggerDraftValidator.UsesRange(op),
            // 許容差は数値比較のときだけ意味がある (docs/DESIGN.md A12)
            Tolerance: TriggerDraftValidator.UsesTolerance(op),
            PollInterval: TriggerDraftValidator.UsesPollInterval(lifecycle),
            PropertyChoiceEnabled: !lifecycleOnly || op != ComparisonOp.Always,
            StoppedMatching: TriggerDraftValidator.UsesNotifyOnStoppedMatching(lifecycle));
    }

    /// <summary>Validates what the user filled in and, if it holds together, commits the trigger.</summary>
    public void Commit()
    {
        if (_confirmedDef is null)
        {
            return;
        }
        TriggerDraft? draft = _view.ReadDraft();
        if (draft is null)
        {
            _view.CommitStatus = _strings.GetString(PickerStringKeys.SelectTriggerShape);
            return;
        }

        // 検証は UI から切り離してある (docs/TESTING.md §1 T1 — TriggerDraftValidatorTests)
        TriggerDraftResult result = TriggerDraftValidator.Validate(draft, RegexValidationTimeout);
        if (!result.IsValid)
        {
            _view.CommitStatus = result.Error ?? string.Empty;
            return;
        }
        TriggerDraftValidator.Apply(_confirmedDef, draft, result);

        // **ホストへ渡すのは写しである** (docs/DESIGN.md C19)。_confirmedDef はコミット後も
        // 手元に残り (§4 が明文化する「開いたまま何件でもコミット」のため)、次のコミットで
        // Apply が同じインスタンスを in-place で書き換える — 写しを取らないと、
        // **1 件目としてホストへ渡した定義がその瞬間に 2 件目へ化ける**。
        // 同梱ホストは受け取った定義を一覧に保持して id で照合するので、症状は
        // 「保存したはずの 1 件目がファイルから消える」という無警告のデータ消失になる。
        // 写しは JSON 往復で取る (エディタの写しと同じ理由 — 手写しはモデルの成長で腐る)
        var committed = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(_confirmedDef, TriggerJsonContext.Default.TriggerDefinition),
            TriggerJsonContext.Default.TriggerDefinition)!;

        TriggerCommitted?.Invoke(this, new TriggerCommittedEventArgs { Definition = committed });
        _view.CommitStatus = Format(PickerStringKeys.TriggerAdded, committed.Id);
        // 閉じるのは**最後**。Close で自分を Dispose する View (WinForms の Form) があるので、
        // この後に _view へ触る行を置いてはいけない。閉じるのは編集セッションのコミットが
        // 成立したときだけ — 検証失敗の early return はこの行に来ない
        if (_editSession)
        {
            _view.Close();
        }
    }

    /// <summary>Stops the timer and releases the overlay and the UI Automation session.</summary>
    /// <remarks>Safe to call more than once.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // セッションを畳む前に手放す。セッションの Dispose でまとめて消えるとはいえ、
        // 「持っていたぶんは自分で返す」形にしておかないと、テストから
        // 「掃き出しが漏らしていないか」を見る足場が無くなる
        ReleaseAllElements();

        _timer.SetRunning(false);
        _timer.Dispose();
        _overlay.ConfirmClicked -= OnOverlayConfirmClicked;
        _overlay.ArrowKeyPressed -= OnOverlayArrowKey;
        _overlay.Dispose();

        // 呼び出し元 (ウィンドウの Closed など) は同期のことがあり await できない。
        // ValueTask を Task 化して確実に消費し、解放中の例外を握り潰さない (CA2012)
        _ = _services.DisposeAsync().AsTask().ContinueWith(
            static t => System.Diagnostics.Debug.WriteLine($"picker services dispose failed: {t.Exception}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    // ---------- マウス自動選択 ----------

    private void OnOverlayConfirmClicked()
        => _dispatcher.Post(() => _ = ConfirmNodeAsync(_currentNode));

    private void OnOverlayArrowKey(bool right)
        => _dispatcher.Post(() =>
        {
            // ツリーがキーボードフォーカスを持っているときだけツリー操作を優先する。
            // ホバー中はフォーカスがピッカーに残ったままなので、ウィンドウ単位で見ると
            // 重なり切替が永久に使えない (docs/DESIGN.md §11)
            if (!_treeHasFocus)
            {
                _ = MoveStackAsync(right);
            }
        });

    /// <summary>カーソルが同じ場所に留まっていれば捕捉する。タイマーの 1 打ぶん。</summary>
    internal async Task TickAsync()
    {
        if (!_cursor.TryGetPosition(out int x, out int y))
        {
            return;
        }
        if (Math.Abs(x - _anchorPoint.X) > MoveTolerancePx || Math.Abs(y - _anchorPoint.Y) > MoveTolerancePx)
        {
            _anchorPoint = new System.Drawing.Point(x, y);
            _anchorTime = _time.GetUtcNow();
            return;
        }
        if (_time.GetUtcNow() - _anchorTime < StationaryTime)
        {
            return;
        }
        if (Math.Abs(x - _lastCapturedPoint.X) <= MoveTolerancePx &&
            Math.Abs(y - _lastCapturedPoint.Y) <= MoveTolerancePx)
        {
            return; // 同じ場所は再捕捉しない
        }
        // 確定アイコン上では捕捉しない (クリックしに行った先で選択が変わるのを防ぐ)。
        // 見るのは要素が持つ矩形ではなく**いま枠を出している矩形**である —
        // 対象ウィンドウが動いたあとは前者が古く、除外領域が画面と食い違う
        if (_currentNode?.Element is not null &&
            OverlayGeometry.IsInIconZone(_shownRect, _dpi.DpiFor(_shownRect), x, y))
        {
            return;
        }
        _lastCapturedPoint = new System.Drawing.Point(x, y);
        await CaptureAtAsync(x, y).ConfigureAwait(true);
    }

    private async Task CaptureAtAsync(int x, int y)
    {
        try
        {
            HoverCapture? capture = await _services.CaptureAtAsync(x, y, _treeView).ConfigureAwait(true);
            if (capture is null)
            {
                return; // 自プロセス上など
            }
            _selectionOrigin = capture.Chain[^1];
            ApplyCapture(capture);
        }
        catch (Exception ex)
        {
            _view.Hint = Format(PickerStringKeys.CaptureFailed, ex.Message);
        }
    }

    // ---------- ツリー構築 ----------

    /// <summary>プロセス → … → 対象要素の直系チェーンでツリーを置き換える。</summary>
    private void ApplyCapture(HoverCapture capture)
    {
        // ApplyCapture は 3 経路 (捕捉 / ビュー切替 / 重なり切替) の合流点なので、
        // チェーンの所有をここで引き受ければ 3 箇所に書かずに済む。
        // 掃き出しより**先**であることが要る (_owned の不変条件 2)
        Own(capture.Chain);

        var expandOrder = new List<PickerTreeNode>();

        _view.DiscardDeferredWork();
        _view.BeginNodeUpdate();
        try
        {
            Roots.Clear();
            var processRoot = new PickerTreeNode(capture.ProcessLabel, null) { ChildrenLoaded = true };
            Roots.Add(processRoot);
            expandOrder.Add(processRoot);

            PickerTreeNode parent = processRoot;
            PickerTreeNode? last = null;
            foreach (IPickerElement element in capture.Chain)
            {
                PickerTreeNode node = CreateNode(element);
                parent.Children.Add(node);
                if (parent.Element is not null)
                {
                    // 中間チェーンノード: 実体化時の Expanding による自動全列挙はさせない
                    // (全列挙はクリック/明示展開時のみ = 仕様)
                    parent.HasUnrealizedChildren = false;
                }
                expandOrder.Add(node);
                parent = node;
                last = node;
            }
            expandOrder.RemoveAt(expandOrder.Count - 1); // 末端 (選択要素) は展開しない
            _currentNode = last;
        }
        finally
        {
            _view.EndNodeUpdate();
        }

        // 置き換えた木のハンドルを手放す。ExpandThenSelect / RefreshCurrentNode より**前**に
        // 掃くのは、あちらが継ぎ目呼び出しを始めるので、解放が在庫中の読み取りと競合する窓が
        // その分だけ狭くなるからである
        ReleaseUnreachable();

        if (_currentNode is not null)
        {
            _view.ExpandThenSelect(expandOrder, _currentNode);
            RefreshCurrentNode();
        }
    }

    private void RefreshCurrentNode()
    {
        if (_currentNode?.Element is { } element)
        {
            // まず手元の (古いかもしれない) 矩形で即座に出し、スナップショットが
            // 返ってきたら現在の矩形へ描き直す。往復は増えない
            ShowRect(element.BoundingRectangle);
            _ = RefreshPropsAsync(_currentNode);
        }
        else
        {
            _overlay.Hide();
            _view.ShowProperties([]);
        }
    }

    private void ShowRect(ElementRect rect)
    {
        _shownRect = rect;
        _overlay.ShowRect(rect);
    }

    internal async Task RefreshPropsAsync(PickerTreeNode node)
    {
        if (node.Element is null)
        {
            return;
        }
        ElementPropertySnapshot? snap;
        try
        {
            snap = await _services.GetSnapshotAsync(node.Element).ConfigureAwait(true);
        }
        catch (ObjectDisposedException)
        {
            // 読み取りの在庫中に木が入れ替わり、この行のハンドルが解放された。
            // 出す先の行がもう無いので何もしない。
            // 握り潰しではない — ここは fire-and-forget (RefreshCurrentNode) で呼ばれるので、
            // 捕まえなければ誰も観測しない faulted Task になる (docs/DESIGN.md §7)
            return;
        }
        if (!ReferenceEquals(node, _currentNode))
        {
            return;
        }
        if (snap is null)
        {
            // 相手から読めない (実測: 塞がれたアプリでは「Operation timed out」になる)。
            // **一覧を古い値のまま残さない** — 残すと「読めている」ようにしか見えず、
            // §12 が名指しする「静かに間違う」形そのものになる。
            //
            // **失敗は null で届く。**継ぎ目 (IPickerServices) の実装が例外を投げるのは
            // 「渡した要素がもう無い」(ObjectDisposedException) ときだけで、UIA の失敗は
            // セッション層が null へ正規化する — かつてここに COMException の catch が
            // あったが、製品経路には投げ手が存在せず**死んだ分岐**だった (偽の安心)。
            // 枠は消さない: 矩形は手元の値で出せており、消すと捕捉ごと壊れたように見える。
            // 空のままにはならない — 次に選択や捕捉が動けば新しい読み取りが走る
            _view.ShowProperties([]);
            return;
        }
        // 対象ウィンドウが動いていれば要素の矩形は古い。スナップショットは現在値なので
        // ここで枠を追随させる (この読み取りはプロパティ一覧のために既に発生している)
        ShowRect(snap.BoundingRectangle);
        // 値は GetDisplayValue (表示用・カルチャ依存) を通す。プロパティ名は UIA の語彙なので
        // 翻訳しない — ユーザーが条件を書くときに指すのはこの名前である (docs/LOCALIZATION.md §3)
        string Row(string name, object? value) => Format(PickerStringKeys.PropertyRow, name, value);

        var items = new List<string>
        {
            Row("Name", snap.GetDisplayValue(TriggerProperty.Name)),
            Row("AutomationId", snap.GetDisplayValue(TriggerProperty.AutomationId)),
            // ControlType だけは 2 つの名前を両方見せる (docs/DESIGN.md L6)。
            // 表示名はプロバイダーのロケール依存なので、条件に書くべき安定名を併記しないと
            // 「画面に出ている名前で Equals を書いたら一致しない」が起きる
            Format(PickerStringKeys.PropertyRowControlType, snap.LocalizedControlType, snap.ControlTypeName, snap.ControlType),
            Row("BoundingRectangle", snap.GetDisplayValue(TriggerProperty.BoundingRectangle)),
            Row("AccessKey", snap.GetDisplayValue(TriggerProperty.AccessKey)),
            Row("AcceleratorKey", snap.GetDisplayValue(TriggerProperty.AcceleratorKey)),
            Row("HelpText", snap.GetDisplayValue(TriggerProperty.HelpText)),
            Row("IsEnabled", snap.GetDisplayValue(TriggerProperty.IsEnabled)),
            Row("IsOffscreen", snap.GetDisplayValue(TriggerProperty.IsOffscreen)),
        };
        if (snap.SupportsValuePattern)
        {
            items.Add(Row("ValuePattern.Value", snap.GetDisplayValue(TriggerProperty.Value)));
        }
        if (snap.SupportsRangeValuePattern)
        {
            items.Add(Row("RangeValue.Value", snap.GetDisplayValue(TriggerProperty.RangeValue)));
            items.Add(Row("RangeValue.Minimum", snap.GetDisplayValue(TriggerProperty.RangeValueMinimum)));
            items.Add(Row("RangeValue.Maximum", snap.GetDisplayValue(TriggerProperty.RangeValueMaximum)));
        }
        _view.ShowProperties(items);
    }

    /// <summary>
    /// ノード VM を生成し、**ユーザーが**展開したときだけ子を全列挙するようにする。
    /// チェーン表示のための展開 (ExpandForDisplay) では列挙しない — でないと
    /// 捕捉のたびに経路上の全段が兄弟を取りに行く。
    /// </summary>
    private PickerTreeNode CreateNode(IPickerElement element)
    {
        // 表示は DisplayLabel (相手アプリのロケールのコントロール型名)。
        // ControlTypeName は識別用の安定形なので画面には使わない (docs/DESIGN.md L6)
        var node = new PickerTreeNode(element.DisplayLabel, element) { HasUnrealizedChildren = true };
        node.ExpandedByUser += n => _ = LoadChildrenAsync(n);
        return node;
    }

    /// <summary>ノードの子を全列挙する (既存のチェーン子ノードはサブツリーを保ったまま差し込む)。</summary>
    internal async Task LoadChildrenAsync(PickerTreeNode node)
    {
        if (node.ChildrenLoaded || node.Element is null)
        {
            return;
        }
        node.ChildrenLoaded = true; // 二重実行防止

        PickerTreeNode? chainChild = node.Children.Count > 0 ? node.Children[0] : null;
        ChildrenResult? result;
        try
        {
            result = await _services.GetChildrenAsync(node.Element, _treeView, chainChild?.Element).ConfigureAwait(true);
        }
        catch (ObjectDisposedException)
        {
            // 列挙の在庫中に木が入れ替わり、この行のハンドルが解放された。行はもう無いので
            // ChildrenLoaded は戻さない (戻しても誰も再試行しない)。理由は RefreshPropsAsync と同じ
            return;
        }
        if (result is null)
        {
            // 列挙に失敗した (相手が塞がっている等)。**「子 0 件」と区別できることが要点である** —
            // 空リストとして扱うと、この行の部分木 (選択中の要素を含みうる) を消したうえ
            // ChildrenLoaded=true / HasUnrealizedChildren=false で二度と列挙しなくなり、
            // 塞ぎが明けても戻らない。子は触らず、再展開で再試行できる状態へ戻す
            node.ChildrenLoaded = false;
            return;
        }
        // 差し込みループはチェーン子の位置 (result.ChainChildIndex) を意図的に飛ばすので、
        // その 1 個はノードに包まれない。所有下に置いておけば、下の掃き出しが回収する
        Own(result.Children);

        _view.BeginNodeUpdate();
        try
        {
            if (chainChild is not null && result.ChainChildIndex >= 0)
            {
                // チェーン子 (index 0 に居る) のサブツリーを保ったまま前後に兄弟を挿入する。
                // Clear すると一時的に空になった行が折りたたまれ、TwoWay バインディング経由で
                // IsExpanded=false が書き戻されてしまう。
                for (int i = 0; i < result.ChainChildIndex; i++)
                {
                    node.Children.Insert(i, CreateNode(result.Children[i]));
                }
                for (int i = result.ChainChildIndex + 1; i < result.Children.Count; i++)
                {
                    node.Children.Add(CreateNode(result.Children[i]));
                }
            }
            else
            {
                node.Children.Clear();
                foreach (IPickerElement child in result.Children)
                {
                    node.Children.Add(CreateNode(child));
                }
            }
            node.HasUnrealizedChildren = false;
        }
        finally
        {
            _view.EndNodeUpdate();
        }

        // 差し込みで使わなかったチェーン子のハンドルと、Clear した行のハンドルを手放す
        ReleaseUnreachable();

        _view.Expand([node]); // 列挙後に展開状態を再保証
        if (_currentNode is not null && !ReferenceEquals(_view.SelectedNode, _currentNode))
        {
            _view.SelectDeferred(_currentNode);
        }
    }

    // ---------- ビュー切替・検索 ----------

    /// <summary>
    /// 現在のビューでチェーンを再構築し、選択をできる限り復元する。
    /// まずユーザーが意図的に選択した要素 (origin) から試み、消えていれば現在の選択要素から試みる。
    /// どちらも解決できなければ元の表示を維持する。
    /// </summary>
    internal async Task RebuildChainAsync()
    {
        IPickerElement? origin = _selectionOrigin;
        IPickerElement? current = _currentNode?.Element;
        if (origin is null && current is null)
        {
            return;
        }

        HoverCapture? capture = null;
        try
        {
            if (origin is not null)
            {
                capture = await _services.GetChainAsync(origin, _treeView).ConfigureAwait(true);
            }
            // ここは「同じ UIA 要素か」ではなく「同じハンドルか」を見ている。2 度目の問い合わせに
            // 意味があるのは別のハンドルを渡すときだけであり、UIA 上の同一性は関係ない
            if (capture is null && current is not null && !ReferenceEquals(current, origin))
            {
                capture = await _services.GetChainAsync(current, _treeView).ConfigureAwait(true);
            }
        }
        catch (ObjectDisposedException)
        {
            // origin / current はローカルに写してあるので、在庫中にホバー捕捉が走って
            // 所有根から外れると解放されうる。そのときは新しい捕捉が既に木を置き換えている
            // ので、ここで復元することは何も無い。理由は RefreshPropsAsync と同じ
            return;
        }

        if (capture is not null)
        {
            ApplyCapture(capture);
        }
        else
        {
            _view.Hint = _strings.GetString(PickerStringKeys.ViewSwitchFailed);
        }
    }

    private void SearchNextCore(string query)
    {
        if (query.Length == 0)
        {
            return;
        }
        // 取得済みノードのみを対象に DFS (未取得の子までは探索しない)
        var matches = new List<(PickerTreeNode Node, List<PickerTreeNode> Ancestors)>();
        var path = new List<PickerTreeNode>();

        void Visit(PickerTreeNode n)
        {
            if (n.Display.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add((n, [.. path]));
            }
            path.Add(n);
            foreach (PickerTreeNode c in n.Children)
            {
                Visit(c);
            }
            path.RemoveAt(path.Count - 1);
        }
        foreach (PickerTreeNode root in Roots)
        {
            Visit(root);
        }

        if (matches.Count == 0)
        {
            _view.Hint = Format(PickerStringKeys.SearchNoMatch, query);
            return;
        }
        _searchCursor = (_searchCursor + 1) % matches.Count;
        (PickerTreeNode node, List<PickerTreeNode> ancestors) = matches[_searchCursor];

        // 見つかった要素までの道を開ける。「ここで初めて開いた段」だけがユーザー操作と同じ扱い
        // = 子の全列挙を伴う。既に開いている段 (捕捉したチェーンの各段) には手を出さない —
        // 触ると検索 1 回で経路の全段が兄弟を取りに行く (深さ 17 段の Chromium で 17 往復)。
        // この抑止は View の setter の同値ガードには頼らず、ここで明示的に行う。
        List<PickerTreeNode> newlyOpened = [.. ancestors.Where(a => !a.IsExpanded)];
        _view.Expand(ancestors);
        foreach (PickerTreeNode ancestor in newlyOpened)
        {
            _ = LoadChildrenAsync(ancestor);
        }

        _currentNode = node; // 遅延選択が破棄されないよう先に選択対象を確定する
        if (node.Element is { } found)
        {
            _selectionOrigin = found;
        }
        RefreshCurrentNode();
        ReleaseUnreachable(); // 所有根が変わった (NotifyTreeSelectionChanged と同じ理由)
        _view.SelectDeferred(node);
        _view.Hint = Format(PickerStringKeys.SearchPosition, _searchCursor + 1, matches.Count);
    }

    // ---------- 重なり要素の切替 (←/→) ----------

    internal async Task MoveStackAsync(bool right)
    {
        try
        {
            if (_lastCapturedPoint.X == int.MinValue)
            {
                return;
            }
            // 毎回 現在の選択要素を起点にスタックを構築し直す。キャッシュを使い回すと、
            // 古い要素参照 (COM RCW) が UI 側の変化 (VS Code 等の動的な描画更新) で
            // 無効になり、前後移動を繰り返した際に元の要素へ戻れなくなることがあるため
            ElementStack? stack = await _services
                .GetStackAsync(_lastCapturedPoint.X, _lastCapturedPoint.Y, _currentNode?.Element)
                .ConfigureAwait(true);
            if (stack is null)
            {
                return;
            }
            // スタックは毎回作り直すので、移動先 1 個以外は使い捨てである。所有下に置いて
            // finally の掃き出しに任せる — 早期 return が 2 つあるので、個別に解放すると漏れる
            Own(stack.Nodes);

            int next = Math.Clamp(stack.CurrentIndex + (right ? 1 : -1), 0, stack.Nodes.Count - 1);
            if (next == stack.CurrentIndex)
            {
                return;
            }
            IPickerElement target = stack.Nodes[next];
            _view.Hint = Format(PickerStringKeys.OverlapPosition, next + 1, stack.Nodes.Count, target.DisplayLabel);
            // 正規化すると、現在の表示ビュー (Control/Content) の条件を満たさない重なり要素が
            // 同じ近傍祖先へ丸められ、複数の重なり要素が区別できなくなるため正規化しない
            HoverCapture? capture = await _services.GetChainForOverlapAsync(target, _treeView).ConfigureAwait(true);
            if (capture is not null)
            {
                _selectionOrigin = target;
                ApplyCapture(capture);
            }
        }
        catch (ObjectDisposedException)
        {
            // 在庫中に掃き出しが起点の要素を解放した。出す先の状態がもう無いので静かに戻る。
            // 生メッセージをヒント欄に出す形にしない — 内部の型名が画面に出るうえ、
            // ユーザーには何も意味しない (RefreshPropsAsync / LoadChildrenAsync と同じ扱い)
        }
        catch (Exception ex)
        {
            _view.Hint = Format(PickerStringKeys.OverlapFailed, ex.Message);
        }
        finally
        {
            // 移動しなかったスタックを手放す。早期 return (stack が null / 端に居る) も覆う
            ReleaseUnreachable();
        }
    }

    // ---------- 確定 → 条件設定 ----------

    internal async Task ConfirmNodeAsync(PickerTreeNode? node)
    {
        if (node?.Element is not { } element)
        {
            return;
        }
        try
        {
            TriggerDefinition? def = await _services.BuildDefinitionAsync(element, _treeView).ConfigureAwait(true);
            ElementPropertySnapshot? snap = await _services.GetSnapshotAsync(element).ConfigureAwait(true);
            if (def is null)
            {
                _view.ConfirmedText = _strings.GetString(PickerStringKeys.ConfirmFailedElementGone);
                return;
            }
            _confirmedDef = def;
            _view.ConfirmedText = Format(PickerStringKeys.Confirmed, def.DisplayName, def.Window.ProcessName);

            List<TriggerProperty> props = [.. StandardProperties];
            if (snap?.SupportsValuePattern == true)
            {
                props.Add(TriggerProperty.Value);
            }
            if (snap?.SupportsRangeValuePattern == true)
            {
                props.Add(TriggerProperty.RangeValue);
                props.Add(TriggerProperty.RangeValueMinimum);
                props.Add(TriggerProperty.RangeValueMaximum);
            }
            _view.ShowTriggerShape(props, TriggerOn.PropertyChanged, ComparisonOp.Always);
            _view.ShowOperands(DescribeOperands(TriggerOn.PropertyChanged, ComparisonOp.Always));

            // 既定 id は確定のたびに作り直す。空のときしか入れないと、続けて別の要素を
            // 確定したときに前の id が残り、コミットで**前のトリガーを黙って置き換える**。
            // ユーザーが自分で書いた値は尊重する — 見分けはこちらが最後に入れた値との一致で行う
            string slug = element.AutomationId.Length > 0 ? element.AutomationId : element.ControlTypeName;
            string suggestedId =
                $"{Path.GetFileNameWithoutExtension(def.Window.ProcessName)}-{slug}".ToLowerInvariant();
            if (_view.KeyText.Length == 0 || string.Equals(_view.KeyText, _suggestedId, StringComparison.Ordinal))
            {
                _view.KeyText = suggestedId;
            }
            _suggestedId = suggestedId;

            // 提案表示名も id と同じ規則で扱う: 空か前回の提案のままなら新しい提案で置き換え、
            // ユーザーが書いた値は残す
            string suggestedDisplayName = def.DisplayName ?? string.Empty;
            if (_view.DisplayNameText.Length == 0 ||
                string.Equals(_view.DisplayNameText, _suggestedDisplayName, StringComparison.Ordinal))
            {
                _view.DisplayNameText = suggestedDisplayName;
            }
            _suggestedDisplayName = suggestedDisplayName;
            _view.SetCommitEnabled(true);
        }
        catch (ObjectDisposedException)
        {
            // 在庫中に掃き出しが対象を解放した (MoveStackAsync と同じ扱い)
        }
        catch (Exception ex)
        {
            _view.ConfirmedText = Format(PickerStringKeys.ConfirmFailed, ex.Message);
        }
    }

    /// <summary>表示専用の整形。現在の UI カルチャに従う (docs/DESIGN.md L7)。</summary>
    private string Format(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, _strings.GetString(key), args);

    // ---------- 要素ハンドルの所有 (docs/DESIGN.md §7) ----------

    /// <summary>継ぎ目から受け取ったハンドルを所有下に置く。</summary>
    /// <remarks>
    /// **破棄後は受け取ったものをその場で手放す。**閉じ際に在庫中だった捕捉は
    /// <see cref="Dispose"/> の後に完了しうるが、そこで台帳へ載せても掃き出しはもう走らない —
    /// 決定的解放 (docs/DESIGN.md §7) が閉窓後にだけ破れる形になる。GC は回収するので
    /// 恒久リークではないが、「持っていたぶんは自分で返す」という §7 の規律の外に出る
    /// </remarks>
    private void Own(IReadOnlyList<IPickerElement> received)
    {
        foreach (IPickerElement element in received)
        {
            if (_disposed)
            {
                element.Dispose();
                continue;
            }
            _owned.Add(element);
        }
    }

    /// <summary>所有根から到達できないハンドルを解放する。</summary>
    /// <remarks>
    /// 到達可能性で見るのは、捨てる場所を数え上げると <see cref="_selectionOrigin"/> 経由でしか
    /// 到達できない要素を取りこぼすからである (<see cref="_owned"/> の remarks)。
    /// 逆に**解放しすぎ**は静かに間違わない — 値のメンバー 4 つはスナップショットなので
    /// 読めたままで、壊れるのは継ぎ目へ渡し返す経路だけであり、そこは例外になる。
    /// </remarks>
    private void ReleaseUnreachable()
    {
        if (_owned.Count == 0)
        {
            return;
        }

        var reachable = new HashSet<IPickerElement>(ReferenceEqualityComparer.Instance);

        void Visit(PickerTreeNode node)
        {
            if (node.Element is { } element)
            {
                reachable.Add(element);
            }
            foreach (PickerTreeNode child in node.Children)
            {
                Visit(child);
            }
        }
        foreach (PickerTreeNode root in Roots)
        {
            Visit(root);
        }
        if (_selectionOrigin is { } origin)
        {
            reachable.Add(origin);
        }
        if (_currentNode?.Element is { } current)
        {
            reachable.Add(current);
        }

        // 列挙中に _owned を書き換えないよう、先に取り出す
        List<IPickerElement> unreachable = [.. _owned.Where(e => !reachable.Contains(e))];
        foreach (IPickerElement element in unreachable)
        {
            _owned.Remove(element);
            element.Dispose();
        }
    }

    /// <summary>持っているハンドルをすべて解放する (終了時)。</summary>
    /// <remarks>
    /// <see cref="Roots"/> は**触らない**。ホストの終了処理の途中で
    /// <c>CollectionChanged(Reset)</c> を View (WinForms の TreeMirror / XAML の ItemsSource) へ
    /// 投げることになる。解放済みのハンドルを持つノードを表示し続けるのは安全である
    /// (値はスナップショットで、<see cref="PickerTreeNode.CanConfirm"/> は null 判定しか見ない)。
    /// </remarks>
    private void ReleaseAllElements()
    {
        foreach (IPickerElement element in _owned)
        {
            element.Dispose();
        }
        _owned.Clear();
        _selectionOrigin = null;
    }
}
