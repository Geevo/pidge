//! The desktop shell.
//!
//! Commands here do argument shuffling and nothing else; the behaviour lives in
//! `api-client-session`, which the VS Code sidecar drives through the same API.

mod commands;
mod context_menu;
mod window_state;

use api_client_session::Session;
use api_client_storage::{Store, adopt_legacy_data_dir, default_data_dir};
use tauri::Manager;

pub fn run() {
    init_tracing();

    adopt_legacy_data_dir();
    let store = Store::in_dir(default_data_dir()).with_system_keyring();
    let session = match Session::start(store) {
        Ok(session) => session,
        Err(err) => {
            // Nothing useful can happen without an HTTP client.
            eprintln!("could not start: {}", err.message);
            std::process::exit(1);
        }
    };

    tauri::Builder::default()
        // The only plugin: a native file chooser for picking certificates.
        .plugin(tauri_plugin_dialog::init())
        .manage(session)
        .setup(|app| {
            if let Some(window) = app.get_webview_window("main") {
                context_menu::keep_copy_and_paste(&window);
                let placement = app.state::<Session>().window_placement();
                window_state::restore(&window, placement);
            }
            Ok(())
        })
        .on_window_event(|window, event| match event {
            tauri::WindowEvent::Moved(_) | tauri::WindowEvent::Resized(_) => {
                window_state::remember(window, &window.state::<Session>());
            }
            tauri::WindowEvent::CloseRequested { .. } => {
                let session = window.state::<Session>();
                window_state::remember(window, &session);
                if let Err(err) = session.persist() {
                    tracing::warn!(error = %err, "could not save on close");
                }
            }
            _ => {}
        })
        .invoke_handler(tauri::generate_handler![
            commands::send_http_request,
            commands::cancel_http_request,
            commands::generate_code,
            commands::load_state,
            commands::save_state,
            commands::save_request,
            commands::delete_saved_request,
            commands::clear_history,
            commands::export_saved_requests,
            commands::import_saved_requests,
            commands::window_buttons,
        ])
        .run(tauri::generate_context!())
        .expect("failed to start the desktop window");
}

fn init_tracing() {
    use tracing_subscriber::EnvFilter;

    let filter = EnvFilter::try_from_env("PIDGE_LOG")
        .unwrap_or_else(|_| EnvFilter::new("api_client_desktop=info,api_client_http_engine=info"));

    let _ = tracing_subscriber::fmt()
        .with_env_filter(filter)
        .with_target(false)
        .try_init();
}
