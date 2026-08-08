using System.Globalization;
using System.Text.RegularExpressions;
using UiaTrigger.Models;
using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 条件句の評価 (docs/DESIGN.md §3 / A12 / A13)。
///
/// 評価はスナップショットだけで完結する純ロジックなので、ここは COM を一切使わない。
/// 条件が単一句で、ライフサイクル (Appeared/Removed) と値の述語が同じ列挙に同居する形では
/// 「出現し、かつ値が X」が表現できない。ここではそれが表現できることを固定する。
/// </summary>
public sealed class ClauseEvaluatorTests
{
    private static ElementPropertySnapshot Snapshot(
        string? name = "hello",
        bool isEnabled = true,
        double? rangeValue = null,
        string? value = null) => new()
        {
            Name = name,
            AutomationId = "id",
            ClassName = "Edit",
            ControlType = 50004,
            ControlTypeName = "Edit",
            IsEnabled = isEnabled,
            SupportsValuePattern = value is not null,
            Value = value,
            SupportsRangeValuePattern = rangeValue is not null,
            RangeValue = rangeValue,
            RangeValueMinimum = 0,
            RangeValueMaximum = 100,
        };

    private static CompiledClause Clause(
        TriggerProperty property, ComparisonOp op,
        string? text = null, double? value = null, double? low = null, double? high = null, double tolerance = 0)
    {
        var clause = new PropertyClause
        {
            Property = property, Op = op, Text = text, Value = value, Low = low, High = high, Tolerance = tolerance,
        };
        Regex? regex = op is ComparisonOp.RegexMatch or ComparisonOp.RegexNotMatch
            ? new Regex(text!, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant)
            : null;
        return new CompiledClause { Clause = clause, Regex = regex };
    }

    private static bool Match(ElementPropertySnapshot snapshot, ClauseCombinator combine, params CompiledClause[] clauses) =>
        ClauseEvaluator.Matches(combine, clauses, i => ClauseValue.From(snapshot, clauses[i].Clause.Property));

    private static bool Match(ElementPropertySnapshot snapshot, CompiledClause clause) =>
        Match(snapshot, ClauseCombinator.All, clause);

    // ---------- 式による結合 (docs/DESIGN.md §4) ----------

    private static bool MatchExpression(
        ElementPropertySnapshot snapshot, string expression, params CompiledClause[] clauses)
    {
        string[] names = [.. clauses.Select((_, i) => "c" + (i + 1))];
        ClauseExpressionResult parsed = ClauseExpression.Parse(expression, names);
        Assert.Null(parsed.Error);
        return ClauseEvaluator.Matches(
            parsed.Root!, clauses, i => ClauseValue.From(snapshot, clauses[i].Clause.Property));
    }

    /// <summary>
    /// 平坦な <see cref="ClauseCombinator"/> では表せない形が、式なら書けること。
    /// 「かつ」と「または」が混ざる条件がこれである。
    /// </summary>
    [Fact]
    public void Matches_WithAnExpression_MixesAndWithOr()
    {
        ElementPropertySnapshot snapshot = Snapshot(name: "Ready", isEnabled: false);

        CompiledClause nameIsReady = Clause(TriggerProperty.Name, ComparisonOp.Equals, text: "Ready");
        CompiledClause nameIsIdle = Clause(TriggerProperty.Name, ComparisonOp.Equals, text: "Idle");
        CompiledClause enabled = Clause(TriggerProperty.IsEnabled, ComparisonOp.Equals, text: "true");

        // (Ready または Idle) かつ 有効 → 有効でないので不成立
        Assert.False(MatchExpression(snapshot, "(c1 || c2) && c3", nameIsReady, nameIsIdle, enabled));
        // (Ready または Idle) かつ 無効 → 成立
        Assert.True(MatchExpression(snapshot, "(c1 || c2) && !c3", nameIsReady, nameIsIdle, enabled));
    }

    /// <summary>
    /// 式の否定は句の否定と別物である。要素がそのプロパティを持たないとき、
    /// 句は**否定形も含めて**不成立になる (A13) が、式の <c>!</c> はその結果をひっくり返す。
    /// 両方を同じ規則にまとめようとすると、どちらかが黙って壊れる。
    /// </summary>
    [Fact]
    public void Matches_ExpressionNot_IsNotTheSameAsAClauseLevelNegation()
    {
        // ValuePattern 非対応の要素
        ElementPropertySnapshot snapshot = Snapshot(value: null);
        CompiledClause valueIsX = Clause(TriggerProperty.Value, ComparisonOp.Equals, text: "x");
        CompiledClause valueIsNotX = Clause(TriggerProperty.Value, ComparisonOp.NotEquals, text: "x");

        // 句としての否定は不成立のまま (値が無いのに「x ではない」を成立させない)
        Assert.False(Match(snapshot, valueIsNotX));
        // 式の否定は「その句が成立していない」を意味するので成立する
        Assert.True(MatchExpression(snapshot, "!c1", valueIsX));
    }

