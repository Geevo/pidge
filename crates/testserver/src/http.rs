use std::collections::HashMap;
use std::io;

use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt, BufReader};

use crate::routes;

/// Anything the server can talk over: a plain socket or a TLS stream.
pub trait Stream: AsyncRead + AsyncWrite + Unpin + Send {}
impl<T: AsyncRead + AsyncWrite + Unpin + Send> Stream for T {}

/// A parsed request. Header names are lowercased.
pub struct Request {
    pub method: String,
    pub path: String,
    pub query: String,
    pub headers: Vec<(String, String)>,
    pub body: Vec<u8>,
}

impl Request {
    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers
            .iter()
            .find(|(key, _)| key == name)
            .map(|(_, value)| value.as_str())
    }

    pub fn query_pairs(&self) -> HashMap<String, String> {
        self.query
            .split('&')
            .filter(|pair| !pair.is_empty())
            .map(|pair| match pair.split_once('=') {
                Some((key, value)) => (decode(key), decode(value)),
                None => (decode(pair), String::new()),
            })
            .collect()
    }

    pub fn body_text(&self) -> String {
        String::from_utf8_lossy(&self.body).into_owned()
    }

    /// Path split into non-empty segments.
    pub fn segments(&self) -> Vec<&str> {
        self.path.split('/').filter(|s| !s.is_empty()).collect()
    }
}

pub(crate) fn decode(input: &str) -> String {
    let bytes = input.replace('+', " ").into_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut index = 0;
    while index < bytes.len() {
        if bytes[index] == b'%' && index + 2 < bytes.len() {
            let hex = std::str::from_utf8(&bytes[index + 1..index + 3]).unwrap_or("");
            if let Ok(byte) = u8::from_str_radix(hex, 16) {
                out.push(byte);
                index += 3;
                continue;
            }
        }
        out.push(bytes[index]);
        index += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

/// What a route decided to send back.
pub struct Response {
    pub status: u16,
    pub reason: String,
    pub headers: Vec<(String, String)>,
    pub body: Vec<u8>,
}

impl Response {
    pub fn new(status: u16, reason: &str) -> Self {
        Self {
            status,
            reason: reason.to_string(),
            headers: Vec::new(),
            body: Vec::new(),
        }
    }

    pub fn ok() -> Self {
        Self::new(200, "OK")
    }

    pub fn header(mut self, name: &str, value: impl Into<String>) -> Self {
        self.headers.push((name.to_string(), value.into()));
        self
    }

    pub fn body(mut self, body: impl Into<Vec<u8>>) -> Self {
        self.body = body.into();
        self
    }

    pub fn text(self, text: impl Into<String>) -> Self {
        self.header("content-type", "text/plain; charset=utf-8")
            .body(text.into().into_bytes())
    }

    pub fn json(self, value: &serde_json::Value) -> Self {
        self.header("content-type", "application/json")
            .body(value.to_string().into_bytes())
    }
}

pub async fn serve_connection<S: Stream>(stream: S) -> io::Result<()> {
    let mut reader = BufReader::new(stream);
    let Some(request) = read_request(&mut reader).await? else {
        return Ok(());
    };

    let stream = reader.into_inner();
    routes::dispatch(request, stream).await
}

async fn read_request<S: Stream>(reader: &mut BufReader<S>) -> io::Result<Option<Request>> {
    let head = match read_until_double_crlf(reader).await? {
        Some(head) => head,
        None => return Ok(None),
    };

    let text = String::from_utf8_lossy(&head).into_owned();
    let mut lines = text.split("\r\n");
    let Some(request_line) = lines.next() else {
        return Ok(None);
    };

    let mut parts = request_line.split_whitespace();
    let method = parts.next().unwrap_or("GET").to_string();
    let target = parts.next().unwrap_or("/").to_string();
    let (path, query) = match target.split_once('?') {
        Some((path, query)) => (path.to_string(), query.to_string()),
        None => (target, String::new()),
    };

    let mut headers = Vec::new();
    for line in lines {
        if line.is_empty() {
            continue;
        }
        if let Some((name, value)) = line.split_once(':') {
            headers.push((name.trim().to_ascii_lowercase(), value.trim().to_string()));
        }
    }

    let mut request = Request {
        method,
        path,
        query,
        headers,
        body: Vec::new(),
    };

    request.body = read_body(reader, &request).await?;
    Ok(Some(request))
}

async fn read_until_double_crlf<S: Stream>(
    reader: &mut BufReader<S>,
) -> io::Result<Option<Vec<u8>>> {
    let mut head = Vec::new();
    let mut byte = [0u8; 1];
    loop {
        let read = reader.read(&mut byte).await?;
        if read == 0 {
            return Ok(if head.is_empty() { None } else { Some(head) });
        }
        head.push(byte[0]);
        if head.ends_with(b"\r\n\r\n") {
            head.truncate(head.len() - 4);
            return Ok(Some(head));
        }
        if head.len() > 64 * 1024 {
            return Ok(Some(head));
        }
    }
}

async fn read_body<S: Stream>(reader: &mut BufReader<S>, request: &Request) -> io::Result<Vec<u8>> {
    if request
        .header("transfer-encoding")
        .is_some_and(|value| value.to_ascii_lowercase().contains("chunked"))
    {
        return read_chunked(reader).await;
    }

    let length: usize = request
        .header("content-length")
        .and_then(|value| value.parse().ok())
        .unwrap_or(0);

    let mut body = vec![0u8; length];
    if length > 0 {
        reader.read_exact(&mut body).await?;
    }
    Ok(body)
}

async fn read_chunked<S: Stream>(reader: &mut BufReader<S>) -> io::Result<Vec<u8>> {
    let mut body = Vec::new();
    loop {
        let line = read_line(reader).await?;
        let size_text = line.split(';').next().unwrap_or("").trim().to_string();
        let size = usize::from_str_radix(&size_text, 16).unwrap_or(0);
        if size == 0 {
            // Consume trailers up to the terminating blank line.
            while !read_line(reader).await?.is_empty() {}
            break;
        }
        let mut chunk = vec![0u8; size];
        reader.read_exact(&mut chunk).await?;
        body.extend_from_slice(&chunk);
        let mut crlf = [0u8; 2];
        reader.read_exact(&mut crlf).await?;
    }
    Ok(body)
}

async fn read_line<S: Stream>(reader: &mut BufReader<S>) -> io::Result<String> {
    let mut line = Vec::new();
    let mut byte = [0u8; 1];
    loop {
        if reader.read(&mut byte).await? == 0 {
            break;
        }
        if byte[0] == b'\n' {
            break;
        }
        if byte[0] != b'\r' {
            line.push(byte[0]);
        }
    }
    Ok(String::from_utf8_lossy(&line).into_owned())
}

pub async fn write_response<S: Stream>(stream: &mut S, response: Response) -> io::Result<()> {
    let mut head = format!("HTTP/1.1 {} {}\r\n", response.status, response.reason);
    for (name, value) in &response.headers {
        head.push_str(&format!("{name}: {value}\r\n"));
    }
    head.push_str(&format!("content-length: {}\r\n", response.body.len()));
    head.push_str("connection: close\r\n\r\n");

    stream.write_all(head.as_bytes()).await?;
    stream.write_all(&response.body).await?;
    stream.flush().await?;
    stream.shutdown().await
}
