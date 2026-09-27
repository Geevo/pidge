use std::collections::BTreeMap;

use api_client_core::{HttpRequest, RequestError};
use api_client_session::{CodeTarget, ExportFormat, SendOutcome, Session};
use api_client_storage::AppState;
use serde::Serialize;
use tauri::{AppHandle, State};
use tauri_plugin_dialog::DialogExt as _;

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

/// Which desktop's title-bar buttons the UI should draw.
///
/// The window is undecorated, so the app draws them itself, and drawing the
/// wrong ones is the fastest way to look like a port of something else.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum WindowButtons {
    Windows,
    Kde,
    Gnome,
}

#[tauri::command]
pub fn window_buttons() -> WindowButtons {
    if cfg!(target_os = "windows") {
        return WindowButtons::Windows;
    }
    buttons_for_desktop(std::env::var("XDG_CURRENT_DESKTOP").ok().as_deref())
}

/// KDE and GNOME look nothing alike, so the desktop decides as much as the
/// operating system does. `XDG_CURRENT_DESKTOP` can name several, colon
/// separated and in no fixed case — "ubuntu:GNOME" is a real value. Anything
/// that is not KDE is closer to Adwaita than to Breeze.
fn buttons_for_desktop(desktop: Option<&str>) -> WindowButtons {
    let is_kde = desktop.is_some_and(|desktop| {
        desktop
            .split(':')
            .any(|name| name.trim().eq_ignore_ascii_case("kde"))
    });

    if is_kde {
        WindowButtons::Kde
    } else {
        WindowButtons::Gnome
    }
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

/// Writes a request out as code, without sending it.
///
/// The error is the engine's own: an unresolved variable or a URL that will not
/// parse are both things the UI already knows how to show.
#[tauri::command]
pub fn generate_code(
    session: State<'_, Session>,
    request: HttpRequest,
    target: CodeTarget,
    variables: Option<BTreeMap<String, String>>,
) -> Result<String, RequestError> {
    session.generate_code(&request, variables.unwrap_or_default(), target)
}

/// Asks where to save, then writes the export there. `None` when the dialog
/// was dismissed.
///
/// The dialog is opened from here rather than from the webview, so the path
/// written to is always one the user just chose: the webview can suggest a
/// name, never a place.
#[tauri::command]
pub async fn export_saved_requests(
    app: AppHandle,
    session: State<'_, Session>,
    saved_request_ids: Vec<String>,
    format: ExportFormat,
    include_secrets: bool,
    file_name: String,
) -> Result<Option<String>, String> {
    let (filter, extension) = match format {
        ExportFormat::Json => ("JSON", "json"),
        ExportFormat::Http => ("HTTP requests", "http"),
    };
    let (chosen, answer) = tokio::sync::oneshot::channel();
    app.dialog()
        .file()
        .set_title("Export saved requests")
        .set_file_name(file_name)
        .add_filter(filter, &[extension])
        .save_file(move |path| {
            let _ = chosen.send(path);
        });

    let Some(path) = answer.await.ok().flatten() else {
        return Ok(None);
    };
    let path = path.into_path().map_err(|err| err.to_string())?;
    let contents = session.export_saved_requests(&saved_request_ids, format, include_secrets);
    std::fs::write(&path, contents)
        .map_err(|err| format!("Could not write {}: {err}", path.display()))?;
    Ok(Some(path.display().to_string()))
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_the_desktop_out_of_the_xdg_variable() {
        assert_eq!(buttons_for_desktop(Some("KDE")), WindowButtons::Kde);
        assert_eq!(buttons_for_desktop(Some("kde")), WindowButtons::Kde);
        // Several desktops, in the order the session set them.
        assert_eq!(
            buttons_for_desktop(Some("KDE:X-Cinnamon")),
            WindowButtons::Kde
        );
        assert_eq!(
            buttons_for_desktop(Some("ubuntu:GNOME")),
            WindowButtons::Gnome
        );
    }

    /// A desktop nobody here has heard of, and a session that sets nothing.
    #[test]
    fn falls_back_to_the_more_common_shape() {
        assert_eq!(buttons_for_desktop(Some("Sway")), WindowButtons::Gnome);
        assert_eq!(buttons_for_desktop(None), WindowButtons::Gnome);
    }
}
