// トリガ一覧エディタの WinUI 3 View (docs/DESIGN.md §4)。
//
// 振る舞いは TriggerListEditorPresenter が持つ。ここに残るのは「WinUI3 でしかそうならない」
// ことだけである:
//   ・窓単位のモーダルが無いので、完了は TaskCompletionSource + Closed で伝える
//   ・MRT Core からの文字列解決 (ラベルは x:Uid、Title だけコードで)
//   ・ListView の SelectedRanges から選択行を組み立てること
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    // ---------- ユーザー操作 → プレゼンター ----------

    private void OnAdd(object sender, RoutedEventArgs e) => _presenter.NotifyAddRequested();

    private void OnEdit(object sender, RoutedEventArgs e) => _presenter.NotifyEditRequested();

    private void OnDelete(object sender, RoutedEventArgs e) => _presenter.NotifyDeleteRequested();

    private void OnCombine(object sender, RoutedEventArgs e) => _presenter.NotifyCombineRequested();

    private void OnDecompose(object sender, RoutedEventArgs e) => _presenter.NotifyDecomposeRequested();

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        _result = _presenter.Snapshot();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    // ---------- ITriggerListEditorView ----------

    string ITriggerListEditorView.Status { set => EditorStatus.Text = value; }

    string ITriggerListEditorView.ExpressionText => ExpressionBox.Text ?? string.Empty;

    string ITriggerListEditorView.UnwatchedText => UnwatchedBox.Text ?? string.Empty;

    // **具体型へ明示的にキャストする** (docs/DESIGN.md §12)。
    // IReadOnlyList<int> を狙ったコレクション式は具体型が決まらず、
    // WinRT 経路では trim / AOT で壊れる (CsWinRT1032)。
    IReadOnlyList<int> ITriggerListEditorView.SelectedIndices =>
        (int[])[.. EditorTriggerList.SelectedRanges
            .SelectMany(r => Enumerable.Range(r.FirstIndex, (int)r.Length))];

    // 継ぎ目が渡すリストは配列にしてから ItemsSource へ渡す。WinUI はインターフェース型の
    // リストをここで受け付けないうえ、プレゼンター側のリストは更新され続けない
    void ITriggerListEditorView.ShowRows(IReadOnlyList<string> rows)
        => EditorTriggerList.ItemsSource = rows.ToArray();

    void ITriggerListEditorView.ShowPicker(TriggerDefinition? definitionToEdit)
    {
        // 同時に開くのは 1 枚。2 枚目を開けるようにすると「どちらのコミットがどの行なのか」が
        // 決まらなくなる (presenter は編集中の行を 1 つだけ覚えている)
        if (_picker is { } open)
        {
            open.Activate();
            if (definitionToEdit is not null)
            {
                open.LoadDefinition(definitionToEdit);
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
        picker.Activate();
        if (definitionToEdit is not null)
        {
            picker.LoadDefinition(definitionToEdit);
        }
    }
}
