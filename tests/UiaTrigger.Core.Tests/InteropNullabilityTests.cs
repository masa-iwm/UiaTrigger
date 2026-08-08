using System.Reflection;
using UiaTrigger.Interop;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// interop の null 許容宣言を固定する (docs/DESIGN.md A15)。
///
/// UIA の ElementFromPoint / ElementFromHandle は S_OK のまま null を返しうる
/// (座標がどの要素にも属さない、対象ウィンドウが UIA を提供していない等)。
/// out を非 null で宣言していたため、呼び出し側が null チェックを書かず
/// NullReferenceException になる経路が残っていた。
/// 「非 null に戻す」変更をここで捕まえる。
/// </summary>
public sealed class InteropNullabilityTests
{
    private static readonly NullabilityInfoContext Context = new();

    private static NullabilityInfo OutParameterOf(string methodName)
    {
        MethodInfo method = typeof(IUIAutomation).GetMethod(methodName)
            ?? throw new InvalidOperationException($"IUIAutomation.{methodName} が見つかりません。");
        ParameterInfo parameter = method.GetParameters().Single(p => p.IsOut);
        return Context.Create(parameter);
    }

    [Theory]
    [InlineData(nameof(IUIAutomation.ElementFromPoint))]
    [InlineData(nameof(IUIAutomation.ElementFromHandle))]
    [InlineData(nameof(IUIAutomation.GetFocusedElement))]
    // 以下 3 つは未使用の宣言。使い始めた日に null 検査なしの経路が復活しないよう、
    // 使っているものと同じ null 許容を先に縛っておく (A15)
    [InlineData(nameof(IUIAutomation.GetFocusedElementBuildCache))]
    [InlineData(nameof(IUIAutomation.ElementFromIAccessible))]
    [InlineData(nameof(IUIAutomation.ElementFromIAccessibleBuildCache))]
    public void ElementLookups_DeclareTheirOutParameterAsNullable(string methodName)
    {
        NullabilityInfo info = OutParameterOf(methodName);

        Assert.Equal(NullabilityState.Nullable, info.ReadState);
    }

    /// <summary>
    /// 反対側の対照: ルート要素は必ず返るため非 null のまま。
    /// これが Nullable と判定されるようなら、上のアサート自体が無意味になっている。
    /// </summary>
    [Fact]
    public void GetRootElement_KeepsItsOutParameterNonNullable()
    {
        NullabilityInfo info = OutParameterOf(nameof(IUIAutomation.GetRootElement));

        Assert.Equal(NullabilityState.NotNull, info.ReadState);
    }
}
