using UiaTrigger.Models;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 複合条件を組む・ほどく規則 (docs/DESIGN.md §4)。
///
/// この規則がホストにしか無いと、どのティアのテストも本体を通らない。
/// その配置が隠す不具合の実例が Watch のものである —
/// <c>unwatched.Remove(name)</c> が同じ反復の <c>!unwatched.Contains(name)</c> より
/// 先に走ると、「絞るだけ」に指定した条件も常に購読される
/// (<see cref="Compose_AnUnwatchedSource_YieldsUnwatchedClauses"/> が固定する)。
/// </summary>
public sealed class TriggerComposerTests
{
    /// <summary>句 1 つの、ふつうのトリガー。</summary>
    private static TriggerDefinition Simple(string id, string? displayName = null) => new()
    {
        Id = id,
        DisplayName = displayName,
        Window = new WindowIdentity { ProcessName = id + ".exe" },
        Locator = new ElementLocator(),
        On = TriggerOn.PropertyChanged,
        Clauses = [new PropertyClause { Property = TriggerProperty.Name, Op = ComparisonOp.Equals, Text = id }],
    };

    private static TriggerCompositionResult Compose(
        IReadOnlyList<TriggerDefinition> sources,
        string? expression = null,
        IReadOnlyCollection<string>? unwatchedNames = null,
        IEnumerable<string>? existingIds = null)
        => TriggerComposer.Compose(
            sources,
            expression,
            unwatchedNames ?? [],
            existingIds ?? sources.Select(s => s.Id));

    // ---- Compose: 基本形 ----

    [Fact]
    public void Compose_TwoSimpleTriggers_MakesAWhileMatchingCompositeNamedByTheirIds()
    {
        TriggerDefinition a = Simple("a");
        TriggerDefinition b = Simple("b");

        TriggerCompositionResult result = Compose([a, b]);

        Assert.True(result.IsValid);
        TriggerDefinition composite = result.Definition!;
        Assert.Equal("composite-1", composite.Id);
        Assert.Equal(TriggerOn.WhileMatching, composite.On);
        Assert.Null(composite.Expression);
        Assert.Equal(ClauseCombinator.All, composite.Combine);
        Assert.Equal(["a", "b"], composite.Clauses.Select(c => c.Name));
        Assert.All(composite.Clauses, c => Assert.True(c.Watch));
        // 既定の要素は先頭の元トリガーに合わせる (全句が要素を上書きしているので解決には使われない)
        Assert.Same(a.Window, composite.Window);
        Assert.Same(a.Locator, composite.Locator);
    }

    [Fact]
    public void Compose_CarriesEachSourcesElementIntoItsClauses()
    {
        TriggerDefinition a = Simple("a");
        TriggerDefinition b = Simple("b");

        TriggerDefinition composite = Compose([a, b]).Definition!;

        // 多要素の要点: 句が自分の元トリガーの要素を持つこと。参照ごと引き継ぐ
        Assert.Same(a.Window, composite.Clauses[0].Window);
        Assert.Same(b.Window, composite.Clauses[1].Window);
        Assert.Same(b.Locator, composite.Clauses[1].Locator);
    }

    [Fact]
    public void Compose_FewerThanTwoSources_IsRefused()
    {
        TriggerCompositionResult result = Compose([Simple("a")]);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
        Assert.Null(result.Definition);
    }

    // ---- Compose: 句の名前付け ----

    [Fact]
    public void Compose_AnIdThatCannotBeAClauseName_FallsBackToAPositionalName()
    {
        // 空白を含む id は式がそこで切れてしまうので、名前には使えない
        TriggerDefinition odd = Simple("has space");
        TriggerDefinition b = Simple("b");

        TriggerDefinition composite = Compose([odd, b], "c1 && b").Definition!;

        Assert.Equal(["c1", "b"], composite.Clauses.Select(c => c.Name));
    }

    [Fact]
    public void Compose_ASourceWithSeveralClauses_ExpandsTheNameWithASuffix()
    {
        TriggerDefinition login = Simple("login", "ログイン");
        login.Clauses.Add(new PropertyClause
        {
            Window = new WindowIdentity { ProcessName = "other.exe" },
            Property = TriggerProperty.Value,
            Op = ComparisonOp.GreaterThan,
            Value = 1,
        });
        TriggerDefinition b = Simple("b");

        TriggerDefinition composite = Compose([login, b]).Definition!;

        Assert.Equal(["login-1", "login-2", "b"], composite.Clauses.Select(c => c.Name));
        // 句が自分の要素を持っていればそれを使い、持っていなければ元トリガーの要素に落ちる
        Assert.Same(login.Window, composite.Clauses[0].Window);
        Assert.Same(login.Clauses[1].Window, composite.Clauses[1].Window);
        // DisplayName は句ごとに元トリガーのものを引き継ぐ
        Assert.All(composite.Clauses.Take(2), c => Assert.Equal("ログイン", c.DisplayName));
    }