    /// <summary>
    /// 評価されない側の句は読まない。<see cref="TriggerProperty.Custom"/> は
    /// 1 句あたりクロスプロセス呼び出しが 1 回かかるので、これは費用に直結する。
    /// </summary>
    [Fact]
    public void Matches_WithAnExpression_ShortCircuits()
    {
        ElementPropertySnapshot snapshot = Snapshot(name: "Ready");
        CompiledClause[] clauses =
        [
            Clause(TriggerProperty.Name, ComparisonOp.Equals, text: "Ready"),
            Clause(TriggerProperty.AutomationId, ComparisonOp.Equals, text: "id"),
        ];
        ClauseExpressionResult parsed = ClauseExpression.Parse("c1 || c2", ["c1", "c2"]);

        List<TriggerProperty> read = [];
        ClauseEvaluator.Matches(parsed.Root!, clauses, i =>
        {
            read.Add(clauses[i].Clause.Property);
            return ClauseValue.From(snapshot, clauses[i].Clause.Property);
        });

        Assert.Equal([TriggerProperty.Name], read);
    }

    /// <summary>
    /// 継ぎ目は**句の添字**を渡すこと。句そのものだと、同じインスタンスが 2 度並んだときに
    /// 位置を復元できない。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TriggerDefinition.Clauses</c> は公開の可変リストなので、同じ
    /// <see cref="PropertyClause"/> を 2 回足した定義は作れる。参照同値で引き直す実装では
    /// 2 つめが常に 1 つめの位置に解決され、<c>TriggerMonitor</c> 側では
    /// その句の <c>ClauseReading</c> が「評価されなかった」のまま配られる。
    /// </para>
    /// <para>
    /// **見ているのは継ぎ目の形である。**句そのものを渡す形へ戻すとコンパイルが通らず、
    /// 「等価に見える」書き換え (添字を <c>IndexOf</c> で作り直す等) は
    /// <c>[0, 1]</c> の assert が実行時に捕まえる。
    /// </para>
    /// </remarks>
    [Fact]
    public void Matches_PassesThePositionOfEachClause_EvenWhenTheSameInstanceRepeats()
    {
        var shared = new PropertyClause
        {
            Property = TriggerProperty.Name, Op = ComparisonOp.Equals, Text = "Ready",
        };
        CompiledClause[] clauses =
        [
            new CompiledClause { Clause = shared },
            new CompiledClause { Clause = shared },
        ];

        List<int> read = [];
        bool matched = ClauseEvaluator.Matches(ClauseCombinator.All, clauses, i =>
        {
            read.Add(i);
            return ClauseValue.From(Snapshot(name: "Ready"), clauses[i].Clause.Property);
        });

        Assert.True(matched);
        Assert.Equal([0, 1], read);
    }

    // ---------- 複数句の結合 ----------

    [Fact]
    public void Matches_WithNoClauses_IsSatisfied()
    {
        Assert.True(Match(Snapshot(), ClauseCombinator.All));
        Assert.True(Match(Snapshot(), ClauseCombinator.Any));
    }

    /// <summary>複数プロパティにまたがる AND。単一句のモデルでは表現できない形。</summary>
    [Fact]
    public void Matches_All_RequiresEveryClause()
    {
        ElementPropertySnapshot snapshot = Snapshot(name: "Ready", isEnabled: true);

        Assert.True(Match(snapshot, ClauseCombinator.All,
            Clause(TriggerProperty.Name, ComparisonOp.Equals, text: "Ready"),
            Clause(TriggerProperty.IsEnabled, ComparisonOp.Equals, text: "true")));

        Assert.False(Match(Snapshot(name: "Ready", isEnabled: false), ClauseCombinator.All,
            Clause(TriggerProperty.Name, ComparisonOp.Equals, text: "Ready"),
            Clause(TriggerProperty.IsEnabled, ComparisonOp.Equals, text: "true")));
    }

    [Fact]
    public void Matches_Any_NeedsOnlyOneClause()
    {
        ElementPropertySnapshot snapshot = Snapshot(name: "Ready", isEnabled: false);

        Assert.True(Match(snapshot, ClauseCombinator.Any,
            Clause(TriggerProperty.Name, ComparisonOp.Equals, text: "Ready"),
            Clause(TriggerProperty.IsEnabled, ComparisonOp.Equals, text: "true")));

        Assert.False(Match(snapshot, ClauseCombinator.Any,
            Clause(TriggerProperty.Name, ComparisonOp.Equals, text: "Other"),
            Clause(TriggerProperty.IsEnabled, ComparisonOp.Equals, text: "true")));
    }

