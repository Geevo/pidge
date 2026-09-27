//! Local persistence.
//!
//! One human-readable JSON file per installation, written atomically, with a
//! version number and a migration chain from the very first release. If the
//! file is unreadable we keep it, say so, and start from defaults — losing a
//! scratch tab is annoying, refusing to open is worse.
//!
//! Passwords and tokens inside it are encrypted with a key the operating
//! system protects, when it can without prompting; see [`secrets`] and
//! [`keyring`].

mod keyring;
mod migrate;
mod model;
mod paths;
mod secrets;

use std::collections::HashMap;
use std::fmt;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, MutexGuard};

use serde::Serialize;

pub use keyring::{KeySource, SystemKeyring};
pub use migrate::{SCHEMA_VERSION, migrate};
pub use model::{
    AppState, HistoryEntry, SavedRequest, ScratchTab, Settings, SyntaxTheme, Theme,
    WindowPlacement, now_ms,
};
pub use paths::{adopt_legacy_data_dir, adopt_legacy_dir, default_data_dir};

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

/// Why the loaded state is not what was on disk, or what the user should
/// know about how it is kept.
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

/// How the secrets in a state file are kept, recorded beside them.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
enum SecretStorage {
    /// Encrypted with the key in `state.key`.
    Keyring,
    /// As typed. Once a file says so, the user has been told.
    PlainText,
}

/// The state file: the state, and a note of how its secrets are kept.
#[derive(Serialize)]
struct OnDisk<'a> {
    #[serde(flatten)]
    state: &'a AppState,
    secrets: SecretStorage,
}

/// What this store encrypts with, settled on first use.
struct Secrets {
    /// `None` saves secrets as plain text.
    cipher: Option<secrets::Cipher>,
    /// Why there is no cipher, when a keyring was asked for one and failed.
    unavailable: Option<String>,
    /// Ciphertext that could not be opened, written back until replaced.
    kept: HashMap<String, String>,
}

/// Reads and writes the state file.
#[derive(Clone)]
pub struct Store {
    path: PathBuf,
    keyring: Option<Arc<dyn KeySource>>,
    secrets: Arc<Mutex<Option<Secrets>>>,
}

impl fmt::Debug for Store {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Store")
            .field("path", &self.path)
            .field("keyring", &self.keyring)
            .finish_non_exhaustive()
    }
}

