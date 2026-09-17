use api_client_core::{
    ApiKeyPlacement, AuthConfig, HttpMethod, HttpRequest, MultipartValue, RequestBody,
    RequestError, RequestErrorKind, redact,
};
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
    let active: Vec<_> = request
        .query_params
        .iter()
        .filter(|entry| entry.is_active())
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

    let mut pairs = url.query_pairs_mut();
    for entry in active {
        pairs.append_pair(entry.name.trim(), &entry.value);
    }
    if let Some((key, value)) = api_key {
        pairs.append_pair(key, value);
    }
    pairs.finish();
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
