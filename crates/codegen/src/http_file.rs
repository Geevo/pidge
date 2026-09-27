//! Saved requests as a `.http` file: the plain-text format that VS Code's REST
//! Client and JetBrains' HTTP Client both run.
//!
//! Unlike the code targets, this writes the request as saved, `{{variables}}`
//! and all — both tools substitute them the same way the app does — rather
//! than the request as it would go on the wire. What the format has no words
//! for is said in a comment, as the code targets do.

use std::collections::HashSet;
use std::fmt::Write as _;

use api_client_core::{
    ApiKeyPlacement, AuthConfig, HttpRequest, KeyValueEntry, MultipartValue, RequestBody,
};
use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use percent_encoding::{AsciiSet, NON_ALPHANUMERIC, percent_decode_str, utf8_percent_encode};

const BOUNDARY: &str = "api-client-boundary";

/// One block per request, in the order given, each headed by its name.
pub fn http_file<'a>(requests: impl IntoIterator<Item = (&'a str, &'a HttpRequest)>) -> String {
    requests
        .into_iter()
        .map(|(name, request)| block(name, request))
        .collect::<Vec<_>>()
        .join("\n")
}

fn block(name: &str, request: &HttpRequest) -> String {
    let mut headers: Vec<(String, String)> = request
        .headers
        .iter()
        .filter(|entry| entry.is_active())
        .map(|entry| (entry.name.trim().to_owned(), entry.value.clone()))
        .collect();
    let has_header = |headers: &[(String, String)], name: &str| {
        headers
            .iter()
            .any(|(existing, _)| existing.eq_ignore_ascii_case(name))
    };

    let mut notes = Vec::new();
    let mut extra_query = Vec::new();
    // A header typed by hand wins over the Auth tab, as it does on Send.
    let authorization_typed = has_header(&headers, "Authorization");
    match &request.auth {
        AuthConfig::None => {}
        _ if authorization_typed && !matches!(request.auth, AuthConfig::ApiKey { .. }) => {}
        AuthConfig::Bearer { token } => {
            headers.push(("Authorization".into(), format!("Bearer {token}")));
        }
        AuthConfig::Basic { username, password } => {
            headers.push(("Authorization".into(), basic(username, password)));
        }
        AuthConfig::Digest { .. } => {
            notes.push("Digest auth is left out: a .http file has no way of saying it that both clients understand.");
        }
        AuthConfig::Ntlm { .. } => {
            notes.push("NTLM auth is left out: a .http file has no way of saying it.");
        }
        AuthConfig::OAuth1(_) => {
            notes.push("OAuth 1 signing is left out: each request is signed over itself, which a .http file cannot do.");
        }
        AuthConfig::OAuth2(_) => {
            notes.push("OAuth 2 is left out: the token has to be fetched first, which a .http file cannot do.");
        }
        AuthConfig::ApiKey {
            key,
            value,
            placement,
        } => {
            let key = key.trim();
            if !key.is_empty() {
                match placement {
                    ApiKeyPlacement::Header if !has_header(&headers, key) => {
                        headers.push((key.to_owned(), value.clone()));
                    }
                    ApiKeyPlacement::Header => {}
                    ApiKeyPlacement::Query => extra_query.push((key.to_owned(), value.clone())),
                }
            }
        }
    }

    let body = body(&request.body, &mut headers);

    let mut out = String::new();
    // `###` separates requests, and both clients show the text after it as the name.
    let _ = writeln!(out, "### {}", one_line(name));
    for note in notes {
        let _ = writeln!(out, "# {note}");
    }
    let _ = writeln!(
        out,
        "{} {}",
        request.method.as_str(),
        url(request, &extra_query)
    );
    for (name, value) in &headers {
        let _ = writeln!(out, "{name}: {}", one_line(value));
    }
    if let Some(body) = body {
        out.push('\n');
        out.push_str(&body);
        if !body.ends_with('\n') {
            out.push('\n');
        }
    }
    out
}

/// Both clients encode `Basic user password` themselves; that form keeps
/// `{{variables}}` usable. Without any, the header is written finished.
fn basic(username: &str, password: &str) -> String {
    if username.contains("{{") || password.contains("{{") {
        format!("Basic {username} {password}")
    } else {
        format!(
            "Basic {}",
            STANDARD.encode(format!("{username}:{password}"))
        )
    }
}

