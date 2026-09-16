use serde::{Deserialize, Serialize};
use ts_rs::TS;

use crate::KeyValueEntry;
use crate::tls::TlsDetails;

/// The result of one successful round trip. "Successful" means we got a
/// response, not that the status code was 2xx.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, TS)]
#[serde(rename_all = "camelCase")]
#[ts(export)]
pub struct HttpResponse {
    pub status: u16,
    pub status_text: String,
    pub headers: Vec<KeyValueEntry>,
    /// Raw bytes, base64 encoded on the wire.
    #[serde(with = "crate::base64_bytes")]
    #[ts(type = "string")]
    pub body: Vec<u8>,
    pub mime_type: Option<String>,
    #[ts(type = "number")]
    pub duration_ms: u64,
    #[ts(type = "number")]
    pub size_bytes: u64,
    /// True when the body hit the configured size limit and was cut short.
    pub truncated: bool,
    /// The URL actually reached, after any redirects.
    pub final_url: String,
    /// Non-fatal notes for the user, e.g. an auth helper that was overridden.
    pub warnings: Vec<String>,
    /// The connection's TLS, for the padlock. `None` for plain HTTP.
    ///
    /// Boxed because it is several hundred bytes of certificate that most
    /// responses do not carry, and `HttpResponse` travels inside the sidecar's
    /// message enum, which is as large as its largest variant.
    #[serde(default)]
    pub tls: Option<Box<TlsDetails>>,
}

impl HttpResponse {
    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers
            .iter()
            .find(|h| h.name.eq_ignore_ascii_case(name))
            .map(|h| h.value.as_str())
    }
}
