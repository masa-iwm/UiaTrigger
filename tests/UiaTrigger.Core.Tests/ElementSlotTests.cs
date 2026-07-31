using System.Reflection;
using UiaTrigger.Models;
using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 1 つのトリガーが複数の要素にまたがれること (docs/DESIGN.md §4)。
///
/// 句を実効 (Window, Locator) でまとめた単位が「スロット」で、解決も購読もスロットごとに起きる。
/// **同じ要素を指す句が別々のスロットになると、1 要素にプロパティハンドラが 2 本立ち、
/// 1 回の変化で 2 回発火する。**ただの無駄ではなく誤りである。
///
/// ここは要素が 1 つも解決しない定義で回す。スロットは定義を追加した時点で決まるので、
/// 実 UIA も対象アプリも要らない (<see cref="TriggerMonitorDiagnostics.WatchedPropertyCount"/> が
/// スロットごとの購読数の合計になる)。
/// </summary>
[Collection(RealUiaLiteTests.Name)]
public sealed class ElementSlotTests
{
    private static ElementLocator Locator(string automationId) =>
        new() { View = TreeViewMode.Control, Search = new ElementSearch { AutomationId = automationId } };

    private static PropertyClause Clause(
        TriggerProperty property, WindowIdentity? window = null, ElementLocator? locator = null, string? name = null) => new()
        {
            Name = name,
            Window = window,
            Locator = locator,
            Property = property,
            Op = ComparisonOp.Always,
        };

    private static async Task<TriggerMonitorDiagnostics> DiagnosticsFor(
        TriggerOn on, WindowIdentity window, params PropertyClause[] clauses)
    {
        await using var monitor = new TriggerMonitor();
        await monitor.StartAsync(
        [
            new TriggerDefinition
            {
                Id = "t",
                Window = window,
                Locator = Locator("default"),
                On = on,
                Clauses = [.. clauses],
            },
        ]);
        return monitor.GetDiagnostics();
    }

    private static Task<TriggerMonitorDiagnostics> DiagnosticsFor(params PropertyClause[] clauses) =>
        DiagnosticsFor(TriggerOn.PropertyChanged, new WindowIdentity { ProcessName = "no-such-a.exe" }, clauses);

    // ---------- 同じ要素を指す句はまとまる ----------

    /// <summary>
    /// **この検査がいちばん重要である。**
    ///
    /// <see cref="WindowIdentity"/> と <see cref="ElementLocator"/> は <c>Equals</c> を持たない
    /// 可変クラスなので、JSON から読んだ「同じ要素を指す 2 つの句」は**別オブジェクト**になる。
    /// 参照同値でまとめようとすると、ここが 2 スロットに割れて二重発火する。
    /// 手で同じインスタンスを使い回す検査では絶対に落ちない形の誤りである。
    /// </summary>
    [Fact]
    public async Task ClausesWithEqualButDistinctTargets_ShareOneSlot()
    {
        var a = new WindowIdentity { ProcessName = "no-such-a.exe", ClassName = "Frame" };
        var b = new WindowIdentity { ProcessName = "no-such-a.exe", ClassName = "Frame" };
        Assert.NotSame(a, b);

        TriggerMonitorDiagnostics diagnostics = await DiagnosticsFor(
            Clause(TriggerProperty.Name, a, Locator("same"), "c1"),
            Clause(TriggerProperty.IsEnabled, b, Locator("same"), "c2"));

        // 1 スロットに 2 プロパティ。2 スロットに割れていても合計は 2 なので、
        // 数だけでは区別が付かない — 下の 2 件と合わせて読むこと
        Assert.Equal(2, diagnostics.WatchedPropertyCount);
        // 同じプロパティなら 1 本に畳まれる。これはスロットが 1 つのときにしか起きない
        TriggerMonitorDiagnostics same = await DiagnosticsFor(
            Clause(TriggerProperty.Name, a, Locator("same"), "c1"),
            Clause(TriggerProperty.Name, b, Locator("same"), "c2"));
        Assert.Equal(1, same.WatchedPropertyCount);
    }

    /// <summary>要素を上書きしていない句は、トリガーの既定の要素に集まる。</summary>
    [Fact]
    public async Task ClausesWithoutAnOverride_ShareTheDefaultTarget()
    {
        TriggerMonitorDiagnostics diagnostics = await DiagnosticsFor(
            Clause(TriggerProperty.Name, name: "c1"),
            Clause(TriggerProperty.Name, name: "c2"));

        Assert.Equal(1, diagnostics.WatchedPropertyCount);
    }

    /// <summary>経路だけが違えば別のスロットになる (同じプロパティでも畳まれない)。</summary>
    [Fact]
    public async Task ClausesOnDifferentLocators_GetSeparateSlots()
    {
        TriggerMonitorDiagnostics diagnostics = await DiagnosticsFor(
            Clause(TriggerProperty.Name, locator: Locator("one"), name: "c1"),
            Clause(TriggerProperty.Name, locator: Locator("two"), name: "c2"));

        Assert.Equal(2, diagnostics.WatchedPropertyCount);
    }

    /// <summary>ウィンドウだけが違っても別のスロットになる。これが「別プロセスにまたがる」形。</summary>
    [Fact]
    public async Task ClausesOnDifferentWindows_GetSeparateSlots()
    {
        TriggerMonitorDiagnostics diagnostics = await DiagnosticsFor(
            Clause(TriggerProperty.Name, new WindowIdentity { ProcessName = "no-such-a.exe" }, Locator("x"), "c1"),
            Clause(TriggerProperty.Name, new WindowIdentity { ProcessName = "no-such-b.exe" }, Locator("x"), "c2"));

        Assert.Equal(2, diagnostics.WatchedPropertyCount);
    }

