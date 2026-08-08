using UiaTrigger.Models;

namespace UiaTrigger.Picker;

/// <summary>Reports a trigger the user completed in the picker. Persisting it is up to the host.</summary>
/// <remarks>
/// <para>
/// The identifier lives inside <see cref="TriggerDefinition.Id"/>, so hosts never need to keep
/// a parallel collection of keys alongside the definitions.
/// </para>
/// <para>
/// The handler runs synchronously, on the UI thread, before the picker updates its own status
/// text — and, in an edit session, before it closes itself. Do not close the picker's window from
/// inside the handler; hand off to your dispatcher (the sample hosts all do) if you need to.
/// </para>
/// </remarks>
public sealed class TriggerCommittedEventArgs : EventArgs
{
    /// <summary>The trigger the user completed, ready to be stored or added to a monitor.</summary>
    /// <remarks>
    /// A copy owned by the handler: the picker keeps its own definition so the user can commit
    /// again without re-picking an element, and this one is unaffected by that.
    /// </remarks>
    public required TriggerDefinition Definition { get; init; }
}
