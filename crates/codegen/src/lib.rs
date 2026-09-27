//! The request, written out as code for somebody else's client.
//!
//! The point of this crate is that the snippet and the Send button agree. Both
//! are built on [`api_client_http_engine::effective`], so the URL, the query
//! string, the headers and the content type in the generated code are the ones
//! that would actually go on the wire — not a second guess at them.
//!
//! What a snippet cannot reproduce it says out loud, in a comment, rather than
//! quietly leaving out: a digest challenge is a flag, but an OAuth 1 signature
//! is a program.

mod csharp;
mod curl;
mod go;
mod http_file;
mod java;
mod node;
mod php;
mod powershell;
mod python;
mod rust;
mod text;
mod zig;

pub use http_file::http_file;

use api_client_core::{AuthConfig, ClientIdentitySettings, HttpRequest, RequestError, TlsSettings};
use api_client_http_engine::{AuthPlan, EffectiveRequest};
use serde::{Deserialize, Serialize};
use ts_rs::TS;

/// What a file of exported saved requests is.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, TS)]
#[serde(rename_all = "camelCase")]
#[ts(export)]
pub enum ExportFormat {
    /// The saved requests exactly as stored. Nothing is lost.
    Json,
    /// Plain-text blocks that VS Code's REST Client and JetBrains run. See
    /// [`http_file`].
    Http,
}

/// One way of writing a request out: a language, and the library it uses.
///
/// Flat rather than a language and a library side by side, because most of
/// these are a single choice and a pair would make every one of them carry an
/// empty half. The picker groups them back together by [`CodeTarget::language`].
///
/// The wire names of the first four are what they have always been, so a
/// webview and a sidecar that disagree about this list still understand each
/// other about those.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, TS)]
#[ts(export)]
pub enum CodeTarget {
    #[serde(rename = "curl")]
    Curl,
    #[serde(rename = "powershell")]
    PowerShell,
    #[serde(rename = "python")]
    Python,
    #[serde(rename = "csharp")]
    CSharp,
    #[serde(rename = "rust-blocking")]
    RustBlocking,
    #[serde(rename = "rust-async")]
    RustAsync,
    #[serde(rename = "node-fetch")]
    NodeFetch,
    #[serde(rename = "node-axios")]
    NodeAxios,
    #[serde(rename = "go")]
    Go,
    #[serde(rename = "java-httpclient")]
    JavaHttpClient,
    #[serde(rename = "java-okhttp")]
    JavaOkHttp,
    #[serde(rename = "php-curl")]
    PhpCurl,
    #[serde(rename = "php-guzzle")]
    PhpGuzzle,
    #[serde(rename = "zig")]
    Zig,
}

impl CodeTarget {
    /// In the order the picker offers them, grouped by language.
    pub const ALL: [CodeTarget; 14] = [
        CodeTarget::Curl,
        CodeTarget::PowerShell,
        CodeTarget::Python,
        CodeTarget::CSharp,
        CodeTarget::RustBlocking,
        CodeTarget::RustAsync,
        CodeTarget::NodeFetch,
        CodeTarget::NodeAxios,
        CodeTarget::Go,
        CodeTarget::JavaHttpClient,
        CodeTarget::JavaOkHttp,
        CodeTarget::PhpCurl,
        CodeTarget::PhpGuzzle,
        CodeTarget::Zig,
    ];

