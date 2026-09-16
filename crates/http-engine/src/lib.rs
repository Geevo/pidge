//! The HTTP engine.
//!
//! This crate knows nothing about Tauri, VS Code, or any UI. It takes an
//! [`HttpRequest`], sends it, and returns a normalized [`HttpResponse`] or
//! [`RequestError`].

mod build;
mod cancel;
mod error;
mod tls;
mod url_input;

use std::time::{Duration, Instant};

use api_client_core::{
    HttpRequest, HttpResponse, KeyValueEntry, RequestError, RequestErrorKind, TlsSettings,
};
use api_client_variables::VariableSet;

pub use cancel::{CancellationHandle, CancellationRegistry};
pub use url_input::normalize_url;

/// 30 seconds, as promised in the docs.
pub const DEFAULT_TIMEOUT_MS: u64 = 30_000;
/// A response bigger than this is truncated rather than allowed to eat all memory.
pub const DEFAULT_MAX_RESPONSE_BYTES: u64 = 50 * 1024 * 1024;
/// Chosen to match what a browser will follow before giving up.
pub const DEFAULT_MAX_REDIRECTS: usize = 10;

/// Knobs the engine exposes. Everything has a sensible default; the UI does not
/// need to set any of it to send a request.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EngineConfig {
    pub default_timeout_ms: u64,
    pub max_response_bytes: u64,
    pub follow_redirects: bool,
    pub max_redirects: usize,
    pub store_cookies: bool,
    pub user_agent: String,
    /// Trust and client-certificate settings.
    pub tls: TlsSettings,
}

impl Default for EngineConfig {
    fn default() -> Self {
        Self {
            default_timeout_ms: DEFAULT_TIMEOUT_MS,
            max_response_bytes: DEFAULT_MAX_RESPONSE_BYTES,
            follow_redirects: true,
            max_redirects: DEFAULT_MAX_REDIRECTS,
            store_cookies: true,
            user_agent: concat!("api-client/", env!("CARGO_PKG_VERSION")).to_string(),
            tls: TlsSettings::default(),
        }
    }
}

/// Owns one reusable connection pool. Build it once and share it.
#[derive(Debug, Clone)]
pub struct HttpEngine {
    client: reqwest::Client,
    config: EngineConfig,
}

impl HttpEngine {
    pub fn new(config: EngineConfig) -> Result<Self, RequestError> {
        let redirect = if config.follow_redirects {
            reqwest::redirect::Policy::limited(config.max_redirects)
        } else {
            reqwest::redirect::Policy::none()
        };

        let builder = reqwest::Client::builder()
            .user_agent(config.user_agent.clone())
            .redirect(redirect)
            .cookie_store(config.store_cookies);

        let client = tls::apply(builder, &config.tls)?.build().map_err(|err| {
            // reqwest defers parsing a DER certificate until the client is
            // built, so a bad certificate surfaces here rather than where it
            // was loaded. If TLS was configured at all, that is the cause worth
            // pointing at.
            if config.tls.is_default() {
                RequestError::new(RequestErrorKind::Other, "Could not start the HTTP client.")
            } else {
                RequestError::new(
                    RequestErrorKind::Tls,
                    "Could not apply the certificate settings. Check that each file is a valid certificate.",
                )
            }
            .with_detail(error::chain(&err))
        })?;

        Ok(Self { client, config })
    }

    pub fn config(&self) -> &EngineConfig {
        &self.config
    }

    /// Sends the request. Variables must already be resolved; use
    /// [`HttpEngine::execute_with_variables`] if they are not.
    pub async fn execute(
        &self,
        request: HttpRequest,
        cancellation: CancellationHandle,
    ) -> Result<HttpResponse, RequestError> {
        let timeout_ms = request.timeout_ms.unwrap_or(self.config.default_timeout_ms);
        let started = Instant::now();

        let prepared = build::prepare(&self.client, &request)?;
        let warnings = prepared.warnings;

        let send = self.run(prepared.builder, started, timeout_ms, &cancellation);

        let response = if timeout_ms == 0 {
            send.await
        } else {
            tokio::select! {
                biased;
                () = tokio::time::sleep(Duration::from_millis(timeout_ms)) => {
                    Err(RequestError::timeout(timeout_ms))
                }
                result = send => result,
            }
        };

        response.map(|mut response| {
            response.warnings = warnings;
            response
        })
    }

    /// Convenience wrapper: resolve `{{variables}}` and then send.
    pub async fn execute_with_variables(
        &self,
        request: HttpRequest,
        variables: &VariableSet,
        cancellation: CancellationHandle,
    ) -> Result<HttpResponse, RequestError> {
        let resolved = api_client_variables::resolve_request(&request, variables)?;
        self.execute(resolved, cancellation).await
    }

    async fn run(
        &self,
        builder: reqwest::RequestBuilder,
        started: Instant,
        timeout_ms: u64,
        cancellation: &CancellationHandle,
    ) -> Result<HttpResponse, RequestError> {
        let response = tokio::select! {
            biased;
            () = cancellation.cancelled() => return Err(RequestError::cancelled()),
            result = builder.send() => result.map_err(|err| error::from_reqwest(err, timeout_ms))?,
        };

        let status = response.status();
        let final_url = response.url().to_string();
        let headers = collect_headers(response.headers());
        let mime_type = response
            .headers()
            .get(reqwest::header::CONTENT_TYPE)
            .and_then(|value| value.to_str().ok())
            .map(|value| value.to_string());

        let (body, truncated) = self.read_body(response, cancellation, timeout_ms).await?;
        let size_bytes = body.len() as u64;

        Ok(HttpResponse {
            status: status.as_u16(),
            status_text: status.canonical_reason().unwrap_or_default().to_string(),
            headers,
            body,
            mime_type,
            duration_ms: started.elapsed().as_millis() as u64,
            size_bytes,
            truncated,
            final_url,
            warnings: Vec::new(),
        })
    }

    /// Streams the body so a huge response is cut off instead of buffered whole,
    /// and so cancellation lands mid-download rather than only before send.
    async fn read_body(
        &self,
        mut response: reqwest::Response,
        cancellation: &CancellationHandle,
        timeout_ms: u64,
    ) -> Result<(Vec<u8>, bool), RequestError> {
        let limit = self.config.max_response_bytes as usize;
        let mut body: Vec<u8> = Vec::new();
        let mut truncated = false;

        loop {
            let chunk = tokio::select! {
                biased;
                () = cancellation.cancelled() => return Err(RequestError::cancelled()),
                chunk = response.chunk() => chunk.map_err(|err| error::from_reqwest(err, timeout_ms))?,
            };

            let Some(chunk) = chunk else { break };

            if body.len() + chunk.len() > limit {
                let room = limit.saturating_sub(body.len());
                body.extend_from_slice(&chunk[..room]);
                truncated = true;
                break;
            }
            body.extend_from_slice(&chunk);
        }

        Ok((body, truncated))
    }
}

fn collect_headers(headers: &reqwest::header::HeaderMap) -> Vec<KeyValueEntry> {
    headers
        .iter()
        .map(|(name, value)| KeyValueEntry {
            id: api_client_core::new_id(),
            enabled: true,
            name: name.as_str().to_string(),
            value: value
                .to_str()
                .map(str::to_string)
                .unwrap_or_else(|_| format!("<{} bytes>", value.len())),
        })
        .collect()
}
