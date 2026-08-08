// 定義の写し・正規化・全域検証の単一点 (docs/DESIGN.md C19 / C20)。
//
// 検証が入口ごと (CreateRuntime / TriggerDraftValidator / TriggerComposer / TriggerStore.Load)
// に分散すると、それぞれが検査する部分集合が食い違い、最も寛容な入口 — STJ の
// デシリアライズ — が事実上の門になる。手編集 JSON は §12 が公開契約と定める正規の入口
// なので、「どの入口から入って受理された定義も AddAsync まで通る」を単一の関数で支える。
//
// **規則を足すときはここに足すこと。**CreateRuntime にも同じ検査が構築の都合で残っている
// (メッセージの互換と、構築物 = Regex / スロット / 式木を作りながら検査する形のため) が、
// あちらに**だけ**足すと Composer と Load の門が黙って緩む。
using System.Text.Json;
using System.Text.RegularExpressions;
using UiaTrigger.Monitoring;
using UiaTrigger.Resources;
using UiaTrigger.Serialization;

namespace UiaTrigger.Models;

internal static class TriggerDefinitionRules
{
    /// <summary>
    /// 定義の深い写し (docs/DESIGN.md C19)。監視は呼び出し元の可変 POCO を参照のまま
    /// 保持しない — 追加後にホストが書き換えた値が UIA スレッドから live に読まれる形は、
    /// データ競合と「検証を通らない値が黙って効く」の両方を生む。
    /// </summary>
    /// <remarks>
    /// JSON 往復なのは手写しがモデルの成長で黙って腐るため (TriggerComposer.Update と同じ判断)。
    /// WhenWritingNull により、宣言上 non-null の文字列に紛れ込んだ null (手編集 JSON 由来) は
    /// 既定値へ戻る — 写しと null 正規化を 1 手で行う。
    /// </remarks>
    public static TriggerDefinition Clone(TriggerDefinition definition) =>
        JsonSerializer.Deserialize(
            JsonSerializer.Serialize(definition, TriggerJsonContext.Default.TriggerDefinition),
            TriggerJsonContext.Default.TriggerDefinition)!;

    /// <summary>
    /// 形の検査: 列挙の定義域と、構造の null。理由 (ローカライズ済み) か null を返す。
    /// </summary>
    /// <remarks>
    /// 列挙は <see cref="System.Text.Json.Serialization.JsonStringEnumConverter{T}"/> でも
    /// **裸の整数を既定で受理する**ため、"Op": 99 の定義がデシリアライズを素通りする。
    /// 域外の列挙は例外を出さずに「鳴らないトリガー / Any 扱い」へ化けるので、ここで名指しで弾く。
    /// </remarks>
    public static string? CheckShape(TriggerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        string id = definition.Id ?? string.Empty;
        if (string.IsNullOrEmpty(id))
        {
            return Strings.Error_TriggerIdRequired;
        }
        if (definition.Window is null)
        {
            return Message.Format(Strings.Error_DefinitionShapeInvalid, id, nameof(definition.Window));
        }
        if (definition.Locator is null)
        {
            return Message.Format(Strings.Error_DefinitionShapeInvalid, id, nameof(definition.Locator));
        }
        if (definition.Clauses is null)
        {
            return Message.Format(Strings.Error_DefinitionShapeInvalid, id, nameof(definition.Clauses));
        }
        if (!Enum.IsDefined(definition.On))
        {
            return Undefined(id, nameof(definition.On), (int)definition.On);
        }
        if (!Enum.IsDefined(definition.Combine))
        {
            return Undefined(id, nameof(definition.Combine), (int)definition.Combine);
        }
        if (CheckWindowShape(id, definition.Window, nameof(definition.Window)) is { } windowReason)
        {
            return windowReason;
        }
        if (CheckLocatorShape(id, definition.Locator, nameof(definition.Locator)) is { } locatorReason)
        {
            return locatorReason;
        }
        for (int i = 0; i < definition.Clauses.Count; i++)
        {
            PropertyClause? clause = definition.Clauses[i];
            string path = $"{nameof(definition.Clauses)}[{i}]";
            if (clause is null)
            {
                return Message.Format(Strings.Error_DefinitionShapeInvalid, id, path);
            }
            if (!Enum.IsDefined(clause.Property))
            {
                return Undefined(id, $"{path}.{nameof(clause.Property)}", (int)clause.Property);
            }
            if (!Enum.IsDefined(clause.Op))
            {
                return Undefined(id, $"{path}.{nameof(clause.Op)}", (int)clause.Op);
            }
            if (clause.Window is not null &&
                CheckWindowShape(id, clause.Window, $"{path}.{nameof(clause.Window)}") is { } clauseWindow)
            {
                return clauseWindow;
            }
            if (clause.Locator is not null &&
                CheckLocatorShape(id, clause.Locator, $"{path}.{nameof(clause.Locator)}") is { } clauseLocator)
            {
                return clauseLocator;
            }
        }
        return null;
    }

