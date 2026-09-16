use api_client_core::{HttpRequest, KeyValueEntry};
use api_client_storage::{
    AppState, HistoryEntry, SCHEMA_VERSION, SavedRequest, ScratchTab, Store, migrate,
};

fn temp_store() -> (tempfile::TempDir, Store) {
    let dir = tempfile::tempdir().expect("temp dir");
    let store = Store::in_dir(dir.path());
    (dir, store)
}

fn entry(url: &str) -> HistoryEntry {
    HistoryEntry::failure(
        HttpRequest::get(url),
        &api_client_core::RequestError::other("boom"),
    )
}

#[test]
fn a_first_launch_has_exactly_one_blank_tab() {
    let (_dir, store) = temp_store();
    let outcome = store.load();

    assert!(outcome.recovery.is_none());
    assert_eq!(outcome.state.tabs.len(), 1);
    assert!(outcome.state.tabs[0].request.is_untouched());
    assert_eq!(
        outcome.state.active_tab_id.as_deref(),
        Some(outcome.state.tabs[0].id.as_str())
    );
    assert!(outcome.state.saved_requests.is_empty());
    assert!(outcome.state.history.is_empty());
}

#[test]
fn state_survives_a_round_trip() {
    let (_dir, store) = temp_store();

    let mut state = AppState::default();
    state.upsert_saved_request(SavedRequest::new(
        "List users",
        HttpRequest::get("https://api.example.com/users"),
    ));
    state.push_history(entry("https://api.example.com/one"));
    state.tabs.push(ScratchTab::blank());

    store.save(&state).expect("save");
    let loaded = store.load();

    assert!(loaded.recovery.is_none());
    assert_eq!(loaded.state.saved_requests.len(), 1);
    assert_eq!(loaded.state.saved_requests[0].name, "List users");
    assert_eq!(loaded.state.history.len(), 1);
    assert_eq!(loaded.state.tabs.len(), 2);
}

#[test]
fn the_state_file_is_readable_json() {
    let (_dir, store) = temp_store();
    store.save(&AppState::default()).unwrap();

    let raw = std::fs::read_to_string(store.path()).unwrap();
    assert!(
        raw.contains("\n  \"version\": 1"),
        "should be pretty-printed"
    );
    assert!(serde_json::from_str::<serde_json::Value>(&raw).is_ok());
}

#[test]
fn saving_leaves_no_temp_file_behind() {
    let (dir, store) = temp_store();
    store.save(&AppState::default()).unwrap();

    let names: Vec<_> = std::fs::read_dir(dir.path())
        .unwrap()
        .map(|entry| entry.unwrap().file_name().to_string_lossy().into_owned())
        .collect();

    assert_eq!(names, vec!["state.json".to_string()]);
}

#[test]
fn history_is_capped_and_newest_first() {
    let mut state = AppState::default();
    state.settings.max_history = 3;

    for index in 0..10 {
        state.push_history(entry(&format!("https://example.com/{index}")));
    }

    assert_eq!(state.history.len(), 3);
    assert_eq!(state.history[0].request.url, "https://example.com/9");
    assert_eq!(state.history[2].request.url, "https://example.com/7");
}

#[test]
fn history_keeps_the_request_but_not_the_response_body() {
    let response = api_client_core::HttpResponse {
        status: 200,
        status_text: "OK".into(),
        headers: vec![],
        body: vec![b'x'; 4096],
        mime_type: None,
        duration_ms: 12,
        size_bytes: 4096,
        truncated: false,
        final_url: "https://example.com".into(),
        warnings: vec![],
    };

    let entry = HistoryEntry::success(HttpRequest::get("https://example.com"), &response);
    let json = serde_json::to_string(&entry).unwrap();

    assert!(!json.contains("xxxx"));
    assert_eq!(entry.size_bytes, Some(4096));
    assert_eq!(entry.status, Some(200));
}

#[test]
fn saved_requests_update_in_place_and_delete() {
    let mut state = AppState::default();
    let saved = SavedRequest::new("First", HttpRequest::get("https://example.com/a"));
    let id = saved.id.clone();
    let created_at = saved.created_at;

    state.upsert_saved_request(saved);

    let mut updated = SavedRequest::new("Renamed", HttpRequest::get("https://example.com/b"));
    updated.id = id.clone();
    state.upsert_saved_request(updated);

    assert_eq!(state.saved_requests.len(), 1);
    assert_eq!(state.saved_requests[0].name, "Renamed");
    assert_eq!(state.saved_requests[0].created_at, created_at);

    assert!(state.delete_saved_request(&id));
    assert!(!state.delete_saved_request(&id));
    assert!(state.saved_requests.is_empty());
}

#[test]
fn corrupt_files_are_preserved_and_defaults_are_used() {
    let (dir, store) = temp_store();
    std::fs::write(store.path(), b"{ not json at all").unwrap();

    let outcome = store.load();
    let recovery = outcome.recovery.expect("should report the problem");

    assert!(recovery.message.contains("could not be loaded"));
    let backup = recovery.backup_path.expect("the bad file should be kept");
    assert!(backup.exists());
    assert_eq!(
        std::fs::read_to_string(&backup).unwrap(),
        "{ not json at all"
    );
    assert_eq!(outcome.state.tabs.len(), 1);

    // The app must be usable straight after a recovery.
    store.save(&outcome.state).unwrap();
    assert!(store.load().recovery.is_none());
    drop(dir);
}

#[test]
fn a_file_from_a_newer_build_is_refused_rather_than_mangled() {
    let value = serde_json::json!({ "version": SCHEMA_VERSION + 1 });
    let error = migrate(value).unwrap_err();
    assert!(error.to_string().contains("newer version"));
}

#[test]
fn an_unversioned_file_is_migrated_to_the_current_schema() {
    let value = serde_json::json!({
        "savedRequests": [],
        "history": [],
        "tabs": [],
    });

    let state = migrate(value).expect("should migrate");
    assert_eq!(state.version, SCHEMA_VERSION);
    // Migration restores the one-tab invariant.
    assert_eq!(state.tabs.len(), 1);
}

#[test]
fn unknown_fields_and_missing_settings_do_not_break_loading() {
    let value = serde_json::json!({
        "version": SCHEMA_VERSION,
        "somethingFromTheFuture": true,
    });

    let state = migrate(value).expect("should load");
    assert_eq!(state.settings.max_history, 500);
    assert_eq!(state.settings.timeout_ms, 30_000);
}

#[test]
fn a_non_object_state_file_is_an_error_not_a_panic() {
    assert!(migrate(serde_json::json!([1, 2, 3])).is_err());
}

#[test]
fn the_active_environment_supplies_the_variables() {
    let mut state = AppState {
        environments: vec![api_client_variables::Environment {
            id: "env-1".into(),
            name: "Local".into(),
            variables: vec![KeyValueEntry::new("baseUrl", "http://localhost:3000")],
        }],
        ..AppState::default()
    };

    assert!(state.active_variables().is_empty());

    state.active_environment_id = Some("env-1".into());
    assert_eq!(
        state.active_variables().get("baseUrl"),
        Some("http://localhost:3000")
    );
}

#[test]
fn ensure_one_tab_repairs_a_dangling_active_id() {
    let mut state = AppState {
        tabs: Vec::new(),
        active_tab_id: Some("gone".into()),
        ..AppState::default()
    };

    state.ensure_one_tab();

    assert_eq!(state.tabs.len(), 1);
    assert_eq!(state.active_tab_id, Some(state.tabs[0].id.clone()));
}
