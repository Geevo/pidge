use api_client_core::{HttpRequest, KeyValueEntry, RequestErrorKind};
use api_client_session::{CodeTarget, Session};
use api_client_storage::{AppState, Store};
use api_client_testserver::TestServer;
use api_client_variables::Environment;

fn session() -> (tempfile::TempDir, Session) {
    let dir = tempfile::tempdir().unwrap();
    let session = Session::start(Store::in_dir(dir.path())).unwrap();
    (dir, session)
}

#[tokio::test]
async fn a_successful_send_lands_in_history() {
    let server = TestServer::start().await.unwrap();
    let (_dir, session) = session();

    let response = session
        .send(HttpRequest::get(server.url("/json")))
        .await
        .unwrap();
    assert_eq!(response.status, 200);

    let state = session.snapshot();
    assert_eq!(state.history.len(), 1);
    assert_eq!(state.history[0].status, Some(200));
    assert!(state.history[0].error.is_none());
    assert!(state.history[0].duration_ms.is_some());
}

#[tokio::test]
async fn a_failed_send_lands_in_history_with_its_message() {
    let (_dir, session) = session();

    let error = session
        .send(HttpRequest::get("not a url"))
        .await
        .unwrap_err();
    assert_eq!(error.kind, RequestErrorKind::InvalidUrl);

    let state = session.snapshot();
    assert_eq!(state.history.len(), 1);
    assert!(state.history[0].error.is_some());
    assert!(state.history[0].status.is_none());
}

#[tokio::test]
async fn a_cancelled_send_is_not_recorded() {
    let server = TestServer::start().await.unwrap();
    let (_dir, session) = session();

    let request = HttpRequest::get(server.url("/never"));
    let id = request.id.clone();

    let canceller = session.clone();
    tokio::spawn(async move {
        tokio::time::sleep(std::time::Duration::from_millis(60)).await;
        assert!(canceller.cancel(&id));
    });

    let error = session.send(request).await.unwrap_err();
    assert_eq!(error.kind, RequestErrorKind::Cancelled);
    assert!(session.snapshot().history.is_empty());
    assert_eq!(session.in_flight(), 0);
}

#[tokio::test]
async fn cancelling_something_that_already_finished_is_harmless() {
    let (_dir, session) = session();
    assert!(!session.cancel("nothing-in-flight"));
}

#[tokio::test]
async fn history_is_persisted_across_restarts() {
    let server = TestServer::start().await.unwrap();
    let dir = tempfile::tempdir().unwrap();

    {
        let session = Session::start(Store::in_dir(dir.path())).unwrap();
        session
            .send(HttpRequest::get(server.url("/json")))
            .await
            .unwrap();
    }

    let reopened = Session::start(Store::in_dir(dir.path())).unwrap();
    assert_eq!(reopened.snapshot().history.len(), 1);
}

#[tokio::test]
async fn variables_come_from_the_active_environment() {
    let server = TestServer::start().await.unwrap();
    let (_dir, session) = session();

    let mut state = session.snapshot();
    state.environments = vec![Environment {
        id: "env-1".into(),
        name: "Local".into(),
        variables: vec![KeyValueEntry::new("baseUrl", server.base_url())],
    }];
    state.active_environment_id = Some("env-1".into());
    session.replace_state(state).unwrap();

    let response = session
        .send(HttpRequest::get("{{baseUrl}}/json"))
        .await
        .unwrap();
    assert_eq!(response.status, 200);
}

#[tokio::test]
async fn explicit_overrides_win_over_the_environment() {
    let server = TestServer::start().await.unwrap();
    let (_dir, session) = session();

    let mut state = session.snapshot();
    state.environments = vec![Environment {
        id: "env-1".into(),
        name: "Local".into(),
        variables: vec![KeyValueEntry::new("baseUrl", "http://127.0.0.1:1")],
    }];
    state.active_environment_id = Some("env-1".into());
    session.replace_state(state).unwrap();

    let outcome = session
        .send_with_overrides(
            HttpRequest::get("{{baseUrl}}/json"),
            [("baseUrl".to_string(), server.base_url())]
                .into_iter()
                .collect(),
        )
        .await;

    assert_eq!(outcome.response.expect("a response").status, 200);
}

#[test]
fn saving_a_request_persists_immediately() {
    let dir = tempfile::tempdir().unwrap();
    let session = Session::start(Store::in_dir(dir.path())).unwrap();

    let saved = session
        .save_request(
            None,
            "List users".into(),
            HttpRequest::get("https://example.com/users"),
        )
        .unwrap();

    let reopened = Session::start(Store::in_dir(dir.path())).unwrap();
    let state = reopened.snapshot();

    assert_eq!(state.saved_requests.len(), 1);
    assert_eq!(state.saved_requests[0].id, saved.id);
    assert_eq!(state.saved_requests[0].name, "List users");
}

