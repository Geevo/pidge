use api_client_core::{RequestError, RequestErrorKind};

/// Maps a reqwest failure onto one of our normalized kinds.
///
/// reqwest exposes coarse predicates, so for the cases developers actually hit
/// most (refused connections, TLS problems) we also look at the source chain.
pub fn from_reqwest(err: reqwest::Error, timeout_ms: u64) -> RequestError {
    let detail = chain(&err);
    let lowered = detail.to_ascii_lowercase();

    let kind = if err.is_timeout() {
        RequestErrorKind::Timeout
    } else if err.is_redirect() {
        if lowered.contains("too many redirects") {
            RequestErrorKind::TooManyRedirects
        } else {
            RequestErrorKind::Redirect
        }
    } else if err.is_dns()
        || lowered.contains("dns error")
        || lowered.contains("name or service not known")
    {
        RequestErrorKind::Dns
    } else if lowered.contains("connection refused") {
        RequestErrorKind::ConnectionRefused
    } else if is_tls(&lowered) {
        RequestErrorKind::Tls
    } else if err.is_connect() {
        RequestErrorKind::ConnectionFailed
    } else if err.is_body() || err.is_decode() {
        RequestErrorKind::BodyRead
    } else if err.is_builder() {
        RequestErrorKind::BodySerialization
    } else {
        RequestErrorKind::Other
    };

    let host = err
        .url()
        .and_then(|url| url.host_str().map(str::to_string))
        .unwrap_or_else(|| "the server".to_string());

    let message = match kind {
        RequestErrorKind::Timeout => return RequestError::timeout(timeout_ms).with_detail(detail),
        RequestErrorKind::Dns => format!("Could not resolve `{host}`."),
        RequestErrorKind::ConnectionRefused => {
            format!("`{host}` refused the connection. Is the server running?")
        }
        RequestErrorKind::ConnectionFailed => format!("Could not connect to `{host}`."),
        RequestErrorKind::Tls => format!("TLS handshake with `{host}` failed."),
        RequestErrorKind::TooManyRedirects => "The server redirected too many times.".to_string(),
        RequestErrorKind::Redirect => "A redirect could not be followed.".to_string(),
        RequestErrorKind::BodyRead => "The response body could not be read.".to_string(),
        RequestErrorKind::BodySerialization => "The request could not be built.".to_string(),
        _ => format!("The request to `{host}` failed."),
    };

    RequestError::new(kind, message).with_detail(detail)
}

fn is_tls(lowered: &str) -> bool {
    ["tls", "certificate", "handshake", "ssl", "self-signed"]
        .iter()
        .any(|needle| lowered.contains(needle))
}

/// Flattens an error and its sources into one diagnostics line.
pub fn chain(err: &(dyn std::error::Error + 'static)) -> String {
    let mut parts = vec![err.to_string()];
    let mut source = err.source();
    while let Some(current) = source {
        let text = current.to_string();
        if !parts.contains(&text) {
            parts.push(text);
        }
        source = current.source();
    }
    parts.join(": ")
}
