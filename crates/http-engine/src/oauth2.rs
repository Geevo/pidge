//! The OAuth 2 grants that are a request to a token endpoint.
//!
//! Client credentials, password and refresh token all come down to the same
//! thing: POST a form, read an access token, send it as a bearer token. The
//! interactive flows are not here — those need a browser and a redirect, which
//! is a different kind of program from this one.
//!
//! Tokens are cached until they expire, so a burst of requests to the same API
//! costs one token request rather than one each.

use std::collections::HashMap;
use std::sync::Mutex;
use std::time::{Duration, Instant};

use api_client_core::{
    OAuth2ClientAuth, OAuth2Grant, OAuth2Settings, RequestError, RequestErrorKind,
};
use serde::Deserialize;

/// Tokens in hand, by the settings that fetched them.
#[derive(Debug, Default)]
pub(crate) struct TokenCache {
    entries: Mutex<HashMap<String, CachedToken>>,
}

#[derive(Debug, Clone)]
struct CachedToken {
    access_token: String,
    /// `None` when the endpoint did not say, in which case it is used once and
    /// fetched again next time rather than kept forever.
    expires_at: Option<Instant>,
}

/*
 * A token is dropped a little before it expires. Sending one that expires in
 * transit costs a confusing 401; half a minute of unused life costs nothing.
 */
const EXPIRY_MARGIN: Duration = Duration::from_secs(30);

impl TokenCache {
    fn get(&self, key: &str) -> Option<String> {
        let entries = self.entries.lock().ok()?;
        let cached = entries.get(key)?;
        match cached.expires_at {
            Some(at) if at <= Instant::now() + EXPIRY_MARGIN => None,
            Some(_) => Some(cached.access_token.clone()),
            None => None,
        }
    }

    fn put(&self, key: String, token: CachedToken) {
        if let Ok(mut entries) = self.entries.lock() {
            entries.insert(key, token);
        }
    }
}

/// What the token endpoint sends back. Only `access_token` is required of it.
#[derive(Debug, Deserialize)]
struct TokenResponse {
    access_token: Option<String>,
    expires_in: Option<u64>,
    error: Option<String>,
    error_description: Option<String>,
}

/// Fetches an access token, or returns the cached one.
pub(crate) async fn access_token(
    client: &reqwest::Client,
    cache: &TokenCache,
    settings: &OAuth2Settings,
) -> Result<String, RequestError> {
    if settings.token_url.trim().is_empty() {
        return Err(failed("No token URL is set for OAuth 2."));
    }

    let key = cache_key(settings);
    if let Some(token) = cache.get(&key) {
        return Ok(token);
    }

    let mut form: Vec<(&str, &str)> = vec![("grant_type", settings.grant.as_str())];
    match settings.grant {
        OAuth2Grant::ClientCredentials => {}
        OAuth2Grant::Password => {
            form.push(("username", &settings.username));
            form.push(("password", &settings.password));
        }
        OAuth2Grant::RefreshToken => {
            if settings.refresh_token.trim().is_empty() {
                return Err(failed("No refresh token is set."));
            }
            form.push(("refresh_token", settings.refresh_token.trim()));
        }
    }
    if !settings.scope.trim().is_empty() {
        form.push(("scope", settings.scope.trim()));
    }

    let mut request = client.post(settings.token_url.trim()).header(
        reqwest::header::ACCEPT,
        reqwest::header::HeaderValue::from_static("application/json"),
    );
    request = match settings.client_auth {
        OAuth2ClientAuth::BasicHeader => {
            request.basic_auth(&settings.client_id, Some(&settings.client_secret))
        }
        OAuth2ClientAuth::RequestBody => {
            form.push(("client_id", &settings.client_id));
            form.push(("client_secret", &settings.client_secret));
            request
        }
    };

    let response = request.form(&form).send().await.map_err(|err| {
        failed("The token request could not be sent.").with_detail(err.to_string())
    })?;

    let status = response.status();
    let body = response.text().await.unwrap_or_default();
    let parsed: Option<TokenResponse> = serde_json::from_str(&body).ok();

    // A failed token request is reported in its own terms: the endpoint's
    // `error` field says more than the status code does.
    if let Some(TokenResponse {
        error: Some(error),
        error_description,
        ..
    }) = &parsed
    {
        let detail = error_description.clone().unwrap_or_else(|| body.clone());
        return Err(
            failed(&format!("The token endpoint refused the request: {error}."))
                .with_detail(detail),
        );
    }

    if !status.is_success() {
        return Err(
            failed(&format!("The token endpoint answered {}.", status.as_u16()))
                .with_detail(first_line(&body)),
        );
    }

    let token = parsed.and_then(|parsed| {
        let access_token = parsed.access_token?;
        (!access_token.trim().is_empty()).then_some((access_token, parsed.expires_in))
    });

    let Some((access_token, expires_in)) = token else {
        return Err(
            failed("The token endpoint returned no access_token.").with_detail(first_line(&body))
        );
    };

    cache.put(
        key,
        CachedToken {
            access_token: access_token.clone(),
            expires_at: expires_in.map(|seconds| Instant::now() + Duration::from_secs(seconds)),
        },
    );

    Ok(access_token)
}

/// Everything that decides which token comes back, so changing any of it asks
/// for a new one rather than reusing a token minted for something else.
fn cache_key(settings: &OAuth2Settings) -> String {
    format!(
        "{}\u{1f}{}\u{1f}{}\u{1f}{}\u{1f}{}\u{1f}{}",
        settings.token_url.trim(),
        settings.grant.as_str(),
        settings.client_id,
        settings.scope.trim(),
        settings.username,
        settings.refresh_token.trim(),
    )
}

fn failed(message: &str) -> RequestError {
    RequestError::new(RequestErrorKind::Auth, message)
}

fn first_line(body: &str) -> String {
    body.lines()
        .next()
        .unwrap_or_default()
        .chars()
        .take(300)
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn settings() -> OAuth2Settings {
        OAuth2Settings {
            token_url: "https://example.com/token".to_string(),
            client_id: "id".to_string(),
            ..OAuth2Settings::default()
        }
    }

    #[test]
    fn a_cached_token_is_reused_until_it_is_nearly_expired() {
        let cache = TokenCache::default();
        let key = cache_key(&settings());

        cache.put(
            key.clone(),
            CachedToken {
                access_token: "fresh".to_string(),
                expires_at: Some(Instant::now() + Duration::from_secs(600)),
            },
        );
        assert_eq!(cache.get(&key).as_deref(), Some("fresh"));

        cache.put(
            key.clone(),
            CachedToken {
                access_token: "nearly-gone".to_string(),
                expires_at: Some(Instant::now() + Duration::from_secs(5)),
            },
        );
        assert_eq!(cache.get(&key), None, "a token about to expire was reused");
    }

    #[test]
    fn a_token_with_no_expiry_is_not_kept() {
        let cache = TokenCache::default();
        let key = cache_key(&settings());
        cache.put(
            key.clone(),
            CachedToken {
                access_token: "unknown-life".to_string(),
                expires_at: None,
            },
        );
        assert_eq!(cache.get(&key), None);
    }

    #[test]
    fn the_key_changes_with_anything_that_changes_the_token() {
        let base = cache_key(&settings());

        let mut other_scope = settings();
        other_scope.scope = "read:things".to_string();
        assert_ne!(base, cache_key(&other_scope));

        let mut other_client = settings();
        other_client.client_id = "someone-else".to_string();
        assert_ne!(base, cache_key(&other_client));
    }
}
