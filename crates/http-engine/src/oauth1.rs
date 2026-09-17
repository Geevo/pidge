//! OAuth 1.0a request signing, RFC 5849.
//!
//! Unlike the others this is arithmetic rather than a conversation: every
//! request carries a signature over its own method, URL and parameters, so
//! there is nothing to fetch and nothing to cache.
//!
//! The fiddly part is the signature base string. Percent-encoding is applied
//! twice — once to each parameter, once to the whole sorted list — and a
//! form-encoded body is signed along with the query. Getting any of that wrong
//! produces a signature the server rejects with no explanation of why, which is
//! exactly why the base string is tested against the example in the RFC.

use std::time::{SystemTime, UNIX_EPOCH};

use api_client_core::{
    OAuth1Settings, OAuth1Signature, RequestBody, RequestError, RequestErrorKind,
};
use base64::Engine as _;
use hmac::{Hmac, Mac};
use percent_encoding::{AsciiSet, NON_ALPHANUMERIC, utf8_percent_encode};

/// RFC 5849 §3.6: everything but unreserved characters is encoded.
const UNRESERVED: &AsciiSet = &NON_ALPHANUMERIC
    .remove(b'-')
    .remove(b'.')
    .remove(b'_')
    .remove(b'~');

/// Builds the `Authorization: OAuth ...` value for one request.
pub(crate) fn authorization(
    method: &str,
    url: &url::Url,
    body: &RequestBody,
    settings: &OAuth1Settings,
    nonce: String,
    timestamp: u64,
) -> Result<String, RequestError> {
    let mut oauth: Vec<(String, String)> = vec![
        ("oauth_consumer_key".into(), settings.consumer_key.clone()),
        ("oauth_nonce".into(), nonce),
        (
            "oauth_signature_method".into(),
            settings.signature_method.as_str().into(),
        ),
        ("oauth_timestamp".into(), timestamp.to_string()),
        ("oauth_version".into(), "1.0".into()),
    ];
    if !settings.token.trim().is_empty() {
        oauth.push(("oauth_token".into(), settings.token.clone()));
    }

    let signature = match settings.signature_method {
        // PLAINTEXT is the signing key itself; there is no base string.
        OAuth1Signature::Plaintext => signing_key(settings),
        method_kind => {
            let base = base_string(method, url, body, &oauth);
            sign(method_kind, &signing_key(settings), &base)?
        }
    };

    let mut parts = Vec::new();
    if !settings.realm.trim().is_empty() {
        parts.push(format!("realm=\"{}\"", encode(settings.realm.trim())));
    }
    for (name, value) in oauth
        .iter()
        .chain(std::iter::once(&("oauth_signature".to_string(), signature)))
    {
        parts.push(format!("{}=\"{}\"", encode(name), encode(value)));
    }

    Ok(format!("OAuth {}", parts.join(", ")))
}

/// `METHOD&url&params`, each part encoded, with the params sorted and encoded
/// a second time as a whole.
fn base_string(
    method: &str,
    url: &url::Url,
    body: &RequestBody,
    oauth: &[(String, String)],
) -> String {
    let mut params: Vec<(String, String)> = url
        .query_pairs()
        .map(|(name, value)| (name.into_owned(), value.into_owned()))
        .collect();

    // A form-encoded body is part of what is being signed (§3.4.1.3.1).
    if let RequestBody::UrlEncoded { entries } = body {
        for entry in entries.iter().filter(|entry| entry.is_active()) {
            params.push((entry.name.trim().to_string(), entry.value.clone()));
        }
    }

    params.extend(oauth.iter().cloned());

    // Sorted by encoded name, then encoded value.
    let mut encoded: Vec<(String, String)> = params
        .into_iter()
        .map(|(name, value)| (encode(&name), encode(&value)))
        .collect();
    encoded.sort();

    let joined = encoded
        .iter()
        .map(|(name, value)| format!("{name}={value}"))
        .collect::<Vec<_>>()
        .join("&");

    format!(
        "{}&{}&{}",
        method.to_ascii_uppercase(),
        encode(&base_url(url)),
        encode(&joined)
    )
}

/// The URL without query, fragment, or a default port (§3.4.1.2).
fn base_url(url: &url::Url) -> String {
    let scheme = url.scheme();
    let host = url.host_str().unwrap_or_default().to_ascii_lowercase();
    let port = match (url.port(), scheme) {
        (None, _) | (Some(80), "http") | (Some(443), "https") => String::new(),
        (Some(port), _) => format!(":{port}"),
    };
    format!("{scheme}://{host}{port}{}", url.path())
}

