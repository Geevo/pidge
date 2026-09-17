use std::io;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Duration;

use serde_json::json;
use tokio::io::AsyncWriteExt;

use crate::http::{Request, Response, Stream, write_response};

pub async fn dispatch<S: Stream>(request: Request, mut stream: S) -> io::Result<()> {
    let segments = request.segments();

    match segments.as_slice() {
        // Streaming routes write their own bytes, so they are handled first.
        ["slow-body", chunks, delay_ms] => {
            return slow_body(&mut stream, parse(chunks, 5), parse(delay_ms, 100)).await;
        }
        ["never"] => {
            // Hold the connection open with no response at all.
            tokio::time::sleep(Duration::from_secs(3600)).await;
            return Ok(());
        }
        _ => {}
    }

    let response = route(&request, &segments).await;
    write_response(&mut stream, response).await
}

async fn route(request: &Request, segments: &[&str]) -> Response {
    match segments {
        [] | ["json"] => Response::ok().json(&json!({
            "ok": true,
            "items": [1, 2, 3],
            "nested": { "message": "hello" }
        })),

        ["echo"] => Response::ok().json(&json!({
            "method": request.method,
            "path": request.path,
            "query": request.query_pairs(),
            "contentType": request.header("content-type"),
            "body": request.body_text(),
        })),

        ["headers"] => {
            let headers: Vec<_> = request
                .headers
                .iter()
                .map(|(name, value)| json!([name, value]))
                .collect();
            Response::ok().json(&json!({ "headers": headers }))
        }

        ["status", code] => {
            let code = parse(code, 200) as u16;
            Response::new(code, reason(code)).text(format!("status {code}"))
        }

        ["delay", ms] => {
            tokio::time::sleep(Duration::from_millis(parse(ms, 0) as u64)).await;
            Response::ok().json(&json!({ "delayedMs": parse(ms, 0) }))
        }

        ["binary"] => Response::ok()
            .header("content-type", "application/octet-stream")
            .body(vec![0u8, 159, 146, 150, 255, 1, 2, 3]),

        ["invalid-json"] => Response::ok()
            .header("content-type", "application/json")
            .body(b"{ this is not json".to_vec()),

        ["redirect", count] => {
            let count = parse(count, 0);
            let target = if count <= 1 {
                "/json".to_string()
            } else {
                format!("/redirect/{}", count - 1)
            };
            Response::new(302, "Found")
                .header("location", target)
                .text("redirecting")
        }

        ["redirect-loop"] => Response::new(302, "Found")
            .header("location", "/redirect-loop")
            .text("looping"),

        ["set-cookie"] => Response::ok()
            .header("set-cookie", "session=abc123; Path=/")
            .text("cookie set"),

        ["cookie"] => Response::ok().json(&json!({
            "cookie": request.header("cookie").unwrap_or_default(),
        })),

        ["multipart"] => Response::ok().json(&json!({
            "contentType": request.header("content-type"),
            "body": request.body_text(),
        })),

        ["large", bytes] => {
            let size = parse(bytes, 1024);
            Response::ok()
                .header("content-type", "application/octet-stream")
                .body(vec![b'x'; size])
        }

        // An OAuth 2 token endpoint: issues a token for the right client, and
        // an RFC 6749 error object for the wrong one.
        ["oauth", "token"] => oauth_token_route(request),

        // Anything with `Authorization: Bearer issued-token-N` gets through.
        ["oauth", "protected"] => match request.header("authorization") {
            Some(value) if value.starts_with("Bearer issued-token-") => {
                Response::ok().json(&json!({ "token": value.trim_start_matches("Bearer ") }))
            }
            _ => Response::new(401, "Unauthorized").json(&json!({ "error": "unauthorized" })),
        },

        // Digest, properly: challenge, then verify the client's arithmetic.
        ["digest"] => digest_route(request),

        ["auth"] => Response::ok().json(&json!({
            "authorization": request.header("authorization").unwrap_or_default(),
        })),

        _ => Response::new(404, "Not Found").json(&json!({ "error": "not found" })),
    }
}

/// Counts tokens issued, so a test can tell a fresh token from a cached one.
static TOKENS_ISSUED: AtomicUsize = AtomicUsize::new(0);

/// The client this endpoint knows, however it identifies itself.
const OAUTH_CLIENT: (&str, &str) = ("test-client", "test-secret");

fn oauth_token_route(request: &Request) -> Response {
    let form = form_pairs(&request.body_text());
    let get = |key: &str| form.get(key).cloned().unwrap_or_default();

    // Either the basic header or the body carries the client's identity.
    let (id, secret) = match request.header("authorization").and_then(basic_credentials) {
        Some(pair) => pair,
        None => (get("client_id"), get("client_secret")),
    };

    if (id.as_str(), secret.as_str()) != OAUTH_CLIENT {
        return Response::new(401, "Unauthorized").json(&json!({
            "error": "invalid_client",
            "error_description": "The client is not this client.",
        }));
    }

    let grant = get("grant_type");
    let refused = match grant.as_str() {
        "client_credentials" => None,
        "password" => {
            (get("username") != "ada" || get("password") != "lovelace").then_some("invalid_grant")
        }
        "refresh_token" => (get("refresh_token") != "a-refresh-token").then_some("invalid_grant"),
        _ => Some("unsupported_grant_type"),
    };

    if let Some(error) = refused {
        return Response::new(400, "Bad Request").json(&json!({
            "error": error,
            "error_description": "The grant was refused.",
        }));
    }

    let issued = TOKENS_ISSUED.fetch_add(1, Ordering::SeqCst);
    Response::ok().json(&json!({
        "access_token": format!("issued-token-{issued}"),
        "token_type": "Bearer",
        "expires_in": 3600,
        "scope": get("scope"),
    }))
}