    [Fact]
    public void Compose_ASourceWithoutClauses_ContributesAnAlwaysClause()
    {
        // 出現だけを見るトリガー。「その要素が在ること」だけを意味する句になる
        var appearOnly = new TriggerDefinition { Id = "a", On = TriggerOn.ElementAppeared };
        TriggerDefinition b = Simple("b");

        TriggerDefinition composite = Compose([appearOnly, b]).Definition!;

        Assert.Equal("a", composite.Clauses[0].Name);
        Assert.Equal(TriggerProperty.ControlType, composite.Clauses[0].Property);
        Assert.Equal(ComparisonOp.Always, composite.Clauses[0].Op);
    }

    // ---- Compose: unwatched (絞るだけの条件) ----

    [Fact]
    public void Compose_AnUnwatchedSource_YieldsUnwatchedClauses()
    {
        TriggerDefinition a = Simple("a");
        TriggerDefinition b = Simple("b");

        TriggerDefinition composite = Compose([a, b], unwatchedNames: ["b"]).Definition!;

        Assert.True(composite.Clauses[0].Watch);
        Assert.False(composite.Clauses[1].Watch);
    }

    [Fact]
    public void Compose_AnUnwatchedSourceWithSeveralClauses_UnwatchesThemAll()
    {
        TriggerDefinition login = Simple("login");
        login.Clauses.Add(new PropertyClause { Property = TriggerProperty.Value, Op = ComparisonOp.GreaterThan, Value = 1 });
        TriggerDefinition b = Simple("b");

        TriggerDefinition composite = Compose([login, b], unwatchedNames: ["login"]).Definition!;

        // 「絞るだけ」は元トリガー単位の指定。展開された login-1 / login-2 の両方に効く
        Assert.False(composite.Clauses[0].Watch);
        Assert.False(composite.Clauses[1].Watch);
        Assert.True(composite.Clauses[2].Watch);
    }

    [Fact]
    public void Compose_AnUnknownUnwatchedName_IsRefusedNamingTheTypo()
    {
        TriggerCompositionResult result = Compose([Simple("a"), Simple("b")], unwatchedNames: ["missing"]);

        Assert.False(result.IsValid);
        Assert.Contains("missing", result.Error, StringComparison.Ordinal);
    }

    // ---- Compose: 式 ----

    [Fact]
    public void Compose_TrimsTheExpression_AndTreatsBlankAsNone()
    {
        TriggerDefinition[] sources = [Simple("a"), Simple("b")];

        Assert.Equal("a && b", Compose(sources, "  a && b  ").Definition!.Expression);
        Assert.Null(Compose(sources, "   ").Definition!.Expression);
    }

    [Fact]
    public void Compose_ABrokenExpression_PassesTheValidatorsReasonThrough()
    {
        TriggerCompositionResult result = Compose([Simple("a"), Simple("b")], "a &&");

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
        Assert.Null(result.Definition);
    }

    // ---- Compose: 採番と非変異 ----

    [Fact]
    public void Compose_SkipsCompositeIdsAlreadyInUse()
    {
        TriggerDefinition[] sources = [Simple("a"), Simple("b")];

        TriggerCompositionResult result = Compose(
            sources, existingIds: ["a", "b", "composite-1", "composite-2"]);

        Assert.Equal("composite-3", result.Definition!.Id);
    }

    [Fact]
    public void Compose_LeavesItsInputsUntouched()
    {
        TriggerDefinition a = Simple("a");
        TriggerDefinition b = Simple("b");
        var unwatched = new List<string> { "b" };

        _ = Compose([a, b], unwatchedNames: unwatched);

        // unwatched の消し込みは写しに対して行われること。元トリガーの句も動かないこと
        Assert.Equal(["b"], unwatched);
        Assert.True(b.Clauses[0].Watch);
        Assert.Null(b.Clauses[0].Name);
        Assert.Equal(TriggerOn.PropertyChanged, b.On);
    }

    // ---- Decompose ----

    /// <summary>手で組んだ複合。句 a は要素を持ち、句 b は複合の既定に頼る。</summary>
    private static TriggerDefinition Composite() => new()
    {
        Id = "composite-1",
        Window = new WindowIdentity { ProcessName = "default.exe" },
        Locator = new ElementLocator(),
        On = TriggerOn.WhileMatching,
        Expression = "a && !b",
        Clauses =
        [
            new PropertyClause
            {
                Name = "a",
                DisplayName = "側 A",
                Window = new WindowIdentity { ProcessName = "a.exe" },
                Locator = new ElementLocator(),
                Property = TriggerProperty.Name,
                Op = ComparisonOp.Equals,
                Text = "A",
            },
            new PropertyClause
            {
                Name = "b",
                Watch = false,
                Property = TriggerProperty.Value,
                Op = ComparisonOp.GreaterThan,
                Value = 3,
                Tolerance = 0.5,
            },
        ],
    };

