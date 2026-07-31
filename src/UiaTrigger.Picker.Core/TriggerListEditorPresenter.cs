// トリガ一覧エディタの振る舞い (docs/DESIGN.md §4)。
//
// ピッカーの presenter と同じ形である — どの UI フレームワークでも同じ答えになるものだけを持ち、
// 違うもの (窓の出し方・一覧コントロール・モーダルの有無) は継ぎ目の向こうに置く。
//
// **作業用の写しの上で編集する。**渡されたリストにも、その中の定義にも触れない。
// 保存先を決めるのも監視を止めるのもホストの仕事であり、エディタは値を受け取って値を返すだけである
// (docs/DESIGN.md §4 が定める継ぎ目の絶対条件)。
using System.Globalization;
using System.Text.Json;
using UiaTrigger.Models;
using UiaTrigger.Serialization;

namespace UiaTrigger.Picker;

/// <summary>The trigger-list editor's view, as its presenter sees it.</summary>
/// <remarks>
/// Everything here is either a place to put text, a place to read the user's text from, or an
/// operation whose implementation is specific to one UI framework. Notably the view — not the
/// presenter — owns the child picker's lifetime: how a window is opened, owned and closed differs
/// per framework, and WinUI has no window-modal dialogs at all.
/// </remarks>
public interface ITriggerListEditorView
{
    /// <summary>Sets the line describing the outcome of the last operation.</summary>
    string Status { set; }

    /// <summary>The clause expression the user typed, for combining.</summary>
    string ExpressionText { get; }

    /// <summary>Ids of the triggers the user wants to narrow with rather than watch.</summary>
    string UnwatchedText { get; }

    /// <summary>The selected rows, as indices into the list last shown, in ascending order.</summary>
    IReadOnlyList<int> SelectedIndices { get; }

    /// <summary>Replaces the rows of the list. The text is already formatted.</summary>
    void ShowRows(IReadOnlyList<string> rows);

    /// <summary>
    /// Opens the picker: on <paramref name="definitionToEdit"/> when it is set, otherwise to record
    /// something new.
    /// </summary>
    /// <remarks>
    /// The view is expected to report what the picker commits through
    /// <see cref="TriggerListEditorPresenter.NotifyPickerCommitted"/>, and to report the picker
    /// closing through <see cref="TriggerListEditorPresenter.NotifyPickerClosed"/>.
    /// </remarks>
    void ShowPicker(TriggerDefinition? definitionToEdit);
}

/// <summary>Drives the trigger-list editor: add, edit, remove, combine and take apart.</summary>
/// <remarks>
/// <para>
/// Works on a copy throughout. The list handed to the constructor is deep-copied, and
/// <see cref="Snapshot"/> returns deep copies, so a host can show the editor and keep its own list
/// untouched until the user accepts. Where triggers are stored, and whether a monitor is running,
/// stays the host's business.
/// </para>
/// <para>
/// A view calls the <c>Notify…</c> methods when the user does something, and implements
/// <see cref="ITriggerListEditorView"/> to be told what to show.
/// </para>
/// </remarks>
public sealed class TriggerListEditorPresenter
{
    private readonly ITriggerListEditorView _view;
    private readonly IPickerStrings _strings;
    private readonly List<TriggerDefinition> _working;

    /// <summary>
    /// 編集セッションで開いている元の id。null = 追加のために開いている (または開いていない)。
    /// </summary>
    /// <remarks>
    /// ピッカーは 1 回開いたら何件でもコミットできるので、「いま編集しているのはどの行か」を
    /// 覚えておかないと、2 度目のコミットが**末尾への追加**に化ける。
    /// </remarks>
    private string? _editingId;

    /// <summary>Creates a presenter over a copy of <paramref name="triggers"/>.</summary>
    /// <param name="view">The view to drive.</param>
    /// <param name="strings">Supplies the user-facing strings.</param>
    /// <param name="triggers">The triggers to edit. Neither the list nor its items are modified.</param>
    public TriggerListEditorPresenter(
        ITriggerListEditorView view,
        IPickerStrings strings,
        IReadOnlyList<TriggerDefinition> triggers)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(triggers);

