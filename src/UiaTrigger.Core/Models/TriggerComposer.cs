// 複合条件を「組む」「ほどく」規則を UI から切り離したもの (docs/DESIGN.md §4)。
//
// 組む側の規則をホストの UI にだけ置くと、NuGet の利用者から見れば
// 「各自が写経する」しかない約 90 行になる。TriggerDraftValidator と同じ理由で
// Core に置く: 第三者のホストが、ライブラリと同じ規則を同じ口から得られるようにする。
//
// ほどく側 (Decompose) について: まとめる前の元トリガーを削除してしまうと
// 複合はどこからも編集できなくなる — が、句には要素 (Window / Locator) と条件が
// 全部残っているので、句 1 つ = トリガー 1 件として常に取り出せる。
using System.Globalization;
using UiaTrigger.Monitoring;
using UiaTrigger.Resources;

namespace UiaTrigger.Models;

/// <summary>The outcome of combining several triggers into one composite definition.</summary>
/// <param name="Error">A localized reason the triggers cannot be combined, or null when they can.</param>
/// <param name="Definition">The composite definition, or null when <paramref name="Error"/> is set.</param>
public readonly record struct TriggerCompositionResult(string? Error, TriggerDefinition? Definition)
{
    /// <summary>Whether the triggers were combined.</summary>
    public bool IsValid => Error is null;
}

/// <summary>
/// Combines recorded triggers into one composite definition, and takes a composite apart again.
/// </summary>
/// <remarks>
/// Public for the same reason as <see cref="TriggerDraftValidator"/>: a host that offers its own
/// "combine" UI gets the library's rules — clause naming, watch narrowing, expression validation,
/// id numbering — from the same place this library takes them, instead of copying them.
/// </remarks>
public static class TriggerComposer
{
    /// <summary>Combines <paramref name="sources"/> into one composite trigger.</summary>
    /// <param name="sources">The triggers to combine, in order. At least two.</param>
    /// <param name="expression">
    /// Boolean expression over the clause names, or null or blank to require every clause. The
    /// names to use are the clause names after expansion, so a source contributing several clauses
    /// is referred to as <c>id-1</c>, <c>id-2</c> and so on.
    /// </param>
    /// <param name="unwatchedNames">
    /// Ids of the source triggers whose clauses should only narrow: they are required but never
    /// subscribed to, so their changes alone fire nothing. Matched against the source trigger's id
    /// as typed — before the <c>-1</c>/<c>-2</c> expansion the expression uses.
    /// </param>
    /// <param name="existingIds">
    /// Ids already in use. The composite's id is the first <c>composite-N</c> not among them.
    /// </param>
    /// <returns>A result carrying either a localized reason, or the composite definition.</returns>
    /// <remarks>
    /// The sources are left untouched; the composite carries copies of their clauses, each clause
    /// naming its own window and locator, which is what lets one trigger span several applications.
    /// A source with no clause of its own contributes "the element is there". The composite fires
    /// on <see cref="TriggerOn.WhileMatching"/>: once, when the combined condition becomes true.
    /// </remarks>
    public static TriggerCompositionResult Compose(
        IReadOnlyList<TriggerDefinition> sources,
        string? expression,
        IReadOnlyCollection<string> unwatchedNames,
        IEnumerable<string> existingIds)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(unwatchedNames);
        ArgumentNullException.ThrowIfNull(existingIds);

        if (sources.Count < 2)
        {
            return new TriggerCompositionResult(Strings.Compose_NeedsTwo, null);
        }

        // 呼び出し元のコレクションを変異させない。消し込みはこの写しに対して行う
        HashSet<string> unwatched = new(unwatchedNames, StringComparer.Ordinal);

        var clauses = new List<PropertyClause>();
        foreach (TriggerDefinition source in sources)
        {
            // id をそのまま名前に使えないことがある (空白や括弧を含む場合)。
            // そのときは位置由来の名前に落とし、式のほうもそれを指す
            string name = TriggerDraftValidator.IsValidClauseName(source.Id)
                ? source.Id
                : "c" + (clauses.Count + 1).ToString(CultureInfo.InvariantCulture);
            // 消し込みと判定を 1 度で。ここを Remove → 後から Contains の 2 段にすると、
            // 消した名前は二度と見つからず Watch が常に true になる — 実際に起きたことの
            // ある不具合である (docs/DESIGN.md §4)。
            // ループの外で 1 回だけ引くのは、複数句へ展開されても全句を同じ側に置くため
            bool watch = !unwatched.Remove(name);

            // 元のトリガーの条件をそのまま持ち込む。条件を持たない (出現だけを見る) トリガーは
            // 「その要素が在ること」だけを意味する句にする
            List<PropertyClause> from = source.Clauses.Count > 0
                ? source.Clauses
                : [new PropertyClause { Property = TriggerProperty.ControlType, Op = ComparisonOp.Always }];
            for (int i = 0; i < from.Count; i++)
            {
                PropertyClause origin = from[i];
                clauses.Add(new PropertyClause
                {
                    Name = from.Count == 1
                        ? name
                        : name + "-" + (i + 1).ToString(CultureInfo.InvariantCulture),
                    DisplayName = source.DisplayName,
                    // ここが多要素の要点。元のトリガーの要素を句に持たせる
                    Window = origin.Window ?? source.Window,
                    Locator = origin.Locator ?? source.Locator,
                    Watch = watch,
                    Property = origin.Property,
                    CustomPropertyId = origin.CustomPropertyId,
                    Op = origin.Op,
                    Text = origin.Text,
                    Value = origin.Value,
                    Low = origin.Low,
                    High = origin.High,
                    Tolerance = origin.Tolerance,
                });
            }
        }