    // ---------- A13: bool の表示文字列 ----------

    /// <summary>
    /// A13。ユーザーが自然に書く "true" が通ること。
    /// bool を "True" と文字列化して Ordinal 比較すると、必ず外れる (A13)。
    /// </summary>
    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    public void Equals_OnABoolean_AcceptsEitherCasing(string text, bool expected)
    {
        Assert.Equal(expected, Match(Snapshot(isEnabled: true), Clause(TriggerProperty.IsEnabled, ComparisonOp.Equals, text: text)));
    }

    /// <summary>比較用文字列としての bool は小文字であること (ログや Regex 条件で見える形)。</summary>
    [Fact]
    public void ComparisonValue_OfABoolean_IsLowerCase()
    {
        Assert.Equal("true", Snapshot(isEnabled: true).GetComparisonValue(TriggerProperty.IsEnabled).Value);
        Assert.Equal("false", Snapshot(isEnabled: false).GetComparisonValue(TriggerProperty.IsEnabled).Value);
    }

    // ---------- A12: 数値の許容差 ----------

    /// <summary>
    /// A12。RangeValue のような double に対する厳密な Equals は実用上まず一致しない。
    /// Tolerance を与えれば一致し、既定 (0) では従来どおり厳密であること。
    /// </summary>
    [Fact]
    public void Equals_OnADouble_HonoursTheTolerance()
    {
        ElementPropertySnapshot snapshot = Snapshot(rangeValue: 42.0000001);

        Assert.False(Match(snapshot, Clause(TriggerProperty.RangeValue, ComparisonOp.Equals, text: "42")));
        Assert.True(Match(snapshot, Clause(TriggerProperty.RangeValue, ComparisonOp.Equals, text: "42", tolerance: 0.001)));
    }

    /// <summary>許容差は「等しいとみなす帯」であり、等号を含む演算子を緩め、含まない演算子を締めること。</summary>
    [Theory]
    [InlineData(ComparisonOp.GreaterOrEqual, 42.5, true)]   // 42 >= 42.5 - 0.6
    [InlineData(ComparisonOp.LessOrEqual, 41.5, true)]      // 42 <= 41.5 + 0.6
    [InlineData(ComparisonOp.GreaterThan, 41.5, false)]     // 42 > 41.5 + 0.6 は不成立
    [InlineData(ComparisonOp.LessThan, 42.5, false)]        // 42 < 42.5 - 0.6 は不成立
    public void OrderingOperators_HonourTheTolerance(ComparisonOp op, double operand, bool expected)
    {
        Assert.Equal(expected, Match(
            Snapshot(rangeValue: 42.0), Clause(TriggerProperty.RangeValue, op, value: operand, tolerance: 0.6)));
    }

    [Fact]
    public void Between_HonoursTheToleranceOnBothBounds()
    {
        Assert.False(Match(Snapshot(rangeValue: 10.5), Clause(TriggerProperty.RangeValue, ComparisonOp.Between, low: 11, high: 20)));
        Assert.True(Match(Snapshot(rangeValue: 10.5), Clause(TriggerProperty.RangeValue, ComparisonOp.Between, low: 11, high: 20, tolerance: 1)));
    }

    // ---------- 非対応プロパティ ----------

    /// <summary>
    /// 要素がそのプロパティを持たない場合、否定形も含めて不成立であること。
    /// 「値が無い」を「NotEquals が成立」と読むと、パターン非対応の要素に対して
    /// 常時発火するトリガーが黙って出来上がる。
    /// </summary>
    [Theory]
    [InlineData(ComparisonOp.Equals)]
    [InlineData(ComparisonOp.NotEquals)]
    [InlineData(ComparisonOp.RegexNotMatch)]
    public void UnsupportedProperty_NeverMatches(ComparisonOp op)
    {
        // ValuePattern 非対応の要素
        Assert.False(Match(Snapshot(value: null), Clause(TriggerProperty.Value, op, text: "x")));
    }

    /// <summary>
    /// 演算子の内部で評価に失敗した場合も、**否定形を含めて**不成立であること
    /// (docs/DESIGN.md A26)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「読めなかった」を <c>false</c> に潰すと、呼び出し側が <c>!</c> を掛けた瞬間に
    /// <see cref="ComparisonOp.RegexNotMatch"/> が**成立**へ化ける。上の
    /// <see cref="UnsupportedProperty_NeverMatches"/> と同じ規則が、
    /// 非対応プロパティだけでなく演算子の内部にも要る。
    /// </para>
    /// <para>
    /// 失敗の作り方は 2 つあるが、制限時間切れは <c>NonBacktracking</c> が線形時間なので
    /// 決定的に起こせない。**未コンパイル (<c>Regex = null</c>) は同じ道を通り、
    /// しかも決定的である** — 製品側では <c>CompileClause</c> が到達不能にしているが、
    /// 規則を固定するのに使えるのはこちらである。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(ComparisonOp.RegexMatch)]
    [InlineData(ComparisonOp.RegexNotMatch)]
    public void AnUnevaluablePattern_NeverMatches(ComparisonOp op)
    {
        var uncompiled = new CompiledClause
        {
            Clause = new PropertyClause { Property = TriggerProperty.Name, Op = op, Text = "hel+o" },
            Regex = null,
        };

        Assert.False(Match(Snapshot(name: "hello"), uncompiled));
    }

