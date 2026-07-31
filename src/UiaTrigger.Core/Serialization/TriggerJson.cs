// ライブラリが JsonSerializerContext を「提供」する (docs/DESIGN.md C8)。
//
// 「JSON 化はホストの責務」とだけ決めると、各ホストに同じ context ファイルが
// 複製されていく。ライブラリが自分のモデルの型情報だけを提供し、ホストが
// TypeInfoResolverChain で自分の context と合成すれば、
// 「ファイル形式はホストの自由」を保ったまま重複が消える。
using System.Text.Json.Serialization;
using UiaTrigger.Models;

namespace UiaTrigger.Serialization;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for the trigger model.
/// </summary>
/// <remarks>
/// <para>
/// The library does not own the file format: a host normally stores triggers inside its own
/// settings object. Chain this context into that host's options so the model's type information
/// comes from here rather than being duplicated per host:
/// </para>
/// <code>
/// var options = new JsonSerializerOptions
/// {
///     TypeInfoResolverChain = { MyHostContext.Default, TriggerJsonContext.Default },
/// };
/// </code>
/// <para>
/// Enums are written as names whichever options are used, because the model's enums declare that
/// themselves. A stored file therefore stays readable, and stays valid when a new enum member is
/// inserted.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TriggerDefinition))]
[JsonSerializable(typeof(List<TriggerDefinition>))]
[JsonSerializable(typeof(TriggerFile))]
public sealed partial class TriggerJsonContext : JsonSerializerContext;

/// <summary>The document the library's own sample hosts persist. Hosts may use any shape.</summary>
public sealed class TriggerFile
{
    /// <summary>Format version. Bumped when the model changes in a way older readers cannot handle.</summary>
    public int Version { get; set; } = TriggerJson.FormatVersion;

    /// <summary>The stored triggers, in the order they were written.</summary>
    public List<TriggerDefinition> Triggers { get; set; } = [];
}

/// <summary>Helpers for persisting <see cref="TriggerDefinition"/> values as JSON.</summary>
public static class TriggerJson
{
    /// <summary>
    /// Version stamped into <see cref="TriggerFile.Version"/> and the highest this build reads.
    /// </summary>
    /// <remarks>
    /// Version 1 is the first shipped shape of the model; nothing predates it, so there are no
    /// older files to migrate. The field exists from the start because a file that carries no
    /// version at all cannot be told apart later.
    /// </remarks>
    public const int FormatVersion = 1;
}
