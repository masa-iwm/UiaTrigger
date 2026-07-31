// ピッカーの入力検証を UI から切り離したもの (docs/TESTING.md §1 T1)。
//
// 検証を UI (TriggerPickerWindow.OnCommit) の中に置くと、WinUI3 のコントロールから
// 直接値を読むことになり単体テストできない。加えて「NumberBox が見えているか」で
// 句に載せる値を決めると、可視性という表示上の都合が永続化される値を左右する。
//
// ここでは「下書き (TriggerDraft) → 検証結果 → PropertyClause」だけを扱う。
// TriggerMonitor.CreateRuntime の検証と重なる部分はあるが、目的が違う:
//   ・ここは **入力中のユーザー**に対して、確定前に理由を返す
//   ・CreateRuntime は **呼び出し元のプログラム**に対して、開始時に例外を投げる
// 二重にしても害が無く、片方だけだと「確定できたのに開始できない定義」が作れてしまう。
using UiaTrigger.Monitoring;
using UiaTrigger.Resources;

namespace UiaTrigger.Models;

/// <summary>The values a picker collects before a <see cref="PropertyClause"/> can be built.</summary>
/// <remarks>
/// Unvalidated input, straight from the user: every field is optional and the combination may be
/// nonsense. <see cref="TriggerDraftValidator.Validate"/> turns it into a clause or a reason.
/// </remarks>
public sealed class TriggerDraft
{
    /// <summary>Identifier for the trigger. Required; trimmed before use.</summary>
    public string? Id { get; set; }

    /// <summary>The lifecycle moment the trigger fires at.</summary>
    public TriggerOn On { get; set; } = TriggerOn.PropertyChanged;

    /// <summary>The property the condition applies to.</summary>
    public TriggerProperty Property { get; set; }

    /// <summary>The comparison to perform.</summary>
    public ComparisonOp Op { get; set; }

    /// <summary>Text operand, for the equality and regular expression operators.</summary>
    public string? Text { get; set; }

    /// <summary>Numeric operand, for the ordering operators.</summary>
    public double? Value { get; set; }

    /// <summary>Lower bound, for the range operators.</summary>
    public double? Low { get; set; }

    /// <summary>Upper bound, for the range operators.</summary>
    public double? High { get; set; }

    /// <summary>Half-width of the band treated as equal when comparing numbers.</summary>
    public double? Tolerance { get; set; }

    /// <summary>Minimum interval between firings, in seconds. Null or non-positive means none.</summary>
    public double? MinIntervalSeconds { get; set; }
}

/// <summary>The outcome of validating a <see cref="TriggerDraft"/>.</summary>
/// <param name="Error">A localized reason the draft cannot be used, or null when it can.</param>
/// <param name="Clause">
/// The clause to add to the definition, or null when the draft asks for no condition at all —
/// valid for a trigger that only watches for the element appearing or disappearing.
/// </param>
/// <param name="MinInterval">The rate limit to apply, or null for none.</param>
public readonly record struct TriggerDraftResult(string? Error, PropertyClause? Clause, TimeSpan? MinInterval)
{
    /// <summary>Whether the draft can be turned into a definition.</summary>
    public bool IsValid => Error is null;
}

/// <summary>
/// Validates the input a picker collects and turns it into the parts of a trigger definition.
/// </summary>
/// <remarks>
/// <para>
/// Public so that a third-party picker gets the same rules as the one in this library, including
/// the ones that are easy to get subtly wrong: which operators need which operand, and that a
/// pattern accepted here also compiles in the monitor.
/// </para>
/// <para>
/// Deciding which input fields to **show** from the same predicates
/// (<see cref="UsesText"/> and friends) matters more than it looks: a picker that instead decides
/// what to store from what happens to be visible ends up persisting whatever was left in a hidden
/// field.
/// </para>
/// </remarks>
public static class TriggerDraftValidator
{
    /// <summary>Whether ordering and range comparisons are meaningful for a property.</summary>
    /// <remarks>
    /// Allowing them elsewhere lets a user build a condition that can never hold, because the value
    /// is text that will not parse as a number.
    /// </remarks>
    public static bool IsNumericProperty(TriggerProperty property) =>
        property is TriggerProperty.Value or TriggerProperty.RangeValue
            or TriggerProperty.RangeValueMinimum or TriggerProperty.RangeValueMaximum;

    /// <summary>Whether an operator compares magnitude or a range.</summary>
    public static bool IsOrderingOp(ComparisonOp op) =>
        op is ComparisonOp.Between or ComparisonOp.NotBetween or ComparisonOp.GreaterThan
            or ComparisonOp.LessThan or ComparisonOp.LessOrEqual or ComparisonOp.GreaterOrEqual;

