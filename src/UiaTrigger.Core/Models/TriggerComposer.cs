// 複合条件を「組む」「ほどく」規則を UI から切り離したもの (docs/DESIGN.md §4)。
//
// 組む側の規則をホストの UI にだけ置くと、NuGet の利用者から見れば
// 「各自が写経する」しかない約 90 行になる。TriggerDraftValidator と同じ理由で
// Core に置く: 第三者のホストが、ライブラリと同じ規則を同じ口から得られるようにする。
//
// ほどく側 (Decompose) について: まとめる前の元トリガーを削除してしまうと
// 複合はどこからも編集できなくなる — が、句には要素 (Window / Locator) と条件が
// 全部残っているので、句 1 つ = トリガー 1 件として常に取り出せる。
//
// 書き換える側 (Update) について: 語彙が Compose と違う (docs/DESIGN.md C17)。
// Compose の unwatchedNames は**元トリガーの id** を指すが、まとめ終えた後に
// 残っているのは句の名前だけである。よって Update と UnwatchedNames は句の実効名で話す。
using System.Globalization;
using System.Text.Json;
using UiaTrigger.Monitoring;
using UiaTrigger.Resources;
using UiaTrigger.Serialization;

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
    /// as typed — before the <c>-1</c>/<c>-2</c> expansion the expression uses. An id that cannot
    /// be a clause name (see <see cref="TriggerDraftValidator.IsValidClauseName"/>) is replaced by
    /// a positional one, <c>c1</c>, <c>c2</c> and so on, and that is what both this and the
    /// expression have to use — the id itself no longer names anything.
    /// </param>
    /// <param name="existingIds">
    /// Ids already in use. The composite's id is the first <c>composite-N</c> not among them.
    /// </param>
    /// <param name="pollInterval">
    /// How often the composite's resolved elements are re-read, or null — the default — for
    /// event-driven monitoring only. Zero also means unset, matching
    /// <see cref="TriggerDefinition.PollInterval"/>; a negative value is a reason not to combine.
    /// </param>
    /// <param name="notifyOnStoppedMatching">
    /// Whether the composite should also report the falling edge
    /// (<see cref="TriggerDefinition.NotifyOnStoppedMatching"/>). Safe to set here because a
    /// composite always fires on <see cref="TriggerOn.WhileMatching"/>, the one lifecycle that flag
    /// applies to.
    /// </param>
    /// <returns>A result carrying either a localized reason, or the composite definition.</returns>
    /// <remarks>
    /// <para>
    /// The sources are left untouched; the composite carries copies of their clauses, each clause
    /// naming its own window and locator, which is what lets one trigger span several applications.
    /// A source with no clause of its own contributes "the element is there". The composite fires
    /// on <see cref="TriggerOn.WhileMatching"/>: once, when the combined condition becomes true.
    /// </para>
    /// <para>
    /// The clauses are copies, but each copy's <see cref="PropertyClause.Window"/> and
    /// <see cref="PropertyClause.Locator"/> are the **same objects** the sources hold — mutating
    /// one afterwards changes both. Add the composite to a monitor and it takes its own copy
    /// (see <see cref="Monitoring.TriggerMonitor.AddAsync"/>); pass it anywhere else and the
    /// sharing is yours to keep in mind.
    /// </para>
    /// <para>
    /// A source that is itself a composite — one carrying an expression, or several clauses
    /// combined with <see cref="ClauseCombinator.Any"/> — is refused. Its expression is not
    /// carried over, so combining it again would silently turn an "or" into an "and". Take it
    /// apart with <see cref="Decompose"/> first and combine the pieces.
    /// </para>
    /// </remarks>
    public static TriggerCompositionResult Compose(
        IReadOnlyList<TriggerDefinition> sources,
        string? expression,
        IReadOnlyCollection<string> unwatchedNames,
        IEnumerable<string> existingIds,
        TimeSpan? pollInterval = null,
        bool notifyOnStoppedMatching = false)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(unwatchedNames);
        ArgumentNullException.ThrowIfNull(existingIds);

        if (sources.Count < 2)
        {
            return new TriggerCompositionResult(Strings.Compose_NeedsTwo, null);
        }
        // 検証は Compose が持つ (docs/DESIGN.md §4)。呼び出し元 (エディタ) に置くと、
        // 自前の「まとめる」UI を持つホストが同じ規則を写経することになる
        if (pollInterval is { Ticks: < 0 })
        {
            return new TriggerCompositionResult(Strings.Compose_PollIntervalNegative, null);
        }

        // 呼び出し元のコレクションを変異させない。消し込みはこの写しに対して行う
        HashSet<string> unwatched = new(unwatchedNames, StringComparer.Ordinal);

        var clauses = new List<PropertyClause>();
        foreach (TriggerDefinition source in sources)
        {
            // 複合を素材にまとめ直すことは許さない (docs/DESIGN.md §4 — 黙って意味が変わる
            // 設定を残さない)。式は持ち込まれず、OR 結合の句は All/式の下で AND に化ける —
            // 元の ∨/¬ が警告なしで消える。先にほどいて (Decompose) 部品をまとめ直すこと
            if (source.Expression is not null ||
                (source.Combine == ClauseCombinator.Any && source.Clauses.Count > 1))
            {
                return new TriggerCompositionResult(
                    Message.Format(Strings.Compose_SourceIsComposite, source.Id), null);
            }
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
            // 0 は「未設定」に倒す — TriggerDefinition.PollInterval の意味論 (Null or Zero =
            // イベント駆動) と揃える。負値は上で拒否済み
            PollInterval = pollInterval is { Ticks: > 0 } ? pollInterval : null,
            // On が WhileMatching で固定なので、ここで立てた旗は必ず監視が受け付ける
            NotifyOnStoppedMatching = notifyOnStoppedMatching,
        };
        // 完成品を単一の全域検証 (docs/DESIGN.md C20) に通す。ここで通った複合は AddAsync も
        // 必ず受け付ける — 規則が増えた日に「保存はできるのに監視だけが弾く」複合を作らない
        // (C18 と同じ形の穴の再発防止)
        if (TriggerDefinitionRules.Validate(composite, new TriggerMonitorOptions().RegexTimeout) is { } invalid)
        {
            return new TriggerCompositionResult(invalid, null);
        }
        return new TriggerCompositionResult(null, composite);
    }

    /// <summary>Rewrites a composite's combining settings, leaving what it combines alone.</summary>
    /// <param name="composite">The composite to rewrite. It is left untouched; the result is a copy.</param>
    /// <param name="expression">
    /// Boolean expression over the clause names, or null or blank to require every clause. The names
    /// are the composite's own clause names — the ones its current expression already refers to.
    /// </param>
    /// <param name="unwatchedNames">
    /// Names of the clauses that should only narrow: required but never subscribed to, so their
    /// changes alone fire nothing. Matched against the composite's clause names, which is where this
    /// differs from <see cref="Compose"/> — that one matches source trigger ids, because before
    /// combining that is what exists. <see cref="UnwatchedNames"/> hands back exactly what this
    /// expects.
    /// </param>
    /// <param name="pollInterval"><inheritdoc cref="Compose" path="/param[@name='pollInterval']"/></param>
    /// <param name="notifyOnStoppedMatching">
    /// <inheritdoc cref="Compose" path="/param[@name='notifyOnStoppedMatching']"/>
    /// </param>
    /// <returns>A result carrying either a localized reason, or the rewritten composite.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="composite"/> is not a composite — it has no expression and at most one clause
    /// — or it does not fire on <see cref="TriggerOn.WhileMatching"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Four things are rewritten and no others: the expression, each clause's
    /// <see cref="PropertyClause.Watch"/>, the poll interval and the falling-edge flag. The id, the
    /// clauses' elements and predicates, and the order of the clauses are carried over as they are,
    /// so a caller can put the result back exactly where the original was. Reading a composite's
    /// current settings and handing them straight back changes nothing.
    /// </para>
    /// <para>
    /// This is the only way to set <see cref="TriggerDefinition.NotifyOnStoppedMatching"/> on an
    /// existing composite: the flag needs <see cref="TriggerOn.WhileMatching"/>, which is what every
    /// composite fires on, but a composite is more than one condition and so cannot be taken back
    /// into a picker (see the trigger-list editor's refusal to edit one).
    /// </para>
    /// </remarks>
    public static TriggerCompositionResult Update(
        TriggerDefinition composite,
        string? expression,
        IReadOnlyCollection<string> unwatchedNames,
        TimeSpan? pollInterval = null,
        bool notifyOnStoppedMatching = false)
    {
        ArgumentNullException.ThrowIfNull(composite);
        ArgumentNullException.ThrowIfNull(unwatchedNames);
        if (composite.Expression is null && composite.Clauses.Count <= 1)
        {
            // Decompose と同じプログラマ向けの契約。呼び出し側 (エディタ) が先に選択をガードする
            throw new ArgumentException(
                "The definition is not a composite: it has no expression and at most one clause.",
                nameof(composite));
        }
        // **On を見る理由。**句が 2 つあるだけの PropertyChanged トリガーも「複合」の形には
        // 合致するが、そこへ NotifyOnStoppedMatching を書くと TriggerMonitor が追加時に弾く
        // 定義ができる (立ち下がり通知は WhileMatching 専用 — docs/DESIGN.md C14)。
        // 黙って On を書き換えるのは論外なので、断る
        if (composite.On != TriggerOn.WhileMatching)
        {
            throw new ArgumentException(
                "The definition does not fire on WhileMatching, so its combining settings cannot be rewritten.",
                nameof(composite));
        }
        if (pollInterval is { Ticks: < 0 })
        {
            return new TriggerCompositionResult(Strings.Compose_PollIntervalNegative, null);
        }

        // **写しは JSON の往復で取る。**手で写すと、モデルにプロパティが増えた日に
        // 「更新を通したときだけ」黙って欠ける — しかも保存されたファイルを見るまで分からない
        // (TriggerListEditorPresenter の写しと同じ理由)。source-generated なので AOT でも動く
        TriggerDefinition updated = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(composite, TriggerJsonContext.Default.TriggerDefinition),
            TriggerJsonContext.Default.TriggerDefinition)!;

        // 消し込みと判定を 1 度で (Compose の同じ行と同じ罠を避ける)
        HashSet<string> unwatched = new(unwatchedNames, StringComparer.Ordinal);
        for (int i = 0; i < updated.Clauses.Count; i++)
        {
            updated.Clauses[i].Watch =
                !unwatched.Remove(ClauseExpression.EffectiveName(updated.Clauses[i], i));
        }
        if (unwatched.Count > 0)
        {
            return new TriggerCompositionResult(
                Message.Format(Strings.Compose_UnknownName, unwatched.First()), null);
        }

        string? trimmed = string.IsNullOrWhiteSpace(expression) ? null : expression.Trim();
        if (TriggerDraftValidator.ValidateExpression(trimmed, updated.Clauses) is { } error)
        {
            return new TriggerCompositionResult(error, null);
        }

        // 書き換えるのはここから下の 4 つだけ。Id・Window・Locator・句の要素と述語は写しのまま
        updated.Expression = trimmed;
        updated.PollInterval = pollInterval is { Ticks: > 0 } ? pollInterval : null;
        updated.NotifyOnStoppedMatching = notifyOnStoppedMatching;
        // Compose と同じ理由で完成品を全域検証に通す (docs/DESIGN.md C20)
        if (TriggerDefinitionRules.Validate(updated, new TriggerMonitorOptions().RegexTimeout) is { } invalid)
        {
            return new TriggerCompositionResult(invalid, null);
        }
        return new TriggerCompositionResult(null, updated);
    }

    /// <summary>The names of the clauses of <paramref name="composite"/> that only narrow.</summary>
    /// <param name="composite">The composite to read. It is left untouched.</param>
    /// <returns>
    /// The names of the clauses whose <see cref="PropertyClause.Watch"/> is false, in clause order.
    /// </returns>
    /// <remarks>
    /// The vocabulary <see cref="Update"/> takes, which is not the one <see cref="Compose"/> takes:
    /// a source contributing several clauses is one name to <see cref="Compose"/> (<c>login</c>) and
    /// several to the composite it produced (<c>login-1</c>, <c>login-2</c>), because a combined
    /// trigger no longer records which source a clause came from. Feeding this straight back to
    /// <see cref="Update"/> leaves the composite as it was.
    /// </remarks>
    public static IReadOnlyList<string> UnwatchedNames(TriggerDefinition composite)
    {
        ArgumentNullException.ThrowIfNull(composite);
        var names = new List<string>();
        for (int i = 0; i < composite.Clauses.Count; i++)
        {
            if (!composite.Clauses[i].Watch)
            {
                names.Add(ClauseExpression.EffectiveName(composite.Clauses[i], i));
            }
        }
        return names;
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
    /// <see cref="TriggerOn.WhileMatching"/> with no intervals set. The composite's own poll
    /// interval stays behind too — it paid for re-reading the combined condition, not for any one
    /// clause. The expression itself is dropped, and <see cref="PropertyClause.Watch"/> is reset —
    /// narrowing only means something inside a composite.
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
