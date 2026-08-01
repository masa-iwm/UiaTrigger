// ピッカーのリソースキー表 (docs/DESIGN.md L3 / docs/LOCALIZATION.md §4)。
//
// 「1 つのキー表・2 つの供給経路」の**キー表**のほうである。値の供給は
//   ・WinUI          … Strings/<culture>/Resources.resw + MRT Core (x:Uid で XAML から)
//   ・WPF / WinForms … Resources/Strings.resx + ResourceManager (コードで代入)
// と分かれるが、キーはここが唯一の出所になる。
//
// キーを文字列リテラルで書かないのは、綴り違いが**例外にならない**ためである
// (IPickerStrings.GetString はキー名をそのまま返す = 画面にキー名が出る)。
// 定数にしておけば綴り違いはコンパイルエラーになり、Uid 側との突き合わせも
// PickerStringTests が All を起点に回せる。
//
// resx の厳密に型指定されたコード生成 (StronglyTypedLanguage) は**使わない**。
// 生成される識別子は '.' を '_' に潰すので "AutoSelectToggle.Header" が
// AutoSelectToggle_Header になり、キー名とは別の名前空間が黙って 1 つ増えてしまう。
namespace UiaTrigger.Picker;

/// <summary>Every resource key the picker uses. The one place a key name is written down.</summary>
/// <remarks>
/// <para>
/// Keys ending in a property name (<c>AutoSelectToggle.Header</c>) label a control: WinUI resolves
/// them through <c>x:Uid</c> as the XAML loads, while the other views assign them in code. The rest
/// are composed at run time by the presenter.
/// </para>
/// <para>
/// A view that shows a label the user can read must take its text from here. Values may contain
/// composite format placeholders, so the picker formats them with the current culture.
/// </para>
/// </remarks>
public static class PickerStringKeys
{
    // ---- ウィンドウ ----

    /// <summary>The picker window's title.</summary>
    /// <remarks>Not a control key: a WinUI <c>Window</c> is no <c>FrameworkElement</c>, so
    /// <c>x:Uid</c> cannot reach it and every view sets the title in code.</remarks>
    public const string WindowTitle = "WindowTitle";

    // ---- 上部バー ----

    /// <summary>Label of the switch that turns hover capture on and off.</summary>
    public const string AutoSelectToggleHeader = "AutoSelectToggle.Header";

    /// <summary>The switch's text while hover capture is off.</summary>
    public const string AutoSelectToggleOffContent = "AutoSelectToggle.OffContent";

    /// <summary>The switch's text while hover capture is on.</summary>
    public const string AutoSelectToggleOnContent = "AutoSelectToggle.OnContent";

    /// <summary>Label of the tree view picker (Raw / Control / Content).</summary>
    public const string ViewComboHeader = "ViewCombo.Header";

    /// <summary>Label of the box to search the tree with.</summary>
    public const string SearchBoxHeader = "SearchBox.Header";

    /// <summary>Caption of the button that steps to the next match.</summary>
    public const string SearchNextButtonContent = "SearchNextButton.Content";

    /// <summary>The hint line's initial text, describing how to pick an element.</summary>
    public const string HintTextText = "HintText.Text";

    /// <summary>Tooltip of the confirm button on a tree row.</summary>
    public const string ConfirmNodeButtonToolTip = "ConfirmNodeButton.ToolTipService.ToolTip";

    /// <summary>Accessible name of the handle that splits the element tree from the properties.</summary>
    /// <remarks>The splitter shows no text, so this name is all a screen reader has to announce. Left
    /// unset, the views fall back to something meaningless — WPF says "Thumb", WinUI reads the
    /// <c>x:Name</c> back, Windows Forms says nothing at all.</remarks>
    public const string TreeSplitterAutomationName = "TreeSplitter.AutomationProperties.Name";

    /// <summary>Accessible name of the handle that splits the properties from the condition fields.</summary>
    /// <remarks>Same reasoning as <see cref="TreeSplitterAutomationName"/>: a handle with no text of its
    /// own has nothing else to announce.</remarks>
    public const string ConditionSplitterAutomationName = "ConditionSplitter.AutomationProperties.Name";

    // ---- 条件設定 ----

    /// <summary>Heading above the condition fields.</summary>
    public const string ConditionHeadingText = "ConditionHeading.Text";

    /// <summary>The line describing the confirmed element, before anything is confirmed.</summary>
    public const string ConfirmedTextText = "ConfirmedText.Text";

    /// <summary>Label of the trigger id box.</summary>
    public const string KeyBoxHeader = "KeyBox.Header";

    /// <summary>Label of the trigger display name box.</summary>
    public const string DisplayNameBoxHeader = "DisplayNameBox.Header";