    /// <summary>
    /// <see cref="WindowIdentity"/> の**どのプロパティ**が違ってもスロットは割れること。
    ///
    /// 手書きの構造比較だと、あとからプロパティが 1 つ増えた日にそれを見落として
    /// 「違う要素を同じと見なす」形で黙って腐る。この検査はリフレクションで全プロパティを
    /// 回すので、プロパティが増えても腐らない。
    /// </summary>
    [Fact]
    public async Task EveryWindowIdentityProperty_TakesPartInTheSlotKey()
    {
        foreach (PropertyInfo property in typeof(WindowIdentity).GetProperties())
        {
            var left = new WindowIdentity { ProcessName = "no-such-a.exe" };
            var right = new WindowIdentity { ProcessName = "no-such-a.exe" };
            property.SetValue(right, DifferentValueFor(property, property.GetValue(right)));

            TriggerMonitorDiagnostics diagnostics = await DiagnosticsFor(
                TriggerOn.PropertyChanged,
                new WindowIdentity { ProcessName = "no-such-default.exe" },
                Clause(TriggerProperty.Name, left, Locator("x"), "c1"),
                Clause(TriggerProperty.Name, right, Locator("x"), "c2"));

            // 同じプロパティを見る 2 句なので、まとまれば 1、割れれば 2 になる
            Assert.Equal(2, diagnostics.WatchedPropertyCount);
        }
    }

    /// <summary>
    /// そのプロパティについて、いまと違う値を 1 つ作る。
    /// <see cref="MatchStrength.Required"/> は使わない — 記録されていない属性を Required にすると
    /// 「一致しうるウィンドウが存在しない」として検証で弾かれ、鍵の話にたどり着けない。
    /// </summary>
    private static object? DifferentValueFor(PropertyInfo property, object? current) =>
        property.PropertyType == typeof(MatchStrength)
            ? (MatchStrength)current! == MatchStrength.Preferred ? MatchStrength.Ignored : MatchStrength.Preferred
            : (string?)current == "changed" ? "changed-2" : "changed";

    // ---------- トリガーの既定の要素をいつ解決するか ----------

    /// <summary>
    /// 全句が要素を上書きしているなら、トリガーの既定の要素は解決しない。
    ///
    /// 誰も要らない要素まで解決しにいくと、それが存在しないときに
    /// <c>IsOrphaned(0, hwnd)</c> が永久に true になって <c>SubscriptionRepair</c> が
    /// armed のままになる = **事実上のポーリング**である (docs/DESIGN.md §8)。
    /// </summary>
    [Fact]
    public async Task WhenEveryClauseOverridesTheTarget_TheDefaultIsNotResolved()
    {
        // 既定のウィンドウは ProcessName が空 = Required を満たさない。
        // それでも通るということは、既定のスロットが作られていないということ
        TriggerMonitorDiagnostics diagnostics = await DiagnosticsFor(
            TriggerOn.PropertyChanged,
            new WindowIdentity { ProcessName = string.Empty },
            Clause(TriggerProperty.Name, new WindowIdentity { ProcessName = "no-such-a.exe" }, Locator("x"), "c1"));

        Assert.Equal(1, diagnostics.WatchedPropertyCount);
        Assert.Equal(0, diagnostics.OrphanedTriggerCount);
    }

    /// <summary>
    /// 逆に <see cref="TriggerOn.ElementAppeared"/> / <see cref="TriggerOn.ElementRemoved"/> は
    /// 「**どの**要素が出現したのか」を言う必要があるので、既定の要素が要る。
    /// </summary>
    [Theory]
    [InlineData(TriggerOn.ElementAppeared)]
    [InlineData(TriggerOn.ElementRemoved)]
    public async Task LifecycleTriggers_StillRequireTheDefaultTarget(TriggerOn on)
    {
        await using var monitor = new TriggerMonitor();
        Exception? error = await Record.ExceptionAsync(() => monitor.StartAsync(
        [
            new TriggerDefinition
            {
                Id = "t",
                Window = new WindowIdentity { ProcessName = string.Empty },
                On = on,
                Clauses = [Clause(TriggerProperty.Name, new WindowIdentity { ProcessName = "no-such-a.exe" }, Locator("x"), "c1")],
            },
        ], TestContext.Current.CancellationToken));

        Assert.IsType<ArgumentException>(error);
    }

    /// <summary>
    /// 句が上書きしたウィンドウの誤りも検証で弾くこと。
    /// 定義の既定だけを見ていると「永久に解決しない」としてしか現れない。
    /// </summary>
    [Fact]
    public async Task AnOverriddenWindowIsValidatedToo()
    {
        await using var monitor = new TriggerMonitor();
        Exception? error = await Record.ExceptionAsync(() => monitor.StartAsync(
        [
            new TriggerDefinition
            {
                Id = "t",
                Window = new WindowIdentity { ProcessName = "no-such-a.exe" },
                On = TriggerOn.PropertyChanged,
                Clauses =
                [
                    Clause(
                        TriggerProperty.Name,
                        new WindowIdentity { ProcessName = string.Empty, ProcessNameMatch = MatchStrength.Required },
                        Locator("x"),
                        "c1"),
                ],
            },
        ], TestContext.Current.CancellationToken));

        Assert.IsType<ArgumentException>(error);
    }
}
