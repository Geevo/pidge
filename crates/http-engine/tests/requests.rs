//! Engine tests against a local server. Nothing here touches the internet.

use api_client_core::{
    AuthConfig, HttpMethod, HttpRequest, KeyValueEntry, MultipartEntry, RequestBody,
    RequestErrorKind,
};
use api_client_http_engine::{CancellationHandle, EngineConfig, HttpEngine, normalize_url};
use api_client_testserver::TestServer;
use api_client_variables::VariableSet;

fn engine() -> HttpEngine {
    HttpEngine::new(EngineConfig::default()).expect("engine")
}

fn engine_with(config: EngineConfig) -> HttpEngine {
    HttpEngine::new(config).expect("engine")
}

fn body_json(bytes: &[u8]) -> serde_json::Value {
    serde_json::from_slice(bytes).expect("response body should be JSON")
}

#[tokio::test]
async fn sends_a_plain_get() {
    let server = TestServer::start().await.unwrap();
    let response = engine()
        .execute(
            HttpRequest::get(server.url("/json")),
            CancellationHandle::new(),
        )
        .await
        .expect("request should succeed");

    assert_eq!(response.status, 200);
    assert_eq!(response.status_text, "OK");
    assert_eq!(response.mime_type.as_deref(), Some("application/json"));
    assert_eq!(body_json(&response.body)["ok"], true);
    assert_eq!(response.size_bytes, response.body.len() as u64);
    assert!(!response.truncated);
}

#[tokio::test]
async fn bare_host_and_port_becomes_http() {
    let url = normalize_url("localhost:3000/test").expect("should normalize");
    assert_eq!(url.as_str(), "http://localhost:3000/test");
}

#[tokio::test]
async fn rejects_unsupported_schemes_and_empty_input() {
    assert_eq!(
        normalize_url("ftp://example.com").unwrap_err().kind,
        RequestErrorKind::UnsupportedScheme
    );
    assert_eq!(
        normalize_url("   ").unwrap_err().kind,
        RequestErrorKind::InvalidUrl
    );
    assert_eq!(
        normalize_url("http://").unwrap_err().kind,
        RequestErrorKind::InvalidUrl
    );
}

#[tokio::test]
async fn query_params_are_appended_without_dropping_existing_ones() {
    let server = TestServer::start().await.unwrap();
    let mut request = HttpRequest::get(server.url("/echo?existing=1"));
    request.query_params = vec![
        KeyValueEntry::new("q", "hello world"),
        KeyValueEntry::disabled("skipped", "nope"),
        KeyValueEntry::new("", "no name is not a param"),
    ];

    let response = engine()
        .execute(request, CancellationHandle::new())
        .await
        .unwrap();
    let query = &body_json(&response.body)["query"];

    assert_eq!(query["existing"], "1");
    assert_eq!(query["q"], "hello world");
    assert!(query.get("skipped").is_none());
}

#[tokio::test]
async fn sends_enabled_headers_only() {
    let server = TestServer::start().await.unwrap();
    let mut request = HttpRequest::get(server.url("/headers"));
    request.headers = vec![
        KeyValueEntry::new("X-Custom", "yes"),
        KeyValueEntry::disabled("X-Skipped", "no"),
    ];

    let response = engine()
        .execute(request, CancellationHandle::new())
        .await
        .unwrap();
    let text = String::from_utf8_lossy(&response.body).to_lowercase();

    assert!(text.contains("x-custom"));
    assert!(!text.contains("x-skipped"));
}

#[tokio::test]
async fn rejects_invalid_header_names() {
    let server = TestServer::start().await.unwrap();
    let mut request = HttpRequest::get(server.url("/json"));
    request.headers = vec![KeyValueEntry::new("bad header", "value")];

    let error = engine()
        .execute(request, CancellationHandle::new())
        .await
        .unwrap_err();
    assert_eq!(error.kind, RequestErrorKind::InvalidHeader);
}

