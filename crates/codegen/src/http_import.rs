//! Reading a `.http` file back into requests: the part of the format that VS
//! Code's REST Client and JetBrains' HTTP Client agree on.
//!
//! That is a `###` line between requests, a request line, headers, a blank
//! line and a body, with `#` and `//` comments. File variables (`@name =
//! value`) are substituted in, since the file that defined them is not coming
//! along. Scripts, response handlers and anything else one client adds are
//! left behind, and a request that cannot come across whole is skipped with a
//! sentence saying why rather than half-imported.

use std::collections::BTreeMap;

use api_client_core::{
    AuthConfig, HttpMethod, HttpRequest, KeyValueEntry, MultipartEntry, MultipartValue, RequestBody,
};
use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use percent_encoding::percent_decode_str;

/// One request read from the file, with the name the file gave it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ParsedRequest {
    pub name: String,
    pub request: HttpRequest,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ParsedHttpFile {
    pub requests: Vec<ParsedRequest>,
    /// One sentence for each request that was left out, saying why.
    pub skipped: Vec<String>,
}

pub fn parse_http_file(text: &str) -> ParsedHttpFile {
    let text = text.replace("\r\n", "\n");
    let mut variables = BTreeMap::new();
    let mut parsed = ParsedHttpFile::default();

    for block in blocks(&text) {
        match parse_block(&block, &mut variables) {
            Some(Ok(request)) => parsed.requests.push(request),
            Some(Err(reason)) => parsed.skipped.push(reason),
            None => {}
        }
    }
    parsed
}

struct Block<'a> {
    /// The text after `###`, if there was any.
    title: Option<&'a str>,
    lines: Vec<&'a str>,
}

fn blocks(text: &str) -> Vec<Block<'_>> {
    let mut blocks = vec![Block {
        title: None,
        lines: Vec::new(),
    }];
    for line in text.lines() {
        if let Some(title) = line.trim_start().strip_prefix("###") {
            let title = title.trim();
            blocks.push(Block {
                title: (!title.is_empty()).then_some(title),
                lines: Vec::new(),
            });
        } else if let Some(block) = blocks.last_mut() {
            block.lines.push(line);
        }
    }
    blocks
}

fn is_comment(line: &str) -> bool {
    let line = line.trim_start();
    line.starts_with('#') || line.starts_with("//")
}

