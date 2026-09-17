use std::io;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Duration;

use md4::Digest as _;
use serde_json::json;
use tokio::io::AsyncWriteExt;

use crate::http::{ConnectionState, Request, Response, Stream, write_response_keeping_alive};

/// Answers one request. `true` means the connection stays open for another.
pub async fn dispatch<S: Stream>(
    request: Request,
    stream: &mut S,
    state: &mut ConnectionState,
) -> io::Result<bool> {
    let segments = request.segments();

    match segments.as_slice() {
        // Streaming routes write their own bytes, so they are handled first.
        ["slow-body", chunks, delay_ms] => {
            slow_body(stream, parse(chunks, 5), parse(delay_ms, 100)).await?;
            return Ok(false);
        }
        ["never"] => {
            // Hold the connection open with no response at all.
            tokio::time::sleep(Duration::from_secs(3600)).await;
            return Ok(false);
        }
        _ => {}
    }

    // Keep-alive only where it is asked for and useful: the NTLM handshake
    // needs it, and everything else still gets the old close-per-response.
    let wants_keep_alive = request
        .header("connection")
        .map(|value| value.to_ascii_lowercase().contains("keep-alive"))
        .unwrap_or(false)
        || !request
            .header("connection")
            .map(|value| value.to_ascii_lowercase().contains("close"))
            .unwrap_or(false);

    let response = route_with_state(&request, &segments, state).await;
    let keep_alive = wants_keep_alive;

    write_response_keeping_alive(stream, response, keep_alive).await?;
    if !keep_alive {
        stream.shutdown().await?;
    }
    Ok(keep_alive)
}

