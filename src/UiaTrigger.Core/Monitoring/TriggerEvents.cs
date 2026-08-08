using UiaTrigger.Models;
using UiaTrigger.Resolution;

namespace UiaTrigger.Monitoring;

/// <summary>Settings for <see cref="TriggerMonitor"/>.</summary>
public sealed class TriggerMonitorOptions
{
    /// <summary>Whether to fire for conditions that are already satisfied when monitoring starts.</summary>
    public bool FireOnInitialMatch { get; set; } = true;

    /// <summary>How long structure changes are coalesced before triggers are resolved again.</summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Time limit for evaluating a regular expression clause.</summary>
    public TimeSpan RegexTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Settings for the <see cref="UiaSession"/> a standalone monitor creates for itself.
    /// </summary>
    /// <remarks>
    /// This is where the UI Automation call timeouts, the clock and the log live. Ignored by
    /// <see cref="UiaSession.CreateMonitor"/>, which uses the settings of the session it belongs to
    /// — one session, one set of these.
    /// </remarks>
    public UiaSessionOptions Session { get; set; } = new();

    /// <summary>Tuning for the element search.</summary>
    public ResolverOptions Resolver { get; set; } = new();
}

/// <summary>A snapshot of a <see cref="TriggerMonitor"/>'s internal state.</summary>
/// <remarks>
/// Read with <see cref="TriggerMonitor.GetDiagnostics"/>, from any thread. The counters are
/// consistent individually but not with each other: they are written as the monitor works, not
/// captured under a lock.
/// </remarks>
public readonly record struct TriggerMonitorDiagnostics
{
    /// <summary>Whether monitoring is currently started.</summary>
    public bool IsRunning { get; init; }

    /// <summary>Number of triggers being monitored.</summary>
    public int TriggerCount { get; init; }

    /// <summary>
    /// How many of those triggers have every element they read currently resolved.
    /// </summary>
    /// <remarks>
    /// A trigger whose clauses name several elements counts only once all of them are resolved, so
    /// this is a ratio over <see cref="TriggerCount"/> and never over the elements themselves. Read
    /// it together with <see cref="ResolvedElementCount"/> and <see cref="ElementSlotCount"/> to see
    /// how far a partly-resolved trigger has got.
    /// </remarks>
    public int ResolvedTriggerCount { get; init; }

    /// <summary>
    /// Number of distinct elements the monitor resolves, summed over every trigger.
    /// </summary>
    /// <remarks>
    /// Clauses that name the same window and locator share one element, so this counts targets
    /// rather than clauses. It equals <see cref="TriggerCount"/> until a trigger's clauses start
    /// naming elements of their own.
    /// </remarks>
    public int ElementSlotCount { get; init; }

    /// <summary>How many of those elements are currently resolved.</summary>
    public int ResolvedElementCount { get; init; }

    /// <summary>Number of resolution passes run so far. Passes are debounced, so this is not an event count.</summary>
    public int SweepCount { get; init; }

    /// <summary>
    /// Number of structure-change events received.
    /// </summary>
    /// <remarks>
    /// A direct measure of how widely the monitor is subscribed. Once every trigger is resolved this
    /// should stay near zero while unrelated parts of the target application change; a count that
    /// keeps climbing means a subscription is scoped to a whole window rather than to the path.
    /// </remarks>
    public int StructureEventCount { get; init; }

    /// <summary>Number of elements the monitor currently has a structure-change subscription on.</summary>
    public int StructureSubscriptionCount { get; init; }

    /// <summary>
    /// Whether every structure-change subscription is scoped to a resolved element's path rather
    /// than to a whole window.
    /// </summary>
    /// <remarks>
    /// Counts only triggers that actually hold a subscription, so a trigger that has none does not
    /// make this false. Read it together with <see cref="OrphanedTriggerCount"/>: path-scoped and
    /// non-zero orphans at the same time means some trigger is watching nothing at all.
    /// </remarks>
    public bool StructureIsPathScoped { get; init; }

    /// <summary>
    /// Number of triggers that should hold a structure-change subscription but hold none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Non-zero means the target application could not be subscribed to — typically because it was
    /// unresponsive when the monitor tried. Those triggers cannot see their element appear, so the
    /// monitor retries them on a widening interval until the subscription is back. That retry is the
    /// only thing the monitor runs on a timer of its own accord, and it stops as soon as the
    /// subscription returns; anything else on a timer was asked for through
    /// <see cref="TriggerDefinition.PollInterval"/> and is reported by <see cref="PollCount"/>.
    /// </para>
    /// <para>
    /// Zero is the normal state, including while a trigger waits for an application that has not
    /// started yet: with no window there is nothing to subscribe to, and
    /// <c>WindowOpened</c> reports its arrival.
    /// </para>
    /// </remarks>
    public int OrphanedTriggerCount { get; init; }

    /// <summary>
    /// Number of element properties the monitor subscribes to, summed over every trigger.
    /// </summary>
    /// <remarks>
    /// Derived from the clauses whose <see cref="PropertyClause.Watch"/> is true, so it is known as
    /// soon as a trigger is added and does not wait for its element to appear. Read it to tell a
    /// clause that only narrows a condition from one that can fire it: a clause meant to require an
    /// element without watching it should not raise this count.
    /// </remarks>
    public int WatchedPropertyCount { get; init; }

    /// <summary>
    /// Number of firings dropped by <see cref="TriggerDefinition.MinInterval"/> rate limits.
    /// </summary>
    public long SuppressedFireCount { get; init; }

    /// <summary>
    /// Number of polling rounds run for triggers that set
    /// <see cref="TriggerDefinition.PollInterval"/>.
    /// </summary>
    /// <remarks>
    /// Stays at zero unless a trigger asks to be polled, which is the observable form of the rule
    /// that the library never polls on its own initiative. Read it with
    /// <see cref="PolledReadCount"/>: rounds alone do not say what polling costs, because a round
    /// re-reads every element the trigger resolves.
    /// </remarks>
    public long PollCount { get; init; }

    /// <summary>
    /// Number of cross-process reads polling has performed.
    /// </summary>
    /// <remarks>
    /// One per resolved element per round, so it grows with the number of elements a polled trigger
    /// spans rather than with the number of triggers. This is the number to watch when deciding an
    /// interval. Clauses on <see cref="TriggerProperty.Custom"/> add a further read per evaluation
    /// that is not counted here, so treat it as a floor for them.
    /// </remarks>
    public long PolledReadCount { get; init; }
}

