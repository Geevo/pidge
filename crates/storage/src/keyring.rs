//! Where the key that encrypts saved secrets is kept.
//!
//! One random key per state file, in `state.key` beside it, protected by
//! whatever the operating system offers that can never ask the user anything:
//!
//! - **Windows**: DPAPI, which ties it to the user's login.
//! - **Linux**: both of two things, where it can. A Secret Service keyring
//!   (GNOME Keyring, KDE Wallet), but only one that is already unlocked, which
//!   answers in a fraction of a second. And `systemd-creds --user` (systemd
//!   256 and later), which ties it to the machine and the user the way DPAPI
//!   does and never needs unlocking, but takes two seconds to decrypt: it is
//!   only asked when the keyring is locked.
//! - **Anywhere else**, macOS included: nothing yet, so secrets are saved as
//!   plain text and the user is told.
//!
//! Something that prompts is treated as not being there. A dialog at startup
//! is worse than a notice.

use std::fmt;
use std::fs;
use std::io;
use std::path::PathBuf;

use serde::{Deserialize, Serialize};

use crate::secrets::Cipher;

/// Somewhere to keep the key. [`SystemKeyring`] outside of tests.
pub trait KeySource: Send + Sync + fmt::Debug {
    /// `Ok(None)` when there is no key yet. An error means it could not be
    /// got at, which is not the same thing and never replaces it.
    fn read(&self) -> Result<Option<String>, String>;
    fn write(&self, key: &str) -> Result<(), String>;
}

/// Finds the key, creating it the first time. An error is the reason the
/// secrets will have to be saved as plain text.
pub(crate) fn resolve(source: &dyn KeySource) -> Result<Cipher, String> {
    let encoded = match source.read()? {
        Some(encoded) => encoded,
        None => {
            source.write(&Cipher::generate_key())?;
            // Read back rather than trusting what was written: two processes
            // starting at once have to settle on the same key.
            source
                .read()?
                .ok_or_else(|| "the new key did not stay where it was put".to_owned())?
        }
    };
    Cipher::from_key(&encoded).ok_or_else(|| "the saved key is not one this build can use".into())
}

/// The key file, protected by the operating system.
#[derive(Debug, Clone)]
pub struct SystemKeyring {
    path: PathBuf,
}

impl SystemKeyring {
    /// `state.key` for `state.json`.
    pub fn beside(state_file: &std::path::Path) -> Self {
        Self {
            path: state_file.with_extension("key"),
        }
    }
}

/// What `state.key` holds: the key under each protection that was available
/// when it was made, tried in the order of the fields.
#[derive(Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct KeyFile {
    /// The id of the keyring item holding the key. The file only points at it.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    secret_service: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    systemd_creds: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    dpapi: Option<String>,
}

impl KeySource for SystemKeyring {
    fn read(&self) -> Result<Option<String>, String> {
        let raw = match fs::read_to_string(&self.path) {
            Ok(raw) => raw,
            Err(err) if err.kind() == io::ErrorKind::NotFound => return Ok(None),
            Err(err) => return Err(format!("the key file could not be read: {err}")),
        };
        // An unreadable file is reported, not replaced: it may be the only
        // way back to everything it encrypted.
        let file: KeyFile = serde_json::from_str(&raw)
            .map_err(|_| "the key file is not one this build understands".to_owned())?;

        let mut problems = Vec::new();
        let mut gone = false;
        if let Some(item) = &file.secret_service {
            match platform::secret_service_read(item) {
                Ok(Some(key)) => return Ok(Some(key)),
                Ok(None) => gone = true,
                Err(err) => problems.push(err),
            }
        }
        if let Some(sealed) = &file.systemd_creds {
            match platform::systemd_creds_decrypt(sealed) {
                Ok(key) => return Ok(Some(key)),
                Err(err) => problems.push(err),
            }
        }
        if let Some(blob) = &file.dpapi {
            match platform::dpapi_unprotect(blob) {
                Ok(key) => return Ok(Some(key)),
                Err(err) => problems.push(err),
            }
        }

        if !problems.is_empty() {
            Err(problems.join("; "))
        } else if gone {
            // Deleted from the keyring with nothing else holding it: lost, the
            // same as a lost file, so a new key can take its place.
            Ok(None)
        } else {
            Err("the key file holds no key".into())
        }
    }

    fn write(&self, key: &str) -> Result<(), String> {
        let file = platform::protect(key)?;
        let json = serde_json::to_string_pretty(&file).map_err(|err| err.to_string())?;
        crate::write_file(&self.path, json.as_bytes())
            .map_err(|err| format!("the key file could not be written: {err}"))
    }
}

