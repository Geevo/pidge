// The Windows release build is a GUI app, not a console app.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    api_client_desktop::run();
}