    /// <summary>Always だけは「値を見ない」ので、プロパティを購読する意図として常に成立すること。</summary>
    [Fact]
    public void Always_IsSatisfiedEvenForAnUnsupportedProperty()
    {
        Assert.True(Match(Snapshot(value: null), Clause(TriggerProperty.Value, ComparisonOp.Always)));
    }

    // ---------- 在否 (Absent) の軸 (docs/DESIGN.md C16) ----------

    /// <summary>
    /// **Always は要素の不在では成立しないこと。**Always の句は「その要素が在ること」
    /// (presence — TriggerComposer が句なしトリガーから作る句の意味) であり、
    /// 不在でも成立すると、複合の presence 句が何も要求しなくなり、
    /// WhileMatching + Always + NotifyOnStoppedMatching の出現/消滅レシピは
    /// 水準が最初から true になって立ち上がりごと消える。
    ///
    /// パターン非対応 (上のテスト — 要素は居るが値が無い) と混ぜないこと。
    /// あちらは true のまま、こちらだけが false である。
    /// </summary>
    [Fact]
    public void Always_IsNotSatisfiedWhenTheElementIsAbsent()
    {
        CompiledClause clause = Clause(TriggerProperty.Name, ComparisonOp.Always);

        // 消えた要素 (最後の値は残っている) も、一度も現れていない要素も、不在は不在
        Assert.False(ClauseEvaluator.MatchesClause(
            clause, ClauseValue.From(Snapshot(), TriggerProperty.Name).AsAbsent()));
        Assert.False(ClauseEvaluator.MatchesClause(clause, ClauseValue.Unsupported.AsAbsent()));
    }

    /// <summary>
    /// **値の述語は不在の印を無視し、最後に見えた値で評価され続けること。**
    /// ここが不在で落ちる形に変わると、成立したまま要素が入れ替わっただけ
    /// (ツリー再構築) で WhileMatching の水準が揺れ、立ち下がり + 立ち上がりが鳴る洪水になる。
    /// </summary>
    [Fact]
    public void ValuePredicates_KeepUsingTheLastSeenValueWhenAbsent()
    {
        ClauseValue lastSeen = ClauseValue.From(Snapshot(name: "on"), TriggerProperty.Name).AsAbsent();

        Assert.True(ClauseEvaluator.MatchesClause(
            Clause(TriggerProperty.Name, ComparisonOp.Equals, text: "on"), lastSeen));
        Assert.False(ClauseEvaluator.MatchesClause(
            Clause(TriggerProperty.Name, ComparisonOp.Equals, text: "off"), lastSeen));
    }

    // ---------- 文字列比較 ----------

    [Fact]
    public void Equals_OnText_IsOrdinal()
    {
        Assert.True(Match(Snapshot(name: "Ready"), Clause(TriggerProperty.Name, ComparisonOp.Equals, text: "Ready")));
        Assert.False(Match(Snapshot(name: "Ready"), Clause(TriggerProperty.Name, ComparisonOp.Equals, text: "ready")));
    }

    [Fact]
    public void Regex_MatchesAgainstTheComparisonForm()
    {
        Assert.True(Match(Snapshot(name: "Progress: 42%"), Clause(TriggerProperty.Name, ComparisonOp.RegexMatch, text: @"\d+%")));
        Assert.False(Match(Snapshot(name: "Progress"), Clause(TriggerProperty.Name, ComparisonOp.RegexMatch, text: @"\d+%")));
    }

    /// <summary>
    /// L4。条件評価に使う値は現在カルチャに影響されないこと。
    /// de-DE では 1234.5 の既定書式が "1234,5" になるため、比較経路が表示経路に
    /// 引きずられていればここで露見する。
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    [InlineData("de-DE")]
    public void Evaluation_IsUnaffectedByTheCurrentCulture(string cultureName)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            ElementPropertySnapshot snapshot = Snapshot(rangeValue: 1234.5);

            Assert.True(Match(snapshot, Clause(TriggerProperty.RangeValue, ComparisonOp.Equals, text: "1234.5")));
            Assert.True(Match(snapshot, Clause(TriggerProperty.RangeValue, ComparisonOp.RegexMatch, text: @"^1234\.5$")));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
