use std::io;
use std::path::{Path, PathBuf};

const APP_DIR: &str = "pidge";

/// Where the data lived before the app was called pidge.
const LEGACY_APP_DIR: &str = "api-client";

/// Where the desktop app keeps its data.
///
/// Resolved by hand rather than with a helper crate: the rules are three lines
/// long and this way there is nothing to disagree with Tauri about.
pub fn default_data_dir() -> PathBuf {
    data_dir_named(APP_DIR)
}

/// Moves the data directory from before the rename into place, so an upgrade
/// opens on the same tabs, history and saved requests. See
/// [`adopt_legacy_dir`].
pub fn adopt_legacy_data_dir() {
    let (legacy, current) = (data_dir_named(LEGACY_APP_DIR), default_data_dir());
    match adopt_legacy_dir(&legacy, &current) {
        Ok(true) => {
            tracing::info!(from = %legacy.display(), to = %current.display(), "moved the data directory")
        }
        Ok(false) => {}
        // The old directory is still there, so nothing is lost; the app opens
        // fresh and the move is tried again next time.
        Err(err) => tracing::warn!(error = %err, "could not move the old data directory"),
    }
}

/// Renames `legacy` to `current` when only `legacy` exists. True if it did.
///
/// Once, and never over anything: if `current` already exists, both are left
/// alone. The key file moves with the state file, and nothing that protects
/// the key depends on where it is, so encrypted secrets still open.
pub fn adopt_legacy_dir(legacy: &Path, current: &Path) -> io::Result<bool> {
    if current.exists() || !legacy.is_dir() {
        return Ok(false);
    }
    std::fs::rename(legacy, current)?;
    Ok(true)
}

fn data_dir_named(name: &str) -> PathBuf {
    if cfg!(target_os = "windows") {
        if let Some(app_data) = std::env::var_os("APPDATA") {
            return PathBuf::from(app_data).join(name);
        }
    } else if cfg!(target_os = "macos") {
        if let Some(home) = std::env::var_os("HOME") {
            return PathBuf::from(home)
                .join("Library")
                .join("Application Support")
                .join(name);
        }
    } else {
        if let Some(data_home) = std::env::var_os("XDG_DATA_HOME").filter(|v| !v.is_empty()) {
            return PathBuf::from(data_home).join(name);
        }
        if let Some(home) = std::env::var_os("HOME") {
            return PathBuf::from(home).join(".local").join("share").join(name);
        }
    }

    std::env::temp_dir().join(name)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn moves_the_old_directory_into_place_once() {
        let root = tempfile::tempdir().unwrap();
        let (legacy, current) = (root.path().join("api-client"), root.path().join("pidge"));
        std::fs::create_dir(&legacy).unwrap();
        std::fs::write(legacy.join("state.json"), "{}").unwrap();

        assert!(adopt_legacy_dir(&legacy, &current).unwrap());
        assert!(current.join("state.json").exists());
        assert!(!legacy.exists());

        // Nothing left to move the second time.
        assert!(!adopt_legacy_dir(&legacy, &current).unwrap());
    }

    #[test]
    fn never_moves_over_a_directory_that_is_already_there() {
        let root = tempfile::tempdir().unwrap();
        let (legacy, current) = (root.path().join("api-client"), root.path().join("pidge"));
        std::fs::create_dir(&legacy).unwrap();
        std::fs::write(legacy.join("state.json"), "old").unwrap();
        std::fs::create_dir(&current).unwrap();
        std::fs::write(current.join("state.json"), "new").unwrap();

        assert!(!adopt_legacy_dir(&legacy, &current).unwrap());
        assert_eq!(
            std::fs::read_to_string(current.join("state.json")).unwrap(),
            "new"
        );
        assert_eq!(
            std::fs::read_to_string(legacy.join("state.json")).unwrap(),
            "old"
        );
    }

    #[test]
    fn does_nothing_on_a_first_install() {
        let root = tempfile::tempdir().unwrap();
        let (legacy, current) = (root.path().join("api-client"), root.path().join("pidge"));
        assert!(!adopt_legacy_dir(&legacy, &current).unwrap());
        assert!(!current.exists());
    }
}
