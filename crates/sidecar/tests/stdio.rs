//! Drives the real binary over stdin/stdout, the way the extension host does.

use std::io::{BufRead, BufReader, Write};
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};

use api_client_codegen::CodeTarget;
use api_client_core::{HttpRequest, RequestErrorKind};
use api_client_protocol::{
    ClientEnvelope, ClientMessage, PROTOCOL_VERSION, ServerEnvelope, ServerMessage,
};
use api_client_testserver::TestServer;

struct Sidecar {
    child: Child,
    stdin: ChildStdin,
    stdout: BufReader<ChildStdout>,
    _state_dir: tempfile::TempDir,
}

impl Sidecar {
    fn start() -> Self {
        let state_dir = tempfile::tempdir().expect("state dir");
        let mut child = Command::new(env!("CARGO_BIN_EXE_api-client-sidecar"))
            .arg("--state-dir")
            .arg(state_dir.path())
            // Keeps the suite out of the user's keyring, and the same
            // whether or not the machine running it has one.
            .env("API_CLIENT_KEYRING", "off")
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()
            .expect("sidecar should start");

        let stdin = child.stdin.take().expect("stdin");
        let stdout = BufReader::new(child.stdout.take().expect("stdout"));

        Self {
            child,
            stdin,
            stdout,
            _state_dir: state_dir,
        }
    }

    fn send(&mut self, id: Option<&str>, msg: ClientMessage) {
        let envelope = ClientEnvelope::new(id.map(str::to_string), msg);
        let line = api_client_protocol::encode_line(&envelope).unwrap();
        self.stdin.write_all(line.as_bytes()).expect("write");
        self.stdin.flush().expect("flush");
    }

    /// Sends a raw line, for the malformed-input cases.
    fn send_raw(&mut self, line: &str) {
        writeln!(self.stdin, "{line}").expect("write");
        self.stdin.flush().expect("flush");
    }

    fn recv(&mut self) -> ServerEnvelope {
        let mut line = String::new();
        let read = self.stdout.read_line(&mut line).expect("read");
        assert!(read > 0, "sidecar closed stdout unexpectedly");
        serde_json::from_str(line.trim()).unwrap_or_else(|err| panic!("bad line {line:?}: {err}"))
    }

    fn handshake(&mut self) {
        self.send(
            Some("hs"),
            ClientMessage::Handshake {
                client_name: "test".into(),
                client_version: "0.0.0".into(),
            },
        );
        let envelope = self.recv();
        assert_eq!(envelope.id.as_deref(), Some("hs"));
        assert!(matches!(
            envelope.msg,
            ServerMessage::HandshakeOk { protocol_version, .. } if protocol_version == PROTOCOL_VERSION
        ));
    }
}