    /// The language, which is what the picker lists.
    pub fn language(self) -> &'static str {
        match self {
            CodeTarget::Curl => "curl",
            CodeTarget::PowerShell => "PowerShell",
            CodeTarget::Python => "Python",
            CodeTarget::CSharp => "C#",
            CodeTarget::RustBlocking | CodeTarget::RustAsync => "Rust",
            CodeTarget::NodeFetch | CodeTarget::NodeAxios => "Node.js",
            CodeTarget::Go => "Go",
            CodeTarget::JavaHttpClient | CodeTarget::JavaOkHttp => "Java",
            CodeTarget::PhpCurl | CodeTarget::PhpGuzzle => "PHP",
            CodeTarget::Zig => "Zig",
        }
    }

    /// The library underneath, which is what the tabs under the picker offer.
    /// A language with only one way of doing this has no tabs and no label.
    pub fn library(self) -> Option<&'static str> {
        match self {
            CodeTarget::Curl | CodeTarget::PowerShell | CodeTarget::Python | CodeTarget::CSharp => {
                None
            }
            CodeTarget::RustBlocking => Some("blocking"),
            CodeTarget::RustAsync => Some("async"),
            CodeTarget::NodeFetch => Some("fetch"),
            CodeTarget::NodeAxios => Some("axios"),
            CodeTarget::Go => None,
            CodeTarget::JavaHttpClient => Some("HttpClient"),
            CodeTarget::JavaOkHttp => Some("OkHttp"),
            CodeTarget::PhpCurl => Some("cURL"),
            CodeTarget::PhpGuzzle => Some("Guzzle"),
            CodeTarget::Zig => None,
        }
    }

    /// What the picker calls it, language and library together.
    pub fn label(self) -> String {
        match self.library() {
            Some(library) => format!("{} ({library})", self.language()),
            None => self.language().to_string(),
        }
    }
}

/// The settings that are the app's rather than the request's.
///
/// Generated code has no app around it to inherit these from, so they have to
/// be written into the snippet. Leaving them out is how a snippet that follows
/// no redirects, or checks a certificate the app was told not to, ends up
/// behaving differently from the send it was copied from.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ClientOptions {
    /// The default, used when the request does not override it. 0 is no limit.
    pub timeout_ms: u64,
    pub follow_redirects: bool,
    /// Trust and identity, straight from Settings.
    pub tls: TlsSettings,
}

impl ClientOptions {
    /// What the app does when nobody has changed anything.
    pub fn standard() -> Self {
        Self {
            timeout_ms: api_client_http_engine::DEFAULT_TIMEOUT_MS,
            follow_redirects: true,
            tls: TlsSettings::default(),
        }
    }
}

/// Writes `request` as `target`.
///
/// The only failure is a URL that cannot be parsed, because there is then no
/// request to describe. Everything else a generator cannot express becomes a
/// comment in the code it returns.
pub fn generate(
    request: &HttpRequest,
    options: &ClientOptions,
    target: CodeTarget,
) -> Result<String, RequestError> {
    let effective = api_client_http_engine::effective(request)?;
    let plan = Plan {
        request,
        effective: &effective,
        options,
    };

    Ok(match target {
        CodeTarget::Curl => curl::generate(&plan),
        CodeTarget::PowerShell => powershell::generate(&plan),
        CodeTarget::Python => python::generate(&plan),
        CodeTarget::CSharp => csharp::generate(&plan),
        CodeTarget::RustBlocking => rust::generate(&plan, rust::Style::Blocking),
        CodeTarget::RustAsync => rust::generate(&plan, rust::Style::Async),
        CodeTarget::NodeFetch => node::generate(&plan, node::Library::Fetch),
        CodeTarget::NodeAxios => node::generate(&plan, node::Library::Axios),
        CodeTarget::Go => go::generate(&plan),
        CodeTarget::JavaHttpClient => java::generate(&plan, java::Library::HttpClient),
        CodeTarget::JavaOkHttp => java::generate(&plan, java::Library::OkHttp),
        CodeTarget::PhpCurl => php::generate(&plan, php::Library::Curl),
        CodeTarget::PhpGuzzle => php::generate(&plan, php::Library::Guzzle),
        CodeTarget::Zig => zig::generate(&plan),
    })
}

/// Which kind of file a client certificate is in.
///
/// The engine decides this by reading the first bytes, which nothing here can
/// do. The extension is what the settings dialog asks for and is the better
/// signal; a password settles the rest, because a PKCS#12 bundle is always
/// encrypted and the PEM the engine accepts never is.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum IdentityKind {
    Pem,
    Pkcs12,
}

