using System.Text.Json;
using System.Text.Json.Serialization;
using Pidge.Codegen;
using Pidge.Core;
using Pidge.Session;
using Pidge.Storage;

namespace Pidge.Desktop;

/// <summary>What the UI needs on startup, in one round trip.</summary>
internal sealed class LoadedState
{
    public required AppState State { get; init; }

    /// <summary>Present when the state file had to be recovered.</summary>
    public string? Recovery { get; init; }

    public required string StoragePath { get; init; }

    /// <summary>The app's own version, for the About tab.</summary>
    public required string Version { get; init; }
}

internal sealed class FilePickFilter
{
    public string Name { get; init; } = "";
    public List<string> Extensions { get; init; } = [];
}

/// <summary>A strip of the page that should move the window, in CSS pixels.</summary>
internal sealed class DragRegion
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(LoadedState))]
[JsonSerializable(typeof(AppState))]
[JsonSerializable(typeof(HttpRequest))]
[JsonSerializable(typeof(RequestError))]
[JsonSerializable(typeof(SendOutcome))]
[JsonSerializable(typeof(ImportOutcome))]
[JsonSerializable(typeof(CodeTarget))]
[JsonSerializable(typeof(ExportFormat))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<FilePickFilter>))]
[JsonSerializable(typeof(List<DragRegion>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
internal sealed partial class DesktopJsonContext : JsonSerializerContext
{
    public static DesktopJsonContext Wire { get; } = new(PidgeJson.CreateOptions(false));
}

internal static class JsonArgs
{
    public static T? Optional<T>(JsonElement args, string name, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var value)
        && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value.Deserialize(type)
            : default;

    public static T Required<T>(JsonElement args, string name, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        Optional(args, name, type) ?? throw new JsonException($"missing field `{name}`");
}
