//! TLS trust and client-certificate tests.
//!
//! Every certificate is generated for the run, so a request only succeeds
//! because of the settings under test — never because the machine happened to
//! already trust something.

use std::io::Write;

use api_client_core::{ClientIdentitySettings, HttpRequest, RequestErrorKind, TlsSettings};
use api_client_http_engine::{CancellationHandle, EngineConfig, HttpEngine};
use api_client_testserver::{ClientAuth, TestCa, TestServer};

fn engine(tls: TlsSettings) -> HttpEngine {
    HttpEngine::new(EngineConfig {
        tls,
        ..EngineConfig::default()
    })
    .expect("engine")
}

/// Writes bytes to a uniquely named temp file that lives as long as the guard.
fn temp_file(name: &str, bytes: &[u8]) -> tempfile::TempPath {
    let mut file = tempfile::Builder::new()
        .prefix(name)
        .tempfile()
        .expect("temp file");
    file.write_all(bytes).expect("write");
    file.flush().expect("flush");
    file.into_temp_path()
}

fn path_of(path: &tempfile::TempPath) -> String {
    path.to_string_lossy().into_owned()
}

#[tokio::test]
async fn an_untrusted_certificate_is_rejected() {
    let ca = TestCa::new();
    let server = TestServer::start_tls(&ca, ClientAuth::None).await.unwrap();

    // Default settings: the OS trust store, which has never heard of this CA.
    let error = engine(TlsSettings::default())
        .execute(
            HttpRequest::get(server.url("/json")),
            CancellationHandle::new(),
        )
        .await
        .unwrap_err();

    assert_eq!(error.kind, RequestErrorKind::Tls);
}

#[tokio::test]
async fn adding_a_ca_makes_its_server_reachable() {
    let ca = TestCa::new();
    let server = TestServer::start_tls(&ca, ClientAuth::None).await.unwrap();
    let ca_file = temp_file("ca", ca.pem().as_bytes());

    let response = engine(TlsSettings {
        extra_ca_files: vec![path_of(&ca_file)],
        ..TlsSettings::default()
    })
    .execute(
        HttpRequest::get(server.url("/json")),
        CancellationHandle::new(),
    )
    .await
    .expect("the added CA should be trusted");

    assert_eq!(response.status, 200);
}

#[tokio::test]
async fn an_added_ca_is_merged_with_the_system_store_not_swapped_for_it() {
    let ca = TestCa::new();
    let ca_file = temp_file("ca", ca.pem().as_bytes());

    // Building succeeds with system roots on and an extra CA, which is the
    // combination that must not be turned into "trust only this one".
    let engine = engine(TlsSettings {
        use_system_roots: true,
        extra_ca_files: vec![path_of(&ca_file)],
        ..TlsSettings::default()
    });

    let server = TestServer::start_tls(&ca, ClientAuth::None).await.unwrap();
    let response = engine
        .execute(
            HttpRequest::get(server.url("/json")),
            CancellationHandle::new(),
        )
        .await
        .unwrap();
    assert_eq!(response.status, 200);

    // A second, unrelated CA is still not trusted, so the merge did not simply
    // disable verification.
    let other = TestCa::new();
    let other_server = TestServer::start_tls(&other, ClientAuth::None)
        .await
        .unwrap();
    let error = engine
        .execute(
            HttpRequest::get(other_server.url("/json")),
            CancellationHandle::new(),
        )
        .await
        .unwrap_err();
    assert_eq!(error.kind, RequestErrorKind::Tls);
}

#[tokio::test]
async fn system_roots_can_be_turned_off_to_trust_only_one_ca() {
    let ca = TestCa::new();
    let server = TestServer::start_tls(&ca, ClientAuth::None).await.unwrap();
    let ca_file = temp_file("ca", ca.pem().as_bytes());

    let response = engine(TlsSettings {
        use_system_roots: false,
        extra_ca_files: vec![path_of(&ca_file)],
        ..TlsSettings::default()
    })
    .execute(
        HttpRequest::get(server.url("/json")),
        CancellationHandle::new(),
    )
    .await
    .unwrap();

    assert_eq!(response.status, 200);
}

#[tokio::test]
async fn accepting_invalid_certificates_bypasses_verification() {
    let ca = TestCa::new();
    let server = TestServer::start_tls(&ca, ClientAuth::None).await.unwrap();

    let response = engine(TlsSettings {
        accept_invalid_certs: true,
        ..TlsSettings::default()
    })
    .execute(
        HttpRequest::get(server.url("/json")),
        CancellationHandle::new(),
    )
    .await
    .unwrap();

    assert_eq!(response.status, 200);
}

