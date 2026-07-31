using UiaTrigger.Models;
using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// <see cref="PropertyClause.Watch"/> が購読の形に効くこと (docs/DESIGN.md §4)。
///
/// 購読は <c>PropertyReader.ToPropertyId</c> が <see cref="PropertyClause.Property"/> から作り
/// <see cref="PropertyClause.Op"/> を一度も見ない。そのため「この要素が存在する」を
/// <see cref="ComparisonOp.Always"/> の句で書いても、そのままでは
/// **その句が名指ししたプロパティが変わるたびに発火する** — つまり
/// 「A が存在していて、B の値が変化したとき」が書けない。それを埋めるのが Watch である。
///
/// ここは要素が 1 つも解決しない定義で回す。<c>PropertyIds</c> は定義を追加した時点で
/// 決まるので、実 UIA も対象アプリも要らない。
/// </summary>
[Collection(RealUiaLiteTests.Name)]
public sealed class TriggerMonitorWatchTests
{
    /// <summary>絶対に解決しないウィンドウ。購読の形だけを見たいので、要素は要らない。</summary>
    private static WindowIdentity NoSuchWindow => new() { ProcessName = "no-such-process-uiatrigger.exe" };

    private static PropertyClause Clause(TriggerProperty property, bool watch = true, string? name = null) => new()
    {
        Name = name,
        Property = property,
        Op = ComparisonOp.Always,
        Watch = watch,
    };

    private static async Task<TriggerMonitorDiagnostics> DiagnosticsFor(params PropertyClause[] clauses)
    {
        await using var monitor = new TriggerMonitor();
        await monitor.StartAsync(
        [
            new TriggerDefinition
            {
                Id = "t",
                Window = NoSuchWindow,
                On = TriggerOn.PropertyChanged,
                Clauses = [.. clauses],
            },
        ]);
        return monitor.GetDiagnostics();
    }

    [Fact]
    public async Task AWatchedClause_IsSubscribedTo()
    {
        TriggerMonitorDiagnostics diagnostics = await DiagnosticsFor(Clause(TriggerProperty.Name));

        Assert.Equal(1, diagnostics.WatchedPropertyCount);
    }

    /// <summary>
    /// 監視しない句は購読を作らない。**この検査の要点である** —
    /// これが落ちると「存在を見るだけ」の句が発火源のままになり、
    /// 「A が存在していて、B の値が変化したとき」が書けない状態に戻る。
    /// </summary>
    [Fact]
    public async Task AnUnwatchedClause_IsNotSubscribedTo()
    {
        TriggerMonitorDiagnostics diagnostics = await DiagnosticsFor(
            Clause(TriggerProperty.Name, name: "b"),
            Clause(TriggerProperty.IsEnabled, watch: false, name: "a"));

        // 購読されるのは監視する側の 1 つだけ
        Assert.Equal(1, diagnostics.WatchedPropertyCount);
    }

    /// <summary>
    /// 同じプロパティを見る句が複数あっても購読は 1 本。
    /// 重複排除が消えると、句を足すたびに同じプロパティの購読が増える。
    /// </summary>
    [Fact]
    public async Task ClausesOnTheSameProperty_ShareOneSubscription()
    {
        TriggerMonitorDiagnostics diagnostics = await DiagnosticsFor(
            Clause(TriggerProperty.Name, name: "a"),
            Clause(TriggerProperty.Name, name: "b"));

        Assert.Equal(1, diagnostics.WatchedPropertyCount);
    }

    [Fact]
    public async Task WatchedClausesOnDifferentProperties_EachGetASubscription()
    {
        TriggerMonitorDiagnostics diagnostics = await DiagnosticsFor(
            Clause(TriggerProperty.Name, name: "a"),
            Clause(TriggerProperty.IsEnabled, name: "b"),
            Clause(TriggerProperty.Value, name: "c"));

        Assert.Equal(3, diagnostics.WatchedPropertyCount);
    }

    /// <summary>
    /// 解決していないトリガーだけを抱えていても孤児は 0 であること。
    /// ウィンドウが無いのだから購読すべきものも無い、が正常な状態である
    /// (docs/DESIGN.md §8)。ここが 0 でなくなると <c>SubscriptionRepair</c> が
    /// armed のままになり、ポーリングしないという売りが壊れる。
    /// </summary>
    [Fact]
    public async Task AnUnresolvedTrigger_IsNotOrphaned()
    {
        TriggerMonitorDiagnostics diagnostics = await DiagnosticsFor(Clause(TriggerProperty.Name));

        Assert.Equal(0, diagnostics.OrphanedTriggerCount);
        Assert.Equal(0, diagnostics.ResolvedTriggerCount);
        Assert.Equal(1, diagnostics.TriggerCount);
    }
}