        if (unwatched.Count > 0)
        {
            return new TriggerCompositionResult(
                Message.Format(Strings.Compose_UnknownName, unwatched.First()), null);
        }

        string? trimmed = string.IsNullOrWhiteSpace(expression) ? null : expression.Trim();
        if (TriggerDraftValidator.ValidateExpression(trimmed, clauses) is { } error)
        {
            return new TriggerCompositionResult(error, null);
        }

        var composite = new TriggerDefinition
        {
            Id = NextCompositeId(existingIds),
            // 既定の要素は先頭の元トリガーに合わせる。全句が要素を上書きしているので
            // 解決には使われないが、モデルとしては空にできない
            Window = sources[0].Window,
            Locator = sources[0].Locator,
            On = TriggerOn.WhileMatching,
            Expression = trimmed,
            Clauses = clauses,
        };
        return new TriggerCompositionResult(null, composite);
    }

    /// <summary>Takes a composite definition apart into one stand-alone trigger per clause.</summary>
    /// <param name="composite">The composite to take apart. It is left untouched.</param>
    /// <param name="existingIds">
    /// Ids already in use. A recovered trigger whose id collides gets <c>-2</c>, <c>-3</c> and so on.
    /// </param>
    /// <returns>One trigger per clause, in clause order.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="composite"/> is not a composite: it has no expression and at most one clause.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This is the way back when the sources a composite was combined from are gone: every clause
    /// still carries its window, its locator and its condition, so editable triggers can always be
    /// recovered. Each recovered trigger's id is the clause's effective name — the same name the
    /// composite's expression referred to — so combining the results again keeps the same names,
    /// and the same expression stays valid.
    /// </para>
    /// <para>
    /// What the composite does not record cannot come back: the lifecycle moment, rate limit and
    /// poll interval of the original sources are gone, so every recovered trigger fires on
    /// <see cref="TriggerOn.WhileMatching"/> with no intervals set. The expression itself is
    /// dropped, and <see cref="PropertyClause.Watch"/> is reset — narrowing only means something
    /// inside a composite.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<TriggerDefinition> Decompose(
        TriggerDefinition composite,
        IEnumerable<string> existingIds)
    {
        ArgumentNullException.ThrowIfNull(composite);
        ArgumentNullException.ThrowIfNull(existingIds);
        if (composite.Expression is null && composite.Clauses.Count <= 1)
        {
            // 利用者向けの理由文字列ではなく例外。呼び出し側 (エディタ) が先に選択をガードする、
            // プログラマ向けの契約である — TriggerDraftValidator.Apply の InvalidOperation と同じ線
            throw new ArgumentException(
                "The definition is not a composite: it has no expression and at most one clause.",
                nameof(composite));
        }

        HashSet<string> used = new(existingIds, StringComparer.Ordinal);
        var recovered = new List<TriggerDefinition>(composite.Clauses.Count);
        for (int i = 0; i < composite.Clauses.Count; i++)
        {
            PropertyClause clause = composite.Clauses[i];
            // id は句の実効名 = 複合の式がこの句を指していた名前。まとめ直すと同じ名前に戻る
            string id = FreeId(ClauseExpression.EffectiveName(clause, i), used);
            used.Add(id);
            recovered.Add(new TriggerDefinition
            {
                Id = id,
                DisplayName = clause.DisplayName,
                Window = clause.Window ?? composite.Window,
                Locator = clause.Locator ?? composite.Locator,
                On = TriggerOn.WhileMatching,
                Clauses =
                [
                    // Name は付けない — 句が 1 つだけの定義に名前は要らない。Watch も既定へ戻す —
                    // 「絞るだけ」は複合の中でだけ意味を持つ
                    new PropertyClause
                    {
                        Property = clause.Property,
                        CustomPropertyId = clause.CustomPropertyId,
                        Op = clause.Op,
                        Text = clause.Text,
                        Value = clause.Value,
                        Low = clause.Low,
                        High = clause.High,
                        Tolerance = clause.Tolerance,
                    },
                ],
            });
        }
        return recovered;
    }

    /// <summary>使われていない composite-N の最小の N を探す。</summary>
    private static string NextCompositeId(IEnumerable<string> existingIds)
    {
        HashSet<string> used = new(existingIds, StringComparer.Ordinal);
        for (int i = 1; ; i++)
        {
            string id = "composite-" + i.ToString(CultureInfo.InvariantCulture);
            if (!used.Contains(id))
            {
                return id;
            }
        }
    }

    /// <summary>その id が空いていればそのまま、塞がっていれば -2, -3… を試す。</summary>
    private static string FreeId(string baseId, HashSet<string> used)
    {
        if (!used.Contains(baseId))
        {
            return baseId;
        }
        for (int n = 2; ; n++)
        {
            string id = baseId + "-" + n.ToString(CultureInfo.InvariantCulture);
            if (!used.Contains(id))
            {
                return id;
            }
        }
    }
}
