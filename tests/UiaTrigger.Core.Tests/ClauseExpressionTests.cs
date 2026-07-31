using UiaTrigger.Models;
using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 句を組み合わせるブール式 (docs/DESIGN.md §4)。
///
/// 入れ子を「POCO を入れ子にする」のではなく「1 本の文字列を解析する」ことで得ているので、
/// 構文と優先順位はここで実 UIA 無しに固定できる。木は永続化されないため、
/// 検査すべきは形ではなく **評価の結果**である。
/// </summary>
public sealed class ClauseExpressionTests
{
    private static readonly string[] Abcd = ["a", "b", "c", "d"];

    /// <summary>式を解析し、与えた真理値割り当てで評価する。</summary>
    private static bool Eval(string text, params bool[] values)
    {
        ClauseExpressionResult result = ClauseExpression.Parse(text, Abcd);
        Assert.Null(result.Error);
        return result.Root!.Evaluate(i => values[i]);
    }

    private static string ErrorOf(string text)
    {
        ClauseExpressionResult result = ClauseExpression.Parse(text, Abcd);
        Assert.Null(result.Root);
        Assert.NotNull(result.Error);
        return result.Error;
    }

    // ---------- 演算子 ----------

    [Theory]
    [InlineData("a", true, true)]
    [InlineData("a", false, false)]
    [InlineData("!a", false, true)]
    [InlineData("!a", true, false)]
    [InlineData("!!a", true, true)]
    [InlineData("!!a", false, false)]
    public void Parse_Unary(string text, bool expected, bool a)
    {
        Assert.Equal(expected, Eval(text, a, false, false, false));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void Parse_And(bool expected, bool a, bool b)
    {
        Assert.Equal(expected, Eval("a && b", a, b, false, false));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void Parse_Or(bool expected, bool a, bool b)
    {
        Assert.Equal(expected, Eval("a || b", a, b, false, false));
    }

    // ---------- 優先順位と結合 ----------

    /// <summary>
    /// <c>&amp;&amp;</c> は <c>||</c> より強く結び付く。
    /// これを取り違えると <c>a || b &amp;&amp; c</c> が <c>(a || b) &amp;&amp; c</c> と読まれ、
    /// <c>a</c> だけが成立している場面で黙って不成立になる。
    /// </summary>
    [Fact]
    public void Parse_AndBindsTighterThanOr()
    {
        // a=true, b=true, c=false: a || (b && c) = true / (a || b) && c = false
        Assert.True(Eval("a || b && c", true, true, false, false));

        // a=false, b=true, c=true: 逆向きに区別が付く組み合わせ
        Assert.True(Eval("a || b && c", false, true, true, false));
        Assert.False(Eval("a && b || c", false, true, false, false));
    }

    /// <summary><c>!</c> は <c>&amp;&amp;</c> より強い。<c>!a &amp;&amp; b</c> は <c>(!a) &amp;&amp; b</c>。</summary>
    [Fact]
    public void Parse_NotBindsTighterThanAnd()
    {
        // a=false, b=true: (!a) && b = true / !(a && b) も true なので区別が付かない。
        // a=true, b=false: (!a) && b = false / !(a && b) = true
        Assert.False(Eval("!a && b", true, false, false, false));
    }

    [Fact]
    public void Parse_ParenthesesOverridePrecedence()
    {
        // a=true, b=true, c=false
        Assert.False(Eval("(a || b) && c", true, true, false, false));
        Assert.True(Eval("(a || b) && !c", true, true, false, false));
    }

    /// <summary>二項演算子は左結合。3 項以上を並べても評価は変わらない。</summary>
    [Fact]
    public void Parse_BinaryOperatorsChain()
    {
        Assert.True(Eval("a && b && c", true, true, true, false));
        Assert.False(Eval("a && b && c", true, true, false, false));
        Assert.True(Eval("a || b || c", false, false, true, false));
        Assert.False(Eval("a || b || c", false, false, false, false));
    }

    [Fact]
    public void Parse_IgnoresWhitespace()
    {
        Assert.True(Eval("  (a||b)   &&\t!c  ", true, false, false, false));
    }

    // ---------- 短絡 ----------

    /// <summary>
    /// 評価されない側の句は読まない。
    /// <see cref="TriggerProperty.Custom"/> は評価のたびにクロスプロセス呼び出しが 1 回走るので、
    /// これは費用に直結する。
    /// </summary>
    [Fact]
    public void Evaluate_ShortCircuits()
    {
        List<int> read = [];
        bool Holds(int i)
        {
            read.Add(i);
            return i == 0;
        }

        // a が成立するので || の右は読まない
        ClauseExpressionResult or = ClauseExpression.Parse("a || b", Abcd);
        Assert.True(or.Root!.Evaluate(Holds));
        Assert.Equal([0], read);

        // b が不成立なので && の右は読まない
        read.Clear();
        ClauseExpressionResult and = ClauseExpression.Parse("b && a", Abcd);
        Assert.False(and.Root!.Evaluate(Holds));
        Assert.Equal([1], read);
    }

    // ---------- 参照された句 ----------

    /// <summary>
    /// 参照された句を返す。どの句も参照されていることを呼び出し元が確かめられないと、
    /// 結果に一切効かない句が解決も購読もされたまま黙って残る。
    /// </summary>
    [Fact]
    public void Parse_ReportsReferencedClauses()
    {
        ClauseExpressionResult result = ClauseExpression.Parse("(c || a) && !c", Abcd);
        Assert.Null(result.Error);
        Assert.Equal([2, 0], result.ReferencedIndices);
    }

    // ---------- 弾くもの ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_RejectsAnEmptyExpression(string text)
    {
        Assert.NotNull(ErrorOf(text));
    }

    [Theory]
    [InlineData("a &&")]
    [InlineData("a ||")]
    [InlineData("!")]
    public void Parse_RejectsATrailingOperator(string text)
    {
        Assert.NotNull(ErrorOf(text));
    }

    [Theory]
    [InlineData("(a")]
    [InlineData("((a)")]
    [InlineData("a)")]
    [InlineData("(a))")]
    public void Parse_RejectsUnbalancedParentheses(string text)
    {
        Assert.NotNull(ErrorOf(text));
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("a (b)")]
    public void Parse_RejectsOperandsWithoutAnOperator(string text)
    {
        Assert.NotNull(ErrorOf(text));
    }

    [Fact]
    public void Parse_RejectsAnUnknownName()
    {
        string error = ErrorOf("a && zzz");
        Assert.Contains("zzz", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// 単独の <c>&amp;</c> / <c>|</c> は「そんな句は無い」ではなく書き方を示す。
    /// 打ち間違いとして一番多く、名前として読ませると原因の分からないエラーになる。
    /// </summary>
    [Theory]
    [InlineData("a & b", "&&")]
    [InlineData("a | b", "||")]
    [InlineData("& a", "&&")]
    [InlineData("| a", "||")]
    public void Parse_RejectsASingleOperatorCharacterWithAdvice(string text, string advice)
    {
        string error = ErrorOf(text);
        Assert.Contains(advice, error, StringComparison.Ordinal);
    }

    /// <summary>
    /// 括弧を深く重ねた入力でスタックを溢れさせない。再帰下降なので、
    /// 深さ = スタックの深さである。正規表現に NonBacktracking を選んだのと同じ理由。
    /// </summary>
    [Fact]
    public void Parse_RejectsAnExpressionThatNestsTooDeep()
    {
        int depth = ClauseExpression.MaxDepth + 5;
        string text = new string('(', depth) + "a" + new string(')', depth);

        Assert.NotNull(ErrorOf(text));
    }

    [Fact]
    public void Parse_AcceptsNestingUpToTheLimit()
    {
        // 上限ちょうどは通ること。上限を下回る側で落ちていたら、上の検査は
        // 「深すぎる」ではなく「そもそも括弧が扱えない」を見ていることになる
        int depth = ClauseExpression.MaxDepth - 2;
        string text = new string('(', depth) + "a" + new string(')', depth);

        ClauseExpressionResult result = ClauseExpression.Parse(text, Abcd);
        Assert.Null(result.Error);
        Assert.True(result.Root!.Evaluate(_ => true));
    }

    // ---------- 句の名前 ----------

    [Fact]
    public void EffectiveName_FallsBackToThePosition()
    {
        Assert.Equal("c1", ClauseExpression.EffectiveName(new PropertyClause(), 0));
        Assert.Equal("c3", ClauseExpression.EffectiveName(new PropertyClause(), 2));
        Assert.Equal("ready", ClauseExpression.EffectiveName(new PropertyClause { Name = "ready" }, 0));
    }

    [Theory]
    [InlineData("ready", true)]
    [InlineData("c1", true)]
    [InlineData("日本語", true)]
    [InlineData("a.b-c_1", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData("a&b", false)]
    [InlineData("a|b", false)]
    [InlineData("!a", false)]
    [InlineData("(a)", false)]
    public void IsValidName(string? name, bool expected)
    {
        Assert.Equal(expected, ClauseExpression.IsValidName(name));
    }

    /// <summary>
    /// 位置由来の名前でも指せること。これが通らないと
    /// <see cref="PropertyClause.Name"/> を埋めない限り式が書けなくなる。
    /// </summary>
    [Fact]
    public void Parse_AcceptsPositionalNames()
    {
        string[] names =
        [
            ClauseExpression.EffectiveName(new PropertyClause(), 0),
            ClauseExpression.EffectiveName(new PropertyClause(), 1),
        ];

        ClauseExpressionResult result = ClauseExpression.Parse("c1 && !c2", names);

        Assert.Null(result.Error);
        Assert.True(result.Root!.Evaluate(i => i == 0));
    }
}