#[tokio::test]
async fn a_server_wanting_a_client_certificate_refuses_a_client_without_one() {
    let ca = TestCa::new();
    let server = TestServer::start_tls(&ca, ClientAuth::Required)
        .await
        .unwrap();
    let ca_file = temp_file("ca", ca.pem().as_bytes());

    let error = engine(TlsSettings {
        extra_ca_files: vec![path_of(&ca_file)],
        ..TlsSettings::default()
    })
    .execute(
        HttpRequest::get(server.url("/json")),
        CancellationHandle::new(),
    )
    .await
    .unwrap_err();

    // The server closes the connection during the handshake.
    assert!(
        matches!(
            error.kind,
            RequestErrorKind::Tls | RequestErrorKind::ConnectionFailed | RequestErrorKind::BodyRead
        ),
        "unexpected kind: {:?} ({})",
        error.kind,
        error.message
    );
}

#[tokio::test]
async fn a_pem_client_certificate_satisfies_mutual_tls() {
    let ca = TestCa::new();
    let server = TestServer::start_tls(&ca, ClientAuth::Required)
        .await
        .unwrap();

    let ca_file = temp_file("ca", ca.pem().as_bytes());
    let client = ca.issue_client("api-client test");
    let identity = temp_file("client", client.identity_pem().as_bytes());

    let response = engine(TlsSettings {
        extra_ca_files: vec![path_of(&ca_file)],
        client_identity: Some(ClientIdentitySettings {
            path: path_of(&identity),
            password: None,
        }),
        ..TlsSettings::default()
    })
    .execute(
        HttpRequest::get(server.url("/json")),
        CancellationHandle::new(),
    )
    .await
    .expect("the client certificate should be accepted");

    assert_eq!(response.status, 200);
}

#[tokio::test]
async fn a_pkcs12_client_certificate_satisfies_mutual_tls() {
    let ca = TestCa::new();
    let server = TestServer::start_tls(&ca, ClientAuth::Required)
        .await
        .unwrap();

    let ca_file = temp_file("ca", ca.pem().as_bytes());
    let client = ca.issue_client("api-client test");
    // The shape Windows exports: one encrypted bundle, no separate key file.
    let bundle = temp_file("client", &client.to_pkcs12("hunter2", &ca));

    let response = engine(TlsSettings {
        extra_ca_files: vec![path_of(&ca_file)],
        client_identity: Some(ClientIdentitySettings {
            path: path_of(&bundle),
            password: Some("hunter2".into()),
        }),
        ..TlsSettings::default()
    })
    .execute(
        HttpRequest::get(server.url("/json")),
        CancellationHandle::new(),
    )
    .await
    .expect("the pkcs12 bundle should be unpacked and accepted");

    assert_eq!(response.status, 200);
}

#[tokio::test]
async fn a_wrong_pkcs12_password_says_so() {
    let ca = TestCa::new();
    let client = ca.issue_client("api-client test");
    let bundle = temp_file("client", &client.to_pkcs12("hunter2", &ca));

    let error = HttpEngine::new(EngineConfig {
        tls: TlsSettings {
            client_identity: Some(ClientIdentitySettings {
                path: path_of(&bundle),
                password: Some("wrong".into()),
            }),
            ..TlsSettings::default()
        },
        ..EngineConfig::default()
    })
    .unwrap_err();

    assert_eq!(error.kind, RequestErrorKind::Tls);
    assert!(
        error.message.contains("password"),
        "unhelpful message: {}",
        error.message
    );
}

#[tokio::test]
async fn a_missing_client_certificate_file_names_the_path() {
    let error = HttpEngine::new(EngineConfig {
        tls: TlsSettings {
            client_identity: Some(ClientIdentitySettings {
                path: "/definitely/not/here.p12".into(),
                password: Some("x".into()),
            }),
            ..TlsSettings::default()
        },
        ..EngineConfig::default()
    })
    .unwrap_err();

    assert_eq!(error.kind, RequestErrorKind::Io);
    assert!(error.message.contains("/definitely/not/here.p12"));
}

#[tokio::test]
async fn a_certificate_file_that_is_not_a_certificate_is_reported_clearly() {
    let junk = temp_file("junk", b"this is not a certificate");

    let error = HttpEngine::new(EngineConfig {
        tls: TlsSettings {
            extra_ca_files: vec![path_of(&junk)],
            ..TlsSettings::default()
        },
        ..EngineConfig::default()
    })
    .unwrap_err();

    assert_eq!(error.kind, RequestErrorKind::Tls);
    assert!(
        error.message.to_lowercase().contains("certificate"),
        "unhelpful message: {}",
        error.message
    );
}

#[tokio::test]
async fn plain_http_still_works_with_tls_settings_present() {
    let ca = TestCa::new();
    let ca_file = temp_file("ca", ca.pem().as_bytes());
    let server = TestServer::start().await.unwrap();

    let response = engine(TlsSettings {
        extra_ca_files: vec![path_of(&ca_file)],
        ..TlsSettings::default()
    })
    .execute(
        HttpRequest::get(server.url("/json")),
        CancellationHandle::new(),
    )
    .await
    .unwrap();

    assert_eq!(response.status, 200);
}