/// The URL as typed, plus any active parameter it does not already carry. The
/// table and the URL are normally one thing shown twice, so that is rare.
fn url(request: &HttpRequest, extra: &[(String, String)]) -> String {
    let mut url = request.url.trim().to_owned();
    let in_url: HashSet<(String, String)> = url
        .split_once('?')
        .map(|(_, query)| query.split('&').map(decode_pair).collect())
        .unwrap_or_default();

    let missing: Vec<(&str, &str)> = request
        .query_params
        .iter()
        .filter(|entry| entry.is_active())
        .map(|entry| (entry.name.trim(), entry.value.as_str()))
        .filter(|(name, value)| !in_url.contains(&((*name).to_owned(), (*value).to_owned())))
        .chain(
            extra
                .iter()
                .map(|(name, value)| (name.as_str(), value.as_str())),
        )
        .collect();

    for (index, (name, value)) in missing.into_iter().enumerate() {
        let separator = if index == 0 && !url.contains('?') {
            '?'
        } else {
            '&'
        };
        url.push(separator);
        url.push_str(&encode(name, request.encode_query, QUERY));
        url.push('=');
        url.push_str(&encode(value, request.encode_query, QUERY));
    }
    url
}

fn decode_pair(pair: &str) -> (String, String) {
    let (name, value) = pair.split_once('=').unwrap_or((pair, ""));
    let decode = |text: &str| percent_decode_str(text).decode_utf8_lossy().into_owned();
    (decode(name), decode(value))
}

/// The body as the file will carry it, adding the content type it implies
/// when no header says otherwise.
fn body(body: &RequestBody, headers: &mut Vec<(String, String)>) -> Option<String> {
    let mut content_type = |value: &str| {
        if !headers
            .iter()
            .any(|(name, _)| name.eq_ignore_ascii_case("Content-Type"))
        {
            headers.push(("Content-Type".into(), value.to_owned()));
        }
    };

    match body {
        RequestBody::None => None,
        RequestBody::Json { text } if text.trim().is_empty() => None,
        RequestBody::Json { text } => {
            content_type("application/json");
            Some(text.clone())
        }
        RequestBody::Text { text, .. } if text.is_empty() => None,
        RequestBody::Text {
            text,
            content_type: kind,
        } => {
            if let Some(kind) = kind.as_deref().filter(|kind| !kind.trim().is_empty()) {
                content_type(kind);
            }
            Some(text.clone())
        }
        RequestBody::UrlEncoded { entries } => {
            let active: Vec<&KeyValueEntry> = entries.iter().filter(|e| e.is_active()).collect();
            if active.is_empty() {
                return None;
            }
            content_type("application/x-www-form-urlencoded");
            let pairs: Vec<String> = active
                .iter()
                .map(|entry| {
                    format!(
                        "{}={}",
                        encode(entry.name.trim(), true, FORM),
                        encode(&entry.value, true, FORM)
                    )
                })
                .collect();
            Some(pairs.join("&"))
        }
        RequestBody::Multipart { entries } => {
            let active: Vec<_> = entries.iter().filter(|e| e.is_active()).collect();
            if active.is_empty() {
                return None;
            }
            content_type(&format!("multipart/form-data; boundary={BOUNDARY}"));
            let mut out = String::new();
            for entry in active {
                let _ = writeln!(out, "--{BOUNDARY}");
                match &entry.value {
                    MultipartValue::Text { value } => {
                        let _ = writeln!(
                            out,
                            "Content-Disposition: form-data; name=\"{}\"\n\n{value}",
                            entry.name.trim()
                        );
                    }
                    MultipartValue::File {
                        path,
                        file_name,
                        content_type,
                    } => {
                        let file_name = file_name.clone().unwrap_or_else(|| {
                            path.rsplit(['/', '\\']).next().unwrap_or(path).to_owned()
                        });
                        let _ = writeln!(
                            out,
                            "Content-Disposition: form-data; name=\"{}\"; filename=\"{file_name}\"",
                            entry.name.trim()
                        );
                        if let Some(kind) = content_type {
                            let _ = writeln!(out, "Content-Type: {kind}");
                        }
                        // `<` reads the file in, in both clients.
                        let _ = writeln!(out, "\n< {path}");
                    }
                }
            }
            let _ = writeln!(out, "--{BOUNDARY}--");
            Some(out)
        }
    }
}

/// RFC 3986's unreserved set, as the engine encodes a query.
const QUERY: &AsciiSet = &NON_ALPHANUMERIC
    .remove(b'-')
    .remove(b'.')
    .remove(b'_')
    .remove(b'~');

/// What `application/x-www-form-urlencoded` escapes.
const FORM: &AsciiSet = &NON_ALPHANUMERIC
    .remove(b'*')
    .remove(b'-')
    .remove(b'.')
    .remove(b'_');

/// Percent-encodes everything but `{{variables}}`, which the client has to
/// see intact to substitute them.
fn encode(text: &str, enabled: bool, set: &'static AsciiSet) -> String {
    if !enabled {
        return text.to_owned();
    }
    let mut out = String::new();
    let mut rest = text;
    while let Some(start) = rest.find("{{") {
        let Some(length) = rest[start..].find("}}") else {
            break;
        };
        out.extend(utf8_percent_encode(&rest[..start], set));
        out.push_str(&rest[start..start + length + 2]);
        rest = &rest[start + length + 2..];
    }
    out.extend(utf8_percent_encode(rest, set));
    out
}