#[tokio::test]
async fn posts_a_json_body_with_a_content_type() {
    let server = TestServer::start().await.unwrap();
    let mut request = HttpRequest::get(server.url("/echo"));
    request.method = HttpMethod::Post;
    request.body = RequestBody::Json {
        text: r#"{"name":"ada"}"#.to_string(),
    };

    let response = engine()
        .execute(request, CancellationHandle::new())
        .await
        .unwrap();
    let echoed = body_json(&response.body);

    assert_eq!(echoed["method"], "POST");
    assert_eq!(echoed["contentType"], "application/json");
    assert_eq!(echoed["body"], r#"{"name":"ada"}"#);
}

#[tokio::test]
async fn an_explicit_content_type_is_not_overwritten() {
    let server = TestServer::start().await.unwrap();
    let mut request = HttpRequest::get(server.url("/echo"));
    request.method = HttpMethod::Post;
    request.headers = vec![KeyValueEntry::new(
        "Content-Type",
        "application/vnd.api+json",
    )];
    request.body = RequestBody::Json {
        text: "{}".to_string(),
    };

    let response = engine()
        .execute(request, CancellationHandle::new())
        .await
        .unwrap();
    assert_eq!(
        body_json(&response.body)["contentType"],
        "application/vnd.api+json"
    );
}

#[tokio::test]
async fn sends_url_encoded_bodies() {
    let server = TestServer::start().await.unwrap();
    let mut request = HttpRequest::get(server.url("/echo"));
    request.method = HttpMethod::Post;
    request.body = RequestBody::UrlEncoded {
        entries: vec![
            KeyValueEntry::new("name", "ada lovelace"),
            KeyValueEntry::disabled("ignored", "yes"),
        ],
    };

    let response = engine()
        .execute(request, CancellationHandle::new())
        .await
        .unwrap();
    let echoed = body_json(&response.body);

    assert_eq!(echoed["contentType"], "application/x-www-form-urlencoded");
    assert_eq!(echoed["body"], "name=ada+lovelace");
}

#[tokio::test]
async fn sends_multipart_bodies() {
    let server = TestServer::start().await.unwrap();
    let mut request = HttpRequest::get(server.url("/multipart"));
    request.method = HttpMethod::Post;
    request.body = RequestBody::Multipart {
        entries: vec![MultipartEntry::text("field", "value")],
    };

    let response = engine()
        .execute(request, CancellationHandle::new())
        .await
        .unwrap();
    let echoed = body_json(&response.body);

    assert!(
        echoed["contentType"]
            .as_str()
            .unwrap()
            .starts_with("multipart/form-data; boundary=")
    );
    let raw = echoed["body"].as_str().unwrap();
    assert!(raw.contains(r#"name="field""#));
    assert!(raw.contains("value"));
}

#[tokio::test]
async fn multipart_sends_file_contents() {
    let server = TestServer::start().await.unwrap();
    let dir = std::env::temp_dir().join(format!("api-client-test-{}", api_client_core::new_id()));
    std::fs::create_dir_all(&dir).unwrap();
    let path = dir.join("note.txt");
    std::fs::write(&path, b"file contents here").unwrap();

    let mut request = HttpRequest::get(server.url("/multipart"));
    request.method = HttpMethod::Post;
    request.body = RequestBody::Multipart {
        entries: vec![MultipartEntry::file("upload", path.display().to_string())],
    };

    let response = engine()
        .execute(request, CancellationHandle::new())
        .await
        .unwrap();
    let raw = body_json(&response.body)["body"]
        .as_str()
        .unwrap()
        .to_string();

    std::fs::remove_dir_all(&dir).ok();

    assert!(raw.contains(r#"filename="note.txt""#));
    assert!(raw.contains("file contents here"));
}

#[tokio::test]
async fn missing_multipart_files_are_reported_clearly() {
    let server = TestServer::start().await.unwrap();
    let mut request = HttpRequest::get(server.url("/multipart"));
    request.method = HttpMethod::Post;
    request.body = RequestBody::Multipart {
        entries: vec![MultipartEntry::file("upload", "/definitely/not/here.txt")],
    };

    let error = engine()
        .execute(request, CancellationHandle::new())
        .await
        .unwrap_err();
    assert_eq!(error.kind, RequestErrorKind::Io);
}

#[tokio::test]
async fn applies_bearer_auth() {
    let server = TestServer::start().await.unwrap();
    let mut request = HttpRequest::get(server.url("/auth"));
    request.auth = AuthConfig::Bearer {
        token: "secret-token".to_string(),
    };

    let response = engine()
        .execute(request, CancellationHandle::new())
        .await
        .unwrap();
    assert_eq!(
        body_json(&response.body)["authorization"],
        "Bearer secret-token"
    );
    assert!(response.warnings.is_empty());
}

#[tokio::test]
async fn applies_basic_auth() {
    let server = TestServer::start().await.unwrap();
    let mut request = HttpRequest::get(server.url("/auth"));
    request.auth = AuthConfig::Basic {
        username: "ada".to_string(),
        password: "lovelace".to_string(),
    };

    let response = engine()
        .execute(request, CancellationHandle::new())
        .await
        .unwrap();
    // base64("ada:lovelace")
    assert_eq!(
        body_json(&response.body)["authorization"],
        "Basic YWRhOmxvdmVsYWNl"
    );
}

#[tokio::test]
async fn an_explicit_authorization_header_wins_and_warns() {
    let server = TestServer::start().await.unwrap();
    let mut request = HttpRequest::get(server.url("/auth"));
    request.headers = vec![KeyValueEntry::new("Authorization", "Token typed-by-hand")];
    request.auth = AuthConfig::Bearer {
        token: "from-the-auth-tab".to_string(),
    };

    let response = engine()
        .execute(request, CancellationHandle::new())
        .await
        .unwrap();

    assert_eq!(
        body_json(&response.body)["authorization"],
        "Token typed-by-hand"
    );
    assert_eq!(response.warnings.len(), 1);
    assert!(response.warnings[0].contains("Authorization"));
}

#[tokio::test]
async fn a_disabled_authorization_header_does_not_block_the_auth_tab() {
    let server = TestServer::start().await.unwrap();
    let mut request = HttpRequest::get(server.url("/auth"));
    request.headers = vec![KeyValueEntry::disabled("Authorization", "Token ignored")];
    request.auth = AuthConfig::Bearer {
        token: "wins".to_string(),
    };

    let response = engine()
        .execute(request, CancellationHandle::new())
        .await
        .unwrap();
    assert_eq!(body_json(&response.body)["authorization"], "Bearer wins");
    assert!(response.warnings.is_empty());
}

#[tokio::test]
async fn reports_non_2xx_statuses_as_responses_not_errors() {
    let server = TestServer::start().await.unwrap();
    let response = engine()
        .execute(
            HttpRequest::get(server.url("/status/418")),
            CancellationHandle::new(),
        )
        .await
        .unwrap();

    assert_eq!(response.status, 418);
    assert_eq!(response.status_text, "I'm a teapot");
}

#[tokio::test]
async fn follows_redirects_to_the_final_url() {
    let server = TestServer::start().await.unwrap();
    let response = engine()
        .execute(
            HttpRequest::get(server.url("/redirect/3")),
            CancellationHandle::new(),
        )
        .await
        .unwrap();

    assert_eq!(response.status, 200);
    assert!(response.final_url.ends_with("/json"));
}

#[tokio::test]
async fn a_redirect_loop_is_a_normalized_error() {
    let server = TestServer::start().await.unwrap();
    let error = engine()
        .execute(
            HttpRequest::get(server.url("/redirect-loop")),
            CancellationHandle::new(),
        )
        .await
        .unwrap_err();

    assert_eq!(error.kind, RequestErrorKind::TooManyRedirects);
    assert!(error.detail.is_some());
}

#[tokio::test]
async fn redirects_can_be_turned_off() {
    let server = TestServer::start().await.unwrap();
    let engine = engine_with(EngineConfig {
        follow_redirects: false,
        ..EngineConfig::default()
    });

    let response = engine
        .execute(
            HttpRequest::get(server.url("/redirect/1")),
            CancellationHandle::new(),
        )
        .await
        .unwrap();

    assert_eq!(response.status, 302);
    assert_eq!(response.header("location"), Some("/json"));
}

#[tokio::test]
async fn cookies_set_by_the_server_come_back_on_the_next_request() {
    let server = TestServer::start().await.unwrap();
    let engine = engine();

    let first = engine
        .execute(
            HttpRequest::get(server.url("/set-cookie")),
            CancellationHandle::new(),
        )
        .await
        .unwrap();
    assert_eq!(first.header("set-cookie"), Some("session=abc123; Path=/"));

    let second = engine
        .execute(
            HttpRequest::get(server.url("/cookie")),
            CancellationHandle::new(),
        )
        .await
        .unwrap();
    assert_eq!(body_json(&second.body)["cookie"], "session=abc123");
}

#[tokio::test]
async fn binary_responses_survive_intact() {
    let server = TestServer::start().await.unwrap();
    let response = engine()
        .execute(
            HttpRequest::get(server.url("/binary")),
            CancellationHandle::new(),
        )
        .await
        .unwrap();

    assert_eq!(response.body, vec![0u8, 159, 146, 150, 255, 1, 2, 3]);
    assert_eq!(
        response.mime_type.as_deref(),
        Some("application/octet-stream")
    );
}

#[tokio::test]
async fn invalid_json_is_returned_as_bytes_not_an_error() {
    let server = TestServer::start().await.unwrap();
    let response = engine()
        .execute(
            HttpRequest::get(server.url("/invalid-json")),
            CancellationHandle::new(),
        )
        .await
        .unwrap();

    assert_eq!(response.status, 200);
    assert_eq!(response.body, b"{ this is not json");
}

#[tokio::test]
async fn large_responses_are_truncated_at_the_limit() {
    let server = TestServer::start().await.unwrap();
    let engine = engine_with(EngineConfig {
        max_response_bytes: 1024,
        ..EngineConfig::default()
    });

    let response = engine
        .execute(
            HttpRequest::get(server.url("/large/8192")),
            CancellationHandle::new(),
        )
        .await
        .unwrap();

    assert!(response.truncated);
    assert_eq!(response.size_bytes, 1024);
}

#[tokio::test]
async fn responses_under_the_limit_are_not_truncated() {
    let server = TestServer::start().await.unwrap();
    let engine = engine_with(EngineConfig {
        max_response_bytes: 8192,
        ..EngineConfig::default()
    });

    let response = engine
        .execute(
            HttpRequest::get(server.url("/large/4096")),
            CancellationHandle::new(),
        )
        .await
        .unwrap();

    assert!(!response.truncated);
    assert_eq!(response.size_bytes, 4096);
}

#[tokio::test]
async fn times_out_instead_of_hanging() {
    let server = TestServer::start().await.unwrap();
    let mut request = HttpRequest::get(server.url("/never"));
    request.timeout_ms = Some(150);

    let error = engine()
        .execute(request, CancellationHandle::new())
        .await
        .unwrap_err();

    assert_eq!(error.kind, RequestErrorKind::Timeout);
    assert!(error.message.contains("150"));
}

#[tokio::test]
async fn times_out_while_the_body_is_still_arriving() {
    let server = TestServer::start().await.unwrap();
    let mut request = HttpRequest::get(server.url("/slow-body/20/200"));
    request.timeout_ms = Some(250);

    let error = engine()
        .execute(request, CancellationHandle::new())
        .await
        .unwrap_err();

    assert_eq!(error.kind, RequestErrorKind::Timeout);
}

#[tokio::test]
async fn cancellation_stops_a_pending_request() {
    let server = TestServer::start().await.unwrap();
    let handle = CancellationHandle::new();
    let cancel = handle.clone();

    tokio::spawn(async move {
        tokio::time::sleep(std::time::Duration::from_millis(50)).await;
        cancel.cancel();
    });

    let error = engine()
        .execute(HttpRequest::get(server.url("/never")), handle)
        .await
        .unwrap_err();

    assert_eq!(error.kind, RequestErrorKind::Cancelled);
}

#[tokio::test]
async fn cancellation_stops_a_body_that_is_still_downloading() {
    let server = TestServer::start().await.unwrap();
    let handle = CancellationHandle::new();
    let cancel = handle.clone();

    tokio::spawn(async move {
        tokio::time::sleep(std::time::Duration::from_millis(80)).await;
        cancel.cancel();
    });

    let error = engine()
        .execute(HttpRequest::get(server.url("/slow-body/50/50")), handle)
        .await
        .unwrap_err();

    assert_eq!(error.kind, RequestErrorKind::Cancelled);
}

#[tokio::test]
async fn an_already_cancelled_handle_never_sends() {
    let server = TestServer::start().await.unwrap();
    let handle = CancellationHandle::new();
    handle.cancel();

    let error = engine()
        .execute(HttpRequest::get(server.url("/json")), handle)
        .await
        .unwrap_err();

    assert_eq!(error.kind, RequestErrorKind::Cancelled);
}

#[tokio::test]
async fn a_refused_connection_says_so() {
    // Bind and drop, so the port is almost certainly closed.
    let port = {
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        listener.local_addr().unwrap().port()
    };

    let error = engine()
        .execute(
            HttpRequest::get(format!("http://127.0.0.1:{port}/")),
            CancellationHandle::new(),
        )
        .await
        .unwrap_err();

    assert_eq!(error.kind, RequestErrorKind::ConnectionRefused);
    assert!(error.message.contains("127.0.0.1"));
}

#[tokio::test]
async fn unresolved_variables_fail_before_anything_is_sent() {
    let server = TestServer::start().await.unwrap();
    let request = HttpRequest::get(format!("{}/{{{{missing}}}}", server.base_url()));

    let error = engine()
        .execute_with_variables(request, &VariableSet::new(), CancellationHandle::new())
        .await
        .unwrap_err();

    assert_eq!(error.kind, RequestErrorKind::UnresolvedVariable);
    assert_eq!(server.connection_count(), 0);
}

#[tokio::test]
async fn variables_are_substituted_before_sending() {
    let server = TestServer::start().await.unwrap();
    let variables: VariableSet = [("baseUrl", server.base_url()), ("token", "abc".to_string())]
        .into_iter()
        .collect();

    let mut request = HttpRequest::get("{{baseUrl}}/auth");
    request.auth = AuthConfig::Bearer {
        token: "{{token}}".to_string(),
    };

    let response = engine()
        .execute_with_variables(request, &variables, CancellationHandle::new())
        .await
        .unwrap();

    assert_eq!(body_json(&response.body)["authorization"], "Bearer abc");
}

#[tokio::test]
async fn head_requests_have_no_body() {
    let server = TestServer::start().await.unwrap();
    let mut request = HttpRequest::get(server.url("/json"));
    request.method = HttpMethod::Head;

    let response = engine()
        .execute(request, CancellationHandle::new())
        .await
        .unwrap();

    assert_eq!(response.status, 200);
    assert!(response.body.is_empty());
}

#[tokio::test]
async fn timing_is_recorded() {
    let server = TestServer::start().await.unwrap();
    let response = engine()
        .execute(
            HttpRequest::get(server.url("/delay/60")),
            CancellationHandle::new(),
        )
        .await
        .unwrap();

    assert!(response.duration_ms >= 60, "got {}", response.duration_ms);
}
