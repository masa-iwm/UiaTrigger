// トリガ一覧エディタのリソースキー表 (docs/DESIGN.md §4)。
//
// PickerStringKeys と**別の表にしてある。**同じ表に混ぜると、ピッカーの View 3 つに対して
// 「キー表のラベルをすべて要求すること」を見ているテスト
// (TriggerPickerWpfWindowTests.TheViewAsksForEveryLabelInTheKeyTable ほか) が、
// ピッカーには存在しないエディタのコントロールを要求し始める。
//
// 供給経路は 2 つとも共有する — resx (WPF / WinForms) と resw (WinUI) はどちらも
// 2 つの表を合わせたものを持ち、PickerStringTests がその和で過不足を見る。
namespace UiaTrigger.Picker;

/// <summary>Every resource key the trigger-list editor uses.</summary>
/// <remarks>
/// A separate table from <see cref="PickerStringKeys"/> because the two are shown by different
/// windows, while the values for both travel the same two supply routes. Keys ending in a property
/// name label a control; the rest are composed at run time by the presenter.
/// </remarks>
public static class EditorStringKeys
{
    // ---- ウィンドウ ----

    /// <summary>The editor window's title.</summary>
    /// <remarks>Not a control key, for the same reason as <see cref="PickerStringKeys.WindowTitle"/>.</remarks>
    public const string WindowTitle = "EditorWindowTitle";

    // ---- 操作 ----

    /// <summary>Caption of the button that records a new trigger.</summary>
    public const string AddButtonContent = "AddTriggerButton.Content";

    /// <summary>Caption of the button that edits the selected trigger's condition.</summary>
    public const string EditButtonContent = "EditTriggerButton.Content";

    /// <summary>Caption of the button that removes the selected triggers.</summary>
    public const string DeleteButtonContent = "DeleteTriggerButton.Content";

    /// <summary>Caption of the button that combines the selected triggers into one.</summary>
    public const string CombineButtonContent = "CombineTriggersButton.Content";

    /// <summary>Caption of the button that takes the selected composite apart again.</summary>
    public const string DecomposeButtonContent = "DecomposeTriggerButton.Content";

    /// <summary>Caption of the button that accepts the edited list.</summary>
    public const string AcceptButtonContent = "AcceptButton.Content";

    /// <summary>Caption of the button that discards every change.</summary>
    public const string CancelButtonContent = "CancelButton.Content";

    /// <summary>Label of the box holding the clause expression to combine with.</summary>
    public const string ExpressionBoxHeader = "ExpressionBox.Header";

    /// <summary>Label of the box listing the triggers that should only narrow.</summary>
    public const string UnwatchedBoxHeader = "UnwatchedBox.Header";

    /// <summary>Accessible name of the list of triggers.</summary>
    /// <remarks>
    /// The list has no visible label of its own, so this is all a screen reader has to announce.
    /// </remarks>
    public const string TriggerListAutomationName = "EditorTriggerList.AutomationProperties.Name";

    // ---- 一覧の行 ----

    /// <summary>One trigger's row. Takes the id, the display name, the process, the lifecycle and the conditions.</summary>
    public const string TriggerRow = "EditorTriggerRow";

    /// <summary>A composite trigger's row. Takes the id, the clause count, the lifecycle and the expression.</summary>
    /// <remarks>
    /// A different format because a composite spans elements: naming one process would be wrong, so
    /// the row shows how many conditions and the expression instead.
    /// </remarks>
    public const string CompositeRow = "EditorCompositeRow";

    /// <summary>Stands in for the conditions of a trigger that has none.</summary>
    public const string NoClauses = "EditorNoClauses";

    // ---- プレゼンターが組み立てる ----

    /// <summary>The selection is not one single trigger, which editing needs.</summary>
    public const string SelectOneToEdit = "EditorSelectOneToEdit";

    /// <summary>The selected trigger is one the picker cannot edit.</summary>
    public const string CannotEditWithThePicker = "EditorCannotEditWithThePicker";

    /// <summary>The selection is not one single composite, which taking apart needs.</summary>
    public const string SelectACompositeToDecompose = "EditorSelectACompositeToDecompose";

    /// <summary>Triggers were combined. Takes the new id and the number of sources.</summary>
    public const string CombineDone = "EditorCombineDone";

    /// <summary>Combining was refused. Takes the reason.</summary>
    public const string CombineFailed = "EditorCombineFailed";

    /// <summary>A composite was taken apart. Takes its id and the number of triggers recovered.</summary>
    public const string DecomposeDone = "EditorDecomposeDone";

    /// <summary>Triggers were removed. Takes how many.</summary>
    public const string DeleteDone = "EditorDeleteDone";

    /// <summary>Nothing is selected, which the operation needs.</summary>
    public const string SelectSomething = "EditorSelectSomething";

    /// <summary>Every key above, in no particular order.</summary>
    /// <remarks><inheritdoc cref="PickerStringKeys.All" path="/remarks"/></remarks>
    public static IReadOnlyList<string> All { get; } =
    [
        WindowTitle,
        AddButtonContent, EditButtonContent, DeleteButtonContent, CombineButtonContent,
        DecomposeButtonContent, AcceptButtonContent, CancelButtonContent,
        ExpressionBoxHeader, UnwatchedBoxHeader, TriggerListAutomationName,
        TriggerRow, CompositeRow, NoClauses,
        SelectOneToEdit, CannotEditWithThePicker, SelectACompositeToDecompose,
        CombineDone, CombineFailed, DecomposeDone, DeleteDone, SelectSomething,
    ];
}
