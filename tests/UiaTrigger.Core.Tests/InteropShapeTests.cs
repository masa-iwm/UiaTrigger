using System.Reflection;
using UiaTrigger.Interop;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 手書き interop の「形」を固定する。
///
/// [GeneratedComInterface] は宣言順に vtable スロットを割り当てるため、メソッドを 1 つ
/// 増減・並べ替えするだけで **別のメソッドを呼ぶ** ようになる。コンパイルは通り、
/// AOT 発行も通り、実行時に静かに壊れる類の事故なのでここで固定する。
/// </summary>
public sealed class InteropShapeTests
{
    // メタデータトークン順 = ソース上の宣言順。
    //
    // 注意: ComInterfaceGenerator は基底インターフェースのメソッドを派生側にも
    // 再宣言する (DeclaringType も派生になるので DeclaredOnly でも DeclaringType でも除けない)。
    // 並びは「派生自身のメソッド → 基底のメソッド」なので、派生の分は先頭から数える。
    private static IReadOnlyList<MethodInfo> DeclaredInVtableOrder(Type type) =>
        [.. type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(m => m.MetadataToken)];

    /// <summary>
    /// <c>IUIAutomation</c> のヘッダー順 (UIAutomationClient.h)。
    /// </summary>
    /// <remarks>
    /// **数だけでは足りない。**数を変えない並べ替え (リファクタリングでは普通に起きる) は
    /// コンパイルも AOT 発行も通り、実行時に別スロットを呼ぶ — このファイルの doc 自身が
    /// 「静かに壊れる類の事故」と呼んでいる形そのものである。順序ごと固定する。
    /// </remarks>
    private static readonly string[] IUIAutomationOrder =
    [
        "CompareElements", "CompareRuntimeIds", "GetRootElement", "ElementFromHandle",
        "ElementFromPoint", "GetFocusedElement", "GetRootElementBuildCache",
        "ElementFromHandleBuildCache", "ElementFromPointBuildCache", "GetFocusedElementBuildCache",
        "CreateTreeWalker", "get_ControlViewWalker", "get_ContentViewWalker", "get_RawViewWalker",
        "get_RawViewCondition", "get_ControlViewCondition", "get_ContentViewCondition",
        "CreateCacheRequest", "CreateTrueCondition", "CreateFalseCondition",
        "CreatePropertyCondition", "CreatePropertyConditionEx", "CreateAndCondition",
        "CreateAndConditionFromArray", "CreateAndConditionFromNativeArray", "CreateOrCondition",
        "CreateOrConditionFromArray", "CreateOrConditionFromNativeArray", "CreateNotCondition",
        "AddAutomationEventHandler", "RemoveAutomationEventHandler",
        "AddPropertyChangedEventHandlerNativeArray", "AddPropertyChangedEventHandler",
        "RemovePropertyChangedEventHandler", "AddStructureChangedEventHandler",
        "RemoveStructureChangedEventHandler", "AddFocusChangedEventHandler",
        "RemoveFocusChangedEventHandler", "RemoveAllEventHandlers", "IntNativeArrayToSafeArray",
        "IntSafeArrayToNativeArray", "RectToVariant", "VariantToRect", "SafeArrayToRectNativeArray",
        "CreateProxyFactoryEntry", "get_ProxyFactoryMapping", "GetPropertyProgrammaticName",
        "GetPatternProgrammaticName", "PollForPotentialSupportedPatterns",
        "PollForPotentialSupportedProperties", "CheckNotSupported",
        "get_ReservedNotSupportedValue", "get_ReservedMixedAttributeValue",
        "ElementFromIAccessible", "ElementFromIAccessibleBuildCache",
    ];

    /// <summary><c>IUIAutomationElement</c> のヘッダー順。</summary>
    /// <inheritdoc cref="IUIAutomationOrder" path="/remarks"/>
    private static readonly string[] IUIAutomationElementOrder =
    [
        "SetFocus", "GetRuntimeId", "FindFirst", "FindAll", "FindFirstBuildCache",
        "FindAllBuildCache", "BuildUpdatedCache", "GetCurrentPropertyValue",
        "GetCurrentPropertyValueEx", "GetCachedPropertyValue", "GetCachedPropertyValueEx",
        "GetCurrentPatternAs", "GetCachedPatternAs", "GetCurrentPattern", "GetCachedPattern",
        "GetCachedParent", "GetCachedChildren", "get_CurrentProcessId", "get_CurrentControlType",
        "get_CurrentLocalizedControlType", "get_CurrentName", "get_CurrentAcceleratorKey",
        "get_CurrentAccessKey", "get_CurrentHasKeyboardFocus", "get_CurrentIsKeyboardFocusable",
        "get_CurrentIsEnabled", "get_CurrentAutomationId", "get_CurrentClassName",
        "get_CurrentHelpText", "get_CurrentCulture", "get_CurrentIsControlElement",
        "get_CurrentIsContentElement", "get_CurrentIsPassword", "get_CurrentNativeWindowHandle",
        "get_CurrentItemType", "get_CurrentIsOffscreen", "get_CurrentOrientation",
        "get_CurrentFrameworkId", "get_CurrentIsRequiredForForm", "get_CurrentItemStatus",
        "get_CurrentBoundingRectangle", "get_CurrentLabeledBy", "get_CurrentAriaRole",
        "get_CurrentAriaProperties", "get_CurrentIsDataValidForForm", "get_CurrentControllerFor",
        "get_CurrentDescribedBy", "get_CurrentFlowsTo", "get_CurrentProviderDescription",
        "get_CachedProcessId", "get_CachedControlType", "get_CachedLocalizedControlType",
        "get_CachedName", "get_CachedAcceleratorKey", "get_CachedAccessKey",
        "get_CachedHasKeyboardFocus", "get_CachedIsKeyboardFocusable", "get_CachedIsEnabled",
        "get_CachedAutomationId", "get_CachedClassName", "get_CachedHelpText", "get_CachedCulture",
        "get_CachedIsControlElement", "get_CachedIsContentElement", "get_CachedIsPassword",
        "get_CachedNativeWindowHandle", "get_CachedItemType", "get_CachedIsOffscreen",
        "get_CachedOrientation", "get_CachedFrameworkId", "get_CachedIsRequiredForForm",
        "get_CachedItemStatus", "get_CachedBoundingRectangle", "get_CachedLabeledBy",
        "get_CachedAriaRole", "get_CachedAriaProperties", "get_CachedIsDataValidForForm",
        "get_CachedControllerFor", "get_CachedDescribedBy", "get_CachedFlowsTo",
        "get_CachedProviderDescription", "GetClickablePoint",
    ];