#[test]
fn saving_with_an_existing_id_updates_in_place() {
    let (_dir, session) = session();

    let saved = session
        .save_request(None, "First".into(), HttpRequest::get("https://a.example"))
        .unwrap();
    session
        .save_request(
            Some(saved.id.clone()),
            "Renamed".into(),
            HttpRequest::get("https://b.example"),
        )
        .unwrap();

    let state = session.snapshot();
    assert_eq!(state.saved_requests.len(), 1);
    assert_eq!(state.saved_requests[0].name, "Renamed");
}

#[test]
fn deleting_and_clearing_persist() {
    let dir = tempfile::tempdir().unwrap();
    let session = Session::start(Store::in_dir(dir.path())).unwrap();

    let saved = session
        .save_request(
            None,
            "Gone soon".into(),
            HttpRequest::get("https://a.example"),
        )
        .unwrap();

    let mut state = session.snapshot();
    state.push_history(api_client_storage::HistoryEntry::failure(
        HttpRequest::get("https://a.example"),
        &api_client_core::RequestError::other("boom"),
    ));
    session.replace_state(state).unwrap();

    assert!(session.delete_saved_request(&saved.id).unwrap());
    session.clear_history().unwrap();

    let reopened = Session::start(Store::in_dir(dir.path())).unwrap();
    let state = reopened.snapshot();
    assert!(state.saved_requests.is_empty());
    assert!(state.history.is_empty());
}

#[test]
fn a_state_with_no_tabs_is_repaired_rather_than_rejected() {
    let (_dir, session) = session();

    session
        .replace_state(AppState {
            tabs: Vec::new(),
            active_tab_id: None,
            ..AppState::default()
        })
        .unwrap();

    let state = session.snapshot();
    assert_eq!(state.tabs.len(), 1);
    assert!(state.active_tab_id.is_some());
}

#[test]
fn a_corrupt_state_file_still_yields_a_working_session() {
    let dir = tempfile::tempdir().unwrap();
    std::fs::write(dir.path().join("state.json"), b"garbage").unwrap();

    let session = Session::start(Store::in_dir(dir.path())).unwrap();

    assert!(session.recovery().is_some());
    assert_eq!(session.snapshot().tabs.len(), 1);
    assert!(session.storage_path().ends_with("state.json"));
}

#[tokio::test]
async fn a_send_returns_the_history_row_it_created() {
    let server = TestServer::start().await.unwrap();
    let (_dir, session) = session();

    let outcome = session
        .send_with_overrides(HttpRequest::get(server.url("/json")), Default::default())
        .await;

    assert!(outcome.error.is_none());
    assert_eq!(outcome.response.unwrap().status, 200);
    assert_eq!(outcome.history_entry.unwrap().status, Some(200));
}

#[tokio::test]
async fn saving_state_cannot_erase_history_recorded_since_the_snapshot() {
    let server = TestServer::start().await.unwrap();
    let (_dir, session) = session();

    // A UI takes a snapshot, then a send happens before it writes back.
    let stale = session.snapshot();
    session
        .send(HttpRequest::get(server.url("/json")))
        .await
        .unwrap();
    session.replace_state(stale).unwrap();

    assert_eq!(session.snapshot().history.len(), 1);
}

#[tokio::test]
async fn clear_history_still_clears_it() {
    let server = TestServer::start().await.unwrap();
    let (_dir, session) = session();

    session
        .send(HttpRequest::get(server.url("/json")))
        .await
        .unwrap();
    session.clear_history().unwrap();

    assert!(session.snapshot().history.is_empty());
}

#[tokio::test]
async fn changing_tls_settings_rebuilds_the_engine() {
    use api_client_testserver::{ClientAuth, TestCa};

    let ca = TestCa::new();
    let server = TestServer::start_tls(&ca, ClientAuth::None).await.unwrap();
    let dir = tempfile::tempdir().unwrap();
    let ca_path = dir.path().join("ca.pem");
    std::fs::write(&ca_path, ca.pem()).unwrap();

    let session = Session::start(Store::in_dir(dir.path())).unwrap();

    // The CA is unknown to the machine, so this fails first.
    let error = session
        .send(HttpRequest::get(server.url("/json")))
        .await
        .unwrap_err();
    assert_eq!(error.kind, RequestErrorKind::Tls);

    let mut state = session.snapshot();
    state.settings.tls.extra_ca_files = vec![ca_path.display().to_string()];
    session.replace_state(state).expect("settings should apply");

    // No restart: the same session now trusts it.
    let response = session
        .send(HttpRequest::get(server.url("/json")))
        .await
        .expect("the new CA should be in effect");
    assert_eq!(response.status, 200);
}

#[tokio::test]
async fn an_unusable_certificate_is_reported_but_the_setting_is_still_saved() {
    let dir = tempfile::tempdir().unwrap();
    let session = Session::start(Store::in_dir(dir.path())).unwrap();

    let mut state = session.snapshot();
    state.settings.tls.extra_ca_files = vec!["/definitely/not/here.pem".into()];
    let error = session.replace_state(state).unwrap_err();

    assert!(error.to_string().contains("/definitely/not/here.pem"));

    // Saved anyway, so the settings dialog has something to correct.
    let reopened = Session::start(Store::in_dir(dir.path()));
    assert!(
        reopened.is_err()
            || !reopened
                .unwrap()
                .snapshot()
                .settings
                .tls
                .extra_ca_files
                .is_empty()
    );
}