    [Fact]
    public void Decompose_RecoversOneTriggerPerClause_InClauseOrder()
    {
        TriggerDefinition composite = Composite();

        IReadOnlyList<TriggerDefinition> parts = TriggerComposer.Decompose(composite, []);

        Assert.Equal(["a", "b"], parts.Select(p => p.Id));
        Assert.All(parts, p => Assert.Equal(TriggerOn.WhileMatching, p.On));
        Assert.All(parts, p => Assert.Null(p.Expression));
        Assert.All(parts, p => Assert.Single(p.Clauses));
        // 条件はそのまま戻る
        Assert.Equal(ComparisonOp.GreaterThan, parts[1].Clauses[0].Op);
        Assert.Equal(3, parts[1].Clauses[0].Value);
        Assert.Equal(0.5, parts[1].Clauses[0].Tolerance);
        Assert.Equal("側 A", parts[0].DisplayName);
    }

    [Fact]
    public void Decompose_FallsBackToTheCompositesElement_WhenAClauseHasNone()
    {
        TriggerDefinition composite = Composite();

        IReadOnlyList<TriggerDefinition> parts = TriggerComposer.Decompose(composite, []);

        Assert.Same(composite.Clauses[0].Window, parts[0].Window);
        Assert.Same(composite.Window, parts[1].Window);
        Assert.Same(composite.Locator, parts[1].Locator);
    }

    [Fact]
    public void Decompose_ResetsClauseNameAndWatch()
    {
        IReadOnlyList<TriggerDefinition> parts = TriggerComposer.Decompose(Composite(), []);

        // 名前は句が 1 つだけの定義に要らず、「絞るだけ」は複合の中でだけ意味を持つ
        Assert.All(parts, p => Assert.Null(p.Clauses[0].Name));
        Assert.All(parts, p => Assert.True(p.Clauses[0].Watch));
    }

    [Fact]
    public void Decompose_UnnamedClauses_RecoverTheirPositionalNames()
    {
        TriggerDefinition composite = Composite();
        composite.Expression = null;
        composite.Clauses[0].Name = null;
        composite.Clauses[1].Name = null;

        IReadOnlyList<TriggerDefinition> parts = TriggerComposer.Decompose(composite, []);

        Assert.Equal(["c1", "c2"], parts.Select(p => p.Id));
    }

    [Fact]
    public void Decompose_AvoidsIdsAlreadyInUse()
    {
        IReadOnlyList<TriggerDefinition> parts = TriggerComposer.Decompose(
            Composite(), ["a", "a-2", "composite-1"]);

        Assert.Equal(["a-3", "b"], parts.Select(p => p.Id));
    }

    [Fact]
    public void Decompose_ANonComposite_Throws()
    {
        Assert.Throws<ArgumentException>(
            "composite", () => TriggerComposer.Decompose(Simple("x"), []));
    }

    [Fact]
    public void Decompose_LeavesTheCompositeUntouched()
    {
        TriggerDefinition composite = Composite();

        _ = TriggerComposer.Decompose(composite, []);

        Assert.Equal("a && !b", composite.Expression);
        Assert.Equal(["a", "b"], composite.Clauses.Select(c => c.Name));
        Assert.False(composite.Clauses[1].Watch);
    }

    // ---- 往復 ----

    [Fact]
    public void ComposeDecomposeCompose_KeepsTheNamesAndTheExpression()
    {
        // まとめる前の元トリガーを削除してしまっても、分解 → まとめ直しで同じ式が生きること。
        // 分解が付ける id は句の実効名 = 式がその句を指していた名前だからである
        TriggerDefinition first = Compose([Simple("a"), Simple("b")], "a && !b").Definition!;

        IReadOnlyList<TriggerDefinition> parts = TriggerComposer.Decompose(first, []);
        TriggerCompositionResult second = Compose(parts, "a && !b");

        Assert.True(second.IsValid);
        Assert.Equal("a && !b", second.Definition!.Expression);
        Assert.Equal(
            first.Clauses.Select(c => c.Name),
            second.Definition!.Clauses.Select(c => c.Name));
    }

    [Fact]
    public void ComposeDecompose_RoundTripsTheSubstituteClause()
    {
        var appearOnly = new TriggerDefinition { Id = "a", On = TriggerOn.ElementAppeared };
        TriggerDefinition composite = Compose([appearOnly, Simple("b")]).Definition!;

        IReadOnlyList<TriggerDefinition> parts = TriggerComposer.Decompose(composite, []);

        Assert.Equal(TriggerProperty.ControlType, parts[0].Clauses[0].Property);
        Assert.Equal(ComparisonOp.Always, parts[0].Clauses[0].Op);
    }
}
