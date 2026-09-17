use api_client_core::{
    ApiKeyPlacement, AuthConfig, HttpMethod, HttpRequest, MultipartValue, RequestBody,
    RequestError, RequestErrorKind, redact,
};
use percent_encoding::{AsciiSet, NON_ALPHANUMERIC, utf8_percent_encode};
use reqwest::header::{CONTENT_TYPE, HeaderMap, HeaderName, HeaderValue};
use reqwest::{Client, Method, RequestBuilder};

use crate::oauth1;
use crate::url_input::normalize_url;

pub struct Prepared {
    pub builder: RequestBuilder,
    pub warnings: Vec<String>,
}

/// Turns a resolved [`HttpRequest`] into something reqwest can send.
pub fn prepare(client: &Client, request: &HttpRequest) -> Result<Prepared, RequestError> {
    let mut url = normalize_url(&request.url)?;
    append_query_params(&mut url, request);

    let mut warnings = Vec::new();
    let headers = build_headers(request)?;
    let user_content_type = headers.contains_key(CONTENT_TYPE);

    tracing::debug!(
        method = %request.method,
        url = %url,
        headers = ?headers
            .iter()
            .map(|(name, value)| (
                name.as_str(),
                redact::header_value_for_log(name.as_str(), value.to_str().unwrap_or("<binary>"))
            ))
            .collect::<Vec<_>>(),
        "sending request"
    );

    let mut builder = client
        .request(method(request.method), url.clone())
        .headers(headers);
    builder = apply_auth(builder, request, &url, &mut warnings)?;
    builder = apply_body(builder, request, user_content_type, &mut warnings)?;

    if let Some(timeout_ms) = request.timeout_ms.filter(|ms| *ms > 0) {
        builder = builder.timeout(std::time::Duration::from_millis(timeout_ms));
    }

    Ok(Prepared { builder, warnings })
}

fn method(method: HttpMethod) -> Method {
    match method {
        HttpMethod::Get => Method::GET,
        HttpMethod::Post => Method::POST,
        HttpMethod::Put => Method::PUT,
        HttpMethod::Patch => Method::PATCH,
        HttpMethod::Delete => Method::DELETE,
        HttpMethod::Head => Method::HEAD,
        HttpMethod::Options => Method::OPTIONS,
    }
}

fn append_query_params(url: &mut url::Url, request: &HttpRequest) {
    /*
     * A row that the URL already carries is not appended again.
     *
     * The two are one thing shown twice: editing the table rewrites the URL's
     * query, and typing a query in the URL fills the table. Appending on top of
     * that sent every parameter twice — `?postcode=SW1A%201AA&postcode=SW1A%201AA`
     * — which an API is entitled to reject, and one did, with a 400 that Bruno
     * never saw.
     *
     * The comparison is on decoded pairs, so it holds however either side spelt
     * the encoding. Only the URL as it arrived is consulted: two identical rows
     * still send two copies, because that is what the table says.
     */
    let in_url: std::collections::HashSet<(String, String)> = url
        .query_pairs()
        .map(|(name, value)| (name.into_owned(), value.into_owned()))
        .collect();

    let active: Vec<_> = request
        .query_params
        .iter()
        .filter(|entry| entry.is_active())
        .filter(|entry| !in_url.contains(&(entry.name.trim().to_string(), entry.value.clone())))
        .collect();

    // An API key placed in the query string is a query param like any other,
    // and belongs here rather than in a second place that edits the URL.
    let api_key = match &request.auth {
        AuthConfig::ApiKey {
            key,
            value,
            placement: ApiKeyPlacement::Query,
        } if !key.trim().is_empty() => Some((key.trim(), value.as_str())),
        _ => None,
    };

    if active.is_empty() && api_key.is_none() {
        return;
    }

    /*
     * Built by hand rather than with `query_pairs_mut`, for two reasons.
     *
     * It encodes as a form does, so a space becomes `+`. That is the rule for a
     * form body, not for a URL, and a server is entitled to read `SW1A+1AA` as
     * a postcode containing a plus. curl, Bruno and every browser send `%20`
     * here, and a request that works in those should work in this.
     *
     * It also re-serialises the query already in the URL, so what the user
     * typed comes back changed — their own `%20` turned into `+`, anything
     * else re-encoded to the crate's taste. The typed query is evidence; it is
     * carried across untouched.
     */
    let mut query = url.query().unwrap_or_default().to_string();
    for (name, value) in active
        .iter()
        .map(|entry| (entry.name.trim(), entry.value.as_str()))
        .chain(api_key)
    {
        if !query.is_empty() {
            query.push('&');
        }
        query.push_str(&maybe_encode(name, request.encode_query));
        query.push('=');
        query.push_str(&maybe_encode(value, request.encode_query));
    }

    url.set_query(Some(&query));
}