impl IdentityKind {
    fn of(identity: &ClientIdentitySettings) -> Self {
        let path = identity.path.trim().to_ascii_lowercase();
        if path.ends_with(".p12") || path.ends_with(".pfx") {
            return IdentityKind::Pkcs12;
        }
        if identity.password.is_some() {
            return IdentityKind::Pkcs12;
        }
        IdentityKind::Pem
    }
}

/// Everything a generator is handed: what the user typed, what that works out
/// to, and the client-wide settings around it.
pub(crate) struct Plan<'a> {
    pub request: &'a HttpRequest,
    pub effective: &'a EffectiveRequest,
    pub options: &'a ClientOptions,
}

impl Plan<'_> {
    pub fn method(&self) -> &'static str {
        self.request.method.as_str()
    }

    /// Basic auth, when the Auth tab is what supplies it.
    ///
    /// Every one of these languages has its own way to say a username and a
    /// password, and all of them read better than the base64 the header would
    /// carry. `None` covers a user who typed the Authorization header
    /// themselves: that one is passed through as typed.
    pub fn basic(&self) -> Option<(&str, &str)> {
        match (&self.effective.auth, &self.request.auth) {
            (AuthPlan::Header { .. }, AuthConfig::Basic { username, password }) => {
                Some((username.as_str(), password.as_str()))
            }
            _ => None,
        }
    }

    /// The headers to write out, less the one [`Plan::basic`] has taken over.
    pub fn headers(&self) -> Vec<(String, String)> {
        let taken = self.basic().is_some();
        self.effective
            .headers
            .iter()
            .filter(|(name, _)| !(taken && name.eq_ignore_ascii_case("authorization")))
            .cloned()
            .collect()
    }

    /// Milliseconds, the request's own override ahead of the app's default.
    pub fn timeout_ms(&self) -> u64 {
        self.request.timeout_ms.unwrap_or(self.options.timeout_ms)
    }

    /// The client certificate to present, with the kind of file it is in.
    pub fn identity(&self) -> Option<(&ClientIdentitySettings, IdentityKind)> {
        let identity = self.options.tls.client_identity.as_ref()?;
        if identity.path.trim().is_empty() {
            return None;
        }
        Some((identity, IdentityKind::of(identity)))
    }

    /// The extra CA files to trust. Blank rows are skipped, as the engine
    /// skips them.
    pub fn extra_ca_files(&self) -> Vec<&str> {
        self.options
            .tls
            .extra_ca_files
            .iter()
            .map(|path| path.trim())
            .filter(|path| !path.is_empty())
            .collect()
    }

    /// True when those CAs are added to the system store rather than replacing
    /// it — which none of these clients can do, and all of them have to be told.
    pub fn merges_ca_files(&self) -> bool {
        self.options.tls.use_system_roots
    }

    pub fn accepts_invalid_certs(&self) -> bool {
        self.options.tls.accept_invalid_certs
    }

    /// What this snippet cannot do, in plain sentences for each generator to
    /// comment in its own way.
    ///
    /// Only the things a reader would otherwise assume were covered. A digest
    /// challenge is answered by a flag in all four languages and says nothing;
    /// an OAuth 1 signature is a library, and silence about it would be a lie.
    pub fn notes(&self) -> Vec<String> {
        let mut notes = Vec::new();

        match &self.effective.auth {
            AuthPlan::OAuth1(_) => notes.push(
                "OAuth 1.0a signs every request over its URL and body. This snippet does not \
                 sign anything; the app does it at send time."
                    .to_string(),
            ),
            AuthPlan::OAuth2 { token_url } => {
                let endpoint = if token_url.trim().is_empty() {
                    "the token endpoint".to_string()
                } else {
                    token_url.clone()
                };
                notes.push(format!(
                    "OAuth 2: the app fetches a token from {endpoint} before it sends. Get one \
                     the same way and send it as an Authorization: Bearer header."
                ));
            }
            _ => {}
        }

        notes.extend(self.effective.warnings.iter().cloned());
        notes
    }
}