impl Store {
    /// A store that keeps secrets as plain text. See [`Store::with_keyring`].
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self {
            path: path.into(),
            keyring: None,
            secrets: Arc::default(),
        }
    }

    /// `<dir>/state.json`
    pub fn in_dir(dir: impl AsRef<Path>) -> Self {
        Self::new(dir.as_ref().join(STATE_FILE_NAME))
    }

    /// Encrypts secrets with a key kept in `keys`. If it cannot be reached,
    /// secrets are saved as plain text and the next load says so.
    pub fn with_keyring(mut self, keys: Arc<dyn KeySource>) -> Self {
        self.keyring = Some(keys);
        self
    }

    /// [`Store::with_keyring`] with a key in `state.key` beside the state
    /// file, protected by the operating system; see [`SystemKeyring`].
    /// `PIDGE_KEYRING=off` skips it, which the tests that start a real
    /// sidecar use to stay out of the user's keyring.
    pub fn with_system_keyring(self) -> Self {
        if std::env::var_os("PIDGE_KEYRING").is_some_and(|value| value == "off") {
            return self;
        }
        let keys = SystemKeyring::beside(&self.path);
        self.with_keyring(Arc::new(keys))
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Never fails: a missing or corrupt file yields defaults plus a [`Recovery`].
    pub fn load(&self) -> LoadOutcome {
        let (mut state, told_plain, recovery) = self.read();
        let mut messages: Vec<String> = recovery.iter().map(|r| r.message.clone()).collect();
        let mut backup_path = recovery.and_then(|r| r.backup_path);
        let mut rewrite = false;

        {
            let mut guard = self.secrets();
            let secrets = guard.get_or_insert_with(|| self.resolve());
            let opened = secrets::open(&mut state, secrets.cipher.as_ref());

            if opened.unreadable > 0 {
                let copy = self.keep_copy("undecryptable");
                let kept_at = copy
                    .as_ref()
                    .map(|path| format!(" The previous file was kept at {}.", path.display()))
                    .unwrap_or_default();
                messages.push(format!(
                    "{} could not be decrypted with the saved key, so {} been cleared.{kept_at}",
                    count_secrets(opened.unreadable),
                    if opened.unreadable == 1 {
                        "it has"
                    } else {
                        "they have"
                    },
                ));
                backup_path = backup_path.or(copy);
                rewrite = true;
            }

            let reason = secrets
                .unavailable
                .as_deref()
                .unwrap_or("no keyring was asked");
            if !opened.kept.is_empty() {
                messages.push(format!(
                    "Your saved passwords and tokens are encrypted, and their key cannot be read right now ({reason}). They are left as they are in the file and come back once it can be; anything you enter meanwhile is saved as plain text.",
                ));
            } else if secrets.unavailable.is_some() && !told_plain {
                messages.push(format!(
                    "Passwords and tokens are saved as plain text, in a file only your user account can read, because nothing could protect their key ({reason}).{}",
                    keyring_hint(),
                ));
                // Written now, so that the file records it has been said.
                rewrite = true;
            }

            if secrets.cipher.is_some() && opened.plain > 0 {
                rewrite = true;
            }
            secrets.kept = opened.kept;
        }

        if rewrite && let Err(err) = self.save(&state) {
            tracing::warn!(error = %err, "could not rewrite the state file");
        }

        LoadOutcome {
            state,
            recovery: (!messages.is_empty()).then(|| Recovery {
                message: messages.join(" "),
                backup_path,
            }),
        }
    }

    /// The state on disk, whether it says its plain-text secrets have been
    /// announced, and what went wrong reading it.
    fn read(&self) -> (AppState, bool, Option<Recovery>) {
        let raw = match fs::read_to_string(&self.path) {
            Ok(raw) => raw,
            Err(err) if err.kind() == io::ErrorKind::NotFound => {
                return (AppState::default(), false, None);
            }
            Err(err) => {
                let recovery = self.recovered(
                    format!("Could not read your saved data ({err}). Starting fresh."),
                    false,
                );
                return (AppState::default(), false, Some(recovery));
            }
        };

        // A file from before the permissions were tightened is fixed on sight.
        make_private(&self.path);

        let parsed = serde_json::from_str::<serde_json::Value>(&raw)
            .map_err(|err| err.to_string())
            .and_then(|value| {
                let told_plain = value.get("secrets").and_then(|v| v.as_str()) == Some("plainText");
                migrate(value)
                    .map(|state| (state, told_plain))
                    .map_err(|err| err.to_string())
            });

        match parsed {
            Ok((state, told_plain)) => (state, told_plain, None),
            Err(message) => {
                let recovery = self.recovered(
                    format!("Your saved data could not be loaded ({message}). Starting fresh."),
                    true,
                );
                (AppState::default(), false, Some(recovery))
            }
        }
    }

    /// Writes to a sibling temp file and renames, so a crash mid-write cannot
    /// leave a half-written state file behind.
    pub fn save(&self, state: &AppState) -> Result<(), StorageError> {
        let mut state = state.clone();
        state.version = SCHEMA_VERSION;

        let storage = {
            let mut guard = self.secrets();
            let secrets = guard.get_or_insert_with(|| self.resolve());
            secrets::seal(&mut state, secrets.cipher.as_ref(), &secrets.kept);
            if secrets.cipher.is_some() {
                SecretStorage::Keyring
            } else {
                SecretStorage::PlainText
            }
        };

        if let Some(parent) = self.path.parent() {
            create_private_dir(parent).map_err(|source| StorageError::Write {
                path: parent.display().to_string(),
                source,
            })?;
        }

        let json = serde_json::to_string_pretty(&OnDisk {
            state: &state,
            secrets: storage,
        })?;
        write_file(&self.path, json.as_bytes()).map_err(|source| StorageError::Write {
            path: self.path.display().to_string(),
            source,
        })
    }

    /// Asks the keyring for the key, once per store.
    fn resolve(&self) -> Secrets {
        let Some(keys) = &self.keyring else {
            return Secrets {
                cipher: None,
                unavailable: None,
                kept: HashMap::new(),
            };
        };
        match keyring::resolve(keys.as_ref()) {
            Ok(cipher) => Secrets {
                cipher: Some(cipher),
                unavailable: None,
                kept: HashMap::new(),
            },
            Err(reason) => {
                tracing::warn!(%reason, "secrets will be saved as plain text");
                Secrets {
                    cipher: None,
                    unavailable: Some(reason),
                    kept: HashMap::new(),
                }
            }
        }
    }

    fn secrets(&self) -> MutexGuard<'_, Option<Secrets>> {
        self.secrets
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// Copies the state file aside before something in it is given up on.
    fn keep_copy(&self, why: &str) -> Option<PathBuf> {
        let copy = self.path.with_extension(format!("{why}-{}.json", now_ms()));
        match fs::copy(&self.path, &copy) {
            Ok(_) => {
                make_private(&copy);
                Some(copy)
            }
            Err(err) => {
                tracing::warn!(error = %err, "could not keep a copy of the state file");
                None
            }
        }
    }

    /// Moves the unreadable file aside so the user can recover it by hand.
    fn recovered(&self, message: String, keep_file: bool) -> Recovery {
        let backup_path = if keep_file {
            let backup = self
                .path
                .with_extension(format!("corrupt-{}.json", now_ms()));
            match fs::rename(&self.path, &backup) {
                Ok(()) => {
                    make_private(&backup);
                    Some(backup)
                }
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

        Recovery {
            message,
            backup_path,
        }
    }
}

fn count_secrets(n: usize) -> String {
    if n == 1 {
        "A saved password or token".to_owned()
    } else {
        format!("{n} saved passwords and tokens")
    }
}

/// What to install, where the answer is not obvious.
fn keyring_hint() -> &'static str {
    if cfg!(target_os = "linux") {
        " That needs systemd 256 or later, or an unlocked GNOME Keyring or KDE Wallet."
    } else {
        ""
    }
}

/// Writes a sibling temp file and renames it over `path`, so a crash
/// mid-write cannot leave half a file behind.
pub(crate) fn write_file(path: &Path, bytes: &[u8]) -> io::Result<()> {
    let mut temp = path.as_os_str().to_owned();
    temp.push(".tmp");
    let temp = PathBuf::from(temp);
    write_and_sync(&temp, bytes)?;
    fs::rename(&temp, path)
}

/// Created readable by this user only. The mode is set again after opening,
/// because a temp file left behind by a crash keeps the mode it was made with.
/// On Windows, %APPDATA% is already private to the user.
fn write_and_sync(path: &Path, bytes: &[u8]) -> io::Result<()> {
    use std::io::Write as _;

    let mut options = fs::OpenOptions::new();
    options.write(true).create(true).truncate(true);
    #[cfg(unix)]
    std::os::unix::fs::OpenOptionsExt::mode(&mut options, 0o600);

    let mut file = options.open(path)?;
    #[cfg(unix)]
    file.set_permissions(std::os::unix::fs::PermissionsExt::from_mode(0o600))?;

    file.write_all(bytes)?;
    file.sync_all()
}

/// Only affects directories it creates; an existing one is left as it is.
fn create_private_dir(dir: &Path) -> io::Result<()> {
    let mut builder = fs::DirBuilder::new();
    builder.recursive(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::DirBuilderExt as _;
        builder.mode(0o700);
    }
    builder.create(dir)
}

/// Takes away access for anyone but the owner, if anyone else has it.
fn make_private(path: &Path) {
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt as _;
        let Ok(metadata) = fs::metadata(path) else {
            return;
        };
        if metadata.permissions().mode() & 0o077 != 0
            && let Err(err) = fs::set_permissions(path, fs::Permissions::from_mode(0o600))
        {
            tracing::warn!(error = %err, "could not make the state file private");
        }
    }
    #[cfg(not(unix))]
    let _ = path;
}