/// Everything but RFC 3986's unreserved set, which is what `encodeURIComponent`
/// and curl both produce.
const UNRESERVED: &AsciiSet = &NON_ALPHANUMERIC
    .remove(b'-')
    .remove(b'.')
    .remove(b'_')
    .remove(b'~');

fn encode(value: &str) -> String {
    utf8_percent_encode(value, UNRESERVED).to_string()
}

/// With encoding off the text goes in as typed; `Url` still escapes what cannot
/// appear in a query at all, such as a space, so the result is a valid URL.
fn maybe_encode(value: &str, encode_query: bool) -> String {
    if encode_query {
        encode(value)
    } else {
        value.to_string()
    }
}

fn build_headers(request: &HttpRequest) -> Result<HeaderMap, RequestError> {
    let mut headers = HeaderMap::new();
    for entry in request.headers.iter().filter(|entry| entry.is_active()) {
        let name = HeaderName::from_bytes(entry.name.trim().as_bytes()).map_err(|err| {
            RequestError::new(
                RequestErrorKind::InvalidHeader,
                format!("`{}` is not a valid header name.", entry.name.trim()),
            )
            .with_detail(err.to_string())
        })?;
        let value = HeaderValue::from_str(&entry.value).map_err(|err| {
            RequestError::new(
                RequestErrorKind::InvalidHeader,
                format!(
                    "The value for `{}` is not a valid header value.",
                    entry.name.trim()
                ),
            )
            .with_detail(err.to_string())
        })?;
        headers.append(name, value);
    }
    Ok(headers)
}

/// A header the user typed always wins; they typed it on purpose. The auth
/// helper is skipped and the conflict is reported, never silently resolved.
///
/// Which header that is depends on the scheme: `Authorization` for bearer and
/// basic, but an API key names its own, and a key in the query string collides
/// with nothing.
fn apply_auth(
    builder: RequestBuilder,
    request: &HttpRequest,
    url: &url::Url,
    warnings: &mut Vec<String>,
) -> Result<RequestBuilder, RequestError> {
    let occupied = |name: &str| request.find_header(name).is_some();

    Ok(match &request.auth {
        AuthConfig::None => builder,

        AuthConfig::Bearer { .. }
        | AuthConfig::Basic { .. }
        | AuthConfig::Digest { .. }
        | AuthConfig::Ntlm { .. }
        | AuthConfig::OAuth1(_)
        | AuthConfig::OAuth2(_)
            if occupied("authorization") =>
        {
            warnings.push(
                "An explicit Authorization header is set, so the Auth tab was ignored.".to_string(),
            );
            builder
        }
        // None of these can be applied here: digest and NTLM wait for a
        // challenge, and OAuth 2 has to fetch a token first. The engine does
        // all three.
        AuthConfig::Digest { .. } | AuthConfig::Ntlm { .. } | AuthConfig::OAuth2(_) => builder,
        AuthConfig::Bearer { token } => builder.bearer_auth(token),
        AuthConfig::Basic { username, password } => builder.basic_auth(username, Some(password)),

        // Signed here, over the URL that is about to be requested.
        AuthConfig::OAuth1(settings) => {
            let header = oauth1::authorization(
                request.method.as_str(),
                url,
                &request.body,
                settings,
                oauth1::nonce(),
                oauth1::timestamp(),
            )?;
            builder.header(reqwest::header::AUTHORIZATION, header)
        }

        // The query placement is handled with the other query params.
        AuthConfig::ApiKey {
            placement: ApiKeyPlacement::Query,
            ..
        } => builder,
        AuthConfig::ApiKey { key, .. } if key.trim().is_empty() => builder,
        AuthConfig::ApiKey { key, .. } if occupied(key.trim()) => {
            warnings.push(format!(
                "An explicit {} header is set, so the Auth tab was ignored.",
                key.trim()
            ));
            builder
        }
        AuthConfig::ApiKey { key, value, .. } => builder.header(key.trim(), value),
    })
}

