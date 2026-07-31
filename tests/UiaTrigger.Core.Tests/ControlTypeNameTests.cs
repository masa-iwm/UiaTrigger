using System.Globalization;
using UiaTrigger.Inspection;
using UiaTrigger.Models;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 識別用の安定名と表示用のローカライズ名の分離 (docs/DESIGN.md L6)。
///
/// コントロール型の名前が 1 つしか無いと、それが
/// <see cref="TriggerDefinition.DisplayName"/> (永続化される) と画面表示の両方に使われる。
/// その状態で表示をローカライズした瞬間に、保存済みの定義の意味が変わり、
/// <see cref="TriggerProperty.ControlType"/> の条件が黙って一致しなくなる — L4 と同じ壊れ方である。
///
/// ここで固定するのは**両方向**:
///   ・比較・永続化は安定名を使うこと
///   ・表示はローカライズ名を使うこと (= 分離が実際に効いていること)
/// 片方だけだと「両方とも安定名なので、たまたま壊れていない」状態に戻れてしまう。
/// </summary>
public sealed class ControlTypeNameTests
{
    private const int ButtonControlType = 50000;

    /// <summary>プロバイダーが返した表示名を模したもの。安定名とは意図的に別の綴りにしてある。</summary>
    private const string ProviderLocalizedName = "ボタン";

    private static ElementPropertySnapshot Snapshot(string localized = ProviderLocalizedName) => new()
    {
        ControlType = ButtonControlType,
        ControlTypeName = UiaControlTypeNames.GetName(ButtonControlType),
        LocalizedControlType = localized,
    };

    /// <summary>安定名は UIA 仕様の英語名であること。</summary>
    [Theory]
    [InlineData(50000, "Button")]
    [InlineData(50004, "Edit")]
    [InlineData(50032, "Window")]
    [InlineData(50033, "Pane")]
    public void GetName_ReturnsTheSpecificationName(int controlType, string expected)
        => Assert.Equal(expected, UiaControlTypeNames.GetName(controlType));

    /// <summary>
    /// 安定名がカルチャで変わらないこと。
    ///
    /// <see cref="UiaControlTypeNames.GetName"/> をリソース経由にする変更は
    /// 「表示を日本語にしたい」という自然な動機から出てくるが、戻り値は永続化されるので
    /// やってはいけない。ここが落ちればその変更だと分かる。
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    [InlineData("de-DE")]
    public void GetName_DoesNotFollowTheCurrentCulture(string cultureName)
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(cultureName);
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            Assert.Equal("Button", UiaControlTypeNames.GetName(ButtonControlType));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>比較形は安定名。ローカライズ名が何であっても影響を受けない。</summary>
    [Fact]
    public void ComparisonValue_ForControlType_IsTheStableName()
    {
        Assert.Equal("Button", Snapshot().GetComparisonValue(TriggerProperty.ControlType).Value);
        Assert.Equal("Button", Snapshot(localized: "botón").GetComparisonValue(TriggerProperty.ControlType).Value);
    }

    /// <summary>
    /// 表示形はローカライズ名。
    ///
    /// これが安定名を返すようになると、L6 は「名前を 2 つ持っているが誰も使っていない」
    /// 状態に戻る — 見た目は何も壊れないので、テストでしか気づけない。
    /// </summary>
    [Fact]
    public void DisplayValue_ForControlType_IsTheLocalizedName()
    {
        Assert.Equal(ProviderLocalizedName, Snapshot().GetDisplayValue(TriggerProperty.ControlType));
        Assert.NotEqual(
            Snapshot().GetDisplayValue(TriggerProperty.ControlType),
            Snapshot().GetComparisonValue(TriggerProperty.ControlType).Value);
    }

    /// <summary>
    /// 記録される既定の <see cref="TriggerDefinition.DisplayName"/> は安定名から作られること。
    ///
    /// DisplayName は永続化されるので、表示用のローカライズ名を混ぜると、
    /// 対象アプリを別の言語で起動し直しただけで保存済みの定義が変わる (docs/DESIGN.md L6)。
    /// 実 UIA での成立は T3 の Record_PutsTheStableNameInTheDisplayName が見ているが、
    /// あちらは CI で continue-on-error である — ブロッキングの網はこちら
    /// (規則は T1・実 UIA での成立は T3、の 2 段構え — docs/LOCALIZATION.md §6)。
    /// </summary>
    [Fact]
    public void TheRecordedDisplayName_IsBuiltFromTheStableName()
    {
        var named = new ElementPropertySnapshot
        {
            ControlType = ButtonControlType,
            ControlTypeName = UiaControlTypeNames.GetName(ButtonControlType),
            LocalizedControlType = ProviderLocalizedName,
            Name = "OK",
        };

        Assert.Equal("Button: OK", DefinitionBuilder.BuildDisplayName(named));
        Assert.DoesNotContain(
            ProviderLocalizedName, DefinitionBuilder.BuildDisplayName(named), StringComparison.Ordinal);

        // Name の無い要素は型名だけになる
        Assert.Equal("Button", DefinitionBuilder.BuildDisplayName(Snapshot()));
    }

    /// <summary>
    /// プロバイダーが表示名を返さないときは安定名で埋めること。
    /// 空欄を画面に出さないための最低限の保険であり、逆向き (ローカライズ名で比較する) は禁止。
    /// </summary>
    [Fact]
    public void DisplayValue_WhenTheProviderSuppliesNothing_FallsBackToTheStableName()
    {
        var snapshot = new ElementPropertySnapshot
        {
            ControlType = ButtonControlType,
            ControlTypeName = "Button",
            LocalizedControlType = "Button", // PropertyReader が空文字を安定名で埋めた状態
        };

        Assert.Equal("Button", snapshot.GetDisplayValue(TriggerProperty.ControlType));
    }
}