/// The routes that need to remember something about the connection.
async fn route_with_state(
    request: &Request,
    segments: &[&str],
    state: &mut ConnectionState,
) -> Response {
    match segments {
        ["ntlm"] => ntlm_route(request, state),
        _ => route(request, segments).await,
    }
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
            // The query as it arrived. `query` is a map, so it cannot show a
            // parameter that was sent twice — which is a thing worth testing.
            "rawQuery": request.query,
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

/// The account this server knows.
const NTLM_USER: (&str, &str, &str) = ("ada", "lovelace", "LOVELACE-LTD");

/// An NTLM server, including the part that matters: the challenge lives on the
/// connection. A client that sends its authenticate message on a different
/// socket finds no challenge waiting and is refused, which is how a real server
/// behaves and the only way to test that the handshake held one connection.
fn ntlm_route(request: &Request, state: &mut ConnectionState) -> Response {
    let Some(header) = request.header("authorization") else {
        return ntlm_challenge_response(None);
    };
    let Some(encoded) = header.trim().strip_prefix("NTLM ") else {
        return ntlm_challenge_response(None);
    };

    let Ok(message) = base64_decode(encoded.trim()) else {
        return Response::new(400, "Bad Request").json(&json!({ "error": "not base64" }));
    };
    if message.len() < 12 || &message[0..8] != b"NTLMSSP\0" {
        return Response::new(400, "Bad Request").json(&json!({ "error": "not ntlmssp" }));
    }

    match u32::from_le_bytes([message[8], message[9], message[10], message[11]]) {
        // Negotiate: answer with a challenge and remember it for this socket.
        1 => {
            let challenge = [0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef];
            state.ntlm_challenge = Some(challenge);
            ntlm_challenge_response(Some(challenge))
        }
        // Authenticate: only answerable against this connection's challenge.
        3 => match state.ntlm_challenge {
            None => Response::new(401, "Unauthorized").json(&json!({
                "error": "no challenge on this connection",
            })),
            Some(challenge) => verify_ntlm_authenticate(&message, &challenge, state),
        },
        other => Response::new(400, "Bad Request").json(&json!({ "messageType": other })),
    }
}

fn ntlm_challenge_response(challenge: Option<[u8; 8]>) -> Response {
    let value = match challenge {
        None => "NTLM".to_string(),
        Some(challenge) => {
            let mut message = Vec::new();
            message.extend_from_slice(b"NTLMSSP\0");
            message.extend_from_slice(&2u32.to_le_bytes());
            // Target name: empty, pointing past the fixed header.
            message.extend_from_slice(&[0, 0, 0, 0]);
            message.extend_from_slice(&48u32.to_le_bytes());
            message.extend_from_slice(&0x0008_0201u32.to_le_bytes()); // unicode, ntlm, ess
            message.extend_from_slice(&challenge);
            message.extend_from_slice(&[0u8; 8]); // reserved
            // Target info: one NetBIOS domain pair and a terminator.
            let mut info = Vec::new();
            info.extend_from_slice(&2u16.to_le_bytes());
            let name: Vec<u8> = "TESTSERVER"
                .encode_utf16()
                .flat_map(u16::to_le_bytes)
                .collect();
            info.extend_from_slice(&(name.len() as u16).to_le_bytes());
            info.extend_from_slice(&name);
            info.extend_from_slice(&[0, 0, 0, 0]);

            message.extend_from_slice(&(info.len() as u16).to_le_bytes());
            message.extend_from_slice(&(info.len() as u16).to_le_bytes());
            message.extend_from_slice(&48u32.to_le_bytes());
            message.extend_from_slice(&info);

            format!("NTLM {}", base64_encode(&message))
        }
    };

    Response::new(401, "Unauthorized")
        .header("WWW-Authenticate", value)
        .json(&json!({ "error": "unauthorized" }))
}

/// Recomputes the NTLMv2 proof from the blob the client sent.
fn verify_ntlm_authenticate(
    message: &[u8],
    challenge: &[u8; 8],
    state: &mut ConnectionState,
) -> Response {
    let field = |at: usize| -> Option<&[u8]> {
        let len = u16::from_le_bytes([*message.get(at)?, *message.get(at + 1)?]) as usize;
        let offset = u32::from_le_bytes([
            *message.get(at + 4)?,
            *message.get(at + 5)?,
            *message.get(at + 6)?,
            *message.get(at + 7)?,
        ]) as usize;
        message.get(offset..offset + len)
    };

    // NT response at 20, domain at 28, user at 36.
    let (Some(nt_response), Some(domain), Some(user)) = (field(20), field(28), field(36)) else {
        return Response::new(400, "Bad Request").json(&json!({ "error": "truncated message" }));
    };
    if nt_response.len() < 16 {
        return Response::new(400, "Bad Request").json(&json!({ "error": "no proof" }));
    }

    let (proof, blob) = nt_response.split_at(16);
    let user = from_utf16le(user);
    let domain = from_utf16le(domain);

    if user != NTLM_USER.0 || domain != NTLM_USER.2 {
        return Response::new(401, "Unauthorized").json(&json!({ "error": "unknown account" }));
    }

    // NTOWFv2, then the proof over the server challenge and the blob.
    let nt_hash = md4::Md4::digest(utf16le(NTLM_USER.1));
    let identity = utf16le(&format!("{}{}", user.to_uppercase(), domain));
    let key = hmac_md5(&nt_hash, &identity);

    let mut signed = Vec::with_capacity(8 + blob.len());
    signed.extend_from_slice(challenge);
    signed.extend_from_slice(blob);

    if hmac_md5(&key, &signed) != proof {
        return Response::new(401, "Unauthorized").json(&json!({ "error": "bad proof" }));
    }

    // Authenticated for the life of this connection, as NTLM has it.
    state.ntlm_challenge = None;
    Response::ok().json(&json!({
        "authenticated": true,
        "user": user,
        "domain": domain,
        "requestsOnThisConnection": state.requests,
    }))
}

fn hmac_md5(key: &[u8], data: &[u8]) -> Vec<u8> {
    use hmac::Mac;

    let mut mac = hmac::Hmac::<md5::Md5>::new_from_slice(key).expect("any key length");
    mac.update(data);
    mac.finalize().into_bytes().to_vec()
}

fn utf16le(value: &str) -> Vec<u8> {
    value.encode_utf16().flat_map(u16::to_le_bytes).collect()
}

fn from_utf16le(bytes: &[u8]) -> String {
    let units: Vec<u16> = bytes
        .as_chunks::<2>()
        .0
        .iter()
        .map(|pair| u16::from_le_bytes(*pair))
        .collect();
    String::from_utf16_lossy(&units)
}

fn base64_encode(bytes: &[u8]) -> String {
    use base64::Engine;
    base64::engine::general_purpose::STANDARD.encode(bytes)
}

fn base64_decode(value: &str) -> Result<Vec<u8>, base64::DecodeError> {
    use base64::Engine;
    base64::engine::general_purpose::STANDARD.decode(value)
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
