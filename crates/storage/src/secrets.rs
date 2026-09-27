//! Passwords and tokens, encrypted inside the state file.
//!
//! The file stays one readable JSON document. Only the values that would let
//! someone else call your APIs are replaced, each by its own ciphertext, so a
//! copy of the file on its own — a backup, a synced dotfile, an attachment to
//! a bug report — gives none of them away.
//!
//! XChaCha20-Poly1305 rather than AES-GCM because every save encrypts every
//! secret afresh with a random nonce, and history multiplies how many there
//! are. A 192-bit nonce makes that safe for as long as the key lives.

use std::collections::HashMap;

use api_client_core::HttpRequest;
use api_client_core::redact::{auth_kind, auth_secrets_mut, is_secret_header};
use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use chacha20poly1305::aead::{Aead, Generate, Key, KeyInit};
use chacha20poly1305::{XChaCha20Poly1305, XNonce};

use crate::model::AppState;

/// Marks a value as ciphertext. Anything without it is plain text.
const PREFIX: &str = "enc:v1:";
const NONCE_LEN: usize = 24;
const TAG_LEN: usize = 16;

pub(crate) struct Cipher(XChaCha20Poly1305);

impl Cipher {
    /// A new random key, base64 encoded: KDE Wallet stores text, not bytes.
    pub fn generate_key() -> String {
        STANDARD.encode(Key::<XChaCha20Poly1305>::generate())
    }

    pub fn from_key(encoded: &str) -> Option<Self> {
        let bytes = STANDARD.decode(encoded.trim()).ok()?;
        XChaCha20Poly1305::new_from_slice(&bytes).ok().map(Self)
    }

    fn seal(&self, plain: &str) -> String {
        let nonce = XNonce::generate();
        let sealed = self
            .0
            .encrypt(&nonce, plain.as_bytes())
            .expect("encrypting into a Vec cannot run out of room");
        let mut bytes = nonce.to_vec();
        bytes.extend(sealed);
        format!("{PREFIX}{}", STANDARD.encode(bytes))
    }

    fn open(&self, sealed: &[u8]) -> Option<String> {
        let (nonce, body) = sealed.split_at(NONCE_LEN);
        let nonce = XNonce::try_from(nonce).ok()?;
        let plain = self.0.decrypt(&nonce, body).ok()?;
        String::from_utf8(plain).ok()
    }
}

/// The raw bytes, if `value` is ciphertext. Something that merely starts with
/// the prefix but does not decode is somebody's password.
fn ciphertext(value: &str) -> Option<Vec<u8>> {
    let bytes = STANDARD.decode(value.strip_prefix(PREFIX)?).ok()?;
    (bytes.len() >= NONCE_LEN + TAG_LEN).then_some(bytes)
}

/// What decrypting a loaded state found.
#[derive(Debug, Default)]
pub(crate) struct Opened {
    /// Secrets that were in plain text, which the next save will encrypt.
    pub plain: usize,
    /// Ciphertext the key could not open, now cleared.
    pub unreadable: usize,
    /// Ciphertext left unopened because there was no key, by where it lives.
    pub kept: HashMap<String, String>,
}

/// Decrypts every secret in place. Without a key, ciphertext is taken out of
/// the state, so it is never sent as a password, and handed back to be
/// written again by [`seal`].
pub(crate) fn open(state: &mut AppState, cipher: Option<&Cipher>) -> Opened {
    let mut opened = Opened::default();

    for_each_secret(state, Pass::Open, |at, value| {
        let Some(sealed) = ciphertext(value) else {
            if !value.is_empty() {
                opened.plain += 1;
            }
            return;
        };
        match cipher {
            Some(cipher) => match cipher.open(&sealed) {
                Some(plain) => *value = plain,
                None => {
                    opened.unreadable += 1;
                    value.clear();
                }
            },
            None => {
                opened.kept.insert(at.to_owned(), std::mem::take(value));
            }
        }
    });

    opened
}

