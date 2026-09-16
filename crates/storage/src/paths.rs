use std::path::PathBuf;

/// Where the desktop app keeps its data.
///
/// Resolved by hand rather than with a helper crate: the rules are three lines
/// long and this way there is nothing to disagree with Tauri about.
pub fn default_data_dir() -> PathBuf {
    const APP_DIR: &str = "api-client";

    if cfg!(target_os = "windows") {
        if let Some(app_data) = std::env::var_os("APPDATA") {
            return PathBuf::from(app_data).join(APP_DIR);
        }
    } else if cfg!(target_os = "macos") {
        if let Some(home) = std::env::var_os("HOME") {
            return PathBuf::from(home)
                .join("Library")
                .join("Application Support")
                .join(APP_DIR);
        }
    } else {
        if let Some(data_home) = std::env::var_os("XDG_DATA_HOME").filter(|v| !v.is_empty()) {
            return PathBuf::from(data_home).join(APP_DIR);
        }
        if let Some(home) = std::env::var_os("HOME") {
            return PathBuf::from(home)
                .join(".local")
                .join("share")
                .join(APP_DIR);
        }
    }

    std::env::temp_dir().join(APP_DIR)
}
