//! The IPC contract. These tests pin the literal JSON, because the TypeScript
//! side reads exactly these strings.

use api_client_codegen::CodeTarget;
use api_client_core::{HttpRequest, HttpResponse, RequestError, RequestErrorKind};
use api_client_protocol::{
    ClientEnvelope, ClientMessage, PROTOCOL_VERSION, ServerEnvelope, ServerMessage, decode_line,
    encode_line, version_mismatch,
};

#[test]
fn every_message_carries_a_version_a_type_and_an_id() {
    let envelope = ClientEnvelope::new(
        Some("req-1".into()),
        ClientMessage::SendRequest {
            request: HttpRequest::get("https://example.com"),
            variables: Default::default(),
        },
    );

    let value: serde_json::Value = serde_json::from_str(&encode_line(&envelope).unwrap()).unwrap();

    assert_eq!(value["v"], PROTOCOL_VERSION);
    assert_eq!(value["id"], "req-1");
    assert_eq!(value["msg"]["type"], "sendRequest");
}

#[test]
fn encoded_messages_are_exactly_one_line() {
    let line = encode_line(&ClientEnvelope::new(None, ClientMessage::Shutdown)).unwrap();

    assert!(line.ends_with('\n'));
    assert_eq!(line.matches('\n').count(), 1);
}

#[test]
fn client_messages_round_trip() {
    let messages = vec![
        ClientMessage::Handshake {
            client_name: "vscode".into(),
            client_version: "0.1.0".into(),
        },
        ClientMessage::SendRequest {
            request: HttpRequest::get("https://example.com"),
            variables: [("token".to_string(), "abc".to_string())]
                .into_iter()
                .collect(),
        },
        ClientMessage::CancelRequest {
            request_id: "req-1".into(),
        },
        ClientMessage::LoadState,
        ClientMessage::ClearHistory,
        ClientMessage::Shutdown,
    ];

    for message in messages {
        let envelope = ClientEnvelope::new(Some("id".into()), message);
        let line = encode_line(&envelope).unwrap();
        let decoded: ClientEnvelope = decode_line(&line).unwrap();
        assert_eq!(decoded, envelope);
    }
}

#[test]
fn response_bodies_travel_as_base64() {
    let response = HttpResponse {
        status: 200,
        status_text: "OK".into(),
        headers: vec![],
        body: b"hello".to_vec(),
        mime_type: Some("text/plain".into()),
        duration_ms: 3,
        size_bytes: 5,
        truncated: false,
        final_url: "https://example.com".into(),
        warnings: vec![],
        tls: None,
    };

    let envelope = ServerEnvelope::new(
        Some("req-1".into()),
        ServerMessage::RequestComplete {
            response: response.clone(),
            history_entry: None,
        },
    );
    let line = encode_line(&envelope).unwrap();

    let value: serde_json::Value = serde_json::from_str(&line).unwrap();
    assert_eq!(value["msg"]["response"]["body"], "aGVsbG8=");

    let decoded: ServerEnvelope = decode_line(&line).unwrap();
    match decoded.msg {
        ServerMessage::RequestComplete {
            response: decoded, ..
        } => assert_eq!(decoded, response),
        other => panic!("unexpected message: {other:?}"),
    }
}

#[test]
fn binary_bodies_survive_the_round_trip() {
    let body: Vec<u8> = (0u8..=255).collect();
    let response = HttpResponse {
        status: 200,
        status_text: "OK".into(),
        headers: vec![],
        body: body.clone(),
        mime_type: None,
        duration_ms: 1,
        size_bytes: body.len() as u64,
        truncated: false,
        final_url: "https://example.com".into(),
        warnings: vec![],
        tls: None,
    };

    let line = encode_line(&response).unwrap();
    let decoded: HttpResponse = decode_line(&line).unwrap();
    assert_eq!(decoded.body, body);
}

#[test]
fn errors_keep_their_kind_across_the_wire() {
    let error = RequestError::new(RequestErrorKind::ConnectionRefused, "nope")
        .with_detail("tcp connect error");

    let envelope = ServerEnvelope::new(
        Some("req-1".into()),
        ServerMessage::RequestError {
            error: error.clone(),
            history_entry: None,
        },
    );
    let line = encode_line(&envelope).unwrap();

    let value: serde_json::Value = serde_json::from_str(&line).unwrap();
    assert_eq!(value["msg"]["type"], "requestError");
    assert_eq!(value["msg"]["error"]["kind"], "connectionRefused");

    let decoded: ServerEnvelope = decode_line(&line).unwrap();
    match decoded.msg {
        ServerMessage::RequestError { error: decoded, .. } => assert_eq!(decoded, error),
        other => panic!("unexpected message: {other:?}"),
    }
}

