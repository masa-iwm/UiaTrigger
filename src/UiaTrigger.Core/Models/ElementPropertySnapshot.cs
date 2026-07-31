using System.Globalization;

namespace UiaTrigger.Models;

/// <summary>An element's bounding rectangle, in screen coordinates.</summary>
public readonly record struct ElementRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Width in physical pixels.</summary>
    public int Width => Right - Left;

    /// <summary>Height in physical pixels.</summary>
    public int Height => Bottom - Top;

    /// <summary>Whether a physical screen point lies inside this rectangle.</summary>
    /// <param name="x">Horizontal position.</param>
    /// <param name="y">Vertical position.</param>
    /// <remarks>
    /// Half open, as Win32 rectangles are: the left and top edges belong to the rectangle and the
    /// right and bottom edges do not, so two rectangles that share an edge never both claim a point.
    /// </remarks>
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    /// <summary>Invariant <c>(L,T)-(R,B)</c> form. Used for both display and comparison.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({Left},{Top})-({Right},{Bottom})");
}

/// <summary>
/// One cross-process read of every observable property of an element.
/// </summary>
/// <remarks>
/// Attached to fired-trigger events and used by the picker's property list. Properties whose
/// pattern the element does not support are null. When <see cref="IsPassword"/> is set the
/// sensitive fields are replaced with <see cref="RedactedMarker"/> before the snapshot is handed
/// out, so a password never reaches a host's log through this type.
/// </remarks>
public sealed record ElementPropertySnapshot
{
    /// <summary>Replaces values that are withheld because the element is a password field.</summary>
    public const string RedactedMarker = "***";

    /// <summary>The element's name, or null when it has none. Withheld for password fields.</summary>
    public string? Name { get; init; }

    /// <summary>The element's automation id, or null when it has none.</summary>
    public string? AutomationId { get; init; }

    /// <summary>The element's class name, or null when it has none.</summary>
    public string? ClassName { get; init; }

    /// <summary>UI Automation control type id, or 0 when it could not be read.</summary>
    public int ControlType { get; init; }

    /// <summary>Stable English name of <see cref="ControlType"/>; see <see cref="UiaControlTypeNames"/>.</summary>
    /// <remarks>
    /// The identity form. This is what a clause on <see cref="TriggerProperty.ControlType"/> is
    /// compared against, so it must not follow any culture. Use
    /// <see cref="LocalizedControlType"/> to show a control type to a user.
    /// </remarks>
    public string ControlTypeName { get; init; } = string.Empty;

    /// <summary>The control type as UI Automation localizes it, for example <c>button</c>.</summary>
    /// <remarks>
    /// The display form. Supplied by the provider in **the target application's** language, so
    /// it is not stable and never takes part in a comparison; falls back to
    /// <see cref="ControlTypeName"/> when the provider supplies nothing.
    /// </remarks>
    public string LocalizedControlType { get; init; } = string.Empty;

    /// <summary>Bounds in physical screen coordinates.</summary>
    public ElementRect BoundingRectangle { get; init; }

    /// <summary>The element's access key, or null when it has none.</summary>
    public string? AccessKey { get; init; }

    /// <summary>The element's accelerator key, or null when it has none.</summary>
    public string? AcceleratorKey { get; init; }

    /// <summary>The element's help text, or null when it has none.</summary>
    public string? HelpText { get; init; }

    /// <summary>Whether the element accepts input.</summary>
    public bool IsEnabled { get; init; }

    /// <summary>Whether the element is scrolled or clipped out of view.</summary>
    public bool IsOffscreen { get; init; }

    /// <summary>True when the element holds a password; see <see cref="RedactedMarker"/>.</summary>
    public bool IsPassword { get; init; }

    /// <summary>Whether the element supports <c>ValuePattern</c>.</summary>
    public bool SupportsValuePattern { get; init; }

    /// <summary>ValuePattern.Value, or null when unsupported. Withheld for password fields.</summary>
    public string? Value { get; init; }

    /// <summary>Whether the element supports <c>RangeValuePattern</c>.</summary>
    public bool SupportsRangeValuePattern { get; init; }

    /// <summary>RangeValuePattern.Value, or null when unsupported.</summary>
    public double? RangeValue { get; init; }

    /// <summary>RangeValuePattern.Minimum, or null when unsupported.</summary>
    public double? RangeValueMinimum { get; init; }

    /// <summary>RangeValuePattern.Maximum, or null when unsupported.</summary>
    public double? RangeValueMaximum { get; init; }

    /// <summary>
    /// Returns this snapshot with the fields that could carry a password replaced by
    /// <see cref="RedactedMarker"/>.
    /// </summary>
    /// <remarks>
    /// Both <see cref="Value"/> and <see cref="Name"/> are withheld: most providers expose a
    /// password box's content through <c>ValuePattern</c>, but some put it in <c>Name</c>, and the
    /// cost of withholding a label is far lower than the cost of leaking a secret.
    /// </remarks>
    internal ElementPropertySnapshot RedactIfPassword() => IsPassword
        ? this with { Name = RedactedMarker, Value = SupportsValuePattern ? RedactedMarker : null }
        : this;

