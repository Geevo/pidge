use serde::{Deserialize, Serialize};
use ts_rs::TS;

/// Normalized failure categories. The UI switches on these; `detail` carries
/// the technical chain for diagnostics without dumping it into the main view.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, TS)]
#[serde(rename_all = "camelCase")]
#[ts(export)]
pub enum RequestErrorKind {
    InvalidUrl,
    UnsupportedScheme,
    UnresolvedVariable,
    InvalidHeader,
    Dns,
    ConnectionRefused,
    ConnectionFailed,
    Tls,
    Timeout,
    Cancelled,
    TooManyRedirects,
    Redirect,
    /// The auth helper could not get credentials, e.g. a token endpoint that
    /// refused. The request itself was never sent.
    Auth,
    BodySerialization,
    BodyRead,
    ResponseTooLarge,
    Io,
    Other,
}

impl RequestErrorKind {
    /// Short label for the response pane.
    pub fn title(self) -> &'static str {
        match self {
            RequestErrorKind::InvalidUrl => "Invalid URL",
            RequestErrorKind::UnsupportedScheme => "Unsupported scheme",
            RequestErrorKind::UnresolvedVariable => "Unresolved variable",
            RequestErrorKind::InvalidHeader => "Invalid header",
            RequestErrorKind::Auth => "Authentication failed",
            RequestErrorKind::Dns => "DNS lookup failed",
            RequestErrorKind::ConnectionRefused => "Connection refused",
            RequestErrorKind::ConnectionFailed => "Connection failed",
            RequestErrorKind::Tls => "TLS error",
            RequestErrorKind::Timeout => "Timed out",
            RequestErrorKind::Cancelled => "Cancelled",
            RequestErrorKind::TooManyRedirects => "Too many redirects",
            RequestErrorKind::Redirect => "Redirect error",
            RequestErrorKind::BodySerialization => "Could not build request body",
            RequestErrorKind::BodyRead => "Could not read response body",
            RequestErrorKind::ResponseTooLarge => "Response too large",
            RequestErrorKind::Io => "I/O error",
            RequestErrorKind::Other => "Request failed",
        }
    }
}

/// A request failure in a shape both frontends can render directly.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, TS, thiserror::Error)]
#[serde(rename_all = "camelCase")]
#[ts(export)]
#[error("{message}")]
pub struct RequestError {
    pub kind: RequestErrorKind,
    /// Plain-language explanation, safe to show in the response pane.
    pub message: String,
    /// Technical chain for the diagnostics disclosure. May contain library detail.
    pub detail: Option<String>,
}

impl RequestError {
    pub fn new(kind: RequestErrorKind, message: impl Into<String>) -> Self {
        Self {
            kind,
            message: message.into(),
            detail: None,
        }
    }

    pub fn with_detail(mut self, detail: impl Into<String>) -> Self {
        self.detail = Some(detail.into());
        self
    }

    pub fn invalid_url(message: impl Into<String>) -> Self {
        Self::new(RequestErrorKind::InvalidUrl, message)
    }

    pub fn cancelled() -> Self {
        Self::new(RequestErrorKind::Cancelled, "Request cancelled.")
    }

    pub fn timeout(timeout_ms: u64) -> Self {
        Self::new(
            RequestErrorKind::Timeout,
            format!("No response after {timeout_ms} ms."),
        )
    }

    pub fn other(message: impl Into<String>) -> Self {
        Self::new(RequestErrorKind::Other, message)
    }

    pub fn title(&self) -> &'static str {
        self.kind.title()
    }
}