#[test]
fn a_matching_version_is_accepted() {
    assert!(version_mismatch(PROTOCOL_VERSION).is_none());
}

#[test]
fn a_mismatched_version_produces_an_actionable_message() {
    let Some(ServerMessage::HandshakeError {
        message,
        expected_protocol_version,
        received_protocol_version,
    }) = version_mismatch(PROTOCOL_VERSION + 1)
    else {
        panic!("should be a handshake error");
    };

    assert_eq!(expected_protocol_version, PROTOCOL_VERSION);
    assert_eq!(received_protocol_version, PROTOCOL_VERSION + 1);
    assert!(message.contains("Reinstall"));
}

#[test]
fn a_message_with_an_unknown_type_fails_to_decode() {
    let line = r#"{"v":1,"id":null,"msg":{"type":"somethingElse"}}"#;
    assert!(decode_line::<ClientEnvelope>(line).is_err());
}

#[test]
fn surrounding_whitespace_is_tolerated() {
    let line = format!(
        "  {}  ",
        serde_json::to_string(&ClientEnvelope::new(None, ClientMessage::LoadState)).unwrap()
    );
    let decoded: ClientEnvelope = decode_line(&line).unwrap();
    assert_eq!(decoded.msg, ClientMessage::LoadState);
}

/// Every target is a plain lowercase name, because the TypeScript side sends
/// exactly these strings — and the first four are the ones they have always
/// been, so adding a language does not change what an older pair understood.
#[test]
fn a_code_target_is_a_plain_lowercase_name() {
    let names: Vec<String> = CodeTarget::ALL
        .into_iter()
        .map(|target| {
            serde_json::to_value(target)
                .unwrap()
                .as_str()
                .unwrap()
                .to_string()
        })
        .collect();

    assert_eq!(&names[..4], ["curl", "powershell", "python", "csharp"]);
    assert!(names.contains(&"rust-blocking".to_string()), "{names:?}");
    assert!(names.contains(&"java-okhttp".to_string()), "{names:?}");

    for name in &names {
        assert!(
            name.chars().all(|c| c.is_ascii_lowercase() || c == '-'),
            "`{name}` is not a plain name"
        );
    }

    let mut sorted = names.clone();
    sorted.sort();
    sorted.dedup();
    assert_eq!(sorted.len(), names.len(), "a name is used twice: {names:?}");
}

#[test]
fn a_generate_names_its_target_and_carries_its_variables() {
    let envelope = ClientEnvelope::new(
        Some("code-1".into()),
        ClientMessage::GenerateCode {
            request: HttpRequest::get("https://example.com"),
            target: CodeTarget::PowerShell,
            variables: [("host".to_string(), "example.com".to_string())]
                .into_iter()
                .collect(),
        },
    );

    let value: serde_json::Value = serde_json::from_str(&encode_line(&envelope).unwrap()).unwrap();

    assert_eq!(value["msg"]["type"], "generateCode");
    assert_eq!(value["msg"]["target"], "powershell");
    assert_eq!(value["msg"]["variables"]["host"], "example.com");
}

/// One message either way: the code, or the reason there is not any.
#[test]
fn a_generate_answers_with_the_code_or_with_the_reason() {
    let generated = ServerEnvelope::new(
        Some("code-1".into()),
        ServerMessage::CodeGenerated {
            code: Some("curl --url 'https://example.com/'".into()),
            error: None,
        },
    );
    let value: serde_json::Value = serde_json::from_str(&encode_line(&generated).unwrap()).unwrap();
    assert_eq!(value["msg"]["type"], "codeGenerated");
    assert_eq!(value["msg"]["error"], serde_json::Value::Null);

    let failed = ServerEnvelope::new(
        Some("code-2".into()),
        ServerMessage::CodeGenerated {
            code: None,
            error: Some(RequestError::new(
                RequestErrorKind::InvalidUrl,
                "Enter a URL.",
            )),
        },
    );
    let line = encode_line(&failed).unwrap();
    let decoded: ServerEnvelope = decode_line(&line).unwrap();
    assert_eq!(decoded, failed);
}
