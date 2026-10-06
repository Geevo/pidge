using System.Text.Json.Serialization;
using Pidge.Core;
using Pidge.Variables;

namespace Pidge.Storage;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(AppState))]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(SavedRequest))]
[JsonSerializable(typeof(List<SavedRequest>))]
[JsonSerializable(typeof(HistoryEntry))]
[JsonSerializable(typeof(List<HistoryEntry>))]
[JsonSerializable(typeof(ScratchTab))]
[JsonSerializable(typeof(WindowPlacement))]
[JsonSerializable(typeof(VariableEnvironment))]
[JsonSerializable(typeof(List<VariableEnvironment>))]
[JsonSerializable(typeof(Theme))]
[JsonSerializable(typeof(SyntaxTheme))]
[JsonSerializable(typeof(PaneLayout))]
[JsonSerializable(typeof(HttpRequest))]
[JsonSerializable(typeof(RequestError))]
[JsonSerializable(typeof(TlsSettings))]
public partial class StorageJsonContext : JsonSerializerContext
{
    /// <summary>Compact.</summary>
    public static StorageJsonContext Wire { get; } = new(PidgeJson.CreateOptions());

    /// <summary>Two-space indent.</summary>
    public static StorageJsonContext Pretty { get; } = new(PidgeJson.CreateOptions(indented: true));
}
