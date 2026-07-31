// GeneratedComInterface のシグネチャで使う UIA の列挙型・構造体。
// ソースジェネレーターは他のジェネレーター (CsWin32) の生成型を参照できないため自前定義する。
// 値は UIAutomationClient.h / UIAutomationCore.h と一致させること。
namespace UiaTrigger.Interop;

[Flags]
internal enum TreeScope
{
    None = 0,
    Element = 1,
    Children = 2,
    Descendants = 4,
    Parent = 8,
    Ancestors = 16,
    Subtree = Element | Children | Descendants,
}

internal enum StructureChangeType
{
    ChildAdded = 0,
    ChildRemoved = 1,
    ChildrenInvalidated = 2,
    ChildrenBulkAdded = 3,
    ChildrenBulkRemoved = 4,
    ChildrenReordered = 5,
}

[Flags]
internal enum PropertyConditionFlags
{
    None = 0,
    IgnoreCase = 1,
    MatchSubstring = 2,
}

internal enum OrientationType
{
    None = 0,
    Horizontal = 1,
    Vertical = 2,
}

internal enum AutomationElementMode
{
    None = 0,
    Full = 1,
}

internal struct UiaRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;

    public override readonly string ToString() => $"[l={Left},t={Top},r={Right},b={Bottom}]";
}
