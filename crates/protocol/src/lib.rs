//! The sidecar wire protocol.
//!
//! One JSON object per line. Every line carries the protocol version, a message
//! type, and a correlation id where one applies, so a version mismatch between
//! the extension and the bundled binary fails loudly instead of subtly.
//!
//! stdout carries protocol messages and nothing else. Logs go to stderr.

use api_client_codegen::{CodeTarget, ExportFormat};
use api_client_core::{HttpRequest, HttpResponse, RequestError};
use api_client_storage::{AppState, HistoryEntry};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use ts_rs::TS;

/// Bump on any breaking change to the message shapes below.
///
/// 2 added `GenerateCode`, 3 `ExportSavedRequests`, 4 `ImportSavedRequests`. A new message is additive for the sidecar, but an
/// extension that sends one to a build that predates it would get a protocol
/// error in place of an answer — which is the mismatch this number exists to
/// catch at the handshake instead.
pub const PROTOCOL_VERSION: u32 = 4;

/// Extension host to sidecar.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, TS)]
#[serde(rename_all = "camelCase")]
#[ts(export)]
pub struct ClientEnvelope {
    /// Protocol version, on every message.
    pub v: u32,
    /// Correlation id, present for anything that expects a reply.
    pub id: Option<String>,
    pub msg: ClientMessage,
}

impl ClientEnvelope {
    pub fn new(id: Option<String>, msg: ClientMessage) -> Self {
        Self {
            v: PROTOCOL_VERSION,
            id,
            msg,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, TS)]
#[serde(tag = "type", rename_all = "camelCase")]
#[ts(export)]
pub enum ClientMessage {
    #[serde(rename_all = "camelCase")]
    Handshake {
        client_name: String,
        client_version: String,
    },
    #[serde(rename_all = "camelCase")]
    SendRequest {
        request: HttpRequest,
        /// Already-flattened environment variables for this send.
        #[serde(default)]
        variables: BTreeMap<String, String>,
    },
    #[serde(rename_all = "camelCase")]
    CancelRequest {
        request_id: String,
    },
    /// Write a request out as code, without sending it.
    #[serde(rename_all = "camelCase")]
    GenerateCode {
        request: HttpRequest,
        target: CodeTarget,
        #[serde(default)]
        variables: BTreeMap<String, String>,
    },
    /// Read the persisted state. The sidecar owns the file so that both
    /// frontends go through the same storage code.
    LoadState,
    #[serde(rename_all = "camelCase")]
    SaveState {
        state: AppState,
    },
    #[serde(rename_all = "camelCase")]
    SaveRequest {
        saved_request_id: Option<String>,
        name: String,
        request: HttpRequest,
    },
    #[serde(rename_all = "camelCase")]
    DeleteSavedRequest {
        saved_request_id: String,
    },
    ClearHistory,
    /// Write saved requests out as a file's contents. The extension host
    /// asks where to put it and writes it; the sidecar never touches a path
    /// it was handed.
    #[serde(rename_all = "camelCase")]
    ExportSavedRequests {
        saved_request_ids: Vec<String>,
        format: ExportFormat,
        include_secrets: bool,
    },
    /// Add the saved requests in a file's contents: a JSON export or a
    /// `.http` file. The extension host picks and reads the file.
    #[serde(rename_all = "camelCase")]
    ImportSavedRequests {
        contents: String,
    },
    Shutdown,
}

/// Sidecar to extension host.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, TS)]
#[serde(rename_all = "camelCase")]
#[ts(export)]
pub struct ServerEnvelope {
    pub v: u32,
    pub id: Option<String>,
    pub msg: ServerMessage,
}

impl ServerEnvelope {
    pub fn new(id: Option<String>, msg: ServerMessage) -> Self {
        Self {
            v: PROTOCOL_VERSION,
            id,
            msg,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, TS)]
#[serde(tag = "type", rename_all = "camelCase")]
#[ts(export)]
pub enum ServerMessage {
    #[serde(rename_all = "camelCase")]
    HandshakeOk {
        server_name: String,
        server_version: String,
        protocol_version: u32,
    },
    /// Sent when the versions do not match, immediately before exiting.
    #[serde(rename_all = "camelCase")]
    HandshakeError {
        message: String,
        expected_protocol_version: u32,
        received_protocol_version: u32,
    },
    #[serde(rename_all = "camelCase")]
    RequestComplete {
        response: HttpResponse,
        /// The row this send added to history, so the webview can show it
        /// without reloading the whole state.
        history_entry: Option<HistoryEntry>,
    },
    #[serde(rename_all = "camelCase")]
    RequestError {
        error: RequestError,
        history_entry: Option<HistoryEntry>,
    },
    /// The snippet, or the reason there is not one: an unresolved variable, or
    /// a URL that will not parse. The webview shows the error where the code
    /// would have been.
    #[serde(rename_all = "camelCase")]
    CodeGenerated {
        code: Option<String>,
        error: Option<RequestError>,
    },
    /// Acknowledges a cancel, whether or not anything was in flight.
    #[serde(rename_all = "camelCase")]
    RequestCancelled { was_in_flight: bool },
    #[serde(rename_all = "camelCase")]
    StateLoaded {
        state: AppState,
        /// Present when the state file had to be recovered; safe to show.
        recovery: Option<String>,
        storage_path: String,
        /// The host's own version, for the About tab.
        version: String,
    },
    /// Acknowledges any state-mutating message, carrying the new state.
    #[serde(rename_all = "camelCase")]
    StateSaved { state: AppState },
    #[serde(rename_all = "camelCase")]
    SavedRequestsExported { contents: String },
    /// What an import added; the fields of `api_client_session::ImportOutcome`.
    #[serde(rename_all = "camelCase")]
    SavedRequestsImported {
        state: AppState,
        imported: u32,
        undefined_variables: Vec<String>,
        skipped: Vec<String>,
        plain_secrets: bool,
    },
    /// The file was not one that can be imported; nothing was changed.
    #[serde(rename_all = "camelCase")]
    ImportRejected { message: String },
    #[serde(rename_all = "camelCase")]
    StorageError { message: String },
    /// The sidecar could not make sense of a line at all.
    #[serde(rename_all = "camelCase")]
    ProtocolError { message: String },
}

/// Serializes one message as a single line, newline included.
pub fn encode_line<T: Serialize>(message: &T) -> Result<String, serde_json::Error> {
    let mut line = serde_json::to_string(message)?;
    line.push('\n');
    Ok(line)
}

/// Parses one line. Blank lines are not messages.
pub fn decode_line<T: for<'de> Deserialize<'de>>(line: &str) -> Result<T, serde_json::Error> {
    serde_json::from_str(line.trim())
}

/// Checks a received version against this build's. `None` means compatible.
pub fn version_mismatch(received: u32) -> Option<ServerMessage> {
    if received == PROTOCOL_VERSION {
        return None;
    }
    Some(ServerMessage::HandshakeError {
        message: format!(
            "Protocol mismatch: the extension speaks version {received}, this sidecar speaks version {PROTOCOL_VERSION}. Reinstall the extension so the two match."
        ),
        expected_protocol_version: PROTOCOL_VERSION,
        received_protocol_version: received,
    })
}
