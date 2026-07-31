// 条件句の評価 (docs/DESIGN.md §3 / A12 / A13 / L4)。
//
// COM に一切触らない純ロジックとして切り出してある。プロパティ値は呼び出し元が
// ClauseValue にして渡すので、評価規則そのものはスナップショットだけで単体テストできる。
using System.Text.RegularExpressions;
using UiaTrigger.Models;

namespace UiaTrigger.Monitoring;

/// <summary>
/// 評価器に渡す 1 プロパティの値。
/// 文字列は必ず <see cref="ComparisonString"/> (Invariant / Ordinal) であり、
/// 表示用文字列がここへ流れ込むことは型で防いである (docs/DESIGN.md L4)。
/// </summary>
internal readonly record struct ClauseValue(bool IsSupported, ComparisonString Text, double? Number, bool IsBoolean)
{
    /// <summary>要素がそのプロパティを持たない (パターン非対応など)。どの条件も成立しない。</summary>
    public static ClauseValue Unsupported => default;

    public static ClauseValue FromText(ComparisonString text, double? number = null) =>
        new(IsSupported: true, text, number, IsBoolean: false);

    public static ClauseValue FromNumber(double value) =>
        new(IsSupported: true, ComparisonString.FromNumber(value), value, IsBoolean: false);

    public static ClauseValue FromBoolean(bool value) =>
        new(IsSupported: true, ComparisonString.FromBoolean(value), value ? 1 : 0, IsBoolean: true);

    /// <summary>スナップショットから 1 プロパティ分を取り出す。</summary>
    public static ClauseValue From(ElementPropertySnapshot snapshot, TriggerProperty property)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.Supports(property))
        {
            return Unsupported;
        }
        return property switch
        {
            TriggerProperty.IsEnabled => FromBoolean(snapshot.IsEnabled),
            TriggerProperty.IsOffscreen => FromBoolean(snapshot.IsOffscreen),
            _ => FromText(snapshot.GetComparisonValue(property), snapshot.GetNumericValue(property)),
        };
    }
}

/// <summary>検証済み・Regex コンパイル済みの条件句。</summary>
internal sealed class CompiledClause
{
    public required PropertyClause Clause { get; init; }

    /// <summary>正規表現条件のときだけ非 null。</summary>
    public Regex? Regex { get; init; }
}

internal static class ClauseEvaluator
{
    /// <summary>
    /// 句リストを結合して評価する。
    /// 句が 0 個なら「条件なし」= 成立とする (出現・削除だけを見るトリガーで使う)。
    /// </summary>
    public static bool Matches(
        ClauseCombinator combine, IReadOnlyList<CompiledClause> clauses, Func<PropertyClause, ClauseValue> read)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        ArgumentNullException.ThrowIfNull(read);
        if (clauses.Count == 0)
        {
            return true;
        }

        foreach (CompiledClause clause in clauses)
        {
            bool matched = MatchesClause(clause, read(clause.Clause));
            if (combine == ClauseCombinator.All)
            {
                if (!matched)
                {
                    return false;
                }
            }
            else if (matched)
            {
                return true;
            }
        }
        return combine == ClauseCombinator.All;
    }

    /// <summary>
    /// 解析済みの式で評価する (docs/DESIGN.md §4)。
    /// 木は <see cref="ClauseExpression"/> が組み、葉は句の添字である。
    /// </summary>
    public static bool Matches(
        ClauseExpressionNode expression, IReadOnlyList<CompiledClause> clauses, Func<PropertyClause, ClauseValue> read)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(clauses);
        ArgumentNullException.ThrowIfNull(read);
        // 短絡は木の側にある。ここで全句を先に評価してはならない —
        // Custom は 1 句あたりクロスプロセス呼び出しが 1 回かかる
        return expression.Evaluate(i => MatchesClause(clauses[i], read(clauses[i].Clause)));
    }

    /// <summary>
    /// 1 句の評価。
    ///
    /// 要素がそのプロパティを持たない場合は **否定形も含めて** 不成立にする。
    /// 「値が無い」を「NotEquals が成立する」と解釈すると、パターン非対応の要素に対して
    /// 常時発火するトリガーが黙って出来上がるため。
    /// </summary>
    public static bool MatchesClause(CompiledClause compiled, ClauseValue value)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        PropertyClause clause = compiled.Clause;
        if (clause.Op == ComparisonOp.Always)
        {
            return true;
        }
        if (!value.IsSupported)
        {
            return false;
        }

        double tolerance = Math.Abs(clause.Tolerance);
        switch (clause.Op)
        {
            case ComparisonOp.Equals:
                return AreEqual(clause, value, tolerance);
            case ComparisonOp.NotEquals:
                return !AreEqual(clause, value, tolerance);

            case ComparisonOp.Between:
                return IsBetween(clause, value, tolerance);
            case ComparisonOp.NotBetween:
                return value.Number is not null && clause.Low is not null && clause.High is not null
                    && !IsBetween(clause, value, tolerance);

            // Tolerance は「等しいとみなす帯」の半幅。等号を含む演算子は帯の分だけ緩め、
            // 含まない演算子は同じだけ厳しくする (帯の中を「等しい」に統一するため)
            case ComparisonOp.GreaterThan:
                return value.Number is { } gt && clause.Value is { } gtv && gt > gtv + tolerance;
            case ComparisonOp.LessThan:
                return value.Number is { } lt && clause.Value is { } ltv && lt < ltv - tolerance;
            case ComparisonOp.GreaterOrEqual:
                return value.Number is { } ge && clause.Value is { } gev && ge >= gev - tolerance;
            case ComparisonOp.LessOrEqual:
                return value.Number is { } le && clause.Value is { } lev && le <= lev + tolerance;

            case ComparisonOp.RegexMatch:
                return IsRegexMatch(compiled, value);
            case ComparisonOp.RegexNotMatch:
                return !IsRegexMatch(compiled, value);

            default:
                return false;
        }
    }

    /// <summary>
    /// 等値判定。数値同士なら <see cref="PropertyClause.Tolerance"/> 込みの数値比較、
    /// bool なら bool として (大小文字を問わず)、それ以外は Ordinal の文字列比較。
    /// </summary>
    private static bool AreEqual(PropertyClause clause, ClauseValue value, double tolerance)
    {
        // "True"/"true"/"TRUE" のどれを書いても通るようにする。
        // 表示形式の "True" 固定に Ordinal 比較を合わせると、ユーザーが自然に書く
        // "true" が必ず外れる (docs/DESIGN.md A13)
        if (value.IsBoolean && bool.TryParse(clause.Text, out bool expectedBool))
        {
            return (value.Number != 0) == expectedBool;
        }

        if (value.Number is { } number &&
            double.TryParse(clause.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double expected))
        {
            // double の厳密比較は RangeValue のような実数では実用上まず一致しない (A12)
            return Math.Abs(number - expected) <= tolerance;
        }

        return value.Text == ComparisonString.FromDefinition(clause.Text ?? string.Empty);
    }

    private static bool IsBetween(PropertyClause clause, ClauseValue value, double tolerance) =>
        value.Number is { } n && clause.Low is { } low && clause.High is { } high
        && n >= low - tolerance && n <= high + tolerance;

    private static bool IsRegexMatch(CompiledClause compiled, ClauseValue value)
    {
        if (compiled.Regex is null)
        {
            return false;
        }
        try
        {
            return compiled.Regex.IsMatch(value.Text.Value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