    /// <summary>
    /// 全域検証: 形 (<see cref="CheckShape"/>) + 意味 (CreateRuntime が受け付ける条件)。
    /// 理由 (ローカライズ済み・CreateRuntime と同じ文言) か null を返す。**投げない** —
    /// 呼び出し口が例外 (AddAsync) にも UI の理由文字列 (Composer) にも変換できる形。
    /// </summary>
    public static string? Validate(TriggerDefinition definition, TimeSpan regexTimeout)
    {
        if (CheckShape(definition) is { } shape)
        {
            return shape;
        }
        string id = definition.Id;

        if (definition.On == TriggerOn.StoppedMatching)
        {
            return Message.Format(Strings.Error_StoppedMatchingIsEventOnly, id);
        }
        if (definition.NotifyOnStoppedMatching && definition.On != TriggerOn.WhileMatching)
        {
            return Message.Format(Strings.Error_NotifyOnStoppedMatchingRequiresWhileMatching, id, definition.On);
        }
        if (definition.MinInterval is { Ticks: < 0 })
        {
            return Message.Format(Strings.Error_MinIntervalNegative, id);
        }
        if (definition.PollInterval is { Ticks: < 0 })
        {
            return Message.Format(Strings.Error_PollIntervalNegative, id);
        }
        if (definition.PollInterval is { Ticks: > 0 } &&
            definition.On is TriggerOn.ElementAppeared or TriggerOn.ElementRemoved)
        {
            return Message.Format(Strings.Error_PollIntervalNotApplicable, id, definition.On);
        }
        if (definition.On is TriggerOn.ElementAppeared or TriggerOn.ElementRemoved &&
            definition.Clauses.Any(c => c.Window is not null || c.Locator is not null))
        {
            return Message.Format(Strings.Error_ClauseElementNotApplicable, id, definition.On);
        }

        // 句名の確定と一意性 (CreateRuntime.ClauseNames と同じ規則)
        var names = new string[definition.Clauses.Count];
        for (int i = 0; i < definition.Clauses.Count; i++)
        {
            string name = ClauseExpression.EffectiveName(definition.Clauses[i], i);
            if (!ClauseExpression.IsValidName(name))
            {
                return Message.Format(Strings.Error_InvalidClauseName, id, name);
            }
            for (int j = 0; j < i; j++)
            {
                if (string.Equals(names[j], name, StringComparison.Ordinal))
                {
                    return Message.Format(Strings.Error_DuplicateClauseName, id, name);
                }
            }
            names[i] = name;
        }

        // 句ごとのオペランド (CreateRuntime.CompileClause と同じ規則)
        int watched = 0;
        foreach (PropertyClause clause in definition.Clauses)
        {
            if (ValidateClauseOperands(id, clause, regexTimeout) is { } operands)
            {
                return operands;
            }
            if (clause.Watch)
            {
                watched++;
            }
        }
        if (definition.On is TriggerOn.PropertyChanged or TriggerOn.WhileMatching && watched == 0)
        {
            return Message.Format(Strings.Error_ClausesRequired, id, definition.On);
        }

        // 実効ウィンドウの要求 (CreateRuntime のスロット検査と同じ集合)。既定のウィンドウは
        // SlotBuilder が既定スロットを作るときだけ検査する — 全句が上書きしている定義の
        // 使われない既定を検査すると、CreateRuntime が受ける定義をここが弾く
        if (definition.Clauses.Count == 0 ||
            definition.On is TriggerOn.ElementAppeared or TriggerOn.ElementRemoved)
        {
            if (ValidateWindowRequirement(id, definition.Window) is { } defaultWindow)
            {
                return defaultWindow;
            }
        }
        foreach (PropertyClause clause in definition.Clauses)
        {
            if (ValidateWindowRequirement(id, clause.Window ?? definition.Window) is { } clauseWindow)
            {
                return clauseWindow;
            }
        }

        // 式 (CreateRuntime.ParseExpression と同じ規則)
        if (!string.IsNullOrWhiteSpace(definition.Expression))
        {
            ClauseExpressionResult parsed = ClauseExpression.Parse(definition.Expression, names);
            if (!parsed.IsValid)
            {
                return Message.Format(Strings.Error_Expression, id, parsed.Error);
            }
            for (int i = 0; i < names.Length; i++)
            {
                if (!parsed.ReferencedIndices.Contains(i))
                {
                    return Message.Format(Strings.Error_UnreferencedClause, id, names[i]);
                }
            }
        }
        return null;
    }