/// `user=a&pass=b` into a map, for a form-encoded body.
fn form_pairs(body: &str) -> std::collections::HashMap<String, String> {
    body.split('&')
        .filter(|pair| !pair.is_empty())
        .filter_map(|pair| pair.split_once('='))
        .map(|(key, value)| (decode_form(key), decode_form(value)))
        .collect()
}

fn decode_form(value: &str) -> String {
    crate::http::decode(&value.replace('+', " "))
}

/// The username and password out of a `Basic` header.
fn basic_credentials(header: &str) -> Option<(String, String)> {
    use base64::Engine;

    let encoded = header.trim().strip_prefix("Basic ")?;
    let decoded = base64::engine::general_purpose::STANDARD
        .decode(encoded.trim())
        .ok()?;
    let text = String::from_utf8(decoded).ok()?;
    let (user, password) = text.split_once(':')?;
    Some((user.to_string(), password.to_string()))
}

/// The fixed nonce this server challenges with. A real one would be random and
/// time-limited; a constant makes the test's arithmetic checkable by hand.
const DIGEST_NONCE: &str = "dcd98b7102dd2f0e8b11d0f600bfb0c093";
const DIGEST_REALM: &str = "testserver";
const DIGEST_USER: &str = "ada";
const DIGEST_PASSWORD: &str = "lovelace";

/// 401 with a challenge until the client answers it correctly, then 200.
///
/// The response is recomputed here rather than pattern-matched, so the test
/// fails if the client's digest is merely well-formed.
fn digest_route(request: &Request) -> Response {
    let Some(header) = request.header("authorization") else {
        return challenge();
    };

    let parts = digest_parts(header);
    let get = |key: &str| parts.get(key).cloned().unwrap_or_default();

    if get("nonce") != DIGEST_NONCE || get("username") != DIGEST_USER {
        return challenge();
    }

    let ha1 = md5_hex(&format!("{DIGEST_USER}:{DIGEST_REALM}:{DIGEST_PASSWORD}"));
    let ha2 = md5_hex(&format!("{}:{}", request.method, get("uri")));
    let expected = if get("qop").is_empty() {
        md5_hex(&format!("{ha1}:{}:{ha2}", DIGEST_NONCE))
    } else {
        md5_hex(&format!(
            "{ha1}:{}:{}:{}:{}:{ha2}",
            DIGEST_NONCE,
            get("nc"),
            get("cnonce"),
            get("qop")
        ))
    };

    if get("response") == expected {
        Response::ok().json(&json!({ "authenticated": true, "uri": get("uri") }))
    } else {
        challenge()
    }
}

fn challenge() -> Response {
    Response::new(401, "Unauthorized")
        .header(
            "WWW-Authenticate",
            format!(
                "Digest realm=\"{DIGEST_REALM}\", qop=\"auth\", algorithm=MD5, nonce=\"{DIGEST_NONCE}\""
            ),
        )
        .json(&json!({ "error": "unauthorized" }))
}

/// `Digest username="ada", realm="...", response="..."` into a map.
fn digest_parts(header: &str) -> std::collections::HashMap<String, String> {
    header
        .trim()
        .strip_prefix("Digest ")
        .unwrap_or("")
        .split(',')
        .filter_map(|part| part.trim().split_once('='))
        .map(|(key, value)| {
            (
                key.trim().to_ascii_lowercase(),
                value.trim().trim_matches('"').to_string(),
            )
        })
        .collect()
}

fn md5_hex(input: &str) -> String {
    use md5::{Digest, Md5};

    let mut hasher = Md5::new();
    hasher.update(input.as_bytes());
    hasher
        .finalize()
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect()
}

/// Sends the head immediately, then dribbles out chunks. Used to test
/// cancellation and timeouts that land while the body is in flight.
async fn slow_body<S: Stream>(stream: &mut S, chunks: usize, delay_ms: usize) -> io::Result<()> {
    let head = "HTTP/1.1 200 OK\r\n\
                content-type: text/plain\r\n\
                transfer-encoding: chunked\r\n\
                connection: close\r\n\r\n";
    stream.write_all(head.as_bytes()).await?;
    stream.flush().await?;

    for index in 0..chunks {
        tokio::time::sleep(Duration::from_millis(delay_ms as u64)).await;
        let payload = format!("chunk-{index}\n");
        stream
            .write_all(format!("{:x}\r\n{payload}\r\n", payload.len()).as_bytes())
            .await?;
        stream.flush().await?;
    }

    stream.write_all(b"0\r\n\r\n").await?;
    stream.flush().await?;
    stream.shutdown().await
}

fn parse(value: &str, fallback: usize) -> usize {
    value.parse().unwrap_or(fallback)
}

fn reason(code: u16) -> &'static str {
    match code {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        301 => "Moved Permanently",
        302 => "Found",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        418 => "I'm a teapot",
        422 => "Unprocessable Entity",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Status",
    }
}
