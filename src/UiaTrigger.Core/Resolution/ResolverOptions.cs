namespace UiaTrigger.Resolution;

/// <summary>Tuning knobs for element resolution.</summary>
/// <remarks>
/// <para>
/// Resolution is a search, not a sum-and-threshold test. A candidate is admitted only if the
/// evidence for it is net positive (its score reaches <see cref="StepAcceptScore"/>); among the
/// admitted candidates the score only decides rank, and the best
/// <see cref="BeamWidth"/> of them are carried forward so a dead end can be backed out of.
/// </para>
/// <para>
/// Folding volatile attributes (a window title), stable ones (a class name) and position (a sibling
/// index) into one score with a per-level threshold on the sum makes a single extra sibling, or one
/// changed class name, permanently unresolvable.
/// </para>
/// </remarks>
public sealed class ResolverOptions
{
    // --- トップレベルウィンドウのランク付け ---
    // Required 指定の属性は WindowCandidateCache が先に足切りする。ここの値は
    // 「残った候補をどの順で試すか」だけを決める。閾値は無い (docs/DESIGN.md A4)。

    /// <summary>Rank bonus when a <see cref="Models.MatchStrength.Preferred"/> process name matches.</summary>
    public int WindowProcessNameScore { get; set; } = 10;

    /// <summary>Rank bonus when a <see cref="Models.MatchStrength.Preferred"/> executable path matches.</summary>
    public int WindowProcessPathScore { get; set; } = 20;

    /// <summary>Rank bonus when a <see cref="Models.MatchStrength.Preferred"/> class name matches.</summary>
    public int WindowClassNameScore { get; set; } = 40;

    /// <summary>Rank bonus when a <see cref="Models.MatchStrength.Preferred"/> window title matches exactly.</summary>
    public int WindowNameExactScore { get; set; } = 25;

    /// <summary>Rank bonus when a <see cref="Models.MatchStrength.Preferred"/> window title matches partially.</summary>
    public int WindowNamePartialScore { get; set; } = 10;

    /// <summary>Maximum number of windows tried per resolution pass.</summary>
    public int MaxWindowCandidates { get; set; } = 16;

    // --- 経路 1 段ごとのスコア ---

    /// <summary>Score for a candidate whose control type matches the recorded one.</summary>
    public int StepControlTypeScore { get; set; } = 40;

    /// <summary>
    /// Penalty for a candidate whose control type differs from the recorded one.
    /// </summary>
    /// <remarks>
    /// A penalty rather than a rejection: some elements change control type with their state
    /// (Text becomes Edit while being edited). Heavy enough that a step identified by nothing else
    /// still fails, light enough that a matching automation id outweighs it.
    /// </remarks>
    public int StepControlTypeMismatchPenalty { get; set; } = -80;

    /// <summary>Score for an exact automation id match.</summary>
    public int StepAutomationIdScore { get; set; } = 100;

    /// <summary>Penalty when the candidate carries a different, non-empty automation id.</summary>
    public int StepAutomationIdMismatchPenalty { get; set; } = -100;

    /// <summary>Penalty when an automation id was recorded but the candidate has none.</summary>
    public int StepAutomationIdMissingPenalty { get; set; } = -20;

    /// <summary>Score for an exact class name match.</summary>
    public int StepClassNameScore { get; set; } = 50;

    /// <summary>Penalty when the candidate carries a different, non-empty class name.</summary>
    public int StepClassNameMismatchPenalty { get; set; } = -50;

    /// <summary>Penalty when a class name was recorded but the candidate has none.</summary>
    public int StepClassNameMissingPenalty { get; set; } = -10;

    /// <summary>Score for an exact name match.</summary>
    public int StepNameExactScore { get; set; } = 40;

    /// <summary>Score when one of the two names contains the other.</summary>
    public int StepNamePartialScore { get; set; } = 15;

    /// <summary>Penalty for an unrelated name. Small: names are the most volatile attribute.</summary>
    public int StepNameMismatchPenalty { get; set; } = -25;

    /// <summary>
    /// Minimum score a candidate must reach to be considered at all.
    /// </summary>
    /// <remarks>
    /// Zero means "the recorded attributes must, on balance, still point at this element". It is
    /// not a quality bar: a step that recorded nothing scores zero and admits every sibling, which
    /// is correct — the sibling index and the rest of the path then decide.
    /// </remarks>
    public int StepAcceptScore { get; set; }

    /// <summary>
    /// How many candidates per level survive into the next one.
    /// </summary>
    /// <remarks>
    /// The reason the search can back out of a dead end (a greedy walk cannot). Costs one
    /// cross-process round trip per surviving candidate at the next level, so the worst case is
    /// <c>BeamWidth</c> round trips per level instead of one.
    /// </remarks>
    public int BeamWidth { get; set; } = 3;

    /// <summary>Maximum number of children examined at one level.</summary>
    public int MaxChildrenPerLevel { get; set; } = 512;

    /// <summary>
    /// Maximum number of levels walked upwards when <see cref="Models.ElementLocator.Search"/>
    /// found the target and its ancestors are needed.
    /// </summary>
    /// <remarks>
    /// A search returns the element only, but the ancestors are what the structure-change
    /// subscription is scoped to, so they have to be walked back up. The bound exists because a
    /// provider that returns a cycle of parents would otherwise loop forever.
    /// </remarks>
    public int MaxSearchDepth { get; set; } = 64;
}
