use api_client_core::{HttpRequest, KeyValueEntry, RequestErrorKind};
use api_client_session::Session;
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
