using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Pidge.Codegen;
using Pidge.Core;
using Pidge.Storage;

namespace Pidge.Protocol;

public static class WireFormat
{
    /// <summary>
    /// Bump on any breaking change to the message shapes.
    ///
    /// 2 added <c>GenerateCode</c>, 3 <c>ExportSavedRequests</c>, 4
    /// <c>ImportSavedRequests</c>. A new message is additive for the sidecar,
    /// but an extension that sends one to a build that predates it would get a
    /// protocol error in place of an answer — which is the mismatch this number
    /// exists to catch at the handshake instead.
    /// </summary>
    public const uint ProtocolVersion = 4;

    /// <summary>Serializes one message as a single line, newline included.</summary>
    public static string EncodeLine<T>(T message, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Serialize(message, typeInfo) + "\n";

    public static string EncodeLine(ClientEnvelope envelope) => EncodeLine(envelope, ProtocolJsonContext.Wire.ClientEnvelope);

    public static string EncodeLine(ServerEnvelope envelope) => EncodeLine(envelope, ProtocolJsonContext.Wire.ServerEnvelope);

    /// <summary>Parses one line. Blank lines are not messages.</summary>
    /// <exception cref="JsonException">The line is not a <typeparamref name="T"/>.</exception>
    public static T DecodeLine<T>(string line, JsonTypeInfo<T> typeInfo)
    {
        T? value;
        try
        {
            value = JsonSerializer.Deserialize(line.Trim(), typeInfo);
        }
        catch (NotSupportedException e)
        {
            // What a missing or unknown message type comes out as: there is no
            // message to fall back to.
            throw new JsonException($"unknown message type: {e.Message}", e);
        }
        return value ?? throw new JsonException($"invalid type: null, expected {typeof(T).Name}");
    }

    /// <exception cref="JsonException">The line is not a client envelope.</exception>
    public static ClientEnvelope DecodeClientLine(string line) => DecodeLine(line, ProtocolJsonContext.Wire.ClientEnvelope);

    /// <exception cref="JsonException">The line is not a server envelope.</exception>
    public static ServerEnvelope DecodeServerLine(string line) => DecodeLine(line, ProtocolJsonContext.Wire.ServerEnvelope);

    /// <summary>Checks a received version against this build's. Null means compatible.</summary>
    public static ServerMessage.HandshakeError? VersionMismatch(uint received)
    {
        if (received == ProtocolVersion)
        {
            return null;
        }
        return new ServerMessage.HandshakeError
        {
            Message =
                $"Protocol mismatch: the extension speaks version {received}, this sidecar speaks version {ProtocolVersion}. Reinstall the extension so the two match.",
            ExpectedProtocolVersion = ProtocolVersion,
            ReceivedProtocolVersion = received,
        };
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ClientEnvelope))]
[JsonSerializable(typeof(ServerEnvelope))]
[JsonSerializable(typeof(ClientMessage))]
[JsonSerializable(typeof(ServerMessage))]
[JsonSerializable(typeof(ServerMessage.RequestError), TypeInfoPropertyName = "ServerRequestError")]
[JsonSerializable(typeof(ServerMessage.StorageError), TypeInfoPropertyName = "ServerStorageError")]
[JsonSerializable(typeof(RequestError))]
[JsonSerializable(typeof(HttpRequest))]
[JsonSerializable(typeof(HttpResponse))]
[JsonSerializable(typeof(AppState))]
[JsonSerializable(typeof(HistoryEntry))]
[JsonSerializable(typeof(CodeTarget))]
[JsonSerializable(typeof(ExportFormat))]
public partial class ProtocolJsonContext : JsonSerializerContext
{
    /// <summary>Compact, one message per line.</summary>
    public static ProtocolJsonContext Wire { get; } = new(PidgeJson.CreateOptions());

    /// <summary>Two-space indent.</summary>
    public static ProtocolJsonContext Pretty { get; } = new(PidgeJson.CreateOptions(indented: true));
}
