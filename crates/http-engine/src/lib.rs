//! The HTTP engine.
//!
//! This crate knows nothing about Tauri, VS Code, or any UI. It takes an
//! [`HttpRequest`], sends it, and returns a normalized [`HttpResponse`] or
//! [`RequestError`].

mod build;
mod cancel;
mod digest;
mod error;
mod ntlm;
mod oauth1;
mod oauth2;
mod peer_cert;
mod tls;
mod url_input;

use std::time::{Duration, Instant};

use api_client_core::{
    AuthConfig, HttpRequest, HttpResponse, KeyValueEntry, RequestError, RequestErrorKind,
    TlsSettings,
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
    /// OAuth 2 access tokens, kept until they expire so a burst of requests
    /// costs one token request rather than one each. Shared rather than cloned:
    /// a cloned engine is the same client and should reuse the same tokens.
    tokens: std::sync::Arc<oauth2::TokenCache>,
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
            .cookie_store(config.store_cookies)
            // Puts the peer certificate on the response, for the padlock.
            .tls_info(true);

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

        Ok(Self {
            client,
            config,
            tokens: std::sync::Arc::default(),
        })
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

        let send = self.send(&request, started, timeout_ms, &cancellation);

        // The timeout covers the whole exchange, including a digest retry: two
        // round trips the user did not ask for should not buy twice the wait.
        if timeout_ms == 0 {
            send.await
        } else {
            tokio::select! {
                biased;
                () = tokio::time::sleep(Duration::from_millis(timeout_ms)) => {
                    Err(RequestError::timeout(timeout_ms))
                }
                result = send => result,
            }
        }
    }

    /// One request, or two when the server answers a digest challenge.
    async fn send(
        &self,
        request: &HttpRequest,
        started: Instant,
        timeout_ms: u64,
        cancellation: &CancellationHandle,
    ) -> Result<HttpResponse, RequestError> {
        let prepared = build::prepare(&self.client, request)?;
        let warnings = prepared.warnings;

        // OAuth 2 needs a token before anything can be sent, and a failure to
        // get one is an error rather than a response: the request never left.
        let builder = match &request.auth {
            AuthConfig::OAuth2(settings) if request.find_header("authorization").is_none() => {
                let token = oauth2::access_token(&self.client, &self.tokens, settings).await?;
                prepared.builder.bearer_auth(token)
            }
            _ => prepared.builder,
        };

        let mut response = self.run(builder, started, timeout_ms, cancellation).await?;
        response.warnings = warnings.clone();

        // 401 is the only status that carries a challenge, and a header the
        // user typed has already won.
        if response.status != 401 || request.find_header("authorization").is_some() {
            return Ok(response);
        }

        match &request.auth {
            AuthConfig::Digest { username, password } => {
                match digest::answer(request, &response, username, password) {
                    Ok(header) => {
                        let answered = self
                            .send_with_authorization(
                                request,
                                header,
                                started,
                                timeout_ms,
                                cancellation,
                                &self.client,
                            )
                            .await?;
                        Ok(HttpResponse {
                            warnings,
                            ..answered
                        })
                    }
                    // The 401 is the honest answer; the note says why there was
                    // no second attempt.
                    Err(why) => {
                        response.warnings.push(why);
                        Ok(response)
                    }
                }
            }

            AuthConfig::Ntlm {
                username,
                password,
                domain,
                workstation,
            } => {
                match self
                    .ntlm_handshake(
                        request,
                        &response,
                        username,
                        password,
                        domain,
                        workstation,
                        started,
                        timeout_ms,
                        cancellation,
                    )
                    .await
                {
                    Ok(Some(answered)) => Ok(HttpResponse {
                        warnings,
                        ..answered
                    }),
                    Ok(None) => Ok(response),
                    Err(why) => {
                        response.warnings.push(why);
                        Ok(response)
                    }
                }
            }

            _ => Ok(response),
        }
    }

    /// The three legs of NTLM, on a connection kept to this exchange.
    ///
    /// NTLM authenticates a connection rather than a request, so all three
    /// messages have to travel the same socket. Nothing in reqwest pins one, so
    /// this builds a client whose pool holds a single connection and is used by
    /// nothing else: the negotiate and authenticate messages go out back to
    /// back, and the idle connection the first leg returns is the only one the
    /// second can take.
    ///
    /// `Ok(None)` means the server offered something other than NTLM, in which
    /// case its 401 stands as the answer.
    #[allow(clippy::too_many_arguments)]
    async fn ntlm_handshake(
        &self,
        request: &HttpRequest,
        challenged: &HttpResponse,
        username: &str,
        password: &str,
        domain: &str,
        workstation: &str,
        started: Instant,
        timeout_ms: u64,
        cancellation: &CancellationHandle,
    ) -> Result<Option<HttpResponse>, String> {
        let offered = challenged.header("www-authenticate").unwrap_or_default();
        if !offered
            .split(',')
            .any(|scheme| scheme.trim().to_ascii_lowercase().starts_with("ntlm"))
        {
            return Ok(None);
        }

        let client = self
            .ntlm_client()
            .map_err(|err| format!("An NTLM connection could not be opened: {}.", err.message))?;

        // Leg one: what the client can do. The server answers 401 again, this
        // time with its challenge.
        let negotiated = self
            .send_with_authorization(
                request,
                ntlm::negotiate_header(domain, workstation),
                started,
                timeout_ms,
                cancellation,
                &client,
            )
            .await
            .map_err(|err| format!("The NTLM negotiation failed: {}.", err.message))?;

        let challenge_header = negotiated
            .header("www-authenticate")
            .ok_or_else(|| "The server did not answer the NTLM negotiation.".to_string())?;
        let challenge = ntlm::parse_challenge(challenge_header)?;

        // Leg two: the response computed from that challenge.
        let answered = self
            .send_with_authorization(
                request,
                ntlm::authenticate_header(
                    &challenge,
                    username,
                    password,
                    domain,
                    workstation,
                    ntlm::client_challenge(),
                    ntlm::timestamp(),
                ),
                started,
                timeout_ms,
                cancellation,
                &client,
            )
            .await
            .map_err(|err| format!("The NTLM authentication failed: {}.", err.message))?;

        Ok(Some(answered))
    }

    /// A client for one NTLM exchange: one connection, shared with nothing.
    fn ntlm_client(&self) -> Result<reqwest::Client, RequestError> {
        let builder = reqwest::Client::builder()
            .user_agent(self.config.user_agent.clone())
            // Redirects would start a new connection mid-handshake.
            .redirect(reqwest::redirect::Policy::none())
            .cookie_store(self.config.store_cookies)
            .pool_max_idle_per_host(1)
            .tls_info(true);

        tls::apply(builder, &self.config.tls)?
            .build()
            .map_err(|err| {
                RequestError::new(RequestErrorKind::Other, "Could not start the NTLM client.")
                    .with_detail(error::chain(&err))
            })
    }

    /// Sends the request again with an `Authorization` header the engine has
    /// computed, optionally on a client of its own.
    async fn send_with_authorization(
        &self,
        request: &HttpRequest,
        header: String,
        started: Instant,
        timeout_ms: u64,
        cancellation: &CancellationHandle,
        client: &reqwest::Client,
    ) -> Result<HttpResponse, RequestError> {
        let prepared = build::prepare(client, request)?;
        let builder = prepared
            .builder
            .header(reqwest::header::AUTHORIZATION, header);
        self.run(builder, started, timeout_ms, cancellation).await
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
        // Read before the body: consuming the response takes the extensions.
        let tls = response
            .extensions()
            .get::<reqwest::tls::TlsInfo>()
            .map(|info| Box::new(peer_cert::describe(info)));
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
            tls,
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
