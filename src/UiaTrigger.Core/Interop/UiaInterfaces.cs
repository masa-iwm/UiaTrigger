// UI Automation COM interfaces, hand-written with [GeneratedComInterface] for Native AOT.
// Method order MUST match the vtable order in UIAutomationClient.h exactly.
// Methods this library does not call are declared with ABI-compatible placeholder
// signatures (nint for interface/SAFEARRAY pointers) to keep vtable slots aligned.
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace UiaTrigger.Interop;

[GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
[Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
internal partial interface IUIAutomation
{
    void CompareElements(IUIAutomationElement el1, IUIAutomationElement el2, [MarshalAs(UnmanagedType.Bool)] out bool areSame);
    void CompareRuntimeIds(nint runtimeId1, nint runtimeId2, [MarshalAs(UnmanagedType.Bool)] out bool areSame);
    void GetRootElement(out IUIAutomationElement root);
    // ElementFromHandle / ElementFromPoint は S_OK のまま null を返しうる
    // (対象が UIA を提供していない・座標がどの要素にも属さない等)。out を非 null 宣言すると
    // 呼び出し側で NullReferenceException になるため nullable にしてある (docs/DESIGN.md A15)。
    void ElementFromHandle(nint hwnd, out IUIAutomationElement? element);
    void ElementFromPoint(Point pt, out IUIAutomationElement? element);
    void GetFocusedElement(out IUIAutomationElement? element);
    void GetRootElementBuildCache(IUIAutomationCacheRequest cacheRequest, out nint root);
    // out を生ポインタで受けるのは、RCW を「一意インスタンス」として自前で作るため。
    // 既定のマーシャリングが作る RCW は ComWrappers の同一性テーブルに載るので
    // FinalRelease() で決定的に解放できない (docs/DESIGN.md B6)。
    // ポインタ 0 = 要素なし (ElementFromHandle 系は S_OK のまま null を返しうる / A15)。
    void ElementFromHandleBuildCache(nint hwnd, IUIAutomationCacheRequest cacheRequest, out nint element);
    void ElementFromPointBuildCache(Point pt, IUIAutomationCacheRequest cacheRequest, out nint element);
    void GetFocusedElementBuildCache(IUIAutomationCacheRequest cacheRequest, out IUIAutomationElement element);
    void CreateTreeWalker(IUIAutomationCondition pCondition, out IUIAutomationTreeWalker walker);
    void get_ControlViewWalker(out IUIAutomationTreeWalker walker);
    void get_ContentViewWalker(out IUIAutomationTreeWalker walker);
    void get_RawViewWalker(out IUIAutomationTreeWalker walker);
    void get_RawViewCondition(out IUIAutomationCondition condition);
    void get_ControlViewCondition(out IUIAutomationCondition condition);
    void get_ContentViewCondition(out IUIAutomationCondition condition);
    void CreateCacheRequest(out IUIAutomationCacheRequest cacheRequest);
    void CreateTrueCondition(out IUIAutomationCondition newCondition);
    void CreateFalseCondition(out IUIAutomationCondition newCondition);
    void CreatePropertyCondition(int propertyId, ComVariant value, out IUIAutomationCondition newCondition);
    void CreatePropertyConditionEx(int propertyId, ComVariant value, PropertyConditionFlags flags, out IUIAutomationCondition newCondition);
    void CreateAndCondition(IUIAutomationCondition condition1, IUIAutomationCondition condition2, out IUIAutomationCondition newCondition);
    void CreateAndConditionFromArray(nint conditions, out IUIAutomationCondition newCondition);
    void CreateAndConditionFromNativeArray(nint conditions, int conditionCount, out IUIAutomationCondition newCondition);
    void CreateOrCondition(IUIAutomationCondition condition1, IUIAutomationCondition condition2, out IUIAutomationCondition newCondition);
    void CreateOrConditionFromArray(nint conditions, out IUIAutomationCondition newCondition);
    void CreateOrConditionFromNativeArray(nint conditions, int conditionCount, out IUIAutomationCondition newCondition);
    void CreateNotCondition(IUIAutomationCondition condition, out IUIAutomationCondition newCondition);
    void AddAutomationEventHandler(int eventId, IUIAutomationElement element, TreeScope scope, IUIAutomationCacheRequest? cacheRequest, IUIAutomationEventHandler handler);
    void RemoveAutomationEventHandler(int eventId, IUIAutomationElement element, IUIAutomationEventHandler handler);
    void AddPropertyChangedEventHandlerNativeArray(IUIAutomationElement element, TreeScope scope, IUIAutomationCacheRequest? cacheRequest, IUIAutomationPropertyChangedEventHandler handler, [MarshalUsing(CountElementName = nameof(propertyCount))] int[] propertyArray, int propertyCount);
    void AddPropertyChangedEventHandler(IUIAutomationElement element, TreeScope scope, IUIAutomationCacheRequest? cacheRequest, IUIAutomationPropertyChangedEventHandler handler, nint propertyArray);
    void RemovePropertyChangedEventHandler(IUIAutomationElement element, IUIAutomationPropertyChangedEventHandler handler);
    void AddStructureChangedEventHandler(IUIAutomationElement element, TreeScope scope, IUIAutomationCacheRequest? cacheRequest, IUIAutomationStructureChangedEventHandler handler);
    void RemoveStructureChangedEventHandler(IUIAutomationElement element, IUIAutomationStructureChangedEventHandler handler);
    void AddFocusChangedEventHandler(IUIAutomationCacheRequest? cacheRequest, nint handler);
    void RemoveFocusChangedEventHandler(nint handler);
    void RemoveAllEventHandlers();
    void IntNativeArrayToSafeArray(nint array, int arrayCount, out nint safeArray);
    void IntSafeArrayToNativeArray(nint intArray, out nint array, out int arrayCount);
    void RectToVariant(UiaRect rc, out ComVariant var);
    void VariantToRect(ComVariant var, out UiaRect rc);
    void SafeArrayToRectNativeArray(nint rects, out nint rectArray, out int rectArrayCount);
    void CreateProxyFactoryEntry(nint factory, out nint factoryEntry);
    void get_ProxyFactoryMapping(out nint factoryMapping);
    void GetPropertyProgrammaticName(int property, out string name);
    void GetPatternProgrammaticName(int pattern, out string name);
    void PollForPotentialSupportedPatterns(IUIAutomationElement pElement, out nint patternIds, out nint patternNames);
    void PollForPotentialSupportedProperties(IUIAutomationElement pElement, out nint propertyIds, out nint propertyNames);
    void CheckNotSupported(ComVariant value, [MarshalAs(UnmanagedType.Bool)] out bool isNotSupported);
    void get_ReservedNotSupportedValue(out nint notSupportedValue);
    void get_ReservedMixedAttributeValue(out nint mixedAttributeValue);
    void ElementFromIAccessible(nint accessible, int childId, out IUIAutomationElement element);
    void ElementFromIAccessibleBuildCache(nint accessible, int childId, IUIAutomationCacheRequest cacheRequest, out IUIAutomationElement element);
}

/// <summary>
/// IUIAutomation2 (CUIAutomation8 が実装する)。IUIAutomation の 55 メソッド
/// (IUnknown 込み 58 slot) の直後に 6 slot 続く。
/// <b>上の IUIAutomation の宣言数がずれると、ここのメソッドが別のスロットを呼ぶ</b>
/// (InteropShapeTests で数と順序を固定してある)。
///
/// これを QI できないと put_TransactionTimeout に到達できず、応答しないアプリが 1 つあるだけで
/// 単一 MTA スレッドが無期限に塞がれる (head-of-line blocking / docs/DESIGN.md B5)。
/// </summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
[Guid("34723AFF-0C9D-49D0-9896-7AB52DF8CD8A")]
internal partial interface IUIAutomation2 : IUIAutomation
{
    void get_AutoSetFocus([MarshalAs(UnmanagedType.Bool)] out bool autoSetFocus);
    void put_AutoSetFocus([MarshalAs(UnmanagedType.Bool)] bool autoSetFocus);
    void get_ConnectionTimeout(out uint timeout);
    void put_ConnectionTimeout(uint timeout);
    void get_TransactionTimeout(out uint timeout);
    void put_TransactionTimeout(uint timeout);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
[Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
internal partial interface IUIAutomationElement
{
    void SetFocus();
    void GetRuntimeId(out nint runtimeId);
    void FindFirst(TreeScope scope, IUIAutomationCondition condition, out IUIAutomationElement? found);
    void FindAll(TreeScope scope, IUIAutomationCondition condition, out IUIAutomationElementArray? found);
    void FindFirstBuildCache(TreeScope scope, IUIAutomationCondition condition, IUIAutomationCacheRequest cacheRequest, out IUIAutomationElement? found);
    // FindAllBuildCache / BuildUpdatedCache も一意インスタンス化のため生ポインタで受ける (上記 B6 の注記参照)
    void FindAllBuildCache(TreeScope scope, IUIAutomationCondition condition, IUIAutomationCacheRequest cacheRequest, out nint found);
    void BuildUpdatedCache(IUIAutomationCacheRequest cacheRequest, out nint updatedElement);
    void GetCurrentPropertyValue(int propertyId, out ComVariant retVal);
    void GetCurrentPropertyValueEx(int propertyId, [MarshalAs(UnmanagedType.Bool)] bool ignoreDefaultValue, out ComVariant retVal);
    void GetCachedPropertyValue(int propertyId, out ComVariant retVal);
    void GetCachedPropertyValueEx(int propertyId, [MarshalAs(UnmanagedType.Bool)] bool ignoreDefaultValue, out ComVariant retVal);
    void GetCurrentPatternAs(int patternId, in Guid riid, out nint patternObject);
    void GetCachedPatternAs(int patternId, in Guid riid, out nint patternObject);
    void GetCurrentPattern(int patternId, out nint patternObject);
    void GetCachedPattern(int patternId, out nint patternObject);
    void GetCachedParent(out IUIAutomationElement? parent);
    void GetCachedChildren(out IUIAutomationElementArray? children);
    void get_CurrentProcessId(out int retVal);
    void get_CurrentControlType(out int retVal);
    void get_CurrentLocalizedControlType(out string retVal);
    void get_CurrentName(out string retVal);
    void get_CurrentAcceleratorKey(out string retVal);
    void get_CurrentAccessKey(out string retVal);
    void get_CurrentHasKeyboardFocus([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CurrentIsKeyboardFocusable([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CurrentIsEnabled([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CurrentAutomationId(out string retVal);
    void get_CurrentClassName(out string retVal);
    void get_CurrentHelpText(out string retVal);
    void get_CurrentCulture(out int retVal);
    void get_CurrentIsControlElement([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CurrentIsContentElement([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CurrentIsPassword([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CurrentNativeWindowHandle(out nint retVal);
    void get_CurrentItemType(out string retVal);
    void get_CurrentIsOffscreen([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CurrentOrientation(out OrientationType retVal);
    void get_CurrentFrameworkId(out string retVal);
    void get_CurrentIsRequiredForForm([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CurrentItemStatus(out string retVal);
    void get_CurrentBoundingRectangle(out UiaRect retVal);
    void get_CurrentLabeledBy(out IUIAutomationElement? retVal);
    void get_CurrentAriaRole(out string retVal);
    void get_CurrentAriaProperties(out string retVal);
    void get_CurrentIsDataValidForForm([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CurrentControllerFor(out IUIAutomationElementArray? retVal);
    void get_CurrentDescribedBy(out IUIAutomationElementArray? retVal);
    void get_CurrentFlowsTo(out IUIAutomationElementArray? retVal);
    void get_CurrentProviderDescription(out string retVal);
    void get_CachedProcessId(out int retVal);
    void get_CachedControlType(out int retVal);
    void get_CachedLocalizedControlType(out string retVal);
    void get_CachedName(out string retVal);
    void get_CachedAcceleratorKey(out string retVal);
    void get_CachedAccessKey(out string retVal);
    void get_CachedHasKeyboardFocus([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CachedIsKeyboardFocusable([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CachedIsEnabled([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CachedAutomationId(out string retVal);
    void get_CachedClassName(out string retVal);
    void get_CachedHelpText(out string retVal);
    void get_CachedCulture(out int retVal);
    void get_CachedIsControlElement([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CachedIsContentElement([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CachedIsPassword([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CachedNativeWindowHandle(out nint retVal);
    void get_CachedItemType(out string retVal);
    void get_CachedIsOffscreen([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CachedOrientation(out OrientationType retVal);
    void get_CachedFrameworkId(out string retVal);
    void get_CachedIsRequiredForForm([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CachedItemStatus(out string retVal);
    void get_CachedBoundingRectangle(out UiaRect retVal);
    void get_CachedLabeledBy(out IUIAutomationElement? retVal);
    void get_CachedAriaRole(out string retVal);
    void get_CachedAriaProperties(out string retVal);
    void get_CachedIsDataValidForForm([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CachedControllerFor(out IUIAutomationElementArray? retVal);
    void get_CachedDescribedBy(out IUIAutomationElementArray? retVal);
    void get_CachedFlowsTo(out IUIAutomationElementArray? retVal);
    void get_CachedProviderDescription(out string retVal);
    void GetClickablePoint(out Point clickable, [MarshalAs(UnmanagedType.Bool)] out bool gotClickable);
}

[GeneratedComInterface]
[Guid("14314595-B4BC-4055-95F2-58F2E42C9855")]
internal partial interface IUIAutomationElementArray
{
    void get_Length(out int length);
    // 解決ループの最ホットパス。既定のマーシャリングだと 1 段あたり数百の RCW が
    // 同一性テーブルに載って GC まで残るため、生ポインタで受けて一意インスタンス化する (docs/DESIGN.md B6)
    void GetElement(int index, out nint element);
}

[GeneratedComInterface]
[Guid("352FFBA8-0973-437C-A61F-F64CAFD81DF9")]
internal partial interface IUIAutomationCondition
{
}

[GeneratedComInterface]
[Guid("4042C624-389C-4AFC-A630-9DF854A541FC")]
internal partial interface IUIAutomationTreeWalker
{
    void GetParentElement(IUIAutomationElement element, out IUIAutomationElement? parent);
    void GetFirstChildElement(IUIAutomationElement element, out IUIAutomationElement? first);
    void GetLastChildElement(IUIAutomationElement element, out IUIAutomationElement? last);
    void GetNextSiblingElement(IUIAutomationElement element, out IUIAutomationElement? next);
    void GetPreviousSiblingElement(IUIAutomationElement element, out IUIAutomationElement? previous);
    void NormalizeElement(IUIAutomationElement element, out IUIAutomationElement? normalized);
    // 親方向のナビゲーションと正規化も生ポインタで受ける。ElementLocator の Search 方式
    // (FindFirst 1 発) は要素しか返さないため、経路購読 (docs/DESIGN.md B3) 用の祖先鎖を
    // ここで遡って作る — 段の数だけ要素を作るので、決定的に解放できる必要がある。
    void GetParentElementBuildCache(IUIAutomationElement element, IUIAutomationCacheRequest cacheRequest, out nint parent);
    void GetFirstChildElementBuildCache(IUIAutomationElement element, IUIAutomationCacheRequest cacheRequest, out IUIAutomationElement? first);
    void GetLastChildElementBuildCache(IUIAutomationElement element, IUIAutomationCacheRequest cacheRequest, out IUIAutomationElement? last);
    void GetNextSiblingElementBuildCache(IUIAutomationElement element, IUIAutomationCacheRequest cacheRequest, out IUIAutomationElement? next);
    void GetPreviousSiblingElementBuildCache(IUIAutomationElement element, IUIAutomationCacheRequest cacheRequest, out IUIAutomationElement? previous);
    void NormalizeElementBuildCache(IUIAutomationElement element, IUIAutomationCacheRequest cacheRequest, out nint normalized);
    void get_Condition(out IUIAutomationCondition condition);
}

[GeneratedComInterface]
[Guid("B32A92B5-BC25-4078-9C08-D7EE95C48E03")]
internal partial interface IUIAutomationCacheRequest
{
    void AddProperty(int propertyId);
    void AddPattern(int patternId);
    void Clone(out IUIAutomationCacheRequest clonedRequest);
    void get_TreeScope(out TreeScope scope);
    void put_TreeScope(TreeScope scope);
    void get_TreeFilter(out IUIAutomationCondition filter);
    void put_TreeFilter(IUIAutomationCondition filter);
    void get_AutomationElementMode(out AutomationElementMode mode);
    void put_AutomationElementMode(AutomationElementMode mode);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
[Guid("A94CD8B1-0844-4CD6-9D2D-640537AB39E9")]
internal partial interface IUIAutomationValuePattern
{
    void SetValue(string val);
    void get_CurrentValue(out string retVal);
    void get_CurrentIsReadOnly([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CachedValue(out string retVal);
    void get_CachedIsReadOnly([MarshalAs(UnmanagedType.Bool)] out bool retVal);
}

[GeneratedComInterface]
[Guid("59213F4F-7346-49E5-B120-80555987A148")]
internal partial interface IUIAutomationRangeValuePattern
{
    void SetValue(double val);
    void get_CurrentValue(out double retVal);
    void get_CurrentIsReadOnly([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CurrentMaximum(out double retVal);
    void get_CurrentMinimum(out double retVal);
    void get_CurrentLargeChange(out double retVal);
    void get_CurrentSmallChange(out double retVal);
    void get_CachedValue(out double retVal);
    void get_CachedIsReadOnly([MarshalAs(UnmanagedType.Bool)] out bool retVal);
    void get_CachedMaximum(out double retVal);
    void get_CachedMinimum(out double retVal);
    void get_CachedLargeChange(out double retVal);
    void get_CachedSmallChange(out double retVal);
}

[GeneratedComInterface]
[Guid("146C3C17-F12E-4E22-8C27-F894B9B79C69")]
internal partial interface IUIAutomationEventHandler
{
    void HandleAutomationEvent(IUIAutomationElement sender, int eventId);
}

[GeneratedComInterface]
[Guid("40CD37D4-C756-4B0C-8C6F-BDDFEEB13B50")]
internal partial interface IUIAutomationPropertyChangedEventHandler
{
    void HandlePropertyChangedEvent(IUIAutomationElement sender, int propertyId, ComVariant newValue);
}

[GeneratedComInterface]
[Guid("E81D1B4E-11C5-42F8-9754-E7036C79F054")]
internal partial interface IUIAutomationStructureChangedEventHandler
{
    void HandleStructureChangedEvent(IUIAutomationElement sender, StructureChangeType changeType, nint runtimeId);
}