fn apply_body(
    builder: RequestBuilder,
    request: &HttpRequest,
    user_content_type: bool,
    warnings: &mut Vec<String>,
) -> Result<RequestBuilder, RequestError> {
    Ok(match &request.body {
        RequestBody::None => builder,

        RequestBody::Json { text } => {
            let builder = with_content_type(builder, user_content_type, "application/json");
            builder.body(text.clone())
        }

        RequestBody::Text { text, content_type } => {
            let fallback = content_type
                .as_deref()
                .unwrap_or("text/plain; charset=utf-8");
            let builder = with_content_type(builder, user_content_type, fallback);
            builder.body(text.clone())
        }

        RequestBody::UrlEncoded { entries } => {
            let mut serializer = url::form_urlencoded::Serializer::new(String::new());
            for entry in entries.iter().filter(|entry| entry.is_active()) {
                serializer.append_pair(entry.name.trim(), &entry.value);
            }
            let builder = with_content_type(
                builder,
                user_content_type,
                "application/x-www-form-urlencoded",
            );
            builder.body(serializer.finish())
        }

        RequestBody::Multipart { entries } => {
            if user_content_type {
                warnings.push(
                    "Multipart sets its own Content-Type with a boundary; the header you set was replaced."
                        .to_string(),
                );
            }
            let mut form = reqwest::multipart::Form::new();
            for entry in entries.iter().filter(|entry| entry.is_active()) {
                let name = entry.name.trim().to_string();
                form = match &entry.value {
                    MultipartValue::Text { value } => form.text(name, value.clone()),
                    MultipartValue::File {
                        path,
                        file_name,
                        content_type,
                    } => {
                        let bytes = std::fs::read(path).map_err(|err| {
                            RequestError::new(
                                RequestErrorKind::Io,
                                format!("Could not read `{path}`."),
                            )
                            .with_detail(err.to_string())
                        })?;
                        let display_name = file_name.clone().unwrap_or_else(|| {
                            std::path::Path::new(path)
                                .file_name()
                                .map(|name| name.to_string_lossy().into_owned())
                                .unwrap_or_else(|| "file".to_string())
                        });
                        let mut part =
                            reqwest::multipart::Part::bytes(bytes).file_name(display_name);
                        if let Some(mime) = content_type {
                            part = part.mime_str(mime).map_err(|err| {
                                RequestError::new(
                                    RequestErrorKind::BodySerialization,
                                    format!("`{mime}` is not a valid content type."),
                                )
                                .with_detail(err.to_string())
                            })?;
                        }
                        form.part(name, part)
                    }
                };
            }
            builder.multipart(form)
        }
    })
}

