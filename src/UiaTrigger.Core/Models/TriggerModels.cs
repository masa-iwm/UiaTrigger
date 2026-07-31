// トリガー定義の永続化モデル (docs/DESIGN.md §3)。
//
// プロパティだけの POCO を維持する。これにより「シリアライズ形式はホストの自由」という
// 性質と STJ source-gen / AOT 適合を両立できる。
// Core 自身が使える JsonSerializerContext は UiaTrigger.Serialization に置いてある (C8)。
//
// 唯一の例外が列挙に付けた [JsonConverter]。これが無いと列挙は数値で書かれ、
// 「後から列挙メンバーを 1 つ挿入しただけで保存済みファイルの意味が変わる」ことになる。
// JsonSourceGenerationOptions(UseStringEnumConverter) で指定する手もあるが、その設定は
// ホストが TypeInfoResolverChain で合成した経路には引き継がれない (実測で確認 —
// TriggerJsonTests.ComposedOptions_StillWriteEnumsAsNames)。
// 永続形式は「誰がシリアライズするか」ではなくモデル自身の性質であるべきなので、型に付ける。
using System.Text.Json.Serialization;

namespace UiaTrigger.Models;

/// <summary>The UI Automation tree view used to record and resolve an element path.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TreeViewMode>))]
public enum TreeViewMode
{
    /// <summary>Every element the providers expose, including ones no user can perceive.</summary>
    Raw = 0,
    /// <summary>Elements a user can interact with or that structure the UI. The usual choice.</summary>
    Control = 1,
    /// <summary>Only elements that carry information, such as text and values.</summary>
    Content = 2,
}

/// <summary>How strongly a recorded attribute constrains the search.</summary>
/// <remarks>
/// <see cref="Required"/> attributes filter; <see cref="Preferred"/> ones only rank. Attributes are
/// deliberately not folded into one thresholded score — that would make a single changed attribute
/// (a WinForms class name token, a window title) permanently unresolvable.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<MatchStrength>))]
public enum MatchStrength
{
    /// <summary>The attribute is recorded but neither filters nor ranks candidates.</summary>
    Ignored = 0,
    /// <summary>A match ranks the candidate higher; a mismatch never rejects it.</summary>
    Preferred = 1,
    /// <summary>Candidates that do not match this attribute are rejected.</summary>
    Required = 2,
}

/// <summary>A property that can be observed on the target element.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TriggerProperty>))]
public enum TriggerProperty
{
    /// <summary>The element's name, which is usually its visible label.</summary>
    Name = 0,
    /// <summary>The element's automation id. Stable across runs when the application sets one.</summary>
    AutomationId = 1,
    /// <summary>The control type, compared against its stable name (see <see cref="UiaControlTypeNames"/>).</summary>
    ControlType = 2,
    /// <summary>The bounds, compared as the invariant text <c>(L,T)-(R,B)</c>.</summary>
    BoundingRectangle = 3,
    /// <summary>The access key ("Alt+F"), as the provider reports it.</summary>
    AccessKey = 4,
    /// <summary>The accelerator key ("Ctrl+S"), as the provider reports it.</summary>
    AcceleratorKey = 5,
    /// <summary>The element's help text, typically what a tooltip would show.</summary>
    HelpText = 6,
    /// <summary>Whether the element accepts input. Compared as <c>true</c> / <c>false</c>.</summary>
    IsEnabled = 7,
    /// <summary>Whether the element is scrolled or clipped out of view. Compared as <c>true</c> / <c>false</c>.</summary>
    IsOffscreen = 8,
    /// <summary>The text value, from <c>ValuePattern.Value</c>. Withheld for password fields.</summary>
    Value = 9,
    /// <summary>The numeric value, from <c>RangeValuePattern.Value</c>.</summary>
    RangeValue = 10,
    /// <summary>The lowest value the element accepts, from <c>RangeValuePattern.Minimum</c>.</summary>
    RangeValueMinimum = 11,
    /// <summary>The highest value the element accepts, from <c>RangeValuePattern.Maximum</c>.</summary>
    RangeValueMaximum = 12,
    /// <summary>The class name. Very stable for Win32 controls, but see <see cref="WindowIdentity.ClassName"/>.</summary>
    ClassName = 13,
    /// <summary>
    /// Any UI Automation property, identified by <see cref="PropertyClause.CustomPropertyId"/>.
    /// </summary>
    /// <remarks>
    /// The escape hatch for the properties this enumeration does not name. Unlike the named
    /// properties, a custom property is read with its own cross-process call rather than coming
    /// from the cached snapshot, so prefer a named property when one fits.
    /// </remarks>
    Custom = 14,
}