    /// <summary>Whether this combination needs a clause at all.</summary>
    /// <remarks>
    /// A trigger that only watches for the element appearing or disappearing can have no condition
    /// on any value, so a picker must be able to build a definition with an empty clause list.
    /// </remarks>
    public static bool NeedsClause(TriggerOn on, ComparisonOp op) =>
        on is TriggerOn.PropertyChanged or TriggerOn.WhileMatching || op != ComparisonOp.Always;

    /// <summary>Whether an operator takes <see cref="TriggerDraft.Text"/>. Show a text box for these.</summary>
    public static bool UsesText(ComparisonOp op) =>
        op is ComparisonOp.Equals or ComparisonOp.NotEquals
            or ComparisonOp.RegexMatch or ComparisonOp.RegexNotMatch;

    /// <summary>Whether an operator takes <see cref="TriggerDraft.Value"/>.</summary>
    public static bool UsesValue(ComparisonOp op) =>
        op is ComparisonOp.GreaterThan or ComparisonOp.LessThan
            or ComparisonOp.LessOrEqual or ComparisonOp.GreaterOrEqual;

    /// <summary>Whether an operator takes <see cref="TriggerDraft.Low"/> and <see cref="TriggerDraft.High"/>.</summary>
    public static bool UsesRange(ComparisonOp op) =>
        op is ComparisonOp.Between or ComparisonOp.NotBetween;

    /// <summary>Whether <see cref="TriggerDraft.Tolerance"/> affects an operator's outcome.</summary>
    public static bool UsesTolerance(ComparisonOp op) =>
        UsesRange(op) || UsesValue(op) || op is ComparisonOp.Equals or ComparisonOp.NotEquals;

    /// <summary>Validates a draft.</summary>
    /// <param name="draft">The values collected from the user.</param>
    /// <param name="regexTimeout">
    /// The regular expression time limit the monitor will use. Passed in so that a pattern accepted
    /// here is one the monitor can also compile.
    /// </param>
    /// <returns>
    /// A result carrying either a localized reason the draft is unusable, or the clause and rate
    /// limit to apply.
    /// </returns>
    public static TriggerDraftResult Validate(TriggerDraft draft, TimeSpan regexTimeout)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (string.IsNullOrWhiteSpace(draft.Id))
        {
            return new TriggerDraftResult(Strings.Draft_IdRequired, null, null);
        }
        if (IsOrderingOp(draft.Op) && !IsNumericProperty(draft.Property))
        {
            return new TriggerDraftResult(Strings.Draft_OrderingNeedsNumericProperty, null, null);
        }

        if (UsesRange(draft.Op))
        {
            if (draft.Low is not { } low || draft.High is not { } high)
            {
                return new TriggerDraftResult(Strings.Draft_RangeRequired, null, null);
            }
            if (low > high)
            {
                return new TriggerDraftResult(Strings.Draft_RangeReversed, null, null);
            }
        }
        else if (UsesValue(draft.Op) && draft.Value is null)
        {
            return new TriggerDraftResult(Strings.Draft_ValueRequired, null, null);
        }

