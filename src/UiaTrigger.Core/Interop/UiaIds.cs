// UI Automation の各種 ID 定数 (UIAutomationClient.h の安定値)。
// CsWin32 生成物はソースジェネレーター間参照の制約で GeneratedComInterface から使えないため自前定義。
namespace UiaTrigger.Interop;

internal static class UiaIds
{
    // Property IDs
    public const int BoundingRectangleProperty = 30001;
    public const int ProcessIdProperty = 30002;
    public const int ControlTypeProperty = 30003;
    // 表示専用。プロバイダーが自分のロケールで返す「ボタン」「button」等 (docs/DESIGN.md L6)。
    // 識別・永続化には絶対に使わないこと — 相手アプリの言語設定で値が変わる
    public const int LocalizedControlTypeProperty = 30004;
    public const int NameProperty = 30005;
    public const int AcceleratorKeyProperty = 30006;
    public const int AccessKeyProperty = 30007;
    public const int IsEnabledProperty = 30010;
    public const int AutomationIdProperty = 30011;
    public const int ClassNameProperty = 30012;
    public const int HelpTextProperty = 30013;
    public const int IsPasswordProperty = 30019;
    public const int NativeWindowHandleProperty = 30020;
    public const int IsOffscreenProperty = 30022;
    public const int ValueValueProperty = 30045;
    public const int RangeValueValueProperty = 30047;
    public const int RangeValueMaximumProperty = 30048;
    public const int RangeValueMinimumProperty = 30049;

    // Pattern IDs
    public const int ValuePattern = 10002;
    public const int RangeValuePattern = 10003;

    // Event IDs
    public const int StructureChangedEvent = 20002;
    public const int WindowOpenedEvent = 20016;
    public const int WindowClosedEvent = 20017;

    // HRESULT の判別は ComErrors を使う (docs/DESIGN.md A11)

    // Control type IDs (50000-50040)。名前の変換は UiaTrigger.UiaControlTypeNames
    public const int WindowControlType = 50032;
}