/// Encrypts every secret in place, or leaves it as plain text without a key.
///
/// A secret that is still empty where `kept` has ciphertext for it gets that
/// ciphertext back: it was cleared by [`open`], not by the user, and it is
/// readable again as soon as the keyring is.
pub(crate) fn seal(state: &mut AppState, cipher: Option<&Cipher>, kept: &HashMap<String, String>) {
    for_each_secret(state, Pass::Seal, |at, value| {
        if value.is_empty() {
            if let Some(sealed) = kept.get(at) {
                value.clone_from(sealed);
            }
        } else if let Some(cipher) = cipher {
            *value = cipher.seal(value);
        }
    });
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Pass {
    Open,
    Seal,
}

/// Calls `visit` with every secret in `state` and a name for where it lives.
///
/// The names are built from ids, not positions, so they still point at the
/// same value after history has grown or the tabs have been reordered.
fn for_each_secret(state: &mut AppState, pass: Pass, mut visit: impl FnMut(&str, &mut String)) {
    if let Some(identity) = &mut state.settings.tls.client_identity
        && let Some(password) = &mut identity.password
    {
        visit("settings/tls/clientIdentity/password", password);
    }

    for saved in &mut state.saved_requests {
        let at = format!("savedRequests/{}", saved.id);
        request_secrets(&mut saved.request, &at, pass, &mut visit);
    }
    for entry in &mut state.history {
        let at = format!("history/{}", entry.id);
        request_secrets(&mut entry.request, &at, pass, &mut visit);
    }
    for tab in &mut state.tabs {
        let at = format!("tabs/{}", tab.id);
        request_secrets(&mut tab.request, &at, pass, &mut visit);
    }

    // Every variable, not only the ones that look secret: a variable is where
    // a token goes to be kept out of the requests that use it.
    for environment in &mut state.environments {
        for variable in &mut environment.variables {
            let at = format!("environments/{}/{}", environment.id, variable.id);
            visit(&at, &mut variable.value);
        }
    }
}

fn request_secrets(
    request: &mut HttpRequest,
    at: &str,
    pass: Pass,
    visit: &mut impl FnMut(&str, &mut String),
) {
    let kind = auth_kind(&request.auth);
    for (field, value) in auth_secrets_mut(&mut request.auth) {
        visit(&format!("{at}/auth/{kind}/{field}"), value);
    }

    // Opening looks at every header, so a value still decrypts after its
    // header was renamed to something that does not look secret.
    for header in &mut request.headers {
        if pass == Pass::Open || is_secret_header(&header.name) {
            visit(&format!("{at}/headers/{}", header.id), &mut header.value);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trips_through_ciphertext() {
        let cipher = Cipher::from_key(&Cipher::generate_key()).unwrap();
        let sealed = cipher.seal("hunter2");

        assert!(sealed.starts_with(PREFIX));
        assert!(!sealed.contains("hunter2"));
        assert_eq!(
            cipher.open(&ciphertext(&sealed).unwrap()).unwrap(),
            "hunter2"
        );
    }

    #[test]
    fn the_same_secret_seals_differently_each_time() {
        let cipher = Cipher::from_key(&Cipher::generate_key()).unwrap();
        assert_ne!(cipher.seal("hunter2"), cipher.seal("hunter2"));
    }

    #[test]
    fn another_key_cannot_open_it() {
        let ours = Cipher::from_key(&Cipher::generate_key()).unwrap();
        let theirs = Cipher::from_key(&Cipher::generate_key()).unwrap();
        let sealed = ours.seal("hunter2");

        assert!(theirs.open(&ciphertext(&sealed).unwrap()).is_none());
    }

    #[test]
    fn a_password_that_only_looks_like_ciphertext_is_a_password() {
        assert!(ciphertext("enc:v1:hunter2").is_none());
        assert!(ciphertext("enc:v1:aGk=").is_none());
    }

    #[test]
    fn refuses_a_key_of_the_wrong_size() {
        assert!(Cipher::from_key(&STANDARD.encode([0u8; 16])).is_none());
        assert!(Cipher::from_key("not base64").is_none());
    }
}
