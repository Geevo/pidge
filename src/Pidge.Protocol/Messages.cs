using System.Text.Json.Serialization;
using Pidge.Codegen;
using Pidge.Core;
using Pidge.Storage;
using CoreRequestError = Pidge.Core.RequestError;

namespace Pidge.Protocol;

/*
 * The sidecar wire protocol.
 *
 * One JSON object per line. Every line carries the protocol version, a message
 * type, and a correlation id where one applies, so a version mismatch between
 * the extension and the bundled binary fails loudly instead of subtly.
 *
 * stdout carries protocol messages and nothing else. Logs go to stderr.
 */

/// <summary>Extension host to sidecar.</summary>
public sealed class ClientEnvelope
{
    public ClientEnvelope()
    {
    }

    public ClientEnvelope(string? id, ClientMessage msg)
    {
        V = WireFormat.ProtocolVersion;
        Id = id;
        Msg = msg;
    }

    /// <summary>Protocol version, on every message.</summary>
    [JsonRequired]
    public uint V { get; set; }

    /// <summary>Correlation id, present for anything that expects a reply.</summary>
    public string? Id { get; set; }

    [JsonRequired]
    public ClientMessage Msg { get; set; } = new ClientMessage.Shutdown();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Handshake), "handshake")]
[JsonDerivedType(typeof(SendRequest), "sendRequest")]
[JsonDerivedType(typeof(CancelRequest), "cancelRequest")]
[JsonDerivedType(typeof(GenerateCode), "generateCode")]
[JsonDerivedType(typeof(LoadState), "loadState")]
[JsonDerivedType(typeof(SaveState), "saveState")]
[JsonDerivedType(typeof(SaveRequest), "saveRequest")]
[JsonDerivedType(typeof(DeleteSavedRequest), "deleteSavedRequest")]
[JsonDerivedType(typeof(ClearHistory), "clearHistory")]
[JsonDerivedType(typeof(DeleteHistoryEntry), "deleteHistoryEntry")]
[JsonDerivedType(typeof(ExportSavedRequests), "exportSavedRequests")]
[JsonDerivedType(typeof(ImportSavedRequests), "importSavedRequests")]
[JsonDerivedType(typeof(Shutdown), "shutdown")]
public abstract class ClientMessage
{
    private ClientMessage()
    {
    }

    public sealed class Handshake : ClientMessage
    {
        [JsonRequired]
        public string ClientName { get; set; } = "";

        [JsonRequired]
        public string ClientVersion { get; set; } = "";
    }

    public sealed class SendRequest : ClientMessage
    {
        [JsonRequired]
        public HttpRequest Request { get; set; } = new();

        /// <summary>Already-flattened environment variables for this send.</summary>
        public Dictionary<string, string> Variables { get; set; } = [];
    }

    public sealed class CancelRequest : ClientMessage
    {
        [JsonRequired]
        public string RequestId { get; set; } = "";
    }

    /// <summary>Write a request out as code, without sending it.</summary>
    public sealed class GenerateCode : ClientMessage
    {
        [JsonRequired]
        public HttpRequest Request { get; set; } = new();

        [JsonRequired]
        public CodeTarget Target { get; set; }

        public Dictionary<string, string> Variables { get; set; } = [];
    }

    /// <summary>
    /// Read the persisted state. The sidecar owns the file so that both
    /// frontends go through the same storage code.
    /// </summary>
    public sealed class LoadState : ClientMessage;

    public sealed class SaveState : ClientMessage
    {
        [JsonRequired]
        public AppState State { get; set; } = new();
    }

    public sealed class SaveRequest : ClientMessage
    {
        public string? SavedRequestId { get; set; }

        [JsonRequired]
        public string Name { get; set; } = "";

        [JsonRequired]
        public HttpRequest Request { get; set; } = new();
    }

    public sealed class DeleteSavedRequest : ClientMessage
    {
        [JsonRequired]
        public string SavedRequestId { get; set; } = "";
    }

    public sealed class ClearHistory : ClientMessage;

    /// <summary>Removes one entry from history. An id that is not there is not an error.</summary>
    public sealed class DeleteHistoryEntry : ClientMessage
    {
        [JsonRequired]
        public string HistoryEntryId { get; set; } = "";
    }

    /// <summary>
    /// Write saved requests out as a file's contents. The extension host asks
    /// where to put it and writes it; the sidecar never touches a path it was
    /// handed.
    /// </summary>
    public sealed class ExportSavedRequests : ClientMessage
    {
        [JsonRequired]
        public List<string> SavedRequestIds { get; set; } = [];

        [JsonRequired]
        public ExportFormat Format { get; set; }

        [JsonRequired]
        public bool IncludeSecrets { get; set; }
    }