impl Drop for Sidecar {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

#[test]
fn handshakes_then_sends_a_request() {
    let runtime = tokio::runtime::Runtime::new().unwrap();
    let server = runtime.block_on(TestServer::start()).unwrap();

    let mut sidecar = Sidecar::start();
    sidecar.handshake();

    sidecar.send(
        Some("req-1"),
        ClientMessage::SendRequest {
            request: HttpRequest::get(server.url("/json")),
            variables: Default::default(),
        },
    );

    let envelope = sidecar.recv();
    assert_eq!(envelope.id.as_deref(), Some("req-1"));
    match envelope.msg {
        ServerMessage::RequestComplete {
            response,
            history_entry,
        } => {
            assert_eq!(response.status, 200);
            let entry = history_entry.expect("a completed send is recorded");
            assert_eq!(entry.status, Some(200));
            assert_eq!(
                serde_json::from_slice::<serde_json::Value>(&response.body).unwrap()["ok"],
                true
            );
        }
        other => panic!("unexpected: {other:?}"),
    }
}

#[test]
fn refuses_to_work_before_a_handshake() {
    let mut sidecar = Sidecar::start();

    sidecar.send(Some("req-1"), ClientMessage::LoadState);

    match sidecar.recv().msg {
        ServerMessage::ProtocolError { message } => assert!(message.contains("handshake")),
        other => panic!("unexpected: {other:?}"),
    }
}

#[test]
fn a_protocol_mismatch_fails_loudly() {
    let mut sidecar = Sidecar::start();

    let line = format!(
        r#"{{"v":{},"id":"hs","msg":{{"type":"handshake","clientName":"test","clientVersion":"0"}}}}"#,
        PROTOCOL_VERSION + 1
    );
    sidecar.send_raw(&line);

    match sidecar.recv().msg {
        ServerMessage::HandshakeError {
            expected_protocol_version,
            received_protocol_version,
            ..
        } => {
            assert_eq!(expected_protocol_version, PROTOCOL_VERSION);
            assert_eq!(received_protocol_version, PROTOCOL_VERSION + 1);
        }
        other => panic!("unexpected: {other:?}"),
    }
}

#[test]
fn a_malformed_line_does_not_kill_the_process() {
    let runtime = tokio::runtime::Runtime::new().unwrap();
    let server = runtime.block_on(TestServer::start()).unwrap();

    let mut sidecar = Sidecar::start();
    sidecar.handshake();

    sidecar.send_raw("{ not json");
    match sidecar.recv().msg {
        ServerMessage::ProtocolError { message } => assert!(message.contains("parse")),
        other => panic!("unexpected: {other:?}"),
    }

    // Still usable afterwards.
    sidecar.send(
        Some("req-1"),
        ClientMessage::SendRequest {
            request: HttpRequest::get(server.url("/json")),
            variables: Default::default(),
        },
    );
    assert!(matches!(
        sidecar.recv().msg,
        ServerMessage::RequestComplete { .. }
    ));
}

#[test]
fn errors_come_back_as_request_errors() {
    let mut sidecar = Sidecar::start();
    sidecar.handshake();

    sidecar.send(
        Some("req-1"),
        ClientMessage::SendRequest {
            request: HttpRequest::get("{{missing}}"),
            variables: Default::default(),
        },
    );

    match sidecar.recv().msg {
        ServerMessage::RequestError {
            error,
            history_entry,
        } => {
            assert_eq!(error.kind, RequestErrorKind::UnresolvedVariable);
            assert!(history_entry.is_some(), "a failure is worth recording");
        }
        other => panic!("unexpected: {other:?}"),
    }
}

#[test]
fn a_request_can_be_cancelled_by_id() {
    let runtime = tokio::runtime::Runtime::new().unwrap();
    let server = runtime.block_on(TestServer::start()).unwrap();

    let mut sidecar = Sidecar::start();
    sidecar.handshake();

    let request = HttpRequest::get(server.url("/never"));
    let request_id = request.id.clone();

    sidecar.send(
        Some("req-1"),
        ClientMessage::SendRequest {
            request,
            variables: Default::default(),
        },
    );

    std::thread::sleep(std::time::Duration::from_millis(150));
    sidecar.send(
        Some("cancel-1"),
        ClientMessage::CancelRequest {
            request_id: request_id.clone(),
        },
    );

    // The ack and the error can arrive in either order.
    let mut saw_ack = false;
    let mut saw_cancelled_error = false;
    for _ in 0..2 {
        match sidecar.recv().msg {
            ServerMessage::RequestCancelled { was_in_flight } => {
                assert!(was_in_flight);
                saw_ack = true;
            }
            ServerMessage::RequestError {
                error,
                history_entry,
            } => {
                assert_eq!(error.kind, RequestErrorKind::Cancelled);
                assert!(history_entry.is_none(), "a cancelled send is not recorded");
                saw_cancelled_error = true;
            }
            other => panic!("unexpected: {other:?}"),
        }
    }

    assert!(saw_ack && saw_cancelled_error);
}

#[test]
fn state_can_be_loaded_saved_and_cleared() {
    let mut sidecar = Sidecar::start();
    sidecar.handshake();

    sidecar.send(Some("load"), ClientMessage::LoadState);
    let mut state = match sidecar.recv().msg {
        ServerMessage::StateLoaded {
            state,
            storage_path,
            recovery,
            version,
        } => {
            assert!(recovery.is_none());
            assert!(storage_path.ends_with("state.json"));
            // Whatever this build is, the UI has something to show in About.
            assert!(!version.is_empty());
            assert_eq!(state.tabs.len(), 1);
            state
        }
        other => panic!("unexpected: {other:?}"),
    };

    state.tabs.push(api_client_storage::ScratchTab::blank());
    sidecar.send(Some("save"), ClientMessage::SaveState { state });
    match sidecar.recv().msg {
        ServerMessage::StateSaved { state } => assert_eq!(state.tabs.len(), 2),
        other => panic!("unexpected: {other:?}"),
    }

    sidecar.send(
        Some("save-req"),
        ClientMessage::SaveRequest {
            saved_request_id: None,
            name: "Users".into(),
            request: HttpRequest::get("https://example.com/users"),
        },
    );
    let saved_id = match sidecar.recv().msg {
        ServerMessage::StateSaved { state } => {
            assert_eq!(state.saved_requests.len(), 1);
            state.saved_requests[0].id.clone()
        }
        other => panic!("unexpected: {other:?}"),
    };

    sidecar.send(
        Some("delete"),
        ClientMessage::DeleteSavedRequest {
            saved_request_id: saved_id,
        },
    );
    match sidecar.recv().msg {
        ServerMessage::StateSaved { state } => assert!(state.saved_requests.is_empty()),
        other => panic!("unexpected: {other:?}"),
    }

    sidecar.send(Some("clear"), ClientMessage::ClearHistory);
    match sidecar.recv().msg {
        ServerMessage::StateSaved { state } => assert!(state.history.is_empty()),
        other => panic!("unexpected: {other:?}"),
    }
}

#[test]
fn concurrent_requests_do_not_interleave_their_lines() {
    let runtime = tokio::runtime::Runtime::new().unwrap();
    let server = runtime.block_on(TestServer::start()).unwrap();

    let mut sidecar = Sidecar::start();
    sidecar.handshake();

    for index in 0..5 {
        sidecar.send(
            Some(&format!("req-{index}")),
            ClientMessage::SendRequest {
                request: HttpRequest::get(server.url(&format!("/delay/{}", 50 - index * 10))),
                variables: Default::default(),
            },
        );
    }

    let mut seen = std::collections::BTreeSet::new();
    for _ in 0..5 {
        let envelope = sidecar.recv();
        assert!(matches!(
            envelope.msg,
            ServerMessage::RequestComplete { .. }
        ));
        seen.insert(envelope.id.expect("correlation id"));
    }

    assert_eq!(seen.len(), 5);
}

#[test]
fn shutdown_ends_the_process_cleanly() {
    let mut sidecar = Sidecar::start();
    sidecar.handshake();
    sidecar.send(None, ClientMessage::Shutdown);

    let status = sidecar.child.wait().expect("should exit");
    assert!(status.success());
}

#[test]
fn reports_its_protocol_version_on_the_command_line() {
    let output = Command::new(env!("CARGO_BIN_EXE_api-client-sidecar"))
        .arg("--protocol-version")
        .output()
        .expect("should run");

    assert!(output.status.success());
    assert_eq!(
        String::from_utf8_lossy(&output.stdout).trim(),
        PROTOCOL_VERSION.to_string()
    );
}

/// Writing a request out as code is the sidecar's job too, so the extension and
/// the desktop app cannot answer the question differently.
#[test]
fn writes_a_request_out_as_code() {
    let mut sidecar = Sidecar::start();
    sidecar.handshake();

    sidecar.send(
        Some("code-1"),
        ClientMessage::GenerateCode {
            request: HttpRequest::get("https://example.com/things"),
            target: CodeTarget::Curl,
            variables: Default::default(),
        },
    );

    let envelope = sidecar.recv();
    assert_eq!(envelope.id.as_deref(), Some("code-1"));
    match envelope.msg {
        ServerMessage::CodeGenerated { code, error } => {
            assert!(error.is_none(), "{error:?}");
            let code = code.expect("a URL that parses has code");
            assert!(code.contains("curl"), "{code}");
            assert!(code.contains("https://example.com/things"), "{code}");
        }
        other => panic!("unexpected reply: {other:?}"),
    }
}

/// A variable with no value is the same answer it would be on Send: there is no
/// honest snippet to write, and the reason comes back in the same message.
#[test]
fn says_why_there_is_no_code() {
    let mut sidecar = Sidecar::start();
    sidecar.handshake();

    sidecar.send(
        Some("code-2"),
        ClientMessage::GenerateCode {
            request: HttpRequest::get("{{host}}/things"),
            target: CodeTarget::Curl,
            variables: Default::default(),
        },
    );

    match sidecar.recv().msg {
        ServerMessage::CodeGenerated { code, error } => {
            assert!(code.is_none());
            let error = error.expect("an unresolved variable is reported");
            assert!(error.message.contains("host"), "{}", error.message);
        }
        other => panic!("unexpected reply: {other:?}"),
    }
}

#[test]
fn exports_and_imports_saved_requests_over_the_pipe() {
    let mut sidecar = Sidecar::start();
    sidecar.handshake();

    let mut request = HttpRequest::get("https://example.com/users");
    request.auth = api_client_core::AuthConfig::Bearer {
        token: "tok-1".into(),
    };
    sidecar.send(
        Some("save"),
        ClientMessage::SaveRequest {
            saved_request_id: None,
            name: "Users".into(),
            request,
        },
    );
    let saved_id = match sidecar.recv().msg {
        ServerMessage::StateSaved { state } => state.saved_requests[0].id.clone(),
        other => panic!("unexpected: {other:?}"),
    };

    sidecar.send(
        Some("export"),
        ClientMessage::ExportSavedRequests {
            saved_request_ids: vec![saved_id],
            format: api_client_codegen::ExportFormat::Http,
            include_secrets: false,
        },
    );
    let contents = match sidecar.recv().msg {
        ServerMessage::SavedRequestsExported { contents } => contents,
        other => panic!("unexpected: {other:?}"),
    };
    assert!(
        contents.contains("Authorization: Bearer {{token}}"),
        "{contents}"
    );
    assert!(!contents.contains("tok-1"));

    sidecar.send(
        Some("import"),
        ClientMessage::ImportSavedRequests { contents },
    );
    match sidecar.recv().msg {
        ServerMessage::SavedRequestsImported {
            state,
            imported,
            undefined_variables,
            plain_secrets,
            ..
        } => {
            assert_eq!(imported, 1);
            assert_eq!(state.saved_requests.len(), 2);
            assert_eq!(undefined_variables, ["token"]);
            assert!(!plain_secrets);
        }
        other => panic!("unexpected: {other:?}"),
    }

    sidecar.send(
        Some("bad"),
        ClientMessage::ImportSavedRequests {
            contents: "{\"tabs\": []}".into(),
        },
    );
    match sidecar.recv().msg {
        ServerMessage::ImportRejected { message } => {
            assert!(
                message.contains("not a file of saved requests"),
                "{message}"
            );
        }
        other => panic!("unexpected: {other:?}"),
    }
}
