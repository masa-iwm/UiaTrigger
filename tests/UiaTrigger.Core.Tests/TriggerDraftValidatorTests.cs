using System.Globalization;
using UiaTrigger.Models;
using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// ピッカーの入力検証 (docs/TESTING.md §1 T1)。
///
/// この規則が View (WinUI3 のコントロール) の中に埋まっていると一切テストできず、
/// 「NumberBox が見えているかどうか」で句へ載せる値を決める形になる —
/// <b>表示の都合が永続化される値を左右する</b>。Core に置いてここで固定する。
/// </summary>
public sealed class TriggerDraftValidatorTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static TriggerDraft Draft(ComparisonOp op = ComparisonOp.Always) => new()
    {
        Id = "trigger-1",
        On = TriggerOn.PropertyChanged,
        Property = TriggerProperty.Name,
        Op = op,
    };

    private static TriggerDraftResult Validate(TriggerDraft draft)
        => TriggerDraftValidator.Validate(draft, RegexTimeout);

    // ---- Id ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithoutAnId_Fails(string? id)
    {
        TriggerDraft draft = Draft();
        draft.Id = id;

        Assert.False(Validate(draft).IsValid);
    }

    [Fact]
    public void Apply_TrimsTheId()
    {
        TriggerDraft draft = Draft();
        draft.Id = "  spaced  ";
        var definition = new TriggerDefinition { Id = "old" };

        TriggerDraftResult result = Validate(draft);
        TriggerDraftValidator.Apply(definition, draft, result);

        Assert.Equal("spaced", definition.Id);
    }

    // ---- 数値比較 ----

    [Theory]
    [InlineData(ComparisonOp.GreaterThan)]
    [InlineData(ComparisonOp.LessThan)]
    [InlineData(ComparisonOp.LessOrEqual)]
    [InlineData(ComparisonOp.GreaterOrEqual)]
    [InlineData(ComparisonOp.Between)]
    [InlineData(ComparisonOp.NotBetween)]
    public void Validate_OrderingOpOnANonNumericProperty_Fails(ComparisonOp op)
    {
        TriggerDraft draft = Draft(op);
        draft.Property = TriggerProperty.Name;
        draft.Value = 1;
        draft.Low = 0;
        draft.High = 2;

        Assert.False(Validate(draft).IsValid);
    }

    [Theory]
    [InlineData(TriggerProperty.Value)]
    [InlineData(TriggerProperty.RangeValue)]
    [InlineData(TriggerProperty.RangeValueMinimum)]
    [InlineData(TriggerProperty.RangeValueMaximum)]
    public void Validate_OrderingOpOnANumericProperty_Succeeds(TriggerProperty property)
    {
        TriggerDraft draft = Draft(ComparisonOp.GreaterThan);
        draft.Property = property;
        draft.Value = 1;

        Assert.True(Validate(draft).IsValid);
    }

    [Fact]
    public void Validate_OrderingOpWithoutAValue_Fails()
    {
        TriggerDraft draft = Draft(ComparisonOp.GreaterThan);
        draft.Property = TriggerProperty.RangeValue;

        Assert.False(Validate(draft).IsValid);
    }

    [Fact]
    public void Validate_RangeWithoutBothBounds_Fails()
    {
        TriggerDraft draft = Draft(ComparisonOp.Between);
        draft.Property = TriggerProperty.RangeValue;
        draft.Low = 1;

        Assert.False(Validate(draft).IsValid);
    }

    [Fact]
    public void Validate_RangeWithReversedBounds_Fails()
    {
        TriggerDraft draft = Draft(ComparisonOp.Between);
        draft.Property = TriggerProperty.RangeValue;
        draft.Low = 10;
        draft.High = 1;

        Assert.False(Validate(draft).IsValid);
    }

    /// <summary>境界が等しいのは有効 (幅ゼロの帯。許容差と組み合わせて使う)。</summary>
    [Fact]
    public void Validate_RangeWithEqualBounds_Succeeds()
    {
        TriggerDraft draft = Draft(ComparisonOp.Between);
        draft.Property = TriggerProperty.RangeValue;
        draft.Low = 5;
        draft.High = 5;

        Assert.True(Validate(draft).IsValid);
    }

    [Fact]
    public void Validate_WithANegativeTolerance_Fails()
    {
        TriggerDraft draft = Draft(ComparisonOp.Equals);
        draft.Text = "x";
        draft.Tolerance = -1;

        Assert.False(Validate(draft).IsValid);
    }

    // ---- テキスト / 正規表現 ----

    [Fact]
    public void Validate_EqualsWithoutText_Fails()
        => Assert.False(Validate(Draft(ComparisonOp.Equals)).IsValid);

    /// <summary>空文字は有効な比較対象 (「値が空になったら」を表現できる)。</summary>
    [Fact]
    public void Validate_EqualsWithEmptyText_Succeeds()
    {
        TriggerDraft draft = Draft(ComparisonOp.Equals);
        draft.Text = string.Empty;

        Assert.True(Validate(draft).IsValid);
    }

    [Fact]
    public void Validate_WithAnInvalidRegex_FailsAndSaysWhy()
    {
        TriggerDraft draft = Draft(ComparisonOp.RegexMatch);
        draft.Text = "[unclosed";

        TriggerDraftResult result = Validate(draft);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
    }

    /// <summary>
    /// 監視側が使えない正規表現をここで通してはいけない。
    /// <c>NonBacktracking</c> は後方参照や先読みを受け付けないため、それを付けずに検証すると
    /// 「確定できたのに監視開始で ArgumentException」という食い違いが起きる。
    /// </summary>
    [Fact]
    public void Validate_WithARegexTheMonitorCannotCompile_Fails()
    {
        TriggerDraft draft = Draft(ComparisonOp.RegexMatch);
        draft.Text = "(?<=a)b";   // 後読み: NonBacktracking では使えない

        Assert.False(Validate(draft).IsValid);
    }

    [Fact]
    public void Validate_WithAValidRegex_Succeeds()
    {
        TriggerDraft draft = Draft(ComparisonOp.RegexMatch);
        draft.Text = "^ready$";

        Assert.True(Validate(draft).IsValid);
    }

    // ---- 句の組み立て ----

    /// <summary>
    /// 使わない欄の値は句に載らないこと。
    ///
    /// 「NumberBox が見えているか」(表示の状態) で永続化される値を決めると、
    /// 演算子を切り替えたあとの残り値が保存され、読み直すと UI と食い違う。
    /// </summary>
    [Fact]
    public void Validate_DoesNotCarryOperandsTheOperatorDoesNotUse()
    {
        TriggerDraft draft = Draft(ComparisonOp.Equals);
        draft.Text = "ready";
        // 別の演算子を選んでいたときの残り値
        draft.Value = 99;
        draft.Low = 1;
        draft.High = 2;

        PropertyClause clause = Assert.IsType<PropertyClause>(Validate(draft).Clause);

        Assert.Equal("ready", clause.Text);
        Assert.Null(clause.Value);
        Assert.Null(clause.Low);
        Assert.Null(clause.High);
    }

    [Fact]
    public void Validate_KeepsToleranceOnlyWhereItMeansSomething()
    {
        TriggerDraft numeric = Draft(ComparisonOp.GreaterThan);
        numeric.Property = TriggerProperty.RangeValue;
        numeric.Value = 1;
        numeric.Tolerance = 0.5;

        TriggerDraft textual = Draft(ComparisonOp.RegexMatch);
        textual.Text = "x";
        textual.Tolerance = 0.5;

        Assert.Equal(0.5, Validate(numeric).Clause!.Tolerance);
        Assert.Equal(0, Validate(textual).Clause!.Tolerance);
    }

    /// <summary>
    /// 出現・削除だけを見るトリガーは句なしにできること。
    /// ライフサイクルと値の述語が同じ列挙にあると、これは表現できない。
    /// </summary>
    [Theory]
    [InlineData(TriggerOn.ElementAppeared)]
    [InlineData(TriggerOn.ElementRemoved)]
    public void Validate_ForALifecycleTriggerWithNoCondition_ProducesNoClause(TriggerOn on)
    {
        TriggerDraft draft = Draft();
        draft.On = on;

        Assert.Null(Validate(draft).Clause);
    }

    /// <summary>逆に、条件を付ければライフサイクルでも句が載ること (「出現し、かつ X のとき」)。</summary>
    [Fact]
    public void Validate_ForALifecycleTriggerWithACondition_ProducesAClause()
    {
        TriggerDraft draft = Draft(ComparisonOp.Equals);
        draft.On = TriggerOn.ElementAppeared;
        draft.Text = "ready";

        Assert.NotNull(Validate(draft).Clause);
    }

    /// <summary>PropertyChanged は条件なしでも句が要る (購読するプロパティが決まらないため)。</summary>
    [Theory]
    [InlineData(TriggerOn.PropertyChanged)]
    [InlineData(TriggerOn.WhileMatching)]
    public void Validate_ForAPropertyTrigger_AlwaysProducesAClause(TriggerOn on)
    {
        TriggerDraft draft = Draft();
        draft.On = on;

        Assert.NotNull(Validate(draft).Clause);
    }

    // ---- レート制限 ----

    [Theory]
    [InlineData(null, null)]
    [InlineData(0.0, null)]
    [InlineData(-1.0, null)]
    [InlineData(1.5, 1.5)]
    public void Validate_TurnsANonPositiveIntervalIntoNoLimit(double? seconds, double? expected)
    {
        TriggerDraft draft = Draft();
        draft.MinIntervalSeconds = seconds;

        TimeSpan? interval = Validate(draft).MinInterval;

        Assert.Equal(expected is null ? null : TimeSpan.FromSeconds(expected.Value), interval);
    }

    // ---- 定義への書き戻し ----

    [Fact]
    public void Apply_ReplacesTheClauseListRatherThanAppending()
    {
        var definition = new TriggerDefinition { Id = "old" };
        definition.Clauses.Add(new PropertyClause { Property = TriggerProperty.HelpText });

        TriggerDraft draft = Draft(ComparisonOp.Equals);
        draft.Text = "ready";
        TriggerDraftValidator.Apply(definition, draft, Validate(draft));

        PropertyClause only = Assert.Single(definition.Clauses);
        Assert.Equal(TriggerProperty.Name, only.Property);
        Assert.Equal(ClauseCombinator.All, definition.Combine);
    }

    /// <summary>
    /// 句を置き換えるなら式も落とすこと (docs/DESIGN.md §4)。
    ///
    /// <para>
    /// <see cref="TriggerDraftValidator.Apply"/> の doc は "replacing its clauses" と言っているのに
    /// 式だけ残ると、定義を使い回したホストで<b>もう存在しない句を指す式</b>が生き残る。
    /// 症状は監視開始時の <see cref="ArgumentException"/> であって、書き換えた瞬間には何も起きない。
    /// </para>
    /// <para>
    /// ピッカーは式を作らないので、この 1 行は消してもビルドが通り他のテストも落ちない。
    /// <c>HostMonitorTests</c> と同じ「腐る側」なので、ここで縛る。
    /// </para>
    /// </summary>
    [Fact]
    public void Apply_ClearsTheExpressionAlongWithTheClauses()
    {
        var definition = new TriggerDefinition { Id = "old", Expression = "a && b" };
        definition.Clauses.Add(new PropertyClause { Name = "a", Property = TriggerProperty.HelpText });
        definition.Clauses.Add(new PropertyClause { Name = "b", Property = TriggerProperty.Value });

        TriggerDraft draft = Draft(ComparisonOp.Equals);
        draft.Text = "ready";
        TriggerDraftValidator.Apply(definition, draft, Validate(draft));

        Assert.Null(definition.Expression);
    }

    /// <summary>
    /// <b>録り直しで <c>On</c> が変わったとき、意味を失うポーリングも落とすこと。</b>
    ///
    /// <para>
    /// 下書きは <c>PollInterval</c> を持たないので、録り直しても既存の値は残る
    /// (<c>Window</c> / <c>Locator</c> と同じで、それ自体は望ましい)。
    /// だが <c>On</c> だけは上書きされるので、<b>「ポーリング付きのまま
    /// <see cref="TriggerOn.ElementAppeared"/> になった定義」</b>が作れてしまう。
    /// それは <c>TriggerMonitor.CreateRuntime</c> が弾く組み合わせである。
    /// </para>
    /// <para>
    /// <b>症状が出ないのが厄介なところである。</b>ホストの録り直しは
    /// <c>RemoveAsync</c> → <c>AddAsync</c> の投げっぱなしなので、
    /// 画面には何も出ないまま<b>トリガーが消える</b>
    /// (監視中の録り直しが壊れやすい理由 — docs/TESTING.md §2)。
    /// 式を落とすのと同じ理由・同じ場所で落とす。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(TriggerOn.ElementAppeared)]
    [InlineData(TriggerOn.ElementRemoved)]
    public void Apply_WhenTheNewOnCannotPoll_ClearsThePollInterval(TriggerOn on)
    {
        var definition = new TriggerDefinition { Id = "old", PollInterval = TimeSpan.FromMilliseconds(500) };
        TriggerDraft draft = Draft();
        draft.On = on;
        draft.Op = ComparisonOp.Always;

        TriggerDraftValidator.Apply(definition, draft, Validate(draft));

        Assert.Null(definition.PollInterval);
    }

    /// <summary>
    /// 逆に、<c>On</c> がポーリングできる側なら**残す**こと。
    /// 利用者が入れた設定を、句を録り直しただけで黙って捨てない。
    /// </summary>
    [Fact]
    public void Apply_WhenTheNewOnCanPoll_KeepsThePollInterval()
    {
        var definition = new TriggerDefinition { Id = "old", PollInterval = TimeSpan.FromMilliseconds(500) };
        TriggerDraft draft = Draft(ComparisonOp.Equals);
        draft.On = TriggerOn.WhileMatching;
        draft.Text = "ready";

        TriggerDraftValidator.Apply(definition, draft, Validate(draft));

        Assert.Equal(TimeSpan.FromMilliseconds(500), definition.PollInterval);
    }

    /// <summary>
    /// <b>2 つの検証が食い違わないこと</b> — 確定できた下書きから作った定義は、
    /// 必ず監視開始まで通ること。
    ///
    /// <para>
    /// <c>TriggerDraftValidator</c> の冒頭が「片方だけだと<b>確定できたのに開始できない定義</b>が
    /// 作れてしまう」と書いている、その食い違いそのものを縛る。
    /// 上の 2 件は個別の規則を見ているが、こちらは<b>規則が増えたときに腐らない</b>形にしてある —
    /// <c>CreateRuntime</c> に新しい拒否理由を足して <c>Apply</c> 側を忘れると、ここで落ちる。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(TriggerOn.ElementAppeared)]
    [InlineData(TriggerOn.ElementRemoved)]
    [InlineData(TriggerOn.PropertyChanged)]
    [InlineData(TriggerOn.WhileMatching)]
    public async Task Apply_AlwaysProducesADefinitionTheMonitorAccepts(TriggerOn on)
    {
        var definition = new TriggerDefinition
        {
            Id = "recorded",
            // 実在しないプロセス。解決はしないが、検証はすべて通る必要がある
            Window = new WindowIdentity { ProcessName = "no-such-process-uiatrigger.exe" },
            PollInterval = TimeSpan.FromMilliseconds(500),
        };
        TriggerDraft draft = Draft(ComparisonOp.Equals);
        draft.On = on;
        draft.Text = "ready";

        TriggerDraftValidator.Apply(definition, draft, Validate(draft));

        await using var monitor = new TriggerMonitor();
        Exception? error = await Record.ExceptionAsync(
            () => monitor.StartAsync([definition], TestContext.Current.CancellationToken));

        Assert.Null(error);
    }

    // ---- 式の検証 (ピッカーが入力中に理由を返せるようにするための口) ----

    private static PropertyClause Named(string? name) =>
        new() { Name = name, Property = TriggerProperty.Name, Op = ComparisonOp.Always };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateExpression_TreatsABlankExpressionAsNone(string? expression)
    {
        Assert.Null(TriggerDraftValidator.ValidateExpression(expression, [Named("a"), Named("b")]));
    }

    [Fact]
    public void ValidateExpression_AcceptsAWellFormedExpression()
    {
        Assert.Null(TriggerDraftValidator.ValidateExpression("(a || b) && !c", [Named("a"), Named("b"), Named("c")]));
    }

    [Theory]
    [InlineData("a &&")]
    [InlineData("(a")]
    [InlineData("a & b")]
    [InlineData("a && zzz")]
    public void ValidateExpression_RejectsAMalformedExpression(string expression)
    {
        Assert.NotNull(TriggerDraftValidator.ValidateExpression(expression, [Named("a"), Named("b")]));
    }

    /// <summary>式が参照しない句は、解決も購読もされるのに結果に一切効かない。</summary>
    [Fact]
    public void ValidateExpression_RejectsAClauseTheExpressionLeavesOut()
    {
        string? error = TriggerDraftValidator.ValidateExpression("a", [Named("a"), Named("unused")]);

        Assert.NotNull(error);
        Assert.Contains("unused", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateExpression_RejectsDuplicateNames()
    {
        Assert.NotNull(TriggerDraftValidator.ValidateExpression("same", [Named("same"), Named("same")]));
    }

    /// <summary>名前を付けていない句は位置由来の名前 (c1, c2…) で指せること。</summary>
    [Fact]
    public void ValidateExpression_AcceptsPositionalNames()
    {
        Assert.Null(TriggerDraftValidator.ValidateExpression("c1 && !c2", [Named(null), Named(null)]));
    }

    [Theory]
    [InlineData("ready", true)]
    [InlineData("c1", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("a&b", false)]
    [InlineData("(a)", false)]
    public void IsValidClauseName(string? name, bool expected)
    {
        Assert.Equal(expected, TriggerDraftValidator.IsValidClauseName(name));
    }

    [Fact]
    public void Apply_WithAnInvalidDraft_Throws()
    {
        TriggerDraft draft = Draft();
        draft.Id = null;

        Assert.Throws<InvalidOperationException>(
            () => TriggerDraftValidator.Apply(new TriggerDefinition { Id = "x" }, draft, Validate(draft)));
    }

    // ---- ローカライズ ----

    /// <summary>
    /// 検証メッセージがリソース経由であること (ハードコードだと ja-JP でも英語のまま出る)。
    /// 表示文字列は CurrentUICulture に従い、比較・永続化に使う値は影響を受けない (docs/LOCALIZATION.md §3)。
    /// </summary>
    [Fact]
    public void Validate_ReportsTheReasonInTheCurrentUiCulture()
    {
        TriggerDraft draft = Draft();
        draft.Id = null;

        string? english = WithCulture("en-US", () => Validate(draft).Error);
        string? japanese = WithCulture("ja-JP", () => Validate(draft).Error);

        Assert.NotNull(english);
        Assert.NotNull(japanese);
        Assert.NotEqual(english, japanese);
    }

    private static T WithCulture<T>(string name, Func<T> action)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(name);
            return action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
