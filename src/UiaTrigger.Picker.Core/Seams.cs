// プレゼンターと View / OS のあいだの継ぎ目 (docs/DESIGN.md §12)。
//
// 置いてあるものはどれも「UI フレームワークごとに答えが違う」か「テストで差し替えられないと
// 検証できない」かのどちらかである。逆に、どのフレームワークでも同じになるもの
// (ホバー滞留の規則・チェーンの組み立て・条件欄の出し分け) は継ぎ目には出さず
// TriggerPickerPresenter が持つ。
//
// SynchronizationContext は使わない。T1 で決定的に差し替えられないからであり、
// この継ぎ目は「FakeDispatcher が作れること」のために在る。
using UiaTrigger.Models;

namespace UiaTrigger.Picker;

/// <summary>Posts work back onto the thread that owns the picker's UI.</summary>
/// <remarks>
/// The overlay raises its events on its own thread — the confirm click on the overlay's message
/// loop, the arrow keys on the keyboard hook's — so everything they lead to has to come back here
/// first.
/// </remarks>
public interface IUiDispatcher
{
    /// <summary>Runs <paramref name="action"/> on the UI thread, without waiting for it.</summary>
    void Post(Action action);

    /// <summary>Creates a UI-thread timer. It starts stopped.</summary>
    IUiTimer CreateTimer(TimeSpan interval, Action tick);
}

/// <summary>A repeating timer that ticks on the UI thread.</summary>
public interface IUiTimer : IDisposable
{
    /// <summary>Starts or stops the ticking. Setting it to what it already is does nothing.</summary>
    void SetRunning(bool running);
}

/// <summary>Reads the mouse cursor position in physical screen coordinates.</summary>
/// <remarks>
/// A seam rather than a direct <c>GetCursorPos</c> call for two reasons: hover dwell is the
/// picker's central behaviour and needs to be testable, and a UI test has no other way to make the
/// picker believe the cursor is somewhere — moving it for real is synthetic input, which this
/// repository does not allow in tests (docs/TESTING.md §3).
/// </remarks>
public interface ICursorSource
{
    /// <summary>Reads the cursor position. False when the position could not be read.</summary>
    bool TryGetPosition(out int x, out int y);
}

/// <summary>要素が乗っているモニターの DPI (docs/DESIGN.md §9)。</summary>
/// <remarks>
/// <para>
/// **矩形から引くのが要点である。**オーバーレイの寸法を決めるのは
/// <see cref="OverlayController"/> (<c>_pendingRect</c>) と
/// <see cref="TriggerPickerPresenter"/> (<c>_shownRect</c>) の 2 か所あり、
/// 同じ矩形からは必ず同じ DPI が出ないと、**絵と当たり判定が食い違う**
/// — 見えているアイコンが押せない / ホバーの除外領域が絵からずれる、という
/// 例外の出ない壊れ方になる。
/// </para>
/// <para>
/// <c>GetDpiForWindow(オーバーレイ)</c> は使えない。寸法を決める時点でまだ移動していないので、
/// 初回は別のモニターの DPI を返しうる。
/// </para>
/// <para>
/// 直接 P/Invoke せず継ぎ目にしてあるのは <see cref="ICursorSource"/> と同じ理由である —
/// 埋め込むと T1 が動いている機械の表示スケールで結果を変え、
/// 96 では緑・175% では赤 (逆もある) という**再現しないテスト**になる。
/// </para>
/// </remarks>
internal interface IDpiSource
{
    /// <summary>矩形の左上が乗っているモニターの DPI。読めなければ 96。</summary>
    int DpiFor(ElementRect rect);
}

/// <summary>Supplies the picker's user-facing strings.</summary>
/// <remarks>
/// One table of keys, several ways of supplying it: WinUI resolves <c>.resw</c> through MRT Core,
/// while other hosts can use whatever they already have. Values may contain composite format
/// placeholders; the picker formats them with the current culture (docs/DESIGN.md L3 / docs/LOCALIZATION.md §3).
/// </remarks>
public interface IPickerStrings
{
    /// <summary>The string for <paramref name="key"/>, or the key itself when it cannot be found.</summary>
    /// <remarks>Returning the key is deliberate: a blank label is far harder to notice.</remarks>
    string GetString(string key);
}

