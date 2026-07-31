// 比較用文字列と表示用文字列の型レベル分離 (docs/DESIGN.md L4)。
//
// 表示用文字列を条件評価にも使い回すと、InvariantCulture 固定のうちは「たまたま」正しいが、
// ローカライズ作業で「表示は現在カルチャで」と手を入れた瞬間に条件評価が静かに壊れる。
// 型で分けておけば、表示用文字列を評価器へ渡すコードはコンパイルできない。
using System.Globalization;

namespace UiaTrigger.Models;

/// <summary>
/// A property value rendered for comparison: always formatted with the invariant culture and
/// always compared with <see cref="StringComparison.Ordinal"/>.
/// </summary>
/// <remarks>
/// Values of this type are the only ones condition evaluation accepts. Display strings are plain
/// <see cref="string"/>s and are formatted for the current UI culture, so they must never reach a
/// comparison — this type exists so that mistake does not compile. Text authored in a trigger
/// definition is comparison text by contract and enters through
/// <see cref="FromDefinition(string?)"/>.
/// </remarks>
public readonly struct ComparisonString : IEquatable<ComparisonString>
{
    private readonly string? _value;

    private ComparisonString(string? value) => _value = value;

    /// <summary>The absent value, used when a property could not be read at all.</summary>
    public static ComparisonString None => default;

    /// <summary>Wraps text that came from a trigger definition, which is invariant by contract.</summary>
    public static ComparisonString FromDefinition(string? text) => new(text);

    /// <summary>Formats a number in the invariant culture.</summary>
    public static ComparisonString FromNumber(double value) => new(value.ToString(CultureInfo.InvariantCulture));

    /// <inheritdoc cref="FromNumber(double)"/>
    public static ComparisonString FromNumber(int value) => new(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Formats a boolean as <c>true</c> / <c>false</c>.</summary>
    /// <remarks>
    /// Lower case on purpose: users write <c>true</c>, and the default <c>Boolean.ToString</c>
    /// form (<c>True</c>) silently fails every ordinal comparison against it.
    /// </remarks>
    public static ComparisonString FromBoolean(bool value) => new(value ? "true" : "false");

    /// <summary>Wraps text read verbatim from a UI element (a name, an automation id, …).</summary>
    public static ComparisonString FromText(string? value) => new(value);

    /// <summary>False when the property could not be read.</summary>
    public bool HasValue => _value is not null;

    /// <summary>The text, or the empty string when <see cref="HasValue"/> is false.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Ordinal equality. Absent and empty are different.</summary>
    public bool Equals(ComparisonString other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ComparisonString other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _value is null ? 0 : StringComparer.Ordinal.GetHashCode(_value);

    /// <summary>Ordinal equality; see <see cref="Equals(ComparisonString)"/>.</summary>
    public static bool operator ==(ComparisonString left, ComparisonString right) => left.Equals(right);

    /// <summary>Ordinal inequality; see <see cref="Equals(ComparisonString)"/>.</summary>
    public static bool operator !=(ComparisonString left, ComparisonString right) => !left.Equals(right);

    /// <summary>Returns <see cref="Value"/>. Safe to log; never re-parsed by the library.</summary>
    public override string ToString() => Value;
}
