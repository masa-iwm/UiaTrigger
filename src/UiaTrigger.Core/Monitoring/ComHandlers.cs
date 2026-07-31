using System.Runtime.InteropServices.Marshalling;
using UiaTrigger.Interop;

namespace UiaTrigger.Monitoring;

// UIA イベントの COM コールバック実装。
// コールバックは UIA のスレッドプールから呼ばれるため、例外を漏らさず、
// 重い処理はしない (呼び出し側で UiaDispatcher へ Post する)。

[GeneratedComClass]
internal sealed partial class AutomationEventHandler(Action<IUIAutomationElement?, int> callback) : IUIAutomationEventHandler
{
    public void HandleAutomationEvent(IUIAutomationElement sender, int eventId)
    {
        try
        {
            callback(sender, eventId);
        }
        catch
        {
            // COM コールバックから例外を漏らさない
        }
    }
}

[GeneratedComClass]
internal sealed partial class PropertyChangedEventHandler(Action<IUIAutomationElement?, int> callback) : IUIAutomationPropertyChangedEventHandler
{
    public void HandlePropertyChangedEvent(IUIAutomationElement sender, int propertyId, ComVariant newValue)
    {
        try
        {
            // newValue はコールバック復帰後に解放されるため保持しない (値は要素から再読取する)
            callback(sender, propertyId);
        }
        catch
        {
        }
    }
}

[GeneratedComClass]
internal sealed partial class StructureChangedEventHandler(Action<IUIAutomationElement?, StructureChangeType> callback) : IUIAutomationStructureChangedEventHandler
{
    public void HandleStructureChangedEvent(IUIAutomationElement sender, StructureChangeType changeType, nint runtimeId)
    {
        try
        {
            callback(sender, changeType);
        }
        catch
        {
        }
    }
}