/// <summary>The lifecycle moment at which a trigger is evaluated and may fire.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TriggerOn>))]
public enum TriggerOn
{
    /// <summary>The target element became resolvable.</summary>
    ElementAppeared = 0,
    /// <summary>The target element disappeared or was replaced.</summary>
    ElementRemoved = 1,
    /// <summary>An observed property changed. Fires on every change that satisfies the clauses.</summary>
    PropertyChanged = 2,
    /// <summary>
    /// The clauses started being satisfied. Fires on the rising edge only, not on every change
    /// while they keep holding.
    /// </summary>
    WhileMatching = 3,
}

/// <summary>How the clauses of a trigger are combined.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClauseCombinator>))]
public enum ClauseCombinator
{
    /// <summary>Every clause must hold (logical AND). An empty clause list is satisfied.</summary>
    All = 0,
    /// <summary>At least one clause must hold (logical OR). An empty clause list is satisfied.</summary>
    Any = 1,
}

/// <summary>The comparison performed by a single <see cref="PropertyClause"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ComparisonOp>))]
public enum ComparisonOp
{
    /// <summary>Always satisfied. Use to observe a property without constraining its value.</summary>
    Always = 0,
    /// <summary>Equal to the operand, within <see cref="PropertyClause.Tolerance"/> for numbers.</summary>
    Equals = 1,
    /// <summary>The negation of <see cref="Equals"/>, tolerance included.</summary>
    NotEquals = 2,
    /// <summary>Within [Low, High] inclusive. Numeric values only.</summary>
    Between = 3,
    /// <summary>Outside [Low, High]. Numeric values only.</summary>
    NotBetween = 4,
    /// <summary>Strictly greater than <see cref="PropertyClause.Value"/>. Numeric values only.</summary>
    GreaterThan = 5,
    /// <summary>Strictly less than <see cref="PropertyClause.Value"/>. Numeric values only.</summary>
    LessThan = 6,
    /// <summary>Less than or equal to <see cref="PropertyClause.Value"/>. Numeric values only.</summary>
    LessOrEqual = 7,
    /// <summary>Greater than or equal to <see cref="PropertyClause.Value"/>. Numeric values only.</summary>
    GreaterOrEqual = 8,
    /// <summary>The value matches the regular expression in <see cref="PropertyClause.Text"/>.</summary>
    RegexMatch = 9,
    /// <summary>The negation of <see cref="RegexMatch"/>.</summary>
    RegexNotMatch = 10,
}

/// <summary>Identifies the top-level window that contains the target element.</summary>
/// <remarks>
/// Each attribute carries its own <see cref="MatchStrength"/>. Only <see cref="ProcessName"/> is
/// required by default: window titles change constantly, and WinForms class names embed a token
/// that changes per process instance, so requiring either of those makes triggers permanently
/// unresolvable. Windows that satisfy every required attribute are all tried, best-ranked first.
/// </remarks>
public sealed class WindowIdentity
{
    /// <summary>Executable file name, for example <c>notepad.exe</c>. Compared case-insensitively.</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <inheritdoc cref="MatchStrength"/>
    public MatchStrength ProcessNameMatch { get; set; } = MatchStrength.Required;

    /// <summary>Full path of the executable. Compared case-insensitively.</summary>
    public string? ProcessPath { get; set; }

    /// <inheritdoc cref="MatchStrength"/>
    public MatchStrength ProcessPathMatch { get; set; } = MatchStrength.Preferred;

    /// <summary>Win32 window class name.</summary>
    public string? ClassName { get; set; }

    /// <inheritdoc cref="MatchStrength"/>
    public MatchStrength ClassNameMatch { get; set; } = MatchStrength.Preferred;

    /// <summary>Window title. Recorded for display; ignored when matching unless raised.</summary>
    public string? WindowName { get; set; }

