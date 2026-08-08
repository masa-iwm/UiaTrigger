using System.Text.Json;
using UiaTrigger.Models;
using UiaTrigger.Serialization;
using Xunit;

namespace UiaTrigger.RealUia.Tests;

/// <summary>
/// パスワード欄の秘匿を**実 UIA の全読み取り経路**で確かめる (docs/DESIGN.md C12/C21)。
///
/// <para>
/// T1 (<c>SnapshotRedactionTests</c>) が縛るのは <c>RedactIfPassword</c> という 1 関数だけで、
/// **その関数を通らない経路があるかどうか**は擬似データでは分からない。実際、経路記録
/// (<c>ElementPathStep.Name</c>) と <see cref="TriggerProperty.Custom"/> の読みは
/// スナップショットを通らない第二・第三の経路であり、伏字化が抜けていた。
/// 「伏せた値は復活しない」(C12) はライブラリ全体の主張なので、ここで経路ごとに測る。
/// </para>
/// <para>
/// 対象は WinForms のみ。<c>UseSystemPasswordChar</c> が MSAA ブリッジ経由で
/// <c>IsPassword</c> を立てる。判定はプロバイダーではなくこちら側の規則なので、
/// プロバイダー差 (A21) の論点にはならない。
/// </para>
/// </summary>
public sealed class RedactionScenarioTests
{
    private const string Secret = "hunter2-should-not-appear";

    /// <summary>
    /// **記録した定義に平文が残らないこと** (C21)。
    ///
    /// <para>
    /// 経路記録は <c>get_CurrentName</c> の生値を <c>ElementPathStep.Name</c> として
    /// **ディスクへ書く**。スナップショット側だけ伏せても、プロバイダーが値を Name に出す
    /// 構成では定義ファイルに平文が残る — 保存された時点で取り返しがつかない類の漏れである。
    /// </para>
    /// <para>
    /// **「値が Name に出る」状況は意図的に作る。**WinForms の MSAA ブリッジは
    /// <c>UseSystemPasswordChar</c> の TextBox の Name に値を出さないので、そのままでは
    /// この検査に検出力が無い (伏字化を外しても緑になることを実測で確認した)。C12 が
    /// 名指しで防いでいるのは**プロパティによっては値が Name 側に出るプロバイダー**なので、
    /// <c>set-accessible-name</c> でその条件を再現する — 伏字化はどこから来た値かではなく
    /// <c>IsPassword</c> で決まるので、これは実装の意味論に沿った再現である。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Record_OnAPasswordField_WritesNoPlaintextIntoTheDefinition()
    {
        using var target = TestTargetProcess.Start();
        target.Send($"add-password txtSecret {Secret}");
        // 値が Name 側に出るプロバイダーを模す (上の remarks を参照)
        target.Send($"set-accessible-name txtSecret {Secret}");

        TriggerDefinition definition = await Recording.RecordAsync(target, "txtSecret");

        // 定義まるごとを見る。個別のプロパティを並べるより取りこぼしが少ない
        string json = JsonSerializer.Serialize(definition, TriggerJsonContext.Default.TriggerDefinition);
        Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);
    }

    /// <summary>
    /// **スナップショット経路で伏せられること** (C12 の本体を実 UIA で)。
    /// あわせて「そもそも IsPassword が立っている」ことも確かめる — 立っていなければ
    /// 上の検査は何も証明していない (対象アプリの構成が前提を満たす、の実測)。
    /// </summary>
    [Fact]
    public async Task ReadSnapshot_OnAPasswordField_WithholdsTheValue()
    {
        using var target = TestTargetProcess.Start();
        target.Send($"add-password txtSecret {Secret}");

        (int x, int y) = target.CenterOf("txtSecret");
        await using var session = new UiaSession();
        UiaElement? element = await session.ElementFromPointAsync(x, y);
        Assert.NotNull(element);
        using (element)
        {
            ElementPropertySnapshot? snapshot = await session.ReadSnapshotAsync(element);
            Assert.NotNull(snapshot);

            Assert.True(
                snapshot.IsPassword,
                "対象がパスワード欄として報告されていない — この一群の検査が何も証明しなくなる " +
                "(UseSystemPasswordChar が IsPassword を立てる、という前提の実測)。");
            Assert.DoesNotContain(Secret, snapshot.ToString(), StringComparison.Ordinal);
            Assert.Equal(ElementPropertySnapshot.RedactedMarker, snapshot.Value);
            Assert.Equal(
                ElementPropertySnapshot.RedactedMarker,
                snapshot.GetComparisonValue(TriggerProperty.Value).Value);
        }
    }

    /// <summary>
    /// **Custom 経路でも伏せられること** (C21)。
    ///
    /// <para>
    /// <see cref="TriggerProperty.Custom"/> はキャッシュ済みスナップショットを通らないので、
    /// <c>CustomPropertyId=30045</c> (Value) の句はプロパティを直接読む。ここが素通しだと、
    /// 発火イベントの <c>ClauseReading</c> として平文がホストへ渡る — 監視を仕掛けた側が
    /// 意図せず秘密を受け取る形になる。
    /// </para>
    /// <para>
    /// 発火させずに読み取り値だけを見る形にはできない (評価は内部で走る) ので、
    /// 発火イベントのペイロードで測る。Op=Always の Custom 句は要素が在る間ずっと成立するので、
    /// 監視開始時の初回発火がそのまま観測点になる。
    /// </para>
    /// </summary>
    [Fact]
    public async Task ACustomValueClause_OnAPasswordField_ReportsNoPlaintext()
    {
        using var target = TestTargetProcess.Start();
        target.Send($"add-password txtSecret {Secret}");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "txtSecret")).PinToWindow(target);
        definition.On = TriggerOn.WhileMatching;
        definition.Clauses.Clear();
        definition.Clauses.Add(new PropertyClause
        {
            Property = TriggerProperty.Custom,
            CustomPropertyId = UiaTrigger.Interop.UiaIds.ValueValueProperty,
            Op = ComparisonOp.Always,
        });

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);

        Monitoring.TriggerFiredEventArgs fired = harness.WaitForFire();

        string reading = Assert.Single(fired.Clauses).Value.Value;
        Assert.DoesNotContain(Secret, reading, StringComparison.Ordinal);
        Assert.Equal(ElementPropertySnapshot.RedactedMarker, reading);
    }
}