/// <summary>What became of one condition while the trigger was being evaluated.</summary>
public enum ClauseOutcome
{
    /// <summary>The condition was never looked at, because evaluation stopped before reaching it.</summary>
    /// <remarks>
    /// Combining stops as soon as the outcome is settled, whichever way the conditions are combined:
    /// the right side of an <c>&amp;&amp;</c> whose left side is false is not read, and neither are
    /// the conditions after the first match under <see cref="ClauseCombinator.Any"/>. That is
    /// deliberate — reading a <see cref="TriggerProperty.Custom"/> condition costs a cross-process
    /// call — and it is a distinct state from "did not hold": nothing is known about this one.
    /// </remarks>
    NotEvaluated,

    /// <summary>The element does not offer that property at all, so no condition on it can hold.</summary>
    /// <remarks>
    /// Either the element was not resolved, or it does not support the pattern the property comes
    /// from. Negated conditions do not hold either; see
    /// <see cref="TriggerDefinition.Expression"/>.
    /// </remarks>
    Unreadable,

    /// <summary>The property was read and the condition did not hold.</summary>
    NotMatched,

    /// <summary>The property was read and the condition held.</summary>
    Matched,
}

/// <summary>One condition's value and outcome, as of a firing.</summary>
/// <param name="Name">
/// The condition's name — <see cref="PropertyClause.Name"/>, or its positional fallback (<c>c1</c>,
/// <c>c2</c>, …). The same name <see cref="TriggerDefinition.Expression"/> refers to it by.
/// </param>
/// <param name="Value">
/// The value read from the property, in comparison form. Absent when
/// <paramref name="Outcome"/> is <see cref="ClauseOutcome.NotEvaluated"/> or
/// <see cref="ClauseOutcome.Unreadable"/>.
/// </param>
/// <param name="Outcome">What became of the condition.</param>
public readonly record struct ClauseReading(string Name, ComparisonString Value, ClauseOutcome Outcome);