fn with_content_type(
    builder: RequestBuilder,
    user_content_type: bool,
    fallback: &str,
) -> RequestBuilder {
    if user_content_type {
        builder
    } else {
        builder.header(CONTENT_TYPE, fallback)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use api_client_core::KeyValueEntry;

    fn query_for(url: &str, params: Vec<KeyValueEntry>) -> String {
        let request = HttpRequest {
            url: url.to_string(),
            query_params: params,
            ..HttpRequest::default()
        };
        let mut url = normalize_url(&request.url).expect("url");
        append_query_params(&mut url, &request);
        url.query().unwrap_or_default().to_string()
    }

    /*
     * A space is `%20` in a URL. `+` means space only in a form body, and a
     * server that takes the query literally answers 400 to the plus — which is
     * how this was found, against an API that Bruno could call and this could
     * not.
     */
    #[test]
    fn a_space_in_a_value_is_percent_encoded() {
        assert_eq!(
            query_for(
                "https://example.com/lookup",
                vec![KeyValueEntry::new("postcode", "SW1A 1AA")],
            ),
            "postcode=SW1A%201AA"
        );
    }

    /// A plus the user typed is a plus, not a space.
    #[test]
    fn a_literal_plus_survives() {
        assert_eq!(
            query_for(
                "https://example.com/c",
                vec![KeyValueEntry::new("phone", "+44 7700 900000")],
            ),
            "phone=%2B44%207700%20900000"
        );
    }

    /// The query already in the URL is the user's own text and is not rewritten.
    #[test]
    fn the_typed_query_is_left_exactly_as_typed() {
        assert_eq!(
            query_for(
                "https://example.com/s?filter=a%20b&raw=x+y&path=/v1/items",
                vec![KeyValueEntry::new("page", "2")],
            ),
            "filter=a%20b&raw=x+y&path=/v1/items&page=2"
        );
    }

    /*
     * What the desktop app sends: the table and the URL hold the same pair,
     * because editing either one writes the other. It goes out once.
     */
    #[test]
    fn a_row_the_url_already_carries_is_not_sent_twice() {
        assert_eq!(
            query_for(
                "https://example.com/lookup?postcode=SW1A%201AA",
                vec![KeyValueEntry::new("postcode", "SW1A 1AA")],
            ),
            "postcode=SW1A%201AA"
        );
    }

    /// Matching is on the decoded pair, so the spelling of the encoding is moot.
    #[test]
    fn the_match_ignores_how_each_side_encoded_it() {
        assert_eq!(
            query_for(
                "https://example.com/lookup?q=a+b",
                vec![KeyValueEntry::new("q", "a b")],
            ),
            "q=a+b"
        );
    }

    /// A row that differs from the URL's is a second parameter, not a duplicate.
    #[test]
    fn a_row_with_another_value_is_still_appended() {
        assert_eq!(
            query_for(
                "https://example.com/s?tag=red",
                vec![KeyValueEntry::new("tag", "blue")],
            ),
            "tag=red&tag=blue"
        );
    }

    /// Two identical rows are two copies on purpose; only the URL is deduped against.
    #[test]
    fn the_table_may_repeat_itself() {
        assert_eq!(
            query_for(
                "https://example.com/s",
                vec![KeyValueEntry::new("id", "7"), KeyValueEntry::new("id", "7")],
            ),
            "id=7&id=7"
        );
    }

    /*
     * Encoding off is for a value that is already encoded, or that a server
     * wants to see unescaped. `Url` still escapes what cannot appear in a query
     * at all, so the result is a valid URL either way.
     */
    #[test]
    fn encoding_can_be_turned_off_for_a_request() {
        let request = HttpRequest {
            url: "https://example.com/s".to_string(),
            query_params: vec![
                KeyValueEntry::new("path", "/v1/a:b"),
                KeyValueEntry::new("pre", "%2F"),
            ],
            encode_query: false,
            ..HttpRequest::default()
        };
        let mut url = normalize_url(&request.url).expect("url");
        append_query_params(&mut url, &request);

        // The `/` and `:` survive, and the already-encoded value is not encoded twice.
        assert_eq!(url.query(), Some("path=/v1/a:b&pre=%2F"));
    }

    /// The same values with encoding on, for contrast.
    #[test]
    fn encoding_on_escapes_the_same_values() {
        assert_eq!(
            query_for(
                "https://example.com/s",
                vec![
                    KeyValueEntry::new("path", "/v1/a:b"),
                    KeyValueEntry::new("pre", "%2F")
                ],
            ),
            "path=%2Fv1%2Fa%3Ab&pre=%252F"
        );
    }

    #[test]
    fn separators_inside_a_value_cannot_split_it() {
        assert_eq!(
            query_for(
                "https://example.com/s",
                vec![KeyValueEntry::new("q", "a&b=c#d")],
            ),
            "q=a%26b%3Dc%23d"
        );
    }
}