    private static string? ValidateClauseOperands(string id, PropertyClause clause, TimeSpan regexTimeout)
    {
        if (clause.Property == TriggerProperty.Custom && clause.CustomPropertyId == 0)
        {
            return Message.Format(Strings.Error_CustomPropertyIdRequired, id);
        }
        switch (clause.Op)
        {
            case ComparisonOp.Between:
            case ComparisonOp.NotBetween:
                if (clause.Low is null || clause.High is null)
                {
                    return Message.Format(Strings.Error_LowHighRequired, id, clause.Op);
                }
                break;
            case ComparisonOp.GreaterThan:
            case ComparisonOp.LessThan:
            case ComparisonOp.LessOrEqual:
            case ComparisonOp.GreaterOrEqual:
                if (clause.Value is null)
                {
                    return Message.Format(Strings.Error_ValueRequired, id, clause.Op);
                }
                break;
            case ComparisonOp.Equals:
            case ComparisonOp.NotEquals:
                if (clause.Text is null)
                {
                    return Message.Format(Strings.Error_TextRequired, id, clause.Op);
                }
                break;
            case ComparisonOp.RegexMatch:
            case ComparisonOp.RegexNotMatch:
                if (string.IsNullOrEmpty(clause.Text))
                {
                    return Message.Format(Strings.Error_RegexPatternRequired, id, clause.Op);
                }
                try
                {
                    // コンパイルできることだけを確かめて捨てる。実行用の Regex は
                    // CreateRuntime.CompileClause が作る
                    _ = new Regex(
                        clause.Text, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, regexTimeout);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    return Message.Format(Strings.Error_InvalidRegex, id, ex.Message);
                }
                break;
        }
        return null;
    }

    private static string? ValidateWindowRequirement(string id, WindowIdentity window)
    {
        if (string.IsNullOrEmpty(window.ProcessName) && window.ProcessNameMatch == MatchStrength.Required)
        {
            return Message.Format(Strings.Error_ProcessNameRequired, id);
        }
        if (Resolution.WindowCandidateCache.HasUnsatisfiableRequirement(window))
        {
            return Message.Format(Strings.Error_WindowRequirementUnsatisfiable, id);
        }
        return null;
    }

    private static string? CheckWindowShape(string id, WindowIdentity window, string path)
    {
        if (!Enum.IsDefined(window.ProcessNameMatch))
        {
            return Undefined(id, $"{path}.{nameof(window.ProcessNameMatch)}", (int)window.ProcessNameMatch);
        }
        if (!Enum.IsDefined(window.ProcessPathMatch))
        {
            return Undefined(id, $"{path}.{nameof(window.ProcessPathMatch)}", (int)window.ProcessPathMatch);
        }
        if (!Enum.IsDefined(window.ClassNameMatch))
        {
            return Undefined(id, $"{path}.{nameof(window.ClassNameMatch)}", (int)window.ClassNameMatch);
        }
        if (!Enum.IsDefined(window.WindowNameMatch))
        {
            return Undefined(id, $"{path}.{nameof(window.WindowNameMatch)}", (int)window.WindowNameMatch);
        }
        return null;
    }

    private static string? CheckLocatorShape(string id, ElementLocator locator, string path)
    {
        if (!Enum.IsDefined(locator.View))
        {
            return Undefined(id, $"{path}.{nameof(locator.View)}", (int)locator.View);
        }
        if (locator.Steps is null)
        {
            return Message.Format(Strings.Error_DefinitionShapeInvalid, id, $"{path}.{nameof(locator.Steps)}");
        }
        for (int i = 0; i < locator.Steps.Count; i++)
        {
            if (locator.Steps[i] is null)
            {
                return Message.Format(Strings.Error_DefinitionShapeInvalid, id, $"{path}.{nameof(locator.Steps)}[{i}]");
            }
        }
        return null;
    }

    private static string Undefined(string id, string path, int value) =>
        Message.Format(Strings.Error_UndefinedEnumValue, id, path, value);
}
