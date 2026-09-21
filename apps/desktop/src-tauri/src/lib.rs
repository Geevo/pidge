//! The desktop shell.
//!
//! Commands here do argument shuffling and nothing else; the behaviour lives in
//! `api-client-session`, which the VS Code sidecar drives through the same API.

mod commands;

use api_client_session::Session;
use api_client_storage::{Store, default_data_dir};

pub fn run() {
    init_tracing();

    let store = Store::in_dir(default_data_dir());
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
        .invoke_handler(tauri::generate_handler![
            commands::send_http_request,
            commands::cancel_http_request,
            commands::generate_code,
            commands::load_state,
            commands::save_state,
            commands::save_request,
            commands::delete_saved_request,
            commands::clear_history,
            commands::window_buttons,
        ])
        .run(tauri::generate_context!())
        .expect("failed to start the desktop window");
}

fn init_tracing() {
    use tracing_subscriber::EnvFilter;

    let filter = EnvFilter::try_from_env("API_CLIENT_LOG")
        .unwrap_or_else(|_| EnvFilter::new("api_client_desktop=info,api_client_http_engine=info"));

    let _ = tracing_subscriber::fmt()
        .with_env_filter(filter)
        .with_target(false)
        .try_init();
}
