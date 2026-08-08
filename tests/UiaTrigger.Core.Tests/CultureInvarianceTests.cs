using System.Globalization;
using UiaTrigger.Models;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 比較・永続化に使う文字列がカルチャに依存しないことを固定する (docs/DESIGN.md L4)。
///
/// 比較用と表示用は型で分離してある。ここで固定するのは 2 つ:
/// (a) <see cref="ComparisonString"/> はどのカルチャでも同じ値になること、
/// (b) 表示用は現在カルチャに従うこと — つまり **分離が実際に効いている**こと。
/// (b) が無いと「両方とも invariant なので、たまたま壊れていないだけ」の状態に逆戻りする。
/// </summary>
public sealed class CultureInvarianceTests
{
    /// <summary>小数点にカンマを使うカルチャ。invariant でない実装ならここで露見する。</summary>
    private static readonly CultureInfo German = new("de-DE");

    private static void WithCulture(CultureInfo culture, Action action)
    {
        using (CultureScope.Enter(culture))
        {
            action();
        }
    }

    public static TheoryData<string> Cultures() => new("en-US", "ja-JP", "de-DE");

    private static ElementPropertySnapshot Snapshot() => new()
    {
        Name = "value",
        ControlType = 50000,
        ControlTypeName = "Button",
        BoundingRectangle = new ElementRect(1, 2, 3, 4),
        IsEnabled = true,
        SupportsRangeValuePattern = true,
        RangeValue = 1234.5,
        RangeValueMinimum = -0.5,
        RangeValueMaximum = 9999.75,
    };

    [Theory]
    [MemberData(nameof(Cultures))]
    public void ComparisonString_ForNumbers_IsInvariant(string cultureName)
    {
        WithCulture(new CultureInfo(cultureName), () =>
        {
            Assert.Equal("1234.5", ComparisonString.FromNumber(1234.5d).Value);
            Assert.Equal("-0.25", ComparisonString.FromNumber(-0.25d).Value);
            Assert.Equal("50000", ComparisonString.FromNumber(50000).Value);
        });
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void ElementRect_ToString_IsInvariant(string cultureName)
    {
        WithCulture(new CultureInfo(cultureName), () =>
            Assert.Equal("(10,-20)-(130,80)", new ElementRect(10, -20, 130, 80).ToString()));
    }

    /// <summary>スナップショットの比較値がカルチャ非依存であること。</summary>
    [Theory]
    [MemberData(nameof(Cultures))]
    public void Snapshot_GetComparisonValue_IsInvariant(string cultureName)
    {
        WithCulture(new CultureInfo(cultureName), () =>
        {
            ElementPropertySnapshot snapshot = Snapshot();

            Assert.Equal("1234.5", snapshot.GetComparisonValue(TriggerProperty.RangeValue).Value);
            Assert.Equal("-0.5", snapshot.GetComparisonValue(TriggerProperty.RangeValueMinimum).Value);
            Assert.Equal("9999.75", snapshot.GetComparisonValue(TriggerProperty.RangeValueMaximum).Value);
            Assert.Equal("(1,2)-(3,4)", snapshot.GetComparisonValue(TriggerProperty.BoundingRectangle).Value);
            Assert.Equal("true", snapshot.GetComparisonValue(TriggerProperty.IsEnabled).Value);
        });
    }

    /// <summary>数値の解釈もカルチャ非依存であること (定義の意味が実行環境で変わらないこと)。</summary>
    [Theory]
    [MemberData(nameof(Cultures))]
    public void Snapshot_GetNumericValue_ParsesTheInvariantForm(string cultureName)
    {
        WithCulture(new CultureInfo(cultureName), () =>
            Assert.Equal(1234.5d, Snapshot().GetNumericValue(TriggerProperty.RangeValue)));
    }

    /// <summary>
    /// 分離が実際に効いていること: 表示用は現在カルチャに従う。
    /// de-DE では小数点がカンマになるので、表示経路と比較経路が同じなら
    /// <see cref="Snapshot_GetComparisonValue_IsInvariant"/> が落ちる。
    /// </summary>
    [Fact]
    public void Snapshot_GetDisplayValue_FollowsTheCurrentCulture()
    {
        WithCulture(German, () =>
        {
            Assert.Equal("1234,5", Snapshot().GetDisplayValue(TriggerProperty.RangeValue));
            Assert.Equal("1234.5", Snapshot().GetComparisonValue(TriggerProperty.RangeValue).Value);
        });
    }

    /// <summary>
    /// 比較値の等値判定は Ordinal であること。
    /// 「値なし」と「空文字列」も別物として扱う (プロパティ非対応と空値の混同を防ぐ)。
    /// </summary>
    [Fact]
    public void ComparisonString_ComparesOrdinallyAndDistinguishesAbsence()
    {
        Assert.Equal(ComparisonString.FromText("OK"), ComparisonString.FromDefinition("OK"));
        Assert.NotEqual(ComparisonString.FromText("OK"), ComparisonString.FromDefinition("ok"));
        Assert.NotEqual(ComparisonString.None, ComparisonString.FromText(string.Empty));
        Assert.False(ComparisonString.None.HasValue);
        Assert.Equal(string.Empty, ComparisonString.None.Value);
    }
}