fn signing_key(settings: &OAuth1Settings) -> String {
    format!(
        "{}&{}",
        encode(&settings.consumer_secret),
        encode(&settings.token_secret)
    )
}

fn sign(method: OAuth1Signature, key: &str, base: &str) -> Result<String, RequestError> {
    let digest = match method {
        OAuth1Signature::HmacSha1 => {
            let mut mac = Hmac::<sha1::Sha1>::new_from_slice(key.as_bytes()).map_err(bad_key)?;
            mac.update(base.as_bytes());
            mac.finalize().into_bytes().to_vec()
        }
        OAuth1Signature::HmacSha256 => {
            let mut mac = Hmac::<sha2::Sha256>::new_from_slice(key.as_bytes()).map_err(bad_key)?;
            mac.update(base.as_bytes());
            mac.finalize().into_bytes().to_vec()
        }
        // Handled by the caller; there is no base string to sign.
        OAuth1Signature::Plaintext => key.as_bytes().to_vec(),
    };

    Ok(base64::engine::general_purpose::STANDARD.encode(digest))
}

fn bad_key(err: impl std::fmt::Display) -> RequestError {
    RequestError::new(
        RequestErrorKind::Auth,
        "The OAuth 1 signing key could not be used.",
    )
    .with_detail(err.to_string())
}

fn encode(value: &str) -> String {
    utf8_percent_encode(value, UNRESERVED).to_string()
}

/// Unique per request, which is all the specification asks of it.
pub(crate) fn nonce() -> String {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|since| since.as_nanos())
        .unwrap_or_default();
    format!("{nanos:x}{:x}", std::process::id())
}

pub(crate) fn timestamp() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|since| since.as_secs())
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The worked example from RFC 5849 §3.4.1.1, base string and all.
    #[test]
    fn builds_the_base_string_from_the_rfc() {
        let url = url::Url::parse("http://example.com/request?b5=%3D%253D&a3=a&c%40=&a2=r%20b")
            .expect("url");
        let body = RequestBody::UrlEncoded {
            entries: vec![
                api_client_core::KeyValueEntry::new("c2", ""),
                api_client_core::KeyValueEntry::new("a3", "2 q"),
            ],
        };
        let oauth = vec![
            (
                "oauth_consumer_key".to_string(),
                "9djdj82h48djs9d2".to_string(),
            ),
            ("oauth_token".to_string(), "kkk9d7dh3k39sjv7".to_string()),
            (
                "oauth_signature_method".to_string(),
                "HMAC-SHA1".to_string(),
            ),
            ("oauth_timestamp".to_string(), "137131201".to_string()),
            ("oauth_nonce".to_string(), "7d8f3e4a".to_string()),
        ];

        assert_eq!(
            base_string("POST", &url, &body, &oauth),
            "POST&http%3A%2F%2Fexample.com%2Frequest&a2%3Dr%2520b%26a3%3D2%2520q%26a3%3Da%26b5%3D%253D%25253D%26c%2540%3D%26c2%3D%26oauth_consumer_key%3D9djdj82h48djs9d2%26oauth_nonce%3D7d8f3e4a%26oauth_signature_method%3DHMAC-SHA1%26oauth_timestamp%3D137131201%26oauth_token%3Dkkk9d7dh3k39sjv7"
        );
    }

    #[test]
    fn drops_a_default_port_from_the_base_url() {
        let parse = |raw: &str| base_url(&url::Url::parse(raw).expect("url"));

        assert_eq!(
            parse("http://Example.com:80/Path"),
            "http://example.com/Path"
        );
        assert_eq!(parse("https://example.com:443/p"), "https://example.com/p");
        assert_eq!(
            parse("https://example.com:8443/p?x=1"),
            "https://example.com:8443/p"
        );
    }

    #[test]
    fn encodes_what_the_rfc_says_to_encode() {
        assert_eq!(encode("a-b_c.d~e"), "a-b_c.d~e");
        assert_eq!(encode("r b"), "r%20b");
        assert_eq!(encode("="), "%3D");
    }

    #[test]
    fn the_signing_key_joins_both_secrets_encoded() {
        let settings = OAuth1Settings {
            consumer_secret: "con sumer".to_string(),
            token_secret: "tok&en".to_string(),
            ..OAuth1Settings::default()
        };
        assert_eq!(signing_key(&settings), "con%20sumer&tok%26en");
    }

    /// No token yet is a legitimate state, and the key still ends with `&`.
    #[test]
    fn signs_with_no_token_secret() {
        let settings = OAuth1Settings {
            consumer_secret: "secret".to_string(),
            ..OAuth1Settings::default()
        };
        assert_eq!(signing_key(&settings), "secret&");
    }
}
