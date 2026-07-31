using UiaTrigger.Models;
using Xunit;

namespace UiaTrigger.RealUia.Tests;

/// <summary>
/// 識別用の安定名と表示用のローカライズ名の分離を、実 UIA で確かめる (docs/DESIGN.md L6)。
///
/// T1 (<c>ControlTypeNameTests</c>) は擬似的なスナップショットを組み立てて分離の規則を固定するが、
/// <b>実 UIA が本当に別の文字列を返すのか</b>は擬似データでは分からない。
/// 表示名が安定名と同じ文字列しか返さないなら、L6 の分離は「持っているが誰も使わない 2 つ目の名前」
/// でしかなく、ピッカーが安定名を出し続けても誰も気づけない。ここでその前提を実測する。
///
/// <para>
/// 両プロファイルで走らせる。表示名を出すのは<b>プロバイダー</b>であり、
/// 1 つのプロバイダーが 2 つ目の名前を返すことを確かめても
/// 「どのプロバイダーでも成り立つ」根拠にはならない。
/// </para>
/// </summary>
public sealed class ControlTypeNameScenarioTests
{
    /// <summary>
    /// プロバイダーが表示名を返し、それが安定名とは<b>別の文字列</b>であること。
    ///
    /// WinForms のボタンは UIA の安定名では <c>Button</c> だが、
    /// <c>LocalizedControlType</c> は OS の表示言語に応じた文字列 (英語なら "button"、
    /// 日本語なら "ボタン") を返す。どちらの言語でも序数比較では安定名と一致しないので、
    /// この assert は実行環境の言語に依存しない。
    /// </summary>
    [Theory]
    [MemberData(nameof(TargetProfile.AllNames), MemberType = typeof(TargetProfile))]
    public async Task Snapshot_LocalizedControlType_DiffersFromTheStableName(string profile)
    {
        using var target = TestTargetProcess.Start(profile);
        target.Send("add-button btnTypeName");

        (int x, int y) = target.CenterOf("btnTypeName");
        await using var session = new UiaSession();
        UiaElement? element = await session.ElementFromPointAsync(x, y);
        Assert.NotNull(element);
        using (element)
        {
            ElementPropertySnapshot? snapshot = await session.ReadSnapshotAsync(element);
            Assert.NotNull(snapshot);

            Assert.Equal("Button", snapshot.ControlTypeName);
            Assert.NotEmpty(snapshot.LocalizedControlType);
            Assert.NotEqual(
                snapshot.ControlTypeName,
                snapshot.LocalizedControlType,
                StringComparer.Ordinal);

            // UiaElement 側も同じ 2 本立てであること (ピッカーが読むのはこちら)
            Assert.Equal(snapshot.ControlTypeName, element.ControlTypeName);
            Assert.Equal(snapshot.LocalizedControlType, element.LocalizedControlType);
            Assert.Contains(element.LocalizedControlType, element.DisplayLabel, StringComparison.Ordinal);
            Assert.Contains(element.ControlTypeName, element.Label, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 記録した定義に載るのは<b>安定名のほう</b>であること。
    ///
    /// <see cref="TriggerDefinition.DisplayName"/> は永続化される。ここが
    /// <c>LocalizedControlType</c> になると、対象アプリを別の言語で起動し直しただけで
    /// 保存済みの定義の見た目が変わり、条件に書いた <see cref="TriggerProperty.ControlType"/> の
    /// 値とも食い違う。例外にはならないので、テストでしか捕まえられない。
    /// </summary>
    [Theory]
    [MemberData(nameof(TargetProfile.AllNames), MemberType = typeof(TargetProfile))]
    public async Task Record_PutsTheStableNameInTheDisplayName(string profile)
    {
        using var target = TestTargetProcess.Start(profile);
        target.Send("add-button btnDisplayName");

        TriggerDefinition definition = await Recording.RecordAsync(target, "btnDisplayName");

        Assert.NotNull(definition.DisplayName);
        Assert.Contains("Button", definition.DisplayName, StringComparison.Ordinal);
    }

    /// <summary>
    /// 表示形と比較形が実 UIA でも分かれていること。
    ///
    /// 条件評価が見るのは比較形だけである (<c>ComparisonString</c>)。
    /// ここが表示形に切り替わると、英語環境では <c>Equals "Button"</c> が
    /// 小文字の "button" と一致しなくなり、日本語環境では完全に別物になる。
    /// </summary>
    [Theory]
    [MemberData(nameof(TargetProfile.AllNames), MemberType = typeof(TargetProfile))]
    public async Task Snapshot_ComparisonAndDisplayValues_ForControlTypeAreDifferent(string profile)
    {
        using var target = TestTargetProcess.Start(profile);
        target.Send("add-button btnCompare");

        (int x, int y) = target.CenterOf("btnCompare");
        await using var session = new UiaSession();
        UiaElement? element = await session.ElementFromPointAsync(x, y);
        Assert.NotNull(element);
        using (element)
        {
            ElementPropertySnapshot? snapshot = await session.ReadSnapshotAsync(element);
            Assert.NotNull(snapshot);

            Assert.Equal("Button", snapshot.GetComparisonValue(TriggerProperty.ControlType).Value);
            Assert.Equal(snapshot.LocalizedControlType, snapshot.GetDisplayValue(TriggerProperty.ControlType));
            Assert.NotEqual(
                snapshot.GetComparisonValue(TriggerProperty.ControlType).Value,
                snapshot.GetDisplayValue(TriggerProperty.ControlType),
                StringComparer.Ordinal);
        }
    }
}
