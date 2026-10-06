using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Pidge.Core;

/*
 * The wire format: camelCase fields, nulls written out, enums as strings,
 * tagged unions with the tag as a property, and text written as UTF-8 rather
 * than \u-escaped. The UI, the extension and every state file on disk read
 * exactly this, so it does not change shape lightly. Every assembly that serializes declares its
 * own source-generated context and builds it from PidgeJson.CreateOptions, so
 * all of them agree. Nothing here uses reflection: both apps are NativeAOT.
 */
public static class PidgeJson
{
    public static JsonSerializerOptions CreateOptions(bool indented = false) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        AllowOutOfOrderMetadataProperties = true,
        RespectNullableAnnotations = true,
        // `<`, `'` and non-ASCII text are written as they are: nothing reading
        // this JSON is an HTML page, and state files stay readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = indented,
        IndentSize = 2,
        // \n on every platform, so a state file is the same file everywhere.
        NewLine = "\n",
    };

    /// <summary>A deep copy, by way of the wire format.</summary>
    public static T Clone<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo), typeInfo)!;

    /// <summary>
    /// Structural equality, by way of the wire format, for the model types,
    /// which are classes and so compare by reference.
    /// </summary>
    public static bool Same<T>(T a, T b, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToUtf8Bytes(a, typeInfo).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(b, typeInfo));
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(HttpRequest))]
[JsonSerializable(typeof(HttpResponse))]
[JsonSerializable(typeof(RequestError))]
[JsonSerializable(typeof(AuthConfig))]
[JsonSerializable(typeof(RequestBody))]
[JsonSerializable(typeof(TlsSettings))]
[JsonSerializable(typeof(TlsDetails))]
[JsonSerializable(typeof(KeyValueEntry))]
[JsonSerializable(typeof(List<KeyValueEntry>))]
[JsonSerializable(typeof(List<HttpRequest>))]
public partial class CoreJsonContext : JsonSerializerContext
{
    /// <summary>Compact.</summary>
    public static CoreJsonContext Wire { get; } = new(PidgeJson.CreateOptions());

    /// <summary>Two-space indent.</summary>
    public static CoreJsonContext Pretty { get; } = new(PidgeJson.CreateOptions(indented: true));
}