/// <summary>Reports that a trigger's condition was met.</summary>
/// <remarks>
/// <para>
/// Raised on a single background worker in detection order; see
/// <see cref="TriggerMonitor.TriggerFired"/>.
/// </para>
/// <para>
/// **The values describe one element: the one the trigger's first condition reads** (or the
/// trigger's own element when it carries no condition). That matters for a trigger spanning several
/// elements, because the condition that *changed* may be a different one — what fired the
/// trigger and what the event reports are not the same question. <see cref="OldValue"/>,
/// <see cref="NewValue"/> and <see cref="Properties"/> are always consistent with each other.
/// </para>
/// </remarks>
public sealed class TriggerFiredEventArgs : EventArgs
{
    /// <summary><see cref="TriggerDefinition.Id"/> of the trigger that fired.</summary>
    public required string TriggerId { get; init; }

    /// <summary>Which lifecycle moment produced this event.</summary>
    public required TriggerOn On { get; init; }

    /// <summary>
    /// Value of the first condition's property before the change, in comparison form. Absent unless
    /// this event is that value changing.
    /// </summary>
    /// <remarks>
    /// So it is absent for <see cref="TriggerOn.ElementAppeared"/> (there was no previous value),
    /// and for a <see cref="TriggerOn.PropertyChanged"/> trigger whose firing came from a condition
    /// other than the first — that condition changing says nothing about what the first one was.
    ///
    /// <para>
    /// For <see cref="TriggerOn.WhileMatching"/> and <see cref="TriggerOn.StoppedMatching"/> it
    /// depends on what moved the edge. A property changing carries the value it held before, so the
    /// transition reads whole; an edge that came from the element resolving or disappearing carries
    /// nothing, because no value changed.
    /// </para>
    /// </remarks>
    public ComparisonString OldValue { get; init; }

    /// <summary>
    /// Value of the first condition's property as of this event. Absent for
    /// <see cref="TriggerOn.ElementRemoved"/>, and for a trigger carrying no condition.
    /// </summary>
    /// <remarks>
    /// "As of this event" rather than "after the change": for a trigger spanning several elements,
    /// the value read here need not be the one that changed. See the remarks on the class.
    /// </remarks>
    public ComparisonString NewValue { get; init; }

    /// <summary>
    /// Every observable property of the element <see cref="NewValue"/> came from, read in one round
    /// trip. For <see cref="TriggerOn.ElementRemoved"/> this is the last snapshot taken while that
    /// element existed. Values of password fields are withheld; see
    /// <see cref="ElementPropertySnapshot.RedactedMarker"/>.
    /// </summary>
    /// <remarks>
    /// **Every observable property, not only the watched ones.** One round trip reads them all,
    /// so restricting the snapshot to the conditions' properties would cost the same and tell the
    /// host less.
    /// </remarks>
    public ElementPropertySnapshot? Properties { get; init; }

    /// <summary>
    /// What each of the trigger's conditions read and what became of it, in definition order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **This is where a trigger spanning several elements becomes readable.**
    /// <see cref="NewValue"/> and <see cref="Properties"/> describe one element — the first
    /// condition's — so for a trigger over several elements they cannot say what the others held.
    /// These can: one entry per condition, each with the value read from its own element.
    /// </para>
    /// <para>
    /// How much the outcomes add depends on how the trigger combines its conditions. With
    /// <see cref="ClauseCombinator.All"/> every one of them held, so the values are the interesting
    /// part; with <see cref="ClauseCombinator.Any"/> or an
    /// <see cref="TriggerDefinition.Expression"/>, *which* ones held is a real question and
    /// only these answer it.
    /// </para>
    /// <para>
    /// Values come from each condition's last snapshot, which is what the trigger was evaluated
    /// against; they are not re-read at firing time. Empty for a trigger carrying no condition.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ClauseReading> Clauses { get; init; } = [];

    /// <summary>When the condition was detected, in UTC.</summary>
    /// <remarks>
    /// Taken from <see cref="UiaSessionOptions.TimeProvider"/> and always UTC. A library that reads
    /// the local clock produces timestamps that cannot be compared across machines and that ignore
    /// whatever clock the host injected.
    /// </remarks>
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>Reports that a trigger's target element became resolvable, or stopped being so.</summary>
public sealed class TriggerResolutionChangedEventArgs : EventArgs
{
    /// <summary><see cref="TriggerDefinition.Id"/> of the trigger this concerns.</summary>
    public required string TriggerId { get; init; }

    /// <summary>Whether the target element is resolved now.</summary>
    public required bool IsResolved { get; init; }

    /// <summary>Localized description of why the state changed. For display and diagnostics.</summary>
    public string? Message { get; init; }

    /// <inheritdoc cref="TriggerFiredEventArgs.Timestamp"/>
    public DateTimeOffset Timestamp { get; init; }
}
