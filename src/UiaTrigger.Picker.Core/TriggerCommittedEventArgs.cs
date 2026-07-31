using UiaTrigger.Models;

namespace UiaTrigger.Picker;

/// <summary>Reports a trigger the user completed in the picker. Persisting it is up to the host.</summary>
/// <remarks>
/// The identifier lives inside <see cref="TriggerDefinition.Id"/>, so hosts never need to keep
/// a parallel collection of keys alongside the definitions.
/// </remarks>
public sealed class TriggerCommittedEventArgs : EventArgs
{
    /// <summary>The trigger the user completed, ready to be stored or added to a monitor.</summary>
    public required TriggerDefinition Definition { get; init; }
}