    /// <summary>Label of the lifecycle event picker.</summary>
    public const string OnComboHeader = "OnCombo.Header";

    /// <summary>Label of the watched-property picker.</summary>
    public const string PropComboHeader = "PropCombo.Header";

    /// <summary>Label of the comparison picker.</summary>
    public const string CondComboHeader = "CondCombo.Header";

    /// <summary>Label of the text operand.</summary>
    public const string TextOperandHeader = "TextOperand.Header";

    /// <summary>Label of the single-value operand.</summary>
    public const string ValueOperandHeader = "ValueOperand.Header";

    /// <summary>Label of the range's lower bound.</summary>
    public const string LowOperandHeader = "LowOperand.Header";

    /// <summary>Label of the range's upper bound.</summary>
    public const string HighOperandHeader = "HighOperand.Header";

    /// <summary>Label of the tolerance operand.</summary>
    public const string ToleranceOperandHeader = "ToleranceOperand.Header";

    /// <summary>Label of the minimum firing interval, in seconds.</summary>
    public const string MinIntervalOperandHeader = "MinIntervalOperand.Header";

    /// <summary>Label of the poll interval, in seconds.</summary>
    public const string PollIntervalOperandHeader = "PollIntervalOperand.Header";

    /// <summary>Caption of the button that commits the trigger.</summary>
    public const string CommitButtonContent = "CommitButton.Content";

    // ---- プレゼンターが組み立てる ----

    /// <summary>Hover capture failed. Takes the exception message.</summary>
    public const string CaptureFailed = "CaptureFailed";

    /// <summary>The selected element could not be found in the newly chosen tree view.</summary>
    public const string ViewSwitchFailed = "ViewSwitchFailed";

    /// <summary>Nothing in the loaded part of the tree matched. Takes the search text.</summary>
    public const string SearchNoMatch = "SearchNoMatch";

    /// <summary>Which match the tree is showing. Takes the position and the total.</summary>
    public const string SearchPosition = "SearchPosition";

    /// <summary>Where the cursor is in the stack of overlapping elements.</summary>
    public const string OverlapPosition = "OverlapPosition";

    /// <summary>Stepping through the overlapping elements failed. Takes the exception message.</summary>
    public const string OverlapFailed = "OverlapFailed";

    /// <summary>The element could not be confirmed because it is no longer there.</summary>
    public const string ConfirmFailedElementGone = "ConfirmFailedElementGone";

    /// <summary>An element was confirmed. Takes its display name and its process name.</summary>
    public const string Confirmed = "Confirmed";

    /// <summary>Confirming the element failed. Takes the exception message.</summary>
    public const string ConfirmFailed = "ConfirmFailed";

    /// <summary>The trigger's shape is not filled in far enough to commit it.</summary>
    public const string SelectTriggerShape = "SelectTriggerShape";

    /// <summary>The trigger was committed. Takes its id.</summary>
    public const string TriggerAdded = "TriggerAdded";

    // ---- プロパティ一覧の行 ----

    /// <summary>One property row. Takes the UI Automation property name and its value.</summary>
    public const string PropertyRow = "PropertyRow";

    /// <summary>The <c>ControlType</c> row, which shows the display name and the stable name.</summary>
    public const string PropertyRowControlType = "PropertyRowControlType";

    /// <summary>Every key above, in no particular order.</summary>
    /// <remarks>
    /// The picker's resources are checked against this: each supply route must carry exactly these
    /// keys, no more and no fewer. A key that only some views can resolve shows up as that view
    /// putting the key name on screen, which nothing throws over.
    /// </remarks>
    public static IReadOnlyList<string> All { get; } =
    [
        WindowTitle,
        AutoSelectToggleHeader, AutoSelectToggleOffContent, AutoSelectToggleOnContent,
        ViewComboHeader, SearchBoxHeader, SearchNextButtonContent, HintTextText,
        ConfirmNodeButtonToolTip, TreeSplitterAutomationName, ConditionSplitterAutomationName,
        ConditionHeadingText, ConfirmedTextText, KeyBoxHeader, DisplayNameBoxHeader, OnComboHeader,
        PropComboHeader, CondComboHeader, TextOperandHeader, ValueOperandHeader, LowOperandHeader,
        HighOperandHeader, ToleranceOperandHeader, MinIntervalOperandHeader,
        PollIntervalOperandHeader, CommitButtonContent,
        CaptureFailed, ViewSwitchFailed, SearchNoMatch, SearchPosition, OverlapPosition,
        OverlapFailed, ConfirmFailedElementGone, Confirmed, ConfirmFailed, SelectTriggerShape,
        TriggerAdded,
        PropertyRow, PropertyRowControlType,
    ];
}
