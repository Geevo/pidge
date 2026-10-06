using System.Text.Json.Serialization;
using Pidge.Core;

namespace Pidge.Session;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(SendOutcome))]
[JsonSerializable(typeof(ImportOutcome))]
public partial class SessionJsonContext : JsonSerializerContext
{
    /// <summary>Compact.</summary>
    public static SessionJsonContext Wire { get; } = new(PidgeJson.CreateOptions());

    /// <summary>Two-space indent.</summary>
    public static SessionJsonContext Pretty { get; } = new(PidgeJson.CreateOptions(indented: true));
}

/// <summary>The export file's own shapes, which nothing outside this assembly reads.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ExportFile))]
[JsonSerializable(typeof(ImportFile))]
internal partial class ExportFileJsonContext : JsonSerializerContext
{
    public static ExportFileJsonContext Wire { get; } = new(PidgeJson.CreateOptions());

    public static ExportFileJsonContext Pretty { get; } = new(PidgeJson.CreateOptions(indented: true));
}