/// A header value or a name that spans lines would end the block early.
fn one_line(text: &str) -> String {
    text.replace(['\r', '\n'], " ")
}

#[cfg(test)]
mod tests {
    use api_client_core::{HttpMethod, MultipartEntry, OAuth2Settings};

    use super::*;

    fn get(url: &str) -> HttpRequest {
        HttpRequest::get(url)
    }

    #[test]
    fn writes_a_named_block_with_headers_and_a_json_body() {
        let mut request = get("https://api.example.com/users");
        request.method = HttpMethod::Post;
        request
            .headers
            .push(KeyValueEntry::new("Accept", "application/json"));
        request
            .headers
            .push(KeyValueEntry::disabled("X-Debug", "1"));
        request.body = RequestBody::Json {
            text: "{\"name\": \"Ada\"}".into(),
        };

        assert_eq!(
            http_file([("Create user", &request)]),
            "### Create user\n\
             POST https://api.example.com/users\n\
             Accept: application/json\n\
             Content-Type: application/json\n\
             \n\
             {\"name\": \"Ada\"}\n"
        );
    }

    #[test]
    fn keeps_variables_as_variables() {
        let mut request = get("{{baseUrl}}/users");
        request.auth = AuthConfig::Bearer {
            token: "{{token}}".into(),
        };
        request
            .query_params
            .push(KeyValueEntry::new("q", "{{name}} smith"));

        let file = http_file([("Search", &request)]);
        assert!(
            file.contains("GET {{baseUrl}}/users?q={{name}}%20smith\n"),
            "{file}"
        );
        assert!(file.contains("Authorization: Bearer {{token}}\n"));
    }

    #[test]
    fn does_not_repeat_a_parameter_the_url_already_carries() {
        let mut request = get("https://x.test/a?postcode=SW1A%201AA");
        request
            .query_params
            .push(KeyValueEntry::new("postcode", "SW1A 1AA"));
        assert!(
            http_file([("A", &request)]).contains("GET https://x.test/a?postcode=SW1A%201AA\n")
        );
    }

    #[test]
    fn basic_auth_is_finished_unless_it_holds_variables() {
        let mut request = get("https://x.test");
        request.auth = AuthConfig::Basic {
            username: "ada".into(),
            password: "s3cret".into(),
        };
        assert!(http_file([("A", &request)]).contains("Authorization: Basic YWRhOnMzY3JldA==\n"));

        request.auth = AuthConfig::Basic {
            username: "ada".into(),
            password: "{{password}}".into(),
        };
        assert!(http_file([("A", &request)]).contains("Authorization: Basic ada {{password}}\n"));
    }

    #[test]
    fn a_typed_authorization_header_wins_over_the_auth_tab() {
        let mut request = get("https://x.test");
        request
            .headers
            .push(KeyValueEntry::new("Authorization", "Token abc"));
        request.auth = AuthConfig::Bearer {
            token: "xyz".into(),
        };

        let file = http_file([("A", &request)]);
        assert!(file.contains("Authorization: Token abc\n"));
        assert!(!file.contains("xyz"));
    }

    #[test]
    fn says_what_it_cannot_write() {
        let mut request = get("https://x.test");
        request.auth = AuthConfig::OAuth2(OAuth2Settings::default());
        assert!(http_file([("A", &request)]).contains("# OAuth 2 is left out"));
    }

    #[test]
    fn an_api_key_goes_where_it_was_placed() {
        let mut request = get("https://x.test/a");
        request.auth = AuthConfig::ApiKey {
            key: "api_key".into(),
            value: "k 1".into(),
            placement: ApiKeyPlacement::Query,
        };
        assert!(http_file([("A", &request)]).contains("GET https://x.test/a?api_key=k%201\n"));
    }

    #[test]
    fn multipart_reads_files_in() {
        let mut request = get("https://x.test/upload");
        request.method = HttpMethod::Post;
        request.body = RequestBody::Multipart {
            entries: vec![
                MultipartEntry::text("title", "Holiday"),
                MultipartEntry::file("photo", "/home/ada/beach.jpg"),
            ],
        };

        let file = http_file([("Upload", &request)]);
        assert!(file.contains("Content-Type: multipart/form-data; boundary=api-client-boundary\n"));
        assert!(file.contains("name=\"photo\"; filename=\"beach.jpg\"\n\n< /home/ada/beach.jpg\n"));
        assert!(file.ends_with("--api-client-boundary--\n"));
    }

    #[test]
    fn separates_requests_with_a_blank_line() {
        let one = get("https://x.test/1");
        let two = get("https://x.test/2");
        assert_eq!(
            http_file([("One", &one), ("Two", &two)]),
            "### One\nGET https://x.test/1\n\n### Two\nGET https://x.test/2\n"
        );
    }
}
