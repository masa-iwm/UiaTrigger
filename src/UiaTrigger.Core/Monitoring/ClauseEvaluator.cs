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
    /// <summary>
    /// 句のスロットに要素が居ない (未解決、または消えた)。
    ///
    /// <see cref="IsSupported"/> とは独立の軸である — 消えた要素の**値**は最後に見えた
    /// スナップショットから読め続ける (入れ替わりで値の述語が揺れないため) が、
    /// 「在ること」だけを見る <see cref="ComparisonOp.Always"/> はここで落ちる。
    /// パターン非対応 (要素は居るが値が無い) と混ぜないこと。
    /// </summary>
    public bool IsAbsent { get; init; }

    /// <summary>同じ値のまま「要素は居ない」と印を付ける。</summary>
    public ClauseValue AsAbsent() => this with { IsAbsent = true };

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
    /// <param name="combine">句の結合方法。</param>
    /// <param name="clauses">評価する句。</param>
    /// <param name="read">
    /// **句の添字**を受け取り、その句が読む値を返す。
    /// <para>
    /// 句そのものを渡さないのは、呼び出し先が位置を復元できなければならないためである。
    /// 同じ <see cref="PropertyClause"/> インスタンスが 2 度並んだ定義は作れる
    /// (<c>TriggerDefinition.Clauses</c> は公開の可変リストである) ので、参照同値で引き直すと
    /// **2 つめが常に 1 つめの位置に解決される**。位置はここで分かっているのだから渡す。
    /// </para>
    /// </param>
    /// <param name="onClauseMatched">
    /// 句の成否が決まるたびに (添字, 成否) で呼ばれる。発火時に述語を当て直さずに
    /// 「評価時の成否」をイベントへ載せるための口 — 当て直すと正規表現の制限時間切れが
    /// 評価時と別の答えになりうる。省略可 (null なら何もしない)。
    /// </param>
    public static bool Matches(
        ClauseCombinator combine, IReadOnlyList<CompiledClause> clauses, Func<int, ClauseValue> read,
        Action<int, bool>? onClauseMatched = null)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        ArgumentNullException.ThrowIfNull(read);
        if (clauses.Count == 0)
        {
            return true;
        }

        for (int i = 0; i < clauses.Count; i++)
        {
            bool matched = MatchesClause(clauses[i], read(i));
            onClauseMatched?.Invoke(i, matched);
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
    /// <param name="expression">解析済みの式。</param>
    /// <param name="clauses">評価する句。</param>
    /// <param name="read"><inheritdoc cref="Matches(ClauseCombinator, IReadOnlyList{CompiledClause}, Func{int, ClauseValue}, Action{int, bool})" path="/param[@name='read']"/></param>
    /// <param name="onClauseMatched"><inheritdoc cref="Matches(ClauseCombinator, IReadOnlyList{CompiledClause}, Func{int, ClauseValue}, Action{int, bool})" path="/param[@name='onClauseMatched']"/></param>
    public static bool Matches(
        ClauseExpressionNode expression, IReadOnlyList<CompiledClause> clauses, Func<int, ClauseValue> read,
        Action<int, bool>? onClauseMatched = null)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(clauses);
        ArgumentNullException.ThrowIfNull(read);
        // 短絡は木の側にある。ここで全句を先に評価してはならない —
        // Custom は 1 句あたりクロスプロセス呼び出しが 1 回かかる
        return expression.Evaluate(i =>
        {
            bool matched = MatchesClause(clauses[i], read(i));
            onClauseMatched?.Invoke(i, matched);
            return matched;
        });
    }

    /// <summary>
    /// 1 句の評価。
    ///
    /// **読めなかったものは否定形も含めて不成立にする** (docs/DESIGN.md A26)。2 つある:
    /// 要素がそのプロパティを持たない場合 (<see cref="ClauseValue.IsSupported"/>) と、
    /// 演算子の内部で評価に失敗した場合 (正規表現の制限時間切れ) である。
    /// どちらも「値が無い」を「NotEquals / RegexNotMatch が成立する」と解釈すると、
    /// 対象に対して常時発火するトリガーが黙って出来上がる。
    /// </summary>
    public static bool MatchesClause(CompiledClause compiled, ClauseValue value)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        PropertyClause clause = compiled.Clause;
        if (clause.Op == ComparisonOp.Always)
        {
            // Always は値を見ないが、**在否は見る** — 「その要素が在ること」を意味する句
            // (TriggerComposer が句なしトリガーから作る presence 句) の正体である。
            // パターン非対応 (IsSupported=false でも要素は居る) では従来どおり成立する
            return !value.IsAbsent;
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

            // **「評価できなかった」は否定形も含めて不成立** (docs/DESIGN.md A26)。
            // null を `!` で反転させると、パターンが走れなかった周に RegexNotMatch が
            // **成立**する — 上の !IsSupported と同じ規則をここでも守る
            case ComparisonOp.RegexMatch:
                return IsRegexMatch(compiled, value) == true;
            case ComparisonOp.RegexNotMatch:
                return IsRegexMatch(compiled, value) == false;

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

    /// <summary>
    /// パターンを当てる。**null は「一致しなかった」ではなく「評価できなかった」** である
    /// (docs/DESIGN.md A26)。
    /// </summary>
    /// <remarks>
    /// 2 つある: 制限時間を使い切った (<see cref="RegexMatchTimeoutException"/>) と、
    /// パターンがコンパイルされていない。後者は <c>CompileClause</c> が到達不能にしているが、
    /// 規則としてはここで閉じておく — false に潰すと、呼び出し側が `!` を掛けた瞬間に
    /// 「読めなかった」が「否定形は成立」へ化ける。
    /// </remarks>
    private static bool? IsRegexMatch(CompiledClause compiled, ClauseValue value)
    {
        if (compiled.Regex is null)
        {
            return null;
        }
        try
        {
            return compiled.Regex.IsMatch(value.Text.Value);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }
}
