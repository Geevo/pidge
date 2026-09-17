use std::io;
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

        // Digest, properly: challenge, then verify the client's arithmetic.
        ["digest"] => digest_route(request),

        ["auth"] => Response::ok().json(&json!({
            "authorization": request.header("authorization").unwrap_or_default(),
        })),

        _ => Response::new(404, "Not Found").json(&json!({ "error": "not found" })),
    }
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
