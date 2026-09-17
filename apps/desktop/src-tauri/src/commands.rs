use std::collections::BTreeMap;

use api_client_core::HttpRequest;
use api_client_session::{SendOutcome, Session};
use api_client_storage::AppState;
use serde::Serialize;
use tauri::State;

/// What the UI needs on startup, in one round trip.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LoadedState {
    pub state: AppState,
    /// Present when the state file had to be recovered.
    pub recovery: Option<String>,
    pub storage_path: String,
    /// The app's own version, for the About tab.
    pub version: String,
}

#[tauri::command]
pub async fn send_http_request(
    session: State<'_, Session>,
    request: HttpRequest,
    variables: Option<BTreeMap<String, String>>,
) -> Result<SendOutcome, String> {
    // A failed request is part of the outcome, not a command failure; the UI
    // renders it in the response pane either way. The Result is here because
    // Tauri requires one for an async command holding borrowed state.
    Ok(session
        .send_with_overrides(request, variables.unwrap_or_default())
        .await)
}

#[tauri::command]
pub fn cancel_http_request(session: State<'_, Session>, request_id: String) -> bool {
    session.cancel(&request_id)
}

#[tauri::command]
pub fn load_state(session: State<'_, Session>) -> LoadedState {
    LoadedState {
        state: session.snapshot(),
        recovery: session.recovery().map(|recovery| recovery.message.clone()),
        storage_path: session.storage_path(),
        version: env!("CARGO_PKG_VERSION").to_string(),
    }
}

#[tauri::command]
pub fn save_state(session: State<'_, Session>, state: AppState) -> Result<AppState, String> {
    session
        .replace_state(state)
        .map_err(|err| err.to_string())?;
    Ok(session.snapshot())
}

#[tauri::command]
pub fn save_request(
    session: State<'_, Session>,
    saved_request_id: Option<String>,
    name: String,
    request: HttpRequest,
) -> Result<AppState, String> {
    session
        .save_request(saved_request_id, name, request)
        .map_err(|err| err.to_string())?;
    Ok(session.snapshot())
}

#[tauri::command]
pub fn delete_saved_request(
    session: State<'_, Session>,
    saved_request_id: String,
) -> Result<AppState, String> {
    session
        .delete_saved_request(&saved_request_id)
        .map_err(|err| err.to_string())?;
    Ok(session.snapshot())
}

#[tauri::command]
pub fn clear_history(session: State<'_, Session>) -> Result<AppState, String> {
    session.clear_history().map_err(|err| err.to_string())?;
    Ok(session.snapshot())
}