/// `None` for a block with no request in it: the part before the first `###`,
/// or one holding only comments and variables.
fn parse_block(
    block: &Block<'_>,
    variables: &mut BTreeMap<String, String>,
) -> Option<Result<ParsedRequest, String>> {
    let mut lines = block.lines.iter().copied().peekable();
    let mut name = block.title.map(str::to_owned);

    // Everything before the request line: blanks, comments, `# @name`,
    // `@variable = value`, and a pre-request script.
    let request_line = loop {
        let line = lines.next()?;
        let trimmed = line.trim();
        if trimmed.is_empty() {
            continue;
        }
        if let Some(tag) = trimmed
            .strip_prefix('#')
            .or_else(|| trimmed.strip_prefix("//"))
            .map(str::trim)
            .and_then(|rest| rest.strip_prefix("@name"))
        {
            if name.is_none() {
                name = Some(tag.trim().trim_start_matches('=').trim().to_owned());
            }
            continue;
        }
        if is_comment(trimmed) {
            continue;
        }
        if let Some((variable, value)) = file_variable(trimmed) {
            let value = substitute(value, variables);
            variables.insert(variable.to_owned(), value);
            continue;
        }
        if trimmed.starts_with("< {%") {
            skip_script(trimmed, &mut lines);
            continue;
        }
        break trimmed;
    };

    let (method, mut url) = match request_line_parts(request_line) {
        Ok(parts) => parts,
        Err(method) => {
            let name = name.unwrap_or_else(|| request_line.to_owned());
            return Some(Err(format!(
                "\u{201c}{name}\u{201d} was skipped: {method} is not a method this app sends."
            )));
        }
    };

    // A query spread over indented `?` and `&` lines.
    while let Some(next) = lines.peek() {
        let continued = next.trim_start();
        if next.starts_with(char::is_whitespace)
            && (continued.starts_with('?') || continued.starts_with('&'))
        {
            url.push_str(continued.trim_end());
            lines.next();
        } else {
            break;
        }
    }

    let mut headers = Vec::new();
    for line in lines.by_ref() {
        let trimmed = line.trim();
        if trimmed.is_empty() {
            break;
        }
        if is_comment(trimmed) {
            continue;
        }
        if let Some((header, value)) = trimmed.split_once(':') {
            headers.push((header.trim().to_owned(), value.trim().to_owned()));
        }
    }

    let mut body = Vec::new();
    for line in lines {
        let trimmed = line.trim_start();
        // A response handler or a reference to a saved response ends the body.
        if trimmed.starts_with("> ") || trimmed.starts_with(">>") || trimmed.starts_with("<> ") {
            break;
        }
        body.push(line);
    }
    while body.last().is_some_and(|line| line.trim().is_empty()) {
        body.pop();
    }
    let body = body.join("\n");

    let url = substitute(&url, variables);
    let headers: Vec<(String, String)> = headers
        .into_iter()
        .map(|(header, value)| (header, substitute(&value, variables)))
        .collect();
    let body = substitute(&body, variables);

    let name = name.unwrap_or_else(|| format!("{} {url}", method.as_str()));
    Some(Ok(ParsedRequest {
        name,
        request: build(method, url, headers, body),
    }))
}

fn file_variable(line: &str) -> Option<(&str, &str)> {
    let rest = line.strip_prefix('@')?;
    let (name, value) = rest.split_once('=')?;
    let name = name.trim();
    let valid = !name.is_empty()
        && name
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || matches!(c, '_' | '-' | '.'));
    valid.then(|| (name, value.trim()))
}

/// Past the `%}` that closes a script, however many lines it takes.
fn skip_script<'a>(first: &str, lines: &mut impl Iterator<Item = &'a str>) {
    if first.contains("%}") {
        return;
    }
    for line in lines {
        if line.contains("%}") {
            return;
        }
    }
}

/// The method and URL, or the method this app does not know.
fn request_line_parts(line: &str) -> Result<(HttpMethod, String), String> {
    let mut parts = line.splitn(2, char::is_whitespace);
    let first = parts.next().unwrap_or_default();
    let rest = parts.next().unwrap_or_default().trim();

    let looks_like_method =
        !first.is_empty() && first.chars().all(|c| c.is_ascii_uppercase()) && !rest.is_empty();
    let (method, url) = if looks_like_method {
        let method = HttpMethod::ALL
            .into_iter()
            .find(|method| method.as_str() == first)
            .ok_or_else(|| first.to_owned())?;
        (method, rest)
    } else {
        // Both clients take a bare URL as a GET.
        (HttpMethod::Get, line)
    };

    // `GET /users HTTP/1.1`: the version is the client's business.
    let url = match url.rsplit_once(char::is_whitespace) {
        Some((before, version)) if version.starts_with("HTTP/") => before.trim_end(),
        _ => url,
    };
    Ok((method, url.to_owned()))
}

/// Replaces `{{name}}` for every file variable; anything else, including
/// `{{$guid}}` and the rest of the dynamic ones, is left for the app.
fn substitute(text: &str, variables: &BTreeMap<String, String>) -> String {
    let mut out = text.to_owned();
    for (name, value) in variables {
        out = out.replace(&format!("{{{{{name}}}}}"), value);
    }
    out
}