    /// <summary>
    /// The value of a property in comparison form: invariant, ordinal-comparable.
    /// </summary>
    /// <remarks>
    /// This is the only form condition evaluation uses. <see cref="TriggerProperty.Custom"/> is
    /// never present in a snapshot and yields <see cref="ComparisonString.None"/>.
    /// </remarks>
    public ComparisonString GetComparisonValue(TriggerProperty property) => property switch
    {
        TriggerProperty.Name => ComparisonString.FromText(Name),
        TriggerProperty.AutomationId => ComparisonString.FromText(AutomationId),
        TriggerProperty.ClassName => ComparisonString.FromText(ClassName),
        TriggerProperty.ControlType => ComparisonString.FromText(ControlTypeName),
        TriggerProperty.BoundingRectangle => ComparisonString.FromText(BoundingRectangle.ToString()),
        TriggerProperty.AccessKey => ComparisonString.FromText(AccessKey),
        TriggerProperty.AcceleratorKey => ComparisonString.FromText(AcceleratorKey),
        TriggerProperty.HelpText => ComparisonString.FromText(HelpText),
        TriggerProperty.IsEnabled => ComparisonString.FromBoolean(IsEnabled),
        TriggerProperty.IsOffscreen => ComparisonString.FromBoolean(IsOffscreen),
        TriggerProperty.Value => ComparisonString.FromText(Value),
        TriggerProperty.RangeValue => Number(RangeValue),
        TriggerProperty.RangeValueMinimum => Number(RangeValueMinimum),
        TriggerProperty.RangeValueMaximum => Number(RangeValueMaximum),
        _ => ComparisonString.None,
    };

    /// <summary>
    /// The value of a property as a number, or null when it is not numeric.
    /// </summary>
    /// <remarks>
    /// Text properties are parsed with the invariant culture so that a definition means the same
    /// thing on every machine.
    /// </remarks>
    public double? GetNumericValue(TriggerProperty property) => property switch
    {
        TriggerProperty.ControlType => ControlType,
        TriggerProperty.IsEnabled => IsEnabled ? 1 : 0,
        TriggerProperty.IsOffscreen => IsOffscreen ? 1 : 0,
        TriggerProperty.RangeValue => RangeValue,
        TriggerProperty.RangeValueMinimum => RangeValueMinimum,
        TriggerProperty.RangeValueMaximum => RangeValueMaximum,
        _ => ParseInvariant(GetComparisonValue(property)),
    };

    /// <summary>
    /// True when the element does not expose the property at all, as opposed to exposing an empty
    /// value. Clauses on an absent property are never satisfied.
    /// </summary>
    public bool Supports(TriggerProperty property) => property switch
    {
        TriggerProperty.Value => SupportsValuePattern,
        TriggerProperty.RangeValue or TriggerProperty.RangeValueMinimum or TriggerProperty.RangeValueMaximum
            => SupportsRangeValuePattern,
        TriggerProperty.Custom => false,
        _ => true,
    };

    /// <summary>
    /// The value of a property formatted for display. Never use for comparison — see
    /// <see cref="GetComparisonValue"/>.
    /// </summary>
    /// <remarks>
    /// Culture-dependent on purpose: numbers use the current culture, and
    /// <see cref="TriggerProperty.ControlType"/> yields <see cref="LocalizedControlType"/> rather
    /// than the stable name that the same property compares against.
    /// </remarks>
    public string? GetDisplayValue(TriggerProperty property) => property switch
    {
        TriggerProperty.Name => Name,
        TriggerProperty.AutomationId => AutomationId,
        TriggerProperty.ClassName => ClassName,
        TriggerProperty.ControlType => LocalizedControlType,
        TriggerProperty.BoundingRectangle => BoundingRectangle.ToString(),
        TriggerProperty.AccessKey => AccessKey,
        TriggerProperty.AcceleratorKey => AcceleratorKey,
        TriggerProperty.HelpText => HelpText,
        TriggerProperty.IsEnabled => IsEnabled.ToString(),
        TriggerProperty.IsOffscreen => IsOffscreen.ToString(),
        TriggerProperty.Value => Value,
        TriggerProperty.RangeValue => RangeValue?.ToString(CultureInfo.CurrentCulture),
        TriggerProperty.RangeValueMinimum => RangeValueMinimum?.ToString(CultureInfo.CurrentCulture),
        TriggerProperty.RangeValueMaximum => RangeValueMaximum?.ToString(CultureInfo.CurrentCulture),
        _ => null,
    };

    private static ComparisonString Number(double? value) =>
        value is { } d ? ComparisonString.FromNumber(d) : ComparisonString.None;

    private static double? ParseInvariant(ComparisonString value) =>
        value.HasValue && double.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : null;
}