        if (UsesText(draft.Op) && draft.Text is null)
        {
            return new TriggerDraftResult(Strings.Draft_TextRequired, null, null);
        }
        if (draft.Op is ComparisonOp.RegexMatch or ComparisonOp.RegexNotMatch)
        {
            if (string.IsNullOrEmpty(draft.Text))
            {
                return new TriggerDraftResult(Strings.Draft_RegexRequired, null, null);
            }
            try
            {
                // 監視側と同じオプションでコンパイルする。ここで通って開始時に落ちる、が無いように
                _ = new System.Text.RegularExpressions.Regex(
                    draft.Text,
                    System.Text.RegularExpressions.RegexOptions.NonBacktracking |
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                    regexTimeout);
            }
            // NonBacktracking は「文法としては正しいが使えない」構文 (後方参照・先読み) に対して
            // ArgumentException ではなく NotSupportedException を投げる。
            // 片方しか捕まえないと、確定できたのに監視開始で落ちる定義が作れてしまう
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return new TriggerDraftResult(Message.Format(Strings.Draft_RegexInvalid, ex.Message), null, null);
            }
        }
        if (draft.Tolerance is < 0)
        {
            return new TriggerDraftResult(Strings.Draft_ToleranceNegative, null, null);
        }

        // 使わない欄の値は句に載せない。表示の都合 (欄が見えているか) ではなく
        // 演算子で決めること — 見えていない欄の残り値が永続化されると、
        // ファイルを読み直したときに UI と食い違う
        PropertyClause? clause = NeedsClause(draft.On, draft.Op)
            ? new PropertyClause
            {
                Property = draft.Property,
                Op = draft.Op,
                Text = UsesText(draft.Op) ? draft.Text : null,
                Value = UsesValue(draft.Op) ? draft.Value : null,
                Low = UsesRange(draft.Op) ? draft.Low : null,
                High = UsesRange(draft.Op) ? draft.High : null,
                Tolerance = UsesTolerance(draft.Op) ? draft.Tolerance ?? 0 : 0,
            }
            : null;

        TimeSpan? minInterval = draft.MinIntervalSeconds is { } seconds and > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

        return new TriggerDraftResult(null, clause, minInterval);
    }

    /// <summary>Whether a string can be used as a <see cref="PropertyClause.Name"/>.</summary>
    /// <remarks>
    /// A name cannot be empty, and cannot contain whitespace or any of <c>( ) ! &amp; |</c>, because
    /// <see cref="TriggerDefinition.Expression"/> would otherwise read it as two things. Offer this
    /// while the user types rather than after: a name is chosen once and referred to from every
    /// expression that follows.
    /// </remarks>
    public static bool IsValidClauseName(string? name) => ClauseExpression.IsValidName(name);

    /// <summary>
    /// Validates a <see cref="TriggerDefinition.Expression"/> against the clauses it will apply to.
    /// </summary>
    /// <param name="expression">The expression, or null or blank for "combine them all".</param>
    /// <param name="clauses">The clauses of the definition, in order.</param>
    /// <returns>A localized reason the expression cannot be used, or null when it can.</returns>
    /// <remarks>
    /// The monitor performs the same check when the trigger is added; this is here so a picker can
    /// say why while the expression is still being typed, which is the one place a syntax error is
    /// cheap to fix. It also reports a clause the expression never refers to — such a clause is
    /// still resolved and subscribed to while never affecting the outcome.
    /// </remarks>
    public static string? ValidateExpression(string? expression, IReadOnlyList<PropertyClause> clauses)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        var names = new string[clauses.Count];
        for (int i = 0; i < clauses.Count; i++)
        {
            names[i] = ClauseExpression.EffectiveName(clauses[i], i);
            if (!ClauseExpression.IsValidName(names[i]))
            {
                return Message.Format(Strings.Draft_InvalidClauseName, names[i]);
            }
            for (int j = 0; j < i; j++)
            {
                if (string.Equals(names[j], names[i], StringComparison.Ordinal))
                {
                    return Message.Format(Strings.Draft_DuplicateClauseName, names[i]);
                }
            }
        }

        ClauseExpressionResult parsed = ClauseExpression.Parse(expression, names);
        if (!parsed.IsValid)
        {
            return parsed.Error;
        }
        for (int i = 0; i < names.Length; i++)
        {
            if (!parsed.ReferencedIndices.Contains(i))
            {
                return Message.Format(Strings.Draft_UnreferencedClause, names[i]);
            }
        }
        return null;
    }

    /// <summary>Writes a validated draft into a definition, replacing its clauses.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="result"/> is not valid.</exception>
    public static void Apply(TriggerDefinition definition, TriggerDraft draft, in TriggerDraftResult result)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(draft);
        if (!result.IsValid)
        {
            throw new InvalidOperationException(result.Error);
        }

        definition.Id = draft.Id!.Trim();
        definition.On = draft.On;
        definition.Combine = ClauseCombinator.All;
        definition.Clauses.Clear();
        // 式も落とすこと。doc は "replacing its clauses" と言っているのに式だけ残ると、
        // 定義を使い回したホストで「もう存在しない句を指す式」が生き残る (docs/DESIGN.md §4)
        definition.Expression = null;
        if (result.Clause is { } clause)
        {
            definition.Clauses.Add(clause);
        }
        definition.MinInterval = result.MinInterval;

        // 新しい On で意味を失うポーリングは落とす。式を落とすのと同じ理由である。
        //
        // 下書きは PollInterval を持たないので、録り直しても既存の値はそのまま残る
        // (Window / Locator と同じ扱いで、これ自体は望ましい — 利用者が入れた設定を
        // 録り直しで黙って捨てない)。だが On だけは上書きされるので、
        // 「PollInterval > 0 のまま On が ElementAppeared になった定義」が作れてしまう。
        // **それは CreateRuntime が弾く組み合わせである** = 確定できたのに監視開始で落ちる。
        // しかもホストの録り直しは RemoveAsync → AddAsync の投げっぱなしなので、
        // 画面には何も出ないままトリガーが消える (監視中の録り直しが壊れやすい理由は
        // docs/TESTING.md §2 の assert 規律に書いてある)
        if (definition.On is TriggerOn.ElementAppeared or TriggerOn.ElementRemoved)
        {
            definition.PollInterval = null;
        }
    }
}
