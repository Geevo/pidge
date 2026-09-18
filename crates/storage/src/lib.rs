//! Local persistence.
//!
//! One human-readable JSON file per installation, written atomically, with a
//! version number and a migration chain from the very first release. If the
//! file is unreadable we keep it, say so, and start from defaults — losing a
//! scratch tab is annoying, refusing to open is worse.

mod migrate;
mod model;
mod paths;

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

pub use migrate::{SCHEMA_VERSION, migrate};
pub use model::{
    AppState, HistoryEntry, SavedRequest, ScratchTab, Settings, SyntaxTheme, Theme, now_ms,
};
pub use paths::default_data_dir;

pub const STATE_FILE_NAME: &str = "state.json";

#[derive(Debug, thiserror::Error)]
pub enum StorageError {
    #[error("could not write {path}: {source}")]
    Write {
        path: String,
        #[source]
        source: io::Error,
    },
    #[error("could not read {path}: {source}")]
    Read {
        path: String,
        #[source]
        source: io::Error,
    },
    #[error("{0}")]
    Migration(String),
    #[error("state file is not valid JSON: {0}")]
    Parse(#[from] serde_json::Error),
}

/// Why the loaded state is not what was on disk.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Recovery {
    /// Safe to show to the user.
    pub message: String,
    /// Where the unreadable file was moved, if we managed to keep it.
    pub backup_path: Option<PathBuf>,
}

/// The result of a load: always a usable state, plus an optional explanation.
#[derive(Debug, Clone)]
pub struct LoadOutcome {
    pub state: AppState,
    pub recovery: Option<Recovery>,
}

/// Reads and writes the state file.
#[derive(Debug, Clone)]
pub struct Store {
    path: PathBuf,
}

impl Store {
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self { path: path.into() }
    }

    /// `<dir>/state.json`
    pub fn in_dir(dir: impl AsRef<Path>) -> Self {
        Self::new(dir.as_ref().join(STATE_FILE_NAME))
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Never fails: a missing or corrupt file yields defaults plus a [`Recovery`].
    pub fn load(&self) -> LoadOutcome {
        let raw = match fs::read_to_string(&self.path) {
            Ok(raw) => raw,
            Err(err) if err.kind() == io::ErrorKind::NotFound => {
                return LoadOutcome {
                    state: AppState::default(),
                    recovery: None,
                };
            }
            Err(err) => {
                return self.recovered(
                    format!("Could not read your saved data ({err}). Starting fresh."),
                    false,
                );
            }
        };

        let parsed = serde_json::from_str::<serde_json::Value>(&raw)
            .map_err(|err| err.to_string())
            .and_then(|value| migrate(value).map_err(|err| err.to_string()));

        match parsed {
            Ok(state) => LoadOutcome {
                state,
                recovery: None,
            },
            Err(message) => self.recovered(
                format!("Your saved data could not be loaded ({message}). Starting fresh."),
                true,
            ),
        }
    }

    /// Writes to a sibling temp file and renames, so a crash mid-write cannot
    /// leave a half-written state file behind.
    pub fn save(&self, state: &AppState) -> Result<(), StorageError> {
        let mut state = state.clone();
        state.version = SCHEMA_VERSION;

        if let Some(parent) = self.path.parent() {
            fs::create_dir_all(parent).map_err(|source| StorageError::Write {
                path: parent.display().to_string(),
                source,
            })?;
        }

        let json = serde_json::to_string_pretty(&state)?;
        let temp = self.path.with_extension("json.tmp");

        write_and_sync(&temp, json.as_bytes()).map_err(|source| StorageError::Write {
            path: temp.display().to_string(),
            source,
        })?;

        fs::rename(&temp, &self.path).map_err(|source| StorageError::Write {
            path: self.path.display().to_string(),
            source,
        })
    }

    /// Moves the unreadable file aside so the user can recover it by hand.
    fn recovered(&self, message: String, keep_file: bool) -> LoadOutcome {
        let backup_path = if keep_file {
            let backup = self
                .path
                .with_extension(format!("corrupt-{}.json", now_ms()));
            match fs::rename(&self.path, &backup) {
                Ok(()) => Some(backup),
                Err(err) => {
                    tracing::warn!(error = %err, "could not preserve the unreadable state file");
                    None
                }
            }
        } else {
            None
        };

        let message = match &backup_path {
            Some(path) => format!(
                "{message} The previous file was kept at {}.",
                path.display()
            ),
            None => message,
        };

        LoadOutcome {
            state: AppState::default(),
            recovery: Some(Recovery {
                message,
                backup_path,
            }),
        }
    }
}

fn write_and_sync(path: &Path, bytes: &[u8]) -> io::Result<()> {
    use std::io::Write as _;

    let mut file = fs::File::create(path)?;
    file.write_all(bytes)?;
    file.sync_all()
}