fn build(
    method: HttpMethod,
    url: String,
    headers: Vec<(String, String)>,
    body: String,
) -> HttpRequest {
    let mut request = HttpRequest::blank();
    request.method = method;
    request.query_params = query_params(&url);
    request.url = url;

    let mut headers = headers;
    if let Some(index) = position(&headers, "Authorization")
        && let Some(auth) = auth_from(&headers[index].1)
    {
        request.auth = auth;
        headers.remove(index);
    }

    let content_type = position(&headers, "Content-Type").map(|index| headers[index].1.clone());
    let (parsed_body, header_is_implied) = body_from(&body, content_type.as_deref());
    request.body = parsed_body;
    if header_is_implied && let Some(index) = position(&headers, "Content-Type") {
        headers.remove(index);
    }

    request.headers = headers
        .into_iter()
        .map(|(name, value)| KeyValueEntry::new(name, value))
        .collect();
    request
}

fn position(headers: &[(String, String)], name: &str) -> Option<usize> {
    headers
        .iter()
        .position(|(header, _)| header.eq_ignore_ascii_case(name))
}

fn query_params(url: &str) -> Vec<KeyValueEntry> {
    let Some((_, query)) = url.split_once('?') else {
        return Vec::new();
    };
    let query = query.split('#').next().unwrap_or_default();
    query
        .split('&')
        .filter(|pair| !pair.is_empty())
        .map(|pair| {
            let (name, value) = pair.split_once('=').unwrap_or((pair, ""));
            KeyValueEntry::new(decode(name, false), decode(value, false))
        })
        .collect()
}

fn decode(text: &str, plus_is_space: bool) -> String {
    let text = if plus_is_space {
        text.replace('+', " ")
    } else {
        text.to_owned()
    };
    percent_decode_str(&text).decode_utf8_lossy().into_owned()
}

/// The Auth tab's version of an `Authorization` header, where there is one.
/// Both clients accept `Basic user password` as well as the encoded form.
fn auth_from(value: &str) -> Option<AuthConfig> {
    let (scheme, rest) = value.trim().split_once(char::is_whitespace)?;
    let rest = rest.trim();
    if scheme.eq_ignore_ascii_case("bearer") {
        return Some(AuthConfig::Bearer {
            token: rest.to_owned(),
        });
    }
    let pair = || -> Option<(String, String)> {
        if let Some((username, password)) = rest.split_once(char::is_whitespace) {
            return Some((username.to_owned(), password.trim().to_owned()));
        }
        let decoded = String::from_utf8(STANDARD.decode(rest).ok()?).ok()?;
        let (username, password) = decoded.split_once(':')?;
        Some((username.to_owned(), password.to_owned()))
    };
    if scheme.eq_ignore_ascii_case("basic") {
        let (username, password) = pair()?;
        return Some(AuthConfig::Basic { username, password });
    }
    if scheme.eq_ignore_ascii_case("digest") {
        let (username, password) = rest
            .split_once(char::is_whitespace)
            .map(|(u, p)| (u.to_owned(), p.trim().to_owned()))?;
        return Some(AuthConfig::Digest { username, password });
    }
    None
}