    /// <inheritdoc cref="MatchStrength"/>
    public MatchStrength WindowNameMatch { get; set; } = MatchStrength.Ignored;
}

/// <summary>One level of the path from the top-level window down to the target element.</summary>
public sealed class ElementPathStep
{
    /// <summary>UI Automation control type id (for example 50000 = Button). 0 = not recorded.</summary>
    public int ControlType { get; set; }

    /// <summary>Automation id of the element at this level, or null when it had none.</summary>
    public string? AutomationId { get; set; }

    /// <summary>Name of the element at this level, or null when it had none.</summary>
    public string? Name { get; set; }

    /// <summary>Class name of the element. Very stable for Win32 controls.</summary>
    public string? ClassName { get; set; }

    /// <summary>
    /// Zero-based index among the siblings in the recorded view, or
    /// <see cref="UnknownSiblingIndex"/> when it could not be determined.
    /// </summary>
    /// <remarks>
    /// Only breaks ties between candidates that score equally; it never rejects a candidate and
    /// never contributes to the score. An undeterminable index is recorded explicitly as
    /// <see cref="UnknownSiblingIndex"/> — silently falling back to 0 would quietly degrade every
    /// later resolution.
    /// </remarks>
    public int SiblingIndex { get; set; } = UnknownSiblingIndex;

    /// <summary>Value of <see cref="SiblingIndex"/> meaning "not known".</summary>
    public const int UnknownSiblingIndex = -1;
}

/// <summary>Finds the target element directly, without walking the path level by level.</summary>
/// <remarks>
/// Recorded only when the target's automation id was **unique inside its window** at record
/// time. Resolution then takes one <c>FindAll</c> over the window's subtree instead of one call per
/// level, and is unaffected by anything that changes above the target. Uniqueness is re-checked at
/// resolve time: if the id has since become ambiguous, or matches nothing, resolution silently
/// falls back to <see cref="ElementLocator.Steps"/>.
/// </remarks>
public sealed class ElementSearch
{
    /// <summary>The automation id to search for. Compared with ordinal equality.</summary>
    public string AutomationId { get; set; } = string.Empty;
}

/// <summary>Describes how to find the target element inside its window.</summary>
/// <remarks>
/// Both ways of finding it are recorded when possible: <see cref="Search"/> is tried first because
/// it is faster and more robust, and <see cref="Steps"/> remains as the fallback for when the
/// search no longer identifies exactly one element.
/// </remarks>
public sealed class ElementLocator
{
    /// <summary>The tree view the path was recorded in and must be resolved in.</summary>
    public TreeViewMode View { get; set; } = TreeViewMode.Control;

    /// <summary>Direct search for the target, or null when none could be recorded.</summary>
    public ElementSearch? Search { get; set; }

    /// <summary>
    /// Steps from the child of the top-level window down to the target. An empty list means the
    /// target is the top-level window itself.
    /// </summary>
    public List<ElementPathStep> Steps { get; set; } = [];
}

/// <summary>One condition on one property of an element.</summary>
public sealed class PropertyClause
{
    /// <summary>
    /// Name this clause is referred to by in <see cref="TriggerDefinition.Expression"/>. Null falls
    /// back to its position: the first clause is <c>c1</c>, the second <c>c2</c>, and so on.
    /// </summary>
    /// <remarks>
    /// Naming is optional because a trigger that combines its clauses with
    /// <see cref="TriggerDefinition.Combine"/> never refers to one. Name them once an expression
    /// does: the positional fallback still parses after a clause is reordered or removed, and
    /// quietly means something else, whereas a name does not. Must be unique within one trigger and
    /// cannot contain whitespace or any of <c>( ) ! &amp; |</c>.
    /// </remarks>
    public string? Name { get; set; }

    /// <summary>Human-readable label for this clause. Not used for matching.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// How to find the window holding the element this clause reads, or null to use
    /// <see cref="TriggerDefinition.Window"/>.
    /// </summary>
    /// <remarks>
    /// This and <see cref="Locator"/> are what let one trigger span several elements, and several
    /// windows. Clauses that name the same window and locator share one resolution and one
    /// subscription, so leaving both null — the common case — costs what a single-element trigger
    /// costs. Leaving them null also states "the same element as the other clauses" in a way
    /// that cannot drift, which repeating a locator per clause does not.
    /// </remarks>
    public WindowIdentity? Window { get; set; }