#[tokio::test]
async fn settings_that_do_not_touch_the_engine_leave_it_alone() {
    let server = TestServer::start().await.unwrap();
    let (_dir, session) = session();

    let mut state = session.snapshot();
    state.settings.wrap_response_lines = true;
    session.replace_state(state).unwrap();

    let response = session
        .send(HttpRequest::get(server.url("/json")))
        .await
        .unwrap();
    assert_eq!(response.status, 200);
}

/// The snippet carries the values the request would be sent with, rather than
/// `{{name}}` for somebody to fill in by hand.
#[test]
fn generated_code_resolves_variables_from_the_active_environment() {
    let (_dir, session) = session();

    let mut state = session.snapshot();
    state.environments = vec![Environment {
        id: "env-1".into(),
        name: "Local".into(),
        variables: vec![
            KeyValueEntry::new("baseUrl", "https://api.example.com"),
            KeyValueEntry::new("token", "tok_123"),
        ],
    }];
    state.active_environment_id = Some("env-1".into());
    session.replace_state(state).unwrap();

    let request = HttpRequest {
        auth: api_client_core::AuthConfig::Bearer {
            token: "{{token}}".into(),
        },
        ..HttpRequest::get("{{baseUrl}}/things")
    };

    let code = session
        .generate_code(&request, Default::default(), CodeTarget::Curl)
        .unwrap();

    assert!(code.contains("https://api.example.com/things"), "{code}");
    assert!(code.contains("Bearer tok_123"), "{code}");
    assert!(!code.contains("{{"), "{code}");
}

/// A name with no value is the same answer Send gives, and for the same reason.
#[test]
fn generated_code_reports_a_variable_it_cannot_resolve() {
    let (_dir, session) = session();

    let error = session
        .generate_code(
            &HttpRequest::get("{{baseUrl}}/things"),
            Default::default(),
            CodeTarget::Curl,
        )
        .expect_err("a name with no value has no code");

    assert!(error.message.contains("baseUrl"), "{}", error.message);
}

/// The settings are the app's, not the request's, so the snippet has to carry
/// them: code copied out of a client that follows redirects should follow them.
#[test]
fn generated_code_carries_the_settings_the_request_would_be_sent_under() {
    let (_dir, session) = session();

    let mut state = session.snapshot();
    state.settings.follow_redirects = false;
    state.settings.timeout_ms = 5_000;
    state.settings.tls.accept_invalid_certs = true;
    session.replace_state(state).unwrap();

    let code = session
        .generate_code(
            &HttpRequest::get("https://example.com/things"),
            Default::default(),
            CodeTarget::Curl,
        )
        .unwrap();

    assert!(!code.contains("--location"), "{code}");
    assert!(code.contains("--insecure"), "{code}");
    assert!(code.contains("--max-time 5"), "{code}");
}

#[test]
fn importing_adds_requests_and_says_what_they_still_need() {
    let dir = tempfile::tempdir().unwrap();
    let session = Session::start(Store::in_dir(dir.path())).unwrap();

    let mut state = session.snapshot();
    state.environments.push(api_client_variables::Environment {
        id: "env".into(),
        name: "Local".into(),
        variables: vec![api_client_core::KeyValueEntry::new(
            "baseUrl",
            "http://localhost",
        )],
    });
    session.replace_state(state).unwrap();

    let file = "### Me\n\
                GET {{baseUrl}}/me\n\
                Authorization: Bearer {{token}}\n\
                \n\
                ### Login\n\
                POST {{baseUrl}}/login\n\
                Authorization: Basic ada s3cret\n";
    let outcome = session.import_saved_requests(file).unwrap();

    assert_eq!(outcome.imported, 2);
    assert_eq!(outcome.undefined_variables, ["token"]);
    assert!(
        outcome.plain_secrets,
        "Basic ada s3cret is a password as it is"
    );
    assert!(outcome.skipped.is_empty());

    // Importing only ever adds, and it lasts.
    session.import_saved_requests(file).unwrap();
    drop(session);
    let reopened = Session::start(Store::in_dir(dir.path())).unwrap();
    let names: Vec<_> = reopened
        .snapshot()
        .saved_requests
        .iter()
        .map(|saved| saved.name.clone())
        .collect();
    assert_eq!(names, ["Me", "Login", "Me", "Login"]);
}

#[test]
fn importing_something_else_changes_nothing() {
    let dir = tempfile::tempdir().unwrap();
    let session = Session::start(Store::in_dir(dir.path())).unwrap();

    let error = session
        .import_saved_requests(r#"{"version": 2, "tabs": []}"#)
        .err()
        .unwrap();
    assert!(error.to_string().contains("not a file of saved requests"));
    assert!(session.snapshot().saved_requests.is_empty());
}
