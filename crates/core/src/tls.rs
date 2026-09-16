use serde::{Deserialize, Serialize};
use ts_rs::TS;

/// How the client establishes trust and identifies itself.
///
/// The defaults are what a developer expects without configuring anything: the
/// operating system's own trust store, no client certificate, and certificate
/// checking on.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, TS)]
#[serde(rename_all = "camelCase", default)]
#[ts(export)]
pub struct TlsSettings {
    /// Trust the OS store: Windows CryptoAPI, the macOS keychain, or the
    /// system CA bundle on Linux. Turning this off trusts only `extra_ca_files`.
    pub use_system_roots: bool,
    /// Extra CAs to trust, as paths to PEM bundles or single DER certificates.
    /// These are added to the system roots rather than replacing them.
    pub extra_ca_files: Vec<String>,
    /// A certificate to present when a server asks for one (mutual TLS).
    pub client_identity: Option<ClientIdentitySettings>,
    /// Skips certificate verification entirely. For a development server with
    /// a self-signed certificate; adding its CA above is the better answer.
    pub accept_invalid_certs: bool,
}

impl Default for TlsSettings {
    fn default() -> Self {
        Self {
            use_system_roots: true,
            extra_ca_files: Vec::new(),
            client_identity: None,
            accept_invalid_certs: false,
        }
    }
}

impl TlsSettings {
    /// True when nothing here changes the default behaviour, so the engine can
    /// skip loading files it does not need.
    pub fn is_default(&self) -> bool {
        *self == Self::default()
    }
}

/// A client certificate and its private key.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, TS, Default)]
#[serde(rename_all = "camelCase")]
#[ts(export)]
pub struct ClientIdentitySettings {
    /// A PEM file holding the certificate chain and key, or a PKCS#12
    /// (`.p12`/`.pfx`) bundle. The format is detected from the contents.
    pub path: String,
    /// Required for PKCS#12, which is always encrypted. An empty string is a
    /// legitimate PKCS#12 password and is not the same as `None`.
    pub password: Option<String>,
}
