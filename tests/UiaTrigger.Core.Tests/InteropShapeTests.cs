using System.Reflection;
using UiaTrigger.Interop;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 手書き interop の「形」を固定する。
///
/// [GeneratedComInterface] は宣言順に vtable スロットを割り当てるため、メソッドを 1 つ
/// 増減・並べ替えするだけで <b>別のメソッドを呼ぶ</b> ようになる。コンパイルは通り、
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
    /// IUIAutomation2 の 6 メソッドは IUIAutomation の直後に並ぶ。
    /// つまり IUIAutomation の宣言数がずれると put_TransactionTimeout が別スロットを呼ぶ。
    /// (UIAutomationClient.h の IUIAutomation は 55 メソッド = IUnknown 込み 58 スロット)
    /// </summary>
    [Fact]
    public void IUIAutomation_DeclaresExactlyTheMethodsOfTheHeader()
    {
        Assert.Equal(55, DeclaredInVtableOrder(typeof(IUIAutomation)).Count);
    }

    /// <summary>IUIAutomationElement は IUnknown 込み 85 スロット。</summary>
    [Fact]
    public void IUIAutomationElement_DeclaresExactlyTheMethodsOfTheHeader()
    {
        Assert.Equal(82, DeclaredInVtableOrder(typeof(IUIAutomationElement)).Count);
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