/// <summary>Which operand fields a condition needs.</summary>
/// <remarks>
/// Derived from <see cref="TriggerDraftValidator"/> so that the fields on show and the fields the
/// validator reads cannot drift apart. When they drift, a value the user cannot see ends up in the
/// saved condition.
/// </remarks>
/// <param name="Text">Whether the text operand applies.</param>
/// <param name="Value">Whether the single-value operand applies.</param>
/// <param name="Range">Whether the low and high operands apply.</param>
/// <param name="Tolerance">Whether the tolerance operand applies (numeric comparisons only).</param>
/// <param name="PollInterval">
/// Whether the poll interval operand applies. Polling re-reads elements that are already resolved,
/// so it means nothing to a trigger that only watches for its element appearing or disappearing.
/// </param>
/// <param name="PropertyChoiceEnabled">
/// Whether the property to watch can be chosen. Triggers that only watch for an element appearing
/// or being removed may carry no condition at all, and then there is no property to pick.
/// </param>
/// <param name="StoppedMatching">
/// Whether the "also notify when it stops matching" choice applies. The falling edge is derived
/// from the <see cref="TriggerOn.WhileMatching"/> level, so the choice means nothing — and the
/// monitor rejects the flag — on any other lifecycle.
/// </param>
public readonly record struct OperandVisibility(
    bool Text,
    bool Value,
    bool Range,
    bool Tolerance,
    bool PollInterval,
    bool PropertyChoiceEnabled,
    bool StoppedMatching);

/// <summary>The picker's view, as its presenter sees it.</summary>
/// <remarks>
/// <para>
/// Everything here is either a place to put text or an operation whose implementation is specific
/// to one UI framework. Notably the view — not the presenter — owns tree expansion and deferred
/// selection: waiting for a row's container to be realised is a WinUI <c>TreeViewItem</c> problem,
/// a WPF <c>ItemContainerGenerator</c> problem, and no problem at all in Windows Forms. A shared
/// implementation would be wrong in all three.
/// </para>
/// <para>
/// The presenter never writes <see cref="PickerTreeNode.IsExpanded"/>: views bind it two-way, so a
/// write from the presenter comes straight back as if a user had made it.
/// </para>
/// </remarks>
public interface IPickerView
{
    /// <summary>Sets the hint line — diagnostics, failures, and the overlap position.</summary>
    string Hint { set; }

    /// <summary>Sets the line describing the element the user has confirmed.</summary>
    string ConfirmedText { set; }

    /// <summary>Sets the line describing the outcome of the last commit.</summary>
    string CommitStatus { set; }

    /// <summary>Sets the caption of the commit button.</summary>
    /// <remarks>
    /// Written when an edit session begins
    /// (<see cref="TriggerPickerPresenter.LoadDefinition(TriggerDefinition, bool)"/>), where
    /// "Add trigger" would misdescribe what the button does.
    /// </remarks>
    string CommitCaption { set; }

    /// <summary>Closes the window hosting the picker.</summary>
    /// <remarks>
    /// Called when an edit session commits: editing ends with the one commit, unlike adding, which
    /// keeps the picker open for the next trigger. The presenter calls this after every other view
    /// write of the commit — a view may dispose itself here, so nothing may touch it afterwards.
    /// </remarks>
    void Close();

    /// <summary>The trigger id the user typed. The presenter fills in a default when it is empty.</summary>
    string KeyText { get; set; }

    /// <summary>
    /// The trigger display name the user typed. The presenter fills in a suggestion when it is
    /// empty or still carries the previous suggestion.
    /// </summary>
    string DisplayNameText { get; set; }

    /// <summary>The row currently selected in the tree, or null when there is none.</summary>
    PickerTreeNode? SelectedNode { get; }

