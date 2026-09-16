use std::io;
use std::time::Duration;

use serde_json::json;
use tokio::io::AsyncWriteExt;
use tokio::net::TcpStream;

use crate::http::{Request, Response, write_response};

pub async fn dispatch(request: Request, mut stream: TcpStream) -> io::Result<()> {
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

        ["auth"] => Response::ok().json(&json!({
            "authorization": request.header("authorization").unwrap_or_default(),
        })),

        _ => Response::new(404, "Not Found").json(&json!({ "error": "not found" })),
    }
}

/// Sends the head immediately, then dribbles out chunks. Used to test
/// cancellation and timeouts that land while the body is in flight.
async fn slow_body(stream: &mut TcpStream, chunks: usize, delay_ms: usize) -> io::Result<()> {
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
