use api_client_core::{RequestError, RequestErrorKind};
use url::Url;

/// Turns what the user typed into a URL we can send.
///
/// `localhost:3000/test` is a hostname and port to a developer, but a scheme
/// and path to a URL parser, so bare inputs get `http://` and nothing else.
pub fn normalize_url(input: &str) -> Result<Url, RequestError> {
    let trimmed = input.trim();
    if trimmed.is_empty() {
        return Err(RequestError::invalid_url("Enter a URL."));
    }

    let candidate = if has_supported_scheme(trimmed) {
        trimmed.to_string()
    } else if let Some((scheme, _)) = split_scheme(trimmed) {
        return Err(RequestError::new(
            RequestErrorKind::UnsupportedScheme,
            format!("{scheme}:// is not supported. Use http:// or https://."),
        ));
    } else {
        format!("http://{trimmed}")
    };

    let url = Url::parse(&candidate).map_err(|err| {
        RequestError::invalid_url(format!("`{trimmed}` is not a valid URL."))
            .with_detail(err.to_string())
    })?;

    if !matches!(url.scheme(), "http" | "https") {
        return Err(RequestError::new(
            RequestErrorKind::UnsupportedScheme,
            format!(
                "{}:// is not supported. Use http:// or https://.",
                url.scheme()
            ),
        ));
    }

    if url.host_str().is_none_or(str::is_empty) {
        return Err(RequestError::invalid_url(format!(
            "`{trimmed}` has no host."
        )));
    }

    Ok(url)
}

fn has_supported_scheme(input: &str) -> bool {
    let lower = input.to_ascii_lowercase();
    lower.starts_with("http://") || lower.starts_with("https://")
}

/// Detects `scheme://rest`. A bare `localhost:3000` is deliberately not a match.
fn split_scheme(input: &str) -> Option<(&str, &str)> {
    let index = input.find("://")?;
    let scheme = &input[..index];
    let valid = !scheme.is_empty()
        && scheme.starts_with(|c: char| c.is_ascii_alphabetic())
        && scheme
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || matches!(c, '+' | '-' | '.'));
    valid.then(|| (scheme, &input[index + 3..]))
}
