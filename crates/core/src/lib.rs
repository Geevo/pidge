//! Shared domain models for the API client.
//!
//! Everything in this crate is platform agnostic: it is used by the HTTP engine,
//! the storage layer, the Tauri commands and the sidecar protocol alike.

mod base64_bytes;
mod error;
pub mod redact;
mod request;
mod response;
mod tls;

pub use error::{RequestError, RequestErrorKind};
pub use request::{
    AuthConfig, HttpMethod, HttpRequest, KeyValueEntry, MultipartEntry, MultipartValue, RequestBody,
};
pub use response::HttpResponse;
pub use tls::{ClientIdentitySettings, TlsSettings};

/// Generates an identifier for a request, tab, or key/value row.
pub fn new_id() -> String {
    uuid::Uuid::new_v4().to_string()
}