/// The body, and whether its content type goes without saying — the app sets
/// it for JSON, forms and multipart, and a copy in the headers would fight it.
fn body_from(body: &str, content_type: Option<&str>) -> (RequestBody, bool) {
    if body.trim().is_empty() {
        return (RequestBody::None, false);
    }
    let kind = content_type.map(|kind| {
        kind.split(';')
            .next()
            .unwrap_or_default()
            .trim()
            .to_ascii_lowercase()
    });

    match kind.as_deref() {
        Some("application/json") => (
            RequestBody::Json {
                text: body.to_owned(),
            },
            true,
        ),
        Some("application/x-www-form-urlencoded") => {
            let joined: String = body.lines().map(str::trim).collect();
            let entries = joined
                .split('&')
                .filter(|pair| !pair.is_empty())
                .map(|pair| {
                    let (name, value) = pair.split_once('=').unwrap_or((pair, ""));
                    KeyValueEntry::new(decode(name, true), decode(value, true))
                })
                .collect();
            (RequestBody::UrlEncoded { entries }, true)
        }
        Some("multipart/form-data") => match content_type.and_then(boundary) {
            Some(boundary) => (
                RequestBody::Multipart {
                    entries: multipart(body, &boundary),
                },
                true,
            ),
            None => (
                RequestBody::Text {
                    text: body.to_owned(),
                    content_type: content_type.map(str::to_owned),
                },
                true,
            ),
        },
        Some(_) => (
            RequestBody::Text {
                text: body.to_owned(),
                content_type: content_type.map(str::to_owned),
            },
            true,
        ),
        None if matches!(body.trim_start().chars().next(), Some('{' | '[')) => (
            RequestBody::Json {
                text: body.to_owned(),
            },
            false,
        ),
        None => (
            RequestBody::Text {
                text: body.to_owned(),
                content_type: None,
            },
            false,
        ),
    }
}

fn boundary(content_type: &str) -> Option<String> {
    content_type.split(';').find_map(|part| {
        let (key, value) = part.split_once('=')?;
        key.trim()
            .eq_ignore_ascii_case("boundary")
            .then(|| value.trim().trim_matches('"').to_owned())
    })
}

fn multipart(body: &str, boundary: &str) -> Vec<MultipartEntry> {
    let delimiter = format!("--{boundary}");
    let mut entries = Vec::new();
    let mut part: Option<Vec<&str>> = None;

    for line in body.lines() {
        let trimmed = line.trim_end();
        if trimmed == delimiter || trimmed == format!("{delimiter}--") {
            if let Some(lines) = part.take()
                && let Some(entry) = multipart_entry(&lines)
            {
                entries.push(entry);
            }
            if trimmed == delimiter {
                part = Some(Vec::new());
            }
        } else if let Some(lines) = part.as_mut() {
            lines.push(line);
        }
    }
    if let Some(lines) = part
        && let Some(entry) = multipart_entry(&lines)
    {
        entries.push(entry);
    }
    entries
}

fn multipart_entry(lines: &[&str]) -> Option<MultipartEntry> {
    let blank = lines.iter().position(|line| line.trim().is_empty())?;
    let (headers, content) = (&lines[..blank], &lines[blank + 1..]);

    let mut name = None;
    let mut file_name = None;
    let mut content_type = None;
    for header in headers {
        let Some((key, value)) = header.split_once(':') else {
            continue;
        };
        if key.trim().eq_ignore_ascii_case("content-disposition") {
            for parameter in value.split(';') {
                if let Some((key, value)) = parameter.split_once('=') {
                    let value = value.trim().trim_matches('"').to_owned();
                    match key.trim() {
                        "name" => name = Some(value),
                        "filename" => file_name = Some(value),
                        _ => {}
                    }
                }
            }
        } else if key.trim().eq_ignore_ascii_case("content-type") {
            content_type = Some(value.trim().to_owned());
        }
    }

    let name = name?;
    let mut content: Vec<&str> = content.to_vec();
    while content.last().is_some_and(|line| line.trim().is_empty()) {
        content.pop();
    }

    // `< path` reads a file in, in both clients.
    let file = match content.as_slice() {
        [only] => only
            .trim()
            .strip_prefix('<')
            .map(|path| path.trim().to_owned()),
        _ => None,
    };
    let value = match file {
        Some(path) => {
            let own_name = path.rsplit(['/', '\\']).next().unwrap_or(&path).to_owned();
            MultipartValue::File {
                file_name: file_name.filter(|given| *given != own_name),
                content_type,
                path,
            }
        }
        None => MultipartValue::Text {
            value: content.join("\n"),
        },
    };

    Some(MultipartEntry {
        id: api_client_core::new_id(),
        enabled: true,
        name,
        value,
    })
}

#[cfg(test)]
mod tests {
    use api_client_core::{ApiKeyPlacement, OAuth2Settings};

