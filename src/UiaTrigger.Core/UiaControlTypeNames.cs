// ControlType ID → 安定名。
//
// 名前空間は UiaTrigger.Interop ではなく UiaTrigger である (docs/DESIGN.md C7)。
// この型は「モデルの語彙」であって「COM の実装詳細」ではなく、COM 型はすべて
// internal に閉じてあるので、interop 名前空間に置く理由が無い。
//
// 返すのは **英語固定の安定名**である (docs/DESIGN.md L6)。
// 表示用のローカライズ名は UiaElement.LocalizedControlType /
// ElementPropertySnapshot.LocalizedControlType として分離してある。値の出所も別で、
// 表示用は UIA の LocalizedControlType (= 相手アプリのロケール) をそのまま使う。
// ここを「現在の UI カルチャでローカライズする」形にしてはならない —
// 戻り値は DisplayName と条件評価の比較形に入っており、永続化される。
using System.Globalization;

namespace UiaTrigger;

/// <summary>Maps a UI Automation control type id to its stable English name.</summary>
/// <remarks>
/// The names are the ones from the UI Automation specification (<c>Button</c>, <c>Pane</c>, …) and
/// never change with the current culture, because they are also persisted — a trigger's
/// <see cref="Models.TriggerDefinition.DisplayName"/> and the comparison form of
/// <see cref="Models.TriggerProperty.ControlType"/> are built from them. To show a control type to
/// a user, use <see cref="UiaElement.LocalizedControlType"/> or
/// <see cref="Models.ElementPropertySnapshot.LocalizedControlType"/> instead.
/// </remarks>
public static class UiaControlTypeNames
{
    /// <summary>The stable name of a control type id, or a <c>ControlType(n)</c> form when unknown.</summary>
    public static string GetName(int controlTypeId) => controlTypeId switch
    {
        50000 => "Button",
        50001 => "Calendar",
        50002 => "CheckBox",
        50003 => "ComboBox",
        50004 => "Edit",
        50005 => "Hyperlink",
        50006 => "Image",
        50007 => "ListItem",
        50008 => "List",
        50009 => "Menu",
        50010 => "MenuBar",
        50011 => "MenuItem",
        50012 => "ProgressBar",
        50013 => "RadioButton",
        50014 => "ScrollBar",
        50015 => "Slider",
        50016 => "Spinner",
        50017 => "StatusBar",
        50018 => "Tab",
        50019 => "TabItem",
        50020 => "Text",
        50021 => "ToolBar",
        50022 => "ToolTip",
        50023 => "Tree",
        50024 => "TreeItem",
        50025 => "Custom",
        50026 => "Group",
        50027 => "Thumb",
        50028 => "DataGrid",
        50029 => "DataItem",
        50030 => "Document",
        50031 => "SplitButton",
        50032 => "Window",
        50033 => "Pane",
        50034 => "Header",
        50035 => "HeaderItem",
        50036 => "Table",
        50037 => "TitleBar",
        50038 => "Separator",
        50039 => "SemanticZoom",
        50040 => "AppBar",
        0 => "(unknown)",
        // 未知 ID の形も**識別子**であり永続化される (docs/DESIGN.md L6)。素の文字列補間は
        // CurrentCulture で数値を書くので、桁区切りを使うカルチャでは同じ ID が別の綴りになり、
        // 保存済み定義との比較が静かに外れる
        _ => string.Create(CultureInfo.InvariantCulture, $"ControlType({controlTypeId})"),
    };
}