    /// <summary>
    /// How to find the element this clause reads inside that window, or null to use
    /// <see cref="TriggerDefinition.Locator"/>.
    /// </summary>
    public ElementLocator? Locator { get; set; }

    /// <summary>Whether a change to this clause's property can fire the trigger. Defaults to true.</summary>
    /// <remarks>
    /// <para>
    /// A watched clause is subscribed to, and its changes are what a
    /// <see cref="TriggerOn.PropertyChanged"/> trigger reports. An unwatched one only narrows: it is
    /// never subscribed to, and a change to it alone raises nothing. The difference is between
    /// "fire when this changes" and "fire when something else changes, provided this holds".
    /// </para>
    /// <para>
    /// Set it to false to require an element without watching it. Note that the subscription is
    /// derived from <see cref="Property"/> and never from <see cref="Op"/>, so a clause written with
    /// <see cref="ComparisonOp.Always"/> to mean "this element is present" still fires on every
    /// change to whichever property it names until this is false.
    /// </para>
    /// <para>
    /// An unwatched clause is re-read only when something else re-evaluates the trigger, so its
    /// value comes from the last snapshot taken of its element rather than from the moment of
    /// firing.
    /// </para>
    /// </remarks>
    public bool Watch { get; set; } = true;

    /// <summary>The property to read from the element.</summary>
    public TriggerProperty Property { get; set; }

    /// <summary>UI Automation property id, used when <see cref="Property"/> is
    /// <see cref="TriggerProperty.Custom"/>.</summary>
    public int CustomPropertyId { get; set; }

    /// <summary>The comparison to apply to that property's value.</summary>
    public ComparisonOp Op { get; set; }

    /// <summary>
    /// Operand for <see cref="ComparisonOp.Equals"/>, <see cref="ComparisonOp.NotEquals"/> and the
    /// regular expression operators. Always interpreted with the invariant culture.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>Operand for the ordering operators.</summary>
    public double? Value { get; set; }

    /// <summary>Lower bound for <see cref="ComparisonOp.Between"/> / <see cref="ComparisonOp.NotBetween"/>.</summary>
    public double? Low { get; set; }

    /// <summary>Upper bound for <see cref="ComparisonOp.Between"/> / <see cref="ComparisonOp.NotBetween"/>.</summary>
    public double? High { get; set; }

    /// <summary>
    /// Half-width of the band treated as "equal" when comparing numbers. Defaults to 0 (exact).
    /// </summary>
    /// <remarks>
    /// Doubles coming from <c>RangeValuePattern</c> rarely land on an exact value, so an exact
    /// <see cref="ComparisonOp.Equals"/> against one is practically never satisfied. The tolerance
    /// widens the region considered equal, which also relaxes <see cref="ComparisonOp.LessOrEqual"/>,
    /// <see cref="ComparisonOp.GreaterOrEqual"/> and the bounds of
    /// <see cref="ComparisonOp.Between"/>, and tightens the strict operators by the same amount.
    /// </remarks>
    public double Tolerance { get; set; }
}

/// <summary>A single trigger: what to watch, where, and when to fire.</summary>
public sealed class TriggerDefinition
{
    /// <summary>
    /// Identifier reported on every event raised for this trigger. Unique within one monitor.
    /// </summary>
    public required string Id { get; set; }

    /// <summary>Human-readable label. Not used for matching.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// How to find the top-level window that contains the target element, for clauses that do not
    /// name their own <see cref="PropertyClause.Window"/>.
    /// </summary>
    public WindowIdentity Window { get; set; } = new();

    /// <summary>
    /// How to find the target element inside that window, for clauses that do not name their own
    /// <see cref="PropertyClause.Locator"/>.
    /// </summary>
    public ElementLocator Locator { get; set; } = new();

    /// <summary>The lifecycle moment at which this trigger is evaluated.</summary>
    public TriggerOn On { get; set; } = TriggerOn.PropertyChanged;

    /// <summary>How <see cref="Clauses"/> are combined when <see cref="Expression"/> is null.</summary>
    public ClauseCombinator Combine { get; set; } = ClauseCombinator.All;