    /// <summary>
    /// IUIAutomation2 の 6 メソッドは IUIAutomation の直後に並ぶ。
    /// つまり IUIAutomation の宣言数がずれると put_TransactionTimeout が別スロットを呼ぶ。
    /// (UIAutomationClient.h の IUIAutomation は 55 メソッド = IUnknown 込み 58 スロット)
    /// </summary>
    [Fact]
    public void IUIAutomation_DeclaresTheMethodsOfTheHeaderInOrder()
    {
        Assert.Equal(
            IUIAutomationOrder,
            DeclaredInVtableOrder(typeof(IUIAutomation)).Select(m => m.Name));
    }

    /// <summary>IUIAutomationElement は IUnknown 込み 85 スロット。並びごと固定する。</summary>
    [Fact]
    public void IUIAutomationElement_DeclaresTheMethodsOfTheHeaderInOrder()
    {
        Assert.Equal(
            IUIAutomationElementOrder,
            DeclaredInVtableOrder(typeof(IUIAutomationElement)).Select(m => m.Name));
    }

    /// <summary>
    /// IUIAutomation2 は IUIAutomation を継承し、独自メソッドはヘッダーの順であること
    /// (docs/DESIGN.md B5)。実機で put/get の往復が一致することは確認済み。
    /// </summary>
    [Fact]
    public void IUIAutomation2_ExtendsIUIAutomationInHeaderOrder()
    {
        Assert.Contains(typeof(IUIAutomation), typeof(IUIAutomation2).GetInterfaces());

        IReadOnlyList<MethodInfo> declared = DeclaredInVtableOrder(typeof(IUIAutomation2));

        // 自身の 6 個 + 再宣言された基底の 55 個
        Assert.Equal(6 + 55, declared.Count);
        Assert.Equal(
            [
                "get_AutoSetFocus",
                "put_AutoSetFocus",
                "get_ConnectionTimeout",
                "put_ConnectionTimeout",
                "get_TransactionTimeout",
                "put_TransactionTimeout",
            ],
            declared.Take(6).Select(m => m.Name));
    }

    /// <summary>
    /// 解決ループが大量に作る要素は、生ポインタで受けて「一意 RCW」にする必要がある
    /// (docs/DESIGN.md B6)。既定のマーシャリングに戻すと ComWrappers の同一性テーブルに載り、
    /// FinalRelease で決定的に解放できなくなる — その差し戻しをここで捕まえる。
    /// </summary>
    [Theory]
    [InlineData(typeof(IUIAutomationElementArray), nameof(IUIAutomationElementArray.GetElement))]
    [InlineData(typeof(IUIAutomationElement), nameof(IUIAutomationElement.FindAllBuildCache))]
    [InlineData(typeof(IUIAutomationElement), nameof(IUIAutomationElement.BuildUpdatedCache))]
    [InlineData(typeof(IUIAutomation), nameof(IUIAutomation.ElementFromHandleBuildCache))]
    public void TransientElementLookups_ReturnRawPointers(Type type, string methodName)
    {
        MethodInfo method = type.GetMethod(methodName)!;
        ParameterInfo output = method.GetParameters().Single(p => p.IsOut);

        Assert.Equal(typeof(nint).MakeByRefType(), output.ParameterType);
    }

    /// <summary>
    /// 対照: 保持する側 (ルート要素) は通常の RCW で受ける。
    /// 上のアサートが「何でも nint」になっていないことの確認。
    /// </summary>
    [Fact]
    public void RetainedElementLookups_StillUseMarshalledInterfaces()
    {
        MethodInfo method = typeof(IUIAutomation).GetMethod(nameof(IUIAutomation.GetRootElement))!;
        ParameterInfo output = method.GetParameters().Single(p => p.IsOut);

        Assert.Equal(typeof(IUIAutomationElement).MakeByRefType(), output.ParameterType);
    }
}