        _view = view;
        _strings = strings;
        _working = [.. triggers.Select(Clone)];
        ShowRows();
    }

    /// <summary>A deep copy of the edited list, in order.</summary>
    /// <remarks>
    /// Copies rather than the presenter's own instances: a host that keeps the result and shows the
    /// editor again would otherwise be handing back the very objects a later session edits in place.
    /// </remarks>
    public IReadOnlyList<TriggerDefinition> Snapshot() => [.. _working.Select(Clone)];

    /// <summary>Reports that the user asked to record a new trigger.</summary>
    public void NotifyAddRequested()
    {
        _editingId = null;
        _view.ShowPicker(null);
    }

    /// <summary>Reports that the user asked to edit the selected trigger's condition.</summary>
    /// <remarks>
    /// Refused with a reason rather than an exception when the selection is not one trigger the
    /// picker can edit — a composite has to be taken apart first
    /// (<see cref="NotifyDecomposeRequested"/>).
    /// </remarks>
    public void NotifyEditRequested()
    {
        if (Selected() is not [int index])
        {
            _view.Status = _strings.GetString(EditorStringKeys.SelectOneToEdit);
            return;
        }
        TriggerDefinition target = _working[index];
        if (!TriggerPickerPresenter.CanEdit(target))
        {
            _view.Status = _strings.GetString(EditorStringKeys.CannotEditWithThePicker);
            return;
        }

        _editingId = target.Id;
        // 写しを渡す。ピッカーは渡された実体へ書き戻すので、直に渡すと
        // **キャンセルしても作業用リストが変わってしまう**
        _view.ShowPicker(Clone(target));
    }

    /// <summary>Reports that the user asked to remove the selected triggers.</summary>
    public void NotifyDeleteRequested()
    {
        int[] selected = Selected();
        if (selected.Length == 0)
        {
            _view.Status = _strings.GetString(EditorStringKeys.SelectSomething);
            return;
        }

        // 後ろから消す。前から消すと、消した時点で以降の index がずれる
        for (int i = selected.Length - 1; i >= 0; i--)
        {
            _working.RemoveAt(selected[i]);
        }
        ShowRows();
        _view.Status = Format(EditorStringKeys.DeleteDone, selected.Length);
    }

    /// <summary>Reports that the user asked to combine the selected triggers into one.</summary>
    /// <remarks>
    /// The sources stay in the list. Combining is additive, which is what makes it undoable without
    /// an undo: remove the composite and the originals are still there.
    /// </remarks>
    public void NotifyCombineRequested()
    {
        int[] selected = Selected();
        string[] unwatched = _view.UnwatchedText.Split(
            ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        TriggerCompositionResult result = TriggerComposer.Compose(
            [.. selected.Select(i => _working[i])],
            _view.ExpressionText,
            unwatched,
            _working.Select(t => t.Id));
        if (!result.IsValid)
        {
            _view.Status = Format(EditorStringKeys.CombineFailed, result.Error);
            return;
        }

        _working.Add(result.Definition!);
        ShowRows();
        _view.Status = Format(EditorStringKeys.CombineDone, result.Definition!.Id, selected.Length);
    }

    /// <summary>Reports that the user asked to take the selected composite apart.</summary>
    /// <remarks>
    /// The composite stays in the list, and one trigger per condition is added — the way back when
    /// the triggers a composite was combined from have since been removed. Additive for the same
    /// reason combining is.
    /// </remarks>
    public void NotifyDecomposeRequested()
    {
        if (Selected() is not [int index] || !IsComposite(_working[index]))
        {
            _view.Status = _strings.GetString(EditorStringKeys.SelectACompositeToDecompose);
            return;
        }

        TriggerDefinition composite = _working[index];
        IReadOnlyList<TriggerDefinition> recovered =
            TriggerComposer.Decompose(composite, _working.Select(t => t.Id));
        _working.AddRange(recovered);
        ShowRows();
        _view.Status = Format(EditorStringKeys.DecomposeDone, composite.Id, recovered.Count);
    }

    /// <summary>Reports a trigger the picker committed.</summary>
    /// <param name="definition">The committed trigger. The presenter copies it.</param>
    /// <remarks>
    /// While editing, the edited row is replaced **where it is** rather than moved to the end, so
    /// a list the user has arranged stays arranged. Otherwise — and whenever the committed id is a
    /// new one — the id decides: an existing trigger with that id is replaced, so re-recording the
    /// same trigger updates it instead of adding a second one.
    /// </remarks>
    public void NotifyPickerCommitted(TriggerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        TriggerDefinition committed = Clone(definition);

        int editing = _editingId is null ? -1 : IndexOf(_editingId);
        if (editing >= 0)
        {
            // id を変えて確定した場合、その id の別の行と衝突する。先に消しておく —
            // 残すと id が重複したリストを返し、TriggerMonitor.AddAsync が投げる
            RemoveOtherRowsWith(committed.Id, editing);
            editing = IndexOf(_editingId!);
            _working[editing] = committed;
            // 以後のコミットも同じ行を差し替える。id を変えたなら新しい id で追う
            _editingId = committed.Id;
        }
        else
        {
            RemoveOtherRowsWith(committed.Id, keep: -1);
            _working.Add(committed);
        }
        ShowRows();
    }

    /// <summary>Reports that the picker was closed.</summary>
    /// <remarks>
    /// Ends the editing session, so a trigger committed by a picker opened later is added rather
    /// than replacing whichever row was last edited.
    /// </remarks>
    public void NotifyPickerClosed() => _editingId = null;

    /// <summary>選択されている行 (昇順、範囲外は捨てる)。</summary>
    private int[] Selected() =>
        [.. _view.SelectedIndices.Where(i => i >= 0 && i < _working.Count).Distinct().Order()];

    private int IndexOf(string id) =>
        _working.FindIndex(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    /// <summary><paramref name="keep"/> 以外の行から、その id を持つものを消す。</summary>
    private void RemoveOtherRowsWith(string id, int keep)
    {
        for (int i = _working.Count - 1; i >= 0; i--)
        {
            if (i != keep && string.Equals(_working[i].Id, id, StringComparison.Ordinal))
            {
                _working.RemoveAt(i);
            }
        }
    }

    private static bool IsComposite(TriggerDefinition definition)
        => definition.Expression is not null || definition.Clauses.Count > 1;

    private void ShowRows()
    {
        var rows = new List<string>(_working.Count);
        foreach (TriggerDefinition def in _working)
        {
            // 列挙メンバー名 (Combine / Property / Op / On) は翻訳しない。ユーザーが JSON や
            // ピッカーで目にするのと同じ語でなければ対応が取れなくなる (docs/LOCALIZATION.md §3)
            if (def.Expression is { } expression)
            {
                // 複合は 1 行に収まらないので別書式にする。プロセス名を出しても意味が無い
                // (要素ごとに違う) ので、条件の数と式を出す。
                //
                // **「要素が何個か」をここで数えないこと。**同じ要素かどうかは Window / Locator の
                // 値で決まるが、あの 2 つは Equals を持たない可変クラスなので、素朴に比べると
                // JSON から読んだ「同じ要素を指す 2 句」を別物と数えてしまう。正しい数は
                // 監視中の TriggerMonitorDiagnostics.ElementSlotCount が持っている
                rows.Add(Format(
                    EditorStringKeys.CompositeRow, def.Id, def.Clauses.Count, def.On, expression));
                continue;
            }
            string clauses = def.Clauses.Count == 0
                ? _strings.GetString(EditorStringKeys.NoClauses)
                : string.Join($" {def.Combine} ", def.Clauses.Select(c => $"{c.Property} {c.Op}"));
            rows.Add(Format(
                EditorStringKeys.TriggerRow, def.Id, def.DisplayName, def.Window.ProcessName, def.On, clauses));
        }
        _view.ShowRows(rows);
    }

    /// <summary>表示専用の整形。現在の UI カルチャに従う (docs/DESIGN.md L7)。</summary>
    private string Format(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, _strings.GetString(key), args);

    /// <summary>定義の深い写し。</summary>
    /// <remarks>
    /// JSON の往復で作る。手で写すと、モデルにプロパティが増えたときに**黙って欠ける** —
    /// しかも欠けるのは「エディタを通したときだけ」なので、保存されたファイルを見るまで分からない。
    /// <see cref="TriggerJsonContext"/> は source-generated なので AOT でも動く。
    /// </remarks>
    private static TriggerDefinition Clone(TriggerDefinition definition) =>
        JsonSerializer.Deserialize(
            JsonSerializer.Serialize(definition, TriggerJsonContext.Default.TriggerDefinition),
            TriggerJsonContext.Default.TriggerDefinition)!;
}