    /// <summary>
    /// Boolean expression over the clause names, or null to combine every clause with
    /// <see cref="Combine"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operators are <c>&amp;&amp;</c>, <c>||</c> and <c>!</c>, grouped with parentheses and
    /// binding in that order, over the names in <see cref="PropertyClause.Name"/> — for example
    /// <c>(ready || idle) &amp;&amp; !busy</c>. This is what expresses a condition that
    /// <see cref="Combine"/> alone cannot, such as one mixing "and" with "or".
    /// </para>
    /// <para>
    /// It is text rather than a tree of objects on purpose: the model stays a plain POCO that any
    /// serializer can round-trip, which a recursive shape would cost. It is parsed once, when the
    /// trigger is added to a monitor, and every clause must be referred to — a clause nothing refers
    /// to is still resolved and subscribed to while never affecting the outcome.
    /// </para>
    /// <para>
    /// Evaluation short-circuits, so a clause on the unevaluated side of an operator is not read.
    /// That is only observable for <see cref="TriggerProperty.Custom"/>, which costs a cross-process
    /// call per evaluation.
    /// </para>
    /// </remarks>
    public string? Expression { get; set; }

    /// <summary>
    /// Conditions on element properties. An empty list means "no condition on the value",
    /// which for <see cref="TriggerOn.PropertyChanged"/> is invalid — there would be nothing to
    /// subscribe to.
    /// </summary>
    /// <remarks>
    /// The list is deliberately flat: clauses combine with a single <see cref="ClauseCombinator"/>
    /// rather than nesting, which keeps the model a plain POCO that any serializer can round-trip.
    /// A condition that needs nesting is written as text in <see cref="Expression"/>, which leaves
    /// the list itself flat.
    /// </remarks>
    public List<PropertyClause> Clauses { get; set; } = [];

    /// <summary>
    /// Minimum time between two firings of this trigger. Firings that arrive sooner are dropped.
    /// </summary>
    /// <remarks>
    /// Some properties change in bursts — <c>BoundingRectangle</c> fires continuously while a
    /// window is dragged — and without a limit every one of those becomes an event for the host.
    /// </remarks>
    public TimeSpan? MinInterval { get; set; }

    /// <summary>
    /// How often to re-read this trigger's elements instead of waiting for UI Automation to report
    /// a change. Null or <see cref="TimeSpan.Zero"/> — the default — waits for events only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Monitoring is event driven, which means a trigger fires only if the watched application
    /// raises the UI Automation event. Some applications never raise <c>PropertyChanged</c> for a
    /// property that visibly changes on screen, and nothing this library does can make them. This is
    /// the way out: set an interval and the trigger's already-resolved elements are re-read on that
    /// cadence, so a change is noticed even though nothing announced it.
    /// </para>
    /// <para>
    /// **It is a cost you are asking for, not one the library takes on its own.** Each round
    /// costs one cross-process read per element this trigger resolves, on the automation thread
    /// shared with everything else on the session. Set it on the triggers that need it rather than
    /// on all of them, prefer the longest interval that still catches the change in time, and read
    /// <see cref="Monitoring.TriggerMonitorDiagnostics.PollCount"/> and
    /// <see cref="Monitoring.TriggerMonitorDiagnostics.PolledReadCount"/> to see what it costs.
    /// </para>
    /// <para>
    /// Polling does not change when a trigger fires, only when a change is noticed:
    /// <see cref="TriggerOn.WhileMatching"/> still fires on the rising edge alone, and
    /// <see cref="TriggerOn.PropertyChanged"/> still fires only when a watched value actually
    /// differs. A round that finds nothing changed raises nothing. The exception is
    /// <see cref="TriggerProperty.Custom"/>, which is not part of the cached snapshot and costs a
    /// further cross-process read per evaluation.
    /// </para>
    /// <para>
    /// Only resolved elements are re-read. Finding an element in the first place stays event driven,
    /// so this is rejected for <see cref="TriggerOn.ElementAppeared"/> and
    /// <see cref="TriggerOn.ElementRemoved"/>, which it could not affect.
    /// </para>
    /// </remarks>
    public TimeSpan? PollInterval { get; set; }
}