    /// <summary>Shows the property rows for the current element. An empty list clears them.</summary>
    /// <remarks><inheritdoc cref="ShowTriggerShape" path="/remarks"/></remarks>
    void ShowProperties(IReadOnlyList<string> rows);

    /// <summary>Offers the trigger shape for a newly confirmed element and preselects it.</summary>
    /// <remarks>
    /// The list belongs to the presenter and is not kept up to date. A view that hands it straight
    /// to a control keeping a live reference — or to one that needs a specific collection type —
    /// should copy it. WinUI in particular rejects an interface-typed list here.
    /// </remarks>
    /// <param name="properties">The properties this element can be watched on.</param>
    /// <param name="lifecycle">The lifecycle event to preselect.</param>
    /// <param name="comparison">The comparison to preselect.</param>
    void ShowTriggerShape(IReadOnlyList<TriggerProperty> properties, TriggerOn lifecycle, ComparisonOp comparison);

    /// <summary>Shows exactly the operand fields the current condition uses.</summary>
    void ShowOperands(OperandVisibility visibility);

    /// <summary>Enables or disables the commit button.</summary>
    void SetCommitEnabled(bool enabled);

    /// <summary>Reads the condition the user has filled in, or null when its shape is incomplete.</summary>
    /// <remarks>
    /// Reading is the view's job because "empty" is framework-specific — WinUI's <c>NumberBox</c>
    /// reports an empty box as <see cref="double.NaN"/>, for instance.
    /// </remarks>
    TriggerDraft? ReadDraft();

    /// <summary>Fills the condition fields in from an existing trigger. The reverse of <see cref="ReadDraft"/>.</summary>
    /// <remarks>
    /// Called right after <see cref="ShowTriggerShape"/>, so the property to select is already on
    /// offer. Writing belongs to the view for the same reason reading does: how a field says "no
    /// value" differs per framework, and so does the round trip — a number written in one culture
    /// has to read back as the same number.
    /// </remarks>
    void ShowDraft(TriggerDraft draft);

    /// <summary>Drops any pending expansion or selection. Called before the tree is replaced.</summary>
    void DiscardDeferredWork();

    /// <summary>Marks the start of a change the presenter is making to the tree.</summary>
    /// <remarks>
    /// Selection changes between this and <see cref="EndNodeUpdate"/> come from the change itself,
    /// not from the user, and must not be reported back through
    /// <see cref="TriggerPickerPresenter.NotifyTreeSelectionChanged"/>.
    /// </remarks>
    void BeginNodeUpdate();

    /// <summary>Marks the end of a change the presenter is making to the tree.</summary>
    void EndNodeUpdate();

    /// <summary>Opens the rows, ancestors first, then selects <paramref name="target"/>.</summary>
    /// <remarks>Rows may not exist as controls yet, so this may finish after it returns.</remarks>
    void ExpandThenSelect(IReadOnlyList<PickerTreeNode> expandOrder, PickerTreeNode target);

    /// <summary>Opens the rows without asking for their children to be enumerated.</summary>
    void Expand(IReadOnlyList<PickerTreeNode> nodes);

    /// <summary>Selects and scrolls to a row, waiting for it to exist as a control if need be.</summary>
    void SelectDeferred(PickerTreeNode node);
}

/// <summary>選択枠 + 確定アイコン + ←/→ の捕捉。実装は <see cref="OverlayController"/>。</summary>
internal interface IOverlay : IDisposable
{
    /// <summary>ウィンドウ作成に失敗した理由 (成功時 null)。</summary>
    string? CreationError { get; }

    /// <summary>確定アイコンがクリックされた (オーバーレイスレッドから呼ばれる)。</summary>
    event Action? ConfirmClicked;

    /// <summary>選択モード中に ←(false)/→(true) が押された (フックスレッドから呼ばれる)。</summary>
    event Action<bool>? ArrowKeyPressed;

    void ShowRect(ElementRect rect);

    void Hide();

    void SetHookEnabled(bool enabled);
}