    use super::*;
    use crate::http_file;

    fn one(text: &str) -> HttpRequest {
        let parsed = parse_http_file(text);
        assert!(parsed.skipped.is_empty(), "{:?}", parsed.skipped);
        assert_eq!(parsed.requests.len(), 1, "{parsed:?}");
        parsed.requests.into_iter().next().unwrap().request
    }

    fn headers(request: &HttpRequest) -> Vec<(&str, &str)> {
        request
            .headers
            .iter()
            .map(|h| (h.name.as_str(), h.value.as_str()))
            .collect()
    }

    #[test]
    fn reads_named_requests_with_headers_and_a_json_body() {
        let parsed = parse_http_file(
            "### Create user\n\
             POST https://api.example.com/users HTTP/1.1\n\
             Accept: application/json\n\
             Content-Type: application/json\n\
             \n\
             {\"name\": \"Ada\"}\n\
             \n\
             ### List users\n\
             GET https://api.example.com/users\n",
        );

        assert!(parsed.skipped.is_empty());
        let names: Vec<_> = parsed.requests.iter().map(|r| r.name.as_str()).collect();
        assert_eq!(names, ["Create user", "List users"]);

        let create = &parsed.requests[0].request;
        assert_eq!(create.method, HttpMethod::Post);
        assert_eq!(create.url, "https://api.example.com/users");
        assert_eq!(headers(create), [("Accept", "application/json")]);
        assert_eq!(
            create.body,
            RequestBody::Json {
                text: "{\"name\": \"Ada\"}".into()
            }
        );
    }

    #[test]
    fn a_bare_url_is_a_get_and_comments_are_ignored() {
        let request = one("# the health check\n// and another comment\nhttps://x.test/health\n");
        assert_eq!(request.method, HttpMethod::Get);
        assert_eq!(request.url, "https://x.test/health");
    }

    #[test]
    fn substitutes_file_variables_and_keeps_the_rest() {
        let request = one("@host = https://api.example.com\n\
             @base = {{host}}/v2\n\
             \n\
             ###\n\
             GET {{base}}/users?id={{$guid}}\n\
             Authorization: Bearer {{token}}\n");
        assert_eq!(request.url, "https://api.example.com/v2/users?id={{$guid}}");
        assert_eq!(
            request.auth,
            AuthConfig::Bearer {
                token: "{{token}}".into()
            }
        );
        assert!(request.headers.is_empty());
    }

    #[test]
    fn joins_a_query_spread_over_lines() {
        let request = one("GET https://x.test/search\n    ?q=rust\n    &page=2\n");
        assert_eq!(request.url, "https://x.test/search?q=rust&page=2");
        let params: Vec<_> = request
            .query_params
            .iter()
            .map(|p| (p.name.as_str(), p.value.as_str()))
            .collect();
        assert_eq!(params, [("q", "rust"), ("page", "2")]);
    }

    #[test]
    fn reads_basic_auth_in_both_spellings() {
        let spaced = one("GET https://x.test\nAuthorization: Basic ada s3cret\n");
        let encoded = one("GET https://x.test\nAuthorization: Basic YWRhOnMzY3JldA==\n");
        let expected = AuthConfig::Basic {
            username: "ada".into(),
            password: "s3cret".into(),
        };
        assert_eq!(spaced.auth, expected);
        assert_eq!(encoded.auth, expected);
    }

    #[test]
    fn keeps_an_authorization_header_it_has_no_tab_for() {
        let request = one("GET https://x.test\nAuthorization: Token abc\n");
        assert_eq!(request.auth, AuthConfig::None);
        assert_eq!(headers(&request), [("Authorization", "Token abc")]);
    }

