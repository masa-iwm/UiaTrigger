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

    /// <summary>句 2 つのトリガー。まとめると <c>id-1</c> / <c>id-2</c> へ展開される。</summary>
    private static TriggerDefinition Multi(string id)
    {
        TriggerDefinition definition = Simple(id);
        definition.Clauses.Add(new PropertyClause
        {
            Property = TriggerProperty.Value, Op = ComparisonOp.GreaterThan, Value = 1,
        });
        return definition;
    }

    /// <summary>定義まるごとの比較用。個別の Assert を並べるより取りこぼしが少ない。</summary>
    private static string Json(TriggerDefinition definition) =>
        System.Text.Json.JsonSerializer.Serialize(
            definition, UiaTrigger.Serialization.TriggerJsonContext.Default.TriggerDefinition);

    private static TriggerCompositionResult Compose(
        IReadOnlyList<TriggerDefinition> sources,
        string? expression = null,
        IReadOnlyCollection<string>? unwatchedNames = null,
        IEnumerable<string>? existingIds = null,
        TimeSpan? pollInterval = null,
        bool notifyOnStoppedMatching = false)
        => TriggerComposer.Compose(
            sources,
            expression,
            unwatchedNames ?? [],
            existingIds ?? sources.Select(s => s.Id),
            pollInterval,
            notifyOnStoppedMatching);

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

    // ---- Compose: ポーリング間隔 ----

    /// <summary>まとめる時に指定したポーリング間隔が複合に載ること (UI からの唯一の入力口)。</summary>
    [Fact]
    public void Compose_WithAPollInterval_PutsItOnTheComposite()
    {
        TriggerCompositionResult result = Compose(
            [Simple("a"), Simple("b")], pollInterval: TimeSpan.FromSeconds(2));

        Assert.True(result.IsValid);
        Assert.Equal(TimeSpan.FromSeconds(2), result.Definition!.PollInterval);
    }

    /// <summary>null と 0 は「未設定」— TriggerDefinition.PollInterval の意味論と揃える。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void Compose_WithoutAUsablePollInterval_LeavesItUnset(int? seconds)
    {
        TriggerCompositionResult result = Compose(
            [Simple("a"), Simple("b")],
            pollInterval: seconds is { } s ? TimeSpan.FromSeconds(s) : null);

        Assert.True(result.IsValid);
        Assert.Null(result.Definition!.PollInterval);
    }

    /// <summary>負値は理由付きで断る。検証は Compose が持つ (呼び出し元に写経させない)。</summary>
    [Fact]
    public void Compose_ANegativePollInterval_IsRefused()
    {
        TriggerCompositionResult result = Compose(
            [Simple("a"), Simple("b")], pollInterval: TimeSpan.FromSeconds(-1));

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
        Assert.Null(result.Definition);
    }

    /// <summary>
    /// 複合自身のポーリング間隔は、ほどいても句由来のトリガーへ持ち出されないこと。
    /// あれは「まとめた条件を読み直す」費用であって、どれか 1 つの条件のものではない。
    /// </summary>
    [Fact]
    public void Decompose_LeavesTheCompositesPollIntervalBehind()
    {
        TriggerDefinition composite = Compose(
            [Simple("a"), Simple("b")], pollInterval: TimeSpan.FromSeconds(2)).Definition!;

        IReadOnlyList<TriggerDefinition> recovered =
            TriggerComposer.Decompose(composite, ["composite-1"]);

        Assert.All(recovered, r => Assert.Null(r.PollInterval));
    }

    /// <summary>
    /// ポーリング間隔付きで組んだ複合が監視開始まで通ること。
    /// Compose と CreateRuntime の検証が食い違うと「まとめられたのに開始できない定義」になる
    /// (<c>Apply_AlwaysProducesADefinitionTheMonitorAccepts</c> と同じ線)。
    /// </summary>
    [Fact]
    public async Task Compose_WithAPollInterval_ProducesADefinitionTheMonitorAccepts()
    {
        TriggerDefinition composite = Compose(
            [Simple("no-such-process-uiatrigger-a"), Simple("no-such-process-uiatrigger-b")],
            pollInterval: TimeSpan.FromMilliseconds(500)).Definition!;

        await using var monitor = new UiaTrigger.Monitoring.TriggerMonitor();
        Exception? error = await Record.ExceptionAsync(
            () => monitor.StartAsync([composite], TestContext.Current.CancellationToken));

        Assert.Null(error);
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

    // ---- Compose: 立ち下がり通知 ----

    /// <summary>
    /// まとめる時に立ち下がり通知を指定できること。複合は必ず WhileMatching なので、
    /// ここで立てた旗は必ず監視が受け付ける (docs/DESIGN.md C14)。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Compose_CarriesNotifyOnStoppedMatchingOntoTheComposite(bool notify)
    {
        TriggerCompositionResult result = Compose(
            [Simple("a"), Simple("b")], notifyOnStoppedMatching: notify);

        Assert.Equal(notify, result.Definition!.NotifyOnStoppedMatching);
    }

    // ---- Update ----

    /// <summary>
    /// 書き換わるのは 4 つ (式・句ごとの Watch・ポーリング間隔・立ち下がり通知) だけで、
    /// id も句の要素も述語も並びも動かないこと。呼び出し元が結果を**元の位置へ**戻せる根拠である。
    /// </summary>
    [Fact]
    public void Update_RewritesTheFourSettings_AndNothingElse()
    {
        TriggerDefinition composite = Composite();

        TriggerCompositionResult result = TriggerComposer.Update(
            composite, "a || b", ["a"], TimeSpan.FromSeconds(3), notifyOnStoppedMatching: true);

        Assert.True(result.IsValid);
        TriggerDefinition updated = result.Definition!;
        Assert.Equal("a || b", updated.Expression);
        Assert.Equal(TimeSpan.FromSeconds(3), updated.PollInterval);
        Assert.True(updated.NotifyOnStoppedMatching);
        Assert.False(updated.Clauses[0].Watch);
        Assert.True(updated.Clauses[1].Watch);
        // 据え置き
        Assert.Equal("composite-1", updated.Id);
        Assert.Equal(TriggerOn.WhileMatching, updated.On);
        Assert.Equal(["a", "b"], updated.Clauses.Select(c => c.Name));
        Assert.Equal(TriggerProperty.Value, updated.Clauses[1].Property);
        Assert.Equal(ComparisonOp.GreaterThan, updated.Clauses[1].Op);
        Assert.Equal(3, updated.Clauses[1].Value);
        Assert.Equal(0.5, updated.Clauses[1].Tolerance);
        Assert.Equal("a.exe", updated.Clauses[0].Window!.ProcessName);
    }

    /// <summary>渡された複合には触れず、返すのは別の実体であること (句まで別であること)。</summary>
    [Fact]
    public void Update_LeavesTheCompositeItWasGivenUntouched()
    {
        TriggerDefinition composite = Composite();
        string before = Json(composite);

        TriggerDefinition updated = TriggerComposer
            .Update(composite, "a || b", [], TimeSpan.FromSeconds(3), true).Definition!;

        Assert.Equal(before, Json(composite));
        Assert.NotSame(composite, updated);
        Assert.NotSame(composite.Clauses[0], updated.Clauses[0]);
    }

    /// <summary>
    /// **語彙が Compose と違う** (docs/DESIGN.md C17)。まとめた後に残っているのは句の名前だけなので、
    /// 複数句のソース由来の句は <c>login-1</c> / <c>login-2</c> として個別に指す。
    /// 元トリガーの id (<c>login</c>) はもうどこにも無い。
    /// </summary>
    [Fact]
    public void Update_MatchesUnwatchedNamesAgainstClauseNames_NotSourceIds()
    {
        TriggerDefinition composite = Compose([Multi("login"), Simple("b")]).Definition!;

        TriggerDefinition updated = TriggerComposer.Update(composite, null, ["login-2"]).Definition!;

        Assert.True(updated.Clauses[0].Watch);    // login-1
        Assert.False(updated.Clauses[1].Watch);   // login-2 だけが絞るだけになる
        Assert.True(updated.Clauses[2].Watch);    // b

        // Compose の語彙 (元トリガーの id) はここでは通らない
        TriggerCompositionResult bySourceId = TriggerComposer.Update(composite, null, ["login"]);
        Assert.False(bySourceId.IsValid);
        Assert.Contains("login", bySourceId.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_AnUnknownUnwatchedName_IsRefusedNamingTheTypo()
    {
        TriggerCompositionResult result = TriggerComposer.Update(Composite(), null, ["missing"]);

        Assert.False(result.IsValid);
        Assert.Contains("missing", result.Error, StringComparison.Ordinal);
        Assert.Null(result.Definition);
    }

    [Fact]
    public void Update_ANegativePollInterval_IsRefused()
    {
        TriggerCompositionResult result = TriggerComposer.Update(
            Composite(), null, [], TimeSpan.FromSeconds(-1));

        Assert.False(result.IsValid);
        Assert.Null(result.Definition);
    }

    /// <summary>null と 0 は「未設定」— Compose と同じ意味論。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void Update_WithoutAUsablePollInterval_LeavesItUnset(int? seconds)
    {
        TriggerDefinition composite = Composite();
        composite.PollInterval = TimeSpan.FromSeconds(9);

        TriggerCompositionResult result = TriggerComposer.Update(
            composite, null, [], seconds is { } s ? TimeSpan.FromSeconds(s) : null);

        Assert.True(result.IsValid);
        Assert.Null(result.Definition!.PollInterval);
    }

    [Fact]
    public void Update_TrimsTheExpression_AndTreatsBlankAsNone()
    {
        Assert.Equal("a && b", TriggerComposer.Update(Composite(), "  a && b  ", []).Definition!.Expression);
        Assert.Null(TriggerComposer.Update(Composite(), "   ", []).Definition!.Expression);
    }

    [Fact]
    public void Update_ABrokenExpression_PassesTheValidatorsReasonThrough()
    {
        TriggerCompositionResult result = TriggerComposer.Update(Composite(), "a &&", []);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
        Assert.Null(result.Definition);
    }

    [Fact]
    public void Update_ANonComposite_Throws()
    {
        Assert.Throws<ArgumentException>(
            "composite", () => TriggerComposer.Update(Simple("x"), null, []));
    }

    /// <summary>
    /// 句が 2 つあるだけの PropertyChanged トリガーは「複合」の形に合致するが、
    /// 立ち下がり通知は WhileMatching 専用なので、書き込むと監視が受け付けない定義になる。
    /// 黙って On を書き換えるのも論外なので断る。
    /// </summary>
    [Fact]
    public void Update_ACompositeThatIsNotWhileMatching_Throws()
    {
        TriggerDefinition twoClause = Multi("x");
        Assert.Equal(TriggerOn.PropertyChanged, twoClause.On);

        Assert.Throws<ArgumentException>(
            "composite", () => TriggerComposer.Update(twoClause, null, []));
    }

    /// <summary>
    /// 書き換えた複合が監視開始まで通ること。Update と CreateRuntime の検証が食い違うと
    /// 「更新できたのに開始できない定義」になる
    /// (<see cref="Compose_WithAPollInterval_ProducesADefinitionTheMonitorAccepts"/> と同じ線)。
    /// </summary>
    [Fact]
    public async Task Update_AlwaysProducesADefinitionTheMonitorAccepts()
    {
        TriggerDefinition composite = Compose(
            [Simple("no-such-process-uiatrigger-a"), Simple("no-such-process-uiatrigger-b")])
            .Definition!;
        TriggerDefinition updated = TriggerComposer.Update(
            composite,
            null,
            [],
            TimeSpan.FromMilliseconds(500),
            notifyOnStoppedMatching: true).Definition!;

        await using var monitor = new UiaTrigger.Monitoring.TriggerMonitor();
        Exception? error = await Record.ExceptionAsync(
            () => monitor.StartAsync([updated], TestContext.Current.CancellationToken));

        Assert.Null(error);
    }

    // ---- UnwatchedNames ----

    [Fact]
    public void UnwatchedNames_ReturnsOnlyTheNarrowingClauses_InClauseOrder()
    {
        TriggerDefinition composite = Compose(
            [Multi("login"), Simple("b"), Simple("c")], unwatchedNames: ["login", "c"]).Definition!;

        Assert.Equal(["login-1", "login-2", "c"], TriggerComposer.UnwatchedNames(composite));
    }

    /// <summary>名前の無い句は位置由来の名前で返る (式がその句を指す名前と同じ)。</summary>
    [Fact]
    public void UnwatchedNames_UnnamedClauses_ComeBackWithTheirPositionalNames()
    {
        TriggerDefinition composite = Composite();
        composite.Expression = null;
        composite.Clauses[0].Name = null;
        composite.Clauses[1].Name = null;

        Assert.Equal(["c2"], TriggerComposer.UnwatchedNames(composite));
    }

    [Fact]
    public void UnwatchedNames_NothingNarrows_ComesBackEmpty()
        => Assert.Empty(TriggerComposer.UnwatchedNames(Compose([Simple("a"), Simple("b")]).Definition!));

    // ---- 往復 ----

    /// <summary>
    /// **恒等** (docs/DESIGN.md C17)。複合の設定を読み出してそのまま書き戻すと何も変わらないこと。
    /// これが崩れると、エディタで複合を選んで何も直さずに [更新] を押しただけで定義が変わる。
    /// 題材は複数句・絞るだけ・式・ポーリング間隔・立ち下がり通知の全部入りにする。
    /// </summary>
    [Fact]
    public void ReadingACompositesSettingsAndWritingThemBack_ChangesNothing()
    {
        TriggerDefinition composite = Compose(
            [Multi("login"), Simple("b")],
            "(login-1 && login-2) || b",
            unwatchedNames: ["login"],
            pollInterval: TimeSpan.FromSeconds(2),
            notifyOnStoppedMatching: true).Definition!;
        string before = Json(composite);

        TriggerCompositionResult result = TriggerComposer.Update(
            composite,
            composite.Expression,
            TriggerComposer.UnwatchedNames(composite),
            composite.PollInterval,
            composite.NotifyOnStoppedMatching);

        Assert.True(result.IsValid);
        Assert.Equal(before, Json(result.Definition!));
    }

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