/// Logs the whole error and returns what is worth showing the user.
#[cfg(any(target_os = "windows", target_os = "linux"))]
fn unavailable(what: &str, err: impl fmt::Display) -> String {
    tracing::warn!(error = %err, "{what}");
    what.to_owned()
}

#[cfg(target_os = "windows")]
mod platform {
    use std::ptr;

    use base64::Engine as _;
    use base64::engine::general_purpose::STANDARD;
    use windows_sys::Win32::Foundation::LocalFree;
    use windows_sys::Win32::Security::Cryptography::{
        CRYPT_INTEGER_BLOB, CRYPTPROTECT_UI_FORBIDDEN, CryptProtectData, CryptUnprotectData,
    };

    use super::{KeyFile, unavailable};

    /// Mixed in so that another program running as the same user cannot hand
    /// the blob to DPAPI and get the key back without knowing it. The app's
    /// name before it was pidge, kept: a key protected with it opens only
    /// with it.
    const ENTROPY: &[u8] = b"api-client state key";

    pub fn protect(key: &str) -> Result<KeyFile, String> {
        let blob = dpapi(key.as_bytes(), true)
            .map_err(|err| unavailable("Windows could not protect the key", err))?;
        Ok(KeyFile {
            dpapi: Some(STANDARD.encode(blob)),
            ..KeyFile::default()
        })
    }

    pub fn dpapi_unprotect(blob: &str) -> Result<String, String> {
        let blob = STANDARD
            .decode(blob)
            .map_err(|_| "the key file is damaged".to_owned())?;
        let key = dpapi(&blob, false)
            .map_err(|err| unavailable("Windows could not unprotect the key", err))?;
        String::from_utf8(key).map_err(|_| "the key file is damaged".into())
    }

    fn dpapi(input: &[u8], protect: bool) -> std::io::Result<Vec<u8>> {
        let input = CRYPT_INTEGER_BLOB {
            cbData: input.len() as u32,
            pbData: input.as_ptr().cast_mut(),
        };
        let entropy = CRYPT_INTEGER_BLOB {
            cbData: ENTROPY.len() as u32,
            pbData: ENTROPY.as_ptr().cast_mut(),
        };
        let mut output = CRYPT_INTEGER_BLOB {
            cbData: 0,
            pbData: ptr::null_mut(),
        };

        // SAFETY: both input blobs point at live slices of the stated length,
        // which DPAPI only reads. UI_FORBIDDEN makes it fail rather than show
        // anything. On success it allocates `output` with LocalAlloc, which
        // is copied out and freed below.
        let ok = unsafe {
            if protect {
                CryptProtectData(
                    &input,
                    ptr::null(),
                    &entropy,
                    ptr::null(),
                    ptr::null(),
                    CRYPTPROTECT_UI_FORBIDDEN,
                    &mut output,
                )
            } else {
                CryptUnprotectData(
                    &input,
                    ptr::null_mut(),
                    &entropy,
                    ptr::null(),
                    ptr::null(),
                    CRYPTPROTECT_UI_FORBIDDEN,
                    &mut output,
                )
            }
        };
        if ok == 0 {
            return Err(std::io::Error::last_os_error());
        }

        // SAFETY: DPAPI succeeded, so `output` is a LocalAlloc'd buffer of
        // `cbData` bytes that nothing else owns.
        unsafe {
            let bytes = std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec();
            LocalFree(output.pbData.cast());
            Ok(bytes)
        }
    }

    pub fn systemd_creds_decrypt(_: &str) -> Result<String, String> {
        Err("the key was protected by systemd on Linux".into())
    }

    pub fn secret_service_read(_: &str) -> Result<Option<String>, String> {
        Err("the key is in a Linux keyring".into())
    }
}

#[cfg(target_os = "linux")]
mod platform {
    use std::collections::HashMap;
    use std::io::Write as _;
    use std::process::{Command, Stdio};

    use secret_service::EncryptionType;
    use secret_service::blocking::SecretService;

    use super::{KeyFile, unavailable};

    /// Bound into the credential, so it cannot be passed off as another one.
    /// Named for the app before it was pidge, and kept: a credential sealed
    /// under one name will not open under another.
    const CREDENTIAL_NAME: &str = "api-client-key";