    /// <summary>
    /// Add the saved requests in a file's contents: a JSON export or a
    /// <c>.http</c> file. The extension host picks and reads the file.
    /// </summary>
    public sealed class ImportSavedRequests : ClientMessage
    {
        [JsonRequired]
        public string Contents { get; set; } = "";
    }

    public sealed class Shutdown : ClientMessage;
}

/// <summary>Sidecar to extension host.</summary>
public sealed class ServerEnvelope
{
    public ServerEnvelope()
    {
    }

    public ServerEnvelope(string? id, ServerMessage msg)
    {
        V = WireFormat.ProtocolVersion;
        Id = id;
        Msg = msg;
    }

    [JsonRequired]
    public uint V { get; set; }

    public string? Id { get; set; }

    [JsonRequired]
    public ServerMessage Msg { get; set; } = new ServerMessage.ProtocolError();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HandshakeOk), "handshakeOk")]
[JsonDerivedType(typeof(HandshakeError), "handshakeError")]
[JsonDerivedType(typeof(RequestComplete), "requestComplete")]
[JsonDerivedType(typeof(RequestError), "requestError")]
[JsonDerivedType(typeof(CodeGenerated), "codeGenerated")]
[JsonDerivedType(typeof(RequestCancelled), "requestCancelled")]
[JsonDerivedType(typeof(StateLoaded), "stateLoaded")]
[JsonDerivedType(typeof(StateSaved), "stateSaved")]
[JsonDerivedType(typeof(SavedRequestsExported), "savedRequestsExported")]
[JsonDerivedType(typeof(SavedRequestsImported), "savedRequestsImported")]
[JsonDerivedType(typeof(ImportRejected), "importRejected")]
[JsonDerivedType(typeof(StorageError), "storageError")]
[JsonDerivedType(typeof(ProtocolError), "protocolError")]
public abstract class ServerMessage
{
    private ServerMessage()
    {
    }

    public sealed class HandshakeOk : ServerMessage
    {
        public string ServerName { get; set; } = "";
        public string ServerVersion { get; set; } = "";
        public uint ProtocolVersion { get; set; }
    }

    /// <summary>Sent when the versions do not match, immediately before exiting.</summary>
    public sealed class HandshakeError : ServerMessage
    {
        public string Message { get; set; } = "";
        public uint ExpectedProtocolVersion { get; set; }
        public uint ReceivedProtocolVersion { get; set; }
    }

    public sealed class RequestComplete : ServerMessage
    {
        public HttpResponse Response { get; set; } = new();

        /// <summary>
        /// The row this send added to history, so the webview can show it
        /// without reloading the whole state.
        /// </summary>
        public HistoryEntry? HistoryEntry { get; set; }
    }

    public sealed class RequestError : ServerMessage
    {
        public CoreRequestError Error { get; set; } = CoreRequestError.Other("");
        public HistoryEntry? HistoryEntry { get; set; }
    }

    /// <summary>
    /// The snippet, or the reason there is not one: an unresolved variable, or
    /// a URL that will not parse. The webview shows the error where the code
    /// would have been.
    /// </summary>
    public sealed class CodeGenerated : ServerMessage
    {
        public string? Code { get; set; }
        public CoreRequestError? Error { get; set; }
    }

    /// <summary>Acknowledges a cancel, whether or not anything was in flight.</summary>
    public sealed class RequestCancelled : ServerMessage
    {
        public bool WasInFlight { get; set; }
    }

    public sealed class StateLoaded : ServerMessage
    {
        public AppState State { get; set; } = new();

        /// <summary>Present when the state file had to be recovered; safe to show.</summary>
        public string? Recovery { get; set; }

        public string StoragePath { get; set; } = "";

        /// <summary>The host's own version, for the About tab.</summary>
        public string Version { get; set; } = "";
    }

    /// <summary>Acknowledges any state-mutating message, carrying the new state.</summary>
    public sealed class StateSaved : ServerMessage
    {
        public AppState State { get; set; } = new();
    }

    public sealed class SavedRequestsExported : ServerMessage
    {
        public string Contents { get; set; } = "";
    }

    /// <summary>What an import added; the fields of the session's ImportOutcome.</summary>
    public sealed class SavedRequestsImported : ServerMessage
    {
        public AppState State { get; set; } = new();
        public uint Imported { get; set; }
        public List<string> UndefinedVariables { get; set; } = [];
        public List<string> Skipped { get; set; } = [];
        public bool PlainSecrets { get; set; }
    }

    /// <summary>The file was not one that can be imported; nothing was changed.</summary>
    public sealed class ImportRejected : ServerMessage
    {
        public string Message { get; set; } = "";
    }

    public sealed class StorageError : ServerMessage
    {
        public string Message { get; set; } = "";
    }

    /// <summary>The sidecar could not make sense of a line at all.</summary>
    public sealed class ProtocolError : ServerMessage
    {
        public string Message { get; set; } = "";
    }
}