    #[test]
    fn skips_what_it_cannot_send_and_says_so() {
        let parsed =
            parse_http_file("### Trace it\nTRACE https://x.test\n\n### Fine\nGET https://x.test\n");
        assert_eq!(parsed.requests.len(), 1);
        assert_eq!(
            parsed.skipped,
            ["\u{201c}Trace it\u{201d} was skipped: TRACE is not a method this app sends."]
        );
    }

    #[test]
    fn leaves_scripts_and_response_handlers_behind() {
        let request = one("< {%\n  request.variables.set(\"x\", 1)\n%}\n\
             POST https://x.test\n\
             Content-Type: text/plain\n\
             \n\
             hello\n\
             \n\
             > {%\n  client.test(\"ok\", () => {})\n%}\n");
        assert_eq!(
            request.body,
            RequestBody::Text {
                text: "hello".into(),
                content_type: Some("text/plain".into())
            }
        );
        assert!(request.headers.is_empty());
    }

    #[test]
    fn reads_a_form_body() {
        let request = one(
            "POST https://x.test\nContent-Type: application/x-www-form-urlencoded\n\nname=Ada+Lovelace&city=London\n",
        );
        let RequestBody::UrlEncoded { entries } = request.body else {
            panic!("expected a form");
        };
        let pairs: Vec<_> = entries
            .iter()
            .map(|e| (e.name.as_str(), e.value.as_str()))
            .collect();
        assert_eq!(pairs, [("name", "Ada Lovelace"), ("city", "London")]);
    }

    #[test]
    fn round_trips_what_the_writer_writes() {
        let mut upload = HttpRequest::get("https://x.test/upload");
        upload.method = HttpMethod::Post;
        upload.headers.push(KeyValueEntry::new("X-Trace", "1"));
        upload.body = RequestBody::Multipart {
            entries: vec![
                MultipartEntry::text("title", "Holiday"),
                MultipartEntry::file("photo", "/home/ada/beach.jpg"),
            ],
        };

        let mut search = HttpRequest::get("{{baseUrl}}/search?q={{name}}");
        search
            .query_params
            .push(KeyValueEntry::new("q", "{{name}}"));
        search.auth = AuthConfig::Basic {
            username: "ada".into(),
            password: "{{password}}".into(),
        };

        let mut keyed = HttpRequest::get("https://x.test/a");
        keyed.auth = AuthConfig::ApiKey {
            key: "X-API-Key".into(),
            value: "k".into(),
            placement: ApiKeyPlacement::Header,
        };

        let written = http_file([("Upload", &upload), ("Search", &search), ("Keyed", &keyed)]);
        let parsed = parse_http_file(&written);
        assert!(parsed.skipped.is_empty());
        let [upload_back, search_back, keyed_back] = parsed.requests.as_slice() else {
            panic!("expected three requests: {written}");
        };

        assert_eq!(upload_back.name, "Upload");
        assert_eq!(headers(&upload_back.request), [("X-Trace", "1")]);
        let RequestBody::Multipart { entries } = &upload_back.request.body else {
            panic!("expected multipart");
        };
        assert_eq!(entries.len(), 2);
        assert_eq!(
            entries[0].value,
            MultipartValue::Text {
                value: "Holiday".into()
            }
        );
        assert_eq!(
            entries[1].value,
            MultipartValue::File {
                path: "/home/ada/beach.jpg".into(),
                file_name: None,
                content_type: None
            }
        );

        assert_eq!(search_back.request.url, search.url);
        assert_eq!(search_back.request.auth, search.auth);

        // An API key is a header like any other once it is in a file.
        assert_eq!(headers(&keyed_back.request), [("X-API-Key", "k")]);
    }

    #[test]
    fn auth_the_writer_left_out_does_not_come_back_as_something_else() {
        let mut request = HttpRequest::get("https://x.test");
        request.auth = AuthConfig::OAuth2(OAuth2Settings::default());
        let parsed = parse_http_file(&http_file([("A", &request)]));
        assert_eq!(parsed.requests[0].request.auth, AuthConfig::None);
    }
}