    /// Both, where both are there; either on its own will do.
    pub fn protect(key: &str) -> Result<KeyFile, String> {
        let item = api_client_core::new_id();
        let keyring = secret_service_create(&item, key).map(|()| item);
        let creds = systemd_creds(&["encrypt", "--tpm2-pcrs="], key.as_bytes())
            .map(|sealed| String::from_utf8_lossy(&sealed).trim().to_owned());

        if let (Err(keyring), Err(creds)) = (&keyring, &creds) {
            tracing::warn!(%keyring, %creds, "nowhere to protect the key");
            return Err("there is neither an unlocked keyring nor systemd-creds".into());
        }
        Ok(KeyFile {
            secret_service: keyring.ok(),
            systemd_creds: creds.ok(),
            ..KeyFile::default()
        })
    }

    pub fn systemd_creds_decrypt(sealed: &str) -> Result<String, String> {
        let key = systemd_creds(&["decrypt"], sealed.as_bytes())
            .map_err(|err| unavailable("systemd-creds could not decrypt the key", err))?;
        String::from_utf8(key).map_err(|_| "the key file is damaged".into())
    }

    /// Runs `systemd-creds --user`. `--no-ask-password` stops it asking for
    /// authentication; with no PCRs, a firmware update or a change to Secure
    /// Boot does not lock the key away.
    fn systemd_creds(args: &[&str], input: &[u8]) -> Result<Vec<u8>, String> {
        let mut child = Command::new("systemd-creds")
            .args(["--user", "--no-ask-password"])
            .args(args)
            .arg(format!("--name={CREDENTIAL_NAME}"))
            .args(["-", "-"])
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .map_err(|err| format!("systemd-creds: {err}"))?;

        // Dropped at the end of the statement, which closes stdin.
        child
            .stdin
            .take()
            .expect("stdin is piped")
            .write_all(input)
            .map_err(|err| format!("systemd-creds: {err}"))?;

        let output = child
            .wait_with_output()
            .map_err(|err| format!("systemd-creds: {err}"))?;
        if !output.status.success() {
            return Err(format!(
                "systemd-creds: {}",
                String::from_utf8_lossy(&output.stderr).trim()
            ));
        }
        Ok(output.stdout)
    }

    /// What a key is found by. `api-client` is the app's old name, kept so
    /// that keys made before the rename are still found.
    fn attributes(item: &str) -> HashMap<&str, &str> {
        HashMap::from([("application", "api-client"), ("key", item)])
    }

    /// Never unlocks anything: unlocking is what shows a password dialog.
    pub fn secret_service_read(item: &str) -> Result<Option<String>, String> {
        let service = SecretService::connect(EncryptionType::Dh)
            .map_err(|err| unavailable("the keyring service did not answer", err))?;
        let found = service
            .search_items(attributes(item))
            .map_err(|err| unavailable("the keyring could not be searched", err))?;

        match found.unlocked.first() {
            Some(found) => {
                let key = found
                    .get_secret()
                    .map_err(|err| unavailable("the keyring would not hand over the key", err))?;
                String::from_utf8(key)
                    .map(Some)
                    .map_err(|_| "the keyring holds a damaged key".into())
            }
            None if !found.locked.is_empty() => Err("the keyring is locked".into()),
            None => Ok(None),
        }
    }

    fn secret_service_create(item: &str, key: &str) -> Result<(), String> {
        let service =
            SecretService::connect(EncryptionType::Dh).map_err(|err| format!("keyring: {err}"))?;
        let collection = service
            .get_default_collection()
            .map_err(|err| format!("keyring: {err}"))?;
        if collection
            .is_locked()
            .map_err(|err| format!("keyring: {err}"))?
        {
            return Err("keyring: locked".into());
        }
        collection
            .create_item(
                // What KDE Wallet and Seahorse show for it.
                "pidge: key for saved passwords",
                attributes(item),
                key.as_bytes(),
                true,
                "text/plain",
            )
            .map(drop)
            .map_err(|err| format!("keyring: {err}"))
    }

    pub fn dpapi_unprotect(_: &str) -> Result<String, String> {
        Err("the key was protected by Windows".into())
    }
}

#[cfg(not(any(target_os = "windows", target_os = "linux")))]
mod platform {
    use super::KeyFile;

    pub fn protect(_: &str) -> Result<KeyFile, String> {
        Err("this platform has nothing to protect it with yet".into())
    }

    pub fn dpapi_unprotect(_: &str) -> Result<String, String> {
        Err("the key was protected by Windows".into())
    }

    pub fn systemd_creds_decrypt(_: &str) -> Result<String, String> {
        Err("the key was protected by systemd on Linux".into())
    }

    pub fn secret_service_read(_: &str) -> Result<Option<String>, String> {
        Err("the key is in a Linux keyring".into())
    }
}
