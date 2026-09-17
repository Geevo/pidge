//! HTTP Digest, which cannot be applied before the server has spoken.
//!
//! Basic and bearer go out with the first request. Digest is a conversation:
//! the server answers 401 with a nonce, and only then can a response be
//! computed. So the engine sends once, reads the challenge, and sends again —
//! which is what a browser does, and why digest costs a round trip.

use api_client_core::{HttpRequest, HttpResponse};
use digest_auth::{AuthContext, HttpMethod};

/// Builds the `Authorization` value answering a challenge.
///
/// The error is a sentence for the user rather than a failure: a challenge we
/// cannot answer leaves the 401 in front of them, which is the truth of what
/// happened, with a note saying why no second attempt was made.
pub(crate) fn answer(
    request: &HttpRequest,
    response: &HttpResponse,
    username: &str,
    password: &str,
) -> Result<String, String> {
    let challenge = response
        .header("www-authenticate")
        .ok_or_else(|| "The server asked for auth but sent no challenge.".to_string())?;

    if !challenge
        .trim_start()
        .to_ascii_lowercase()
        .starts_with("digest")
    {
        return Err(format!(
            "The server asked for {} auth, not Digest.",
            scheme_name(challenge)
        ));
    }

    let mut prompt = digest_auth::parse(challenge)
        .map_err(|err| format!("The server's Digest challenge could not be read: {err}."))?;

    // The digest is computed over the path and query actually requested, not
    // the whole URL.
    let uri = path_and_query(&response.final_url);
    let context = AuthContext::new_with_method(
        username,
        password,
        &uri,
        None::<&[u8]>,
        method_for(request.method.as_str()),
    );

    prompt
        .respond(&context)
        .map(|answer| answer.to_header_string())
        .map_err(|err| format!("This Digest challenge could not be answered: {err}."))
}

fn scheme_name(challenge: &str) -> String {
    challenge
        .split_whitespace()
        .next()
        .unwrap_or("unknown")
        .to_string()
}

fn path_and_query(url: &str) -> String {
    match url::Url::parse(url) {
        Ok(parsed) => match parsed.query() {
            Some(query) => format!("{}?{}", parsed.path(), query),
            None => parsed.path().to_string(),
        },
        // Only reachable if the URL we just requested will not parse back.
        Err(_) => "/".to_string(),
    }
}

/// `digest_auth` wraps the method in its own newtype.
fn method_for(method: &str) -> HttpMethod<'_> {
    HttpMethod(std::borrow::Cow::Borrowed(method))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn digests_over_the_path_and_query_only() {
        assert_eq!(
            path_and_query("https://example.com/api/x?a=1"),
            "/api/x?a=1"
        );
        assert_eq!(path_and_query("https://example.com"), "/");
    }

    #[test]
    fn names_the_scheme_a_server_asked_for() {
        assert_eq!(scheme_name("Negotiate abcdef"), "Negotiate");
        assert_eq!(scheme_name(""), "unknown");
    }
}
