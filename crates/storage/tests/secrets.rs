use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use api_client_core::{
    AuthConfig, ClientIdentitySettings, HttpRequest, KeyValueEntry, OAuth1Settings,
};
use api_client_storage::{AppState, HistoryEntry, KeySource, SavedRequest, Store};
use api_client_variables::Environment;

/// A keyring that lives as long as the test, and can be switched off.
#[derive(Debug, Default)]
struct MemoryKeyring {
    key: Mutex<Option<String>>,
    down: AtomicBool,
}

impl MemoryKeyring {
    fn set_down(&self, down: bool) {
        self.down.store(down, Ordering::SeqCst);
    }
}

impl KeySource for MemoryKeyring {
    fn read(&self) -> Result<Option<String>, String> {
        if self.down.load(Ordering::SeqCst) {
            return Err("the keyring is locked".into());
        }
        Ok(self.key.lock().unwrap().clone())
    }

    fn write(&self, key: &str) -> Result<(), String> {
        if self.down.load(Ordering::SeqCst) {
            return Err("the keyring is locked".into());
        }
        *self.key.lock().unwrap() = Some(key.to_owned());
        Ok(())
    }
}

const SECRETS: &[&str] = &[
    "basic-password",
    "header-api-key",
    "history-bearer-token",
    "environment-token",
    "p12-password",
    "oauth1-consumer-secret",
    "oauth1-token-secret",
];

fn state_with_secrets() -> AppState {
    let mut request = HttpRequest::get("https://api.example.com/users");
    request.auth = AuthConfig::Basic {
        username: "ada".into(),
        password: "basic-password".into(),
    };
    request
        .headers
        .push(KeyValueEntry::new("X-Api-Key", "header-api-key"));
    request
        .headers
        .push(KeyValueEntry::new("Accept", "application/json"));

    let mut signed = HttpRequest::get("https://api.example.com/timeline");
    signed.auth = AuthConfig::OAuth1(OAuth1Settings {
        consumer_key: "consumer-key".into(),
        consumer_secret: "oauth1-consumer-secret".into(),
        token: "visible-oauth1-token".into(),
        token_secret: "oauth1-token-secret".into(),
        ..OAuth1Settings::default()
    });

    let mut sent = HttpRequest::get("https://api.example.com/me");
    sent.auth = AuthConfig::Bearer {
        token: "history-bearer-token".into(),
    };

    let mut state = AppState::default();
    state.tabs[0].request = request.clone();
    state.upsert_saved_request(SavedRequest::new("Users", request));
    state.upsert_saved_request(SavedRequest::new("Timeline", signed));
    state.push_history(HistoryEntry::failure(
        sent,
        &api_client_core::RequestError::other("boom"),
    ));
    state.environments.push(Environment {
        id: api_client_core::new_id(),
        name: "Local".into(),
        variables: vec![KeyValueEntry::new("token", "environment-token")],
    });
    state.settings.tls.client_identity = Some(ClientIdentitySettings {
        path: "/home/ada/client.p12".into(),
        password: Some("p12-password".into()),
    });
    state
}

fn tab_password(state: &AppState) -> &str {
    match &state.tabs[0].request.auth {
        AuthConfig::Basic { password, .. } => password,
        other => panic!("expected basic auth, got {other:?}"),
    }
}

fn keyed_store(dir: &tempfile::TempDir, keys: &Arc<MemoryKeyring>) -> Store {
    Store::in_dir(dir.path()).with_keyring(keys.clone())
}

fn raw(store: &Store) -> String {
    std::fs::read_to_string(store.path()).unwrap()
}

#[test]
fn secrets_are_encrypted_on_disk_and_come_back_on_load() {
    let dir = tempfile::tempdir().unwrap();
    let keys = Arc::new(MemoryKeyring::default());
    let state = state_with_secrets();

    keyed_store(&dir, &keys).save(&state).unwrap();

    let on_disk = raw(&keyed_store(&dir, &keys));
    for secret in SECRETS {
        assert!(
            !on_disk.contains(secret),
            "{secret} is on disk in plain text"
        );
    }
    assert!(on_disk.contains("enc:v1:"));
    assert!(on_disk.contains("\"secrets\": \"keyring\""));
    // Identifiers and ordinary headers stay readable.
    for visible in [
        "ada",
        "application/json",
        "consumer-key",
        "visible-oauth1-token",
    ] {
        assert!(on_disk.contains(visible), "{visible} should be readable");
    }

    let loaded = keyed_store(&dir, &keys).load();
    assert!(loaded.recovery.is_none(), "{:?}", loaded.recovery);
    assert_eq!(loaded.state, state);
}

#[test]
fn a_file_from_before_encryption_is_encrypted_as_soon_as_it_loads() {
    let dir = tempfile::tempdir().unwrap();
    let state = state_with_secrets();

    // Written the way version 1 wrote it.
    Store::in_dir(dir.path()).save(&state).unwrap();
    let mut value: serde_json::Value =
        serde_json::from_str(&raw(&Store::in_dir(dir.path()))).unwrap();
    value["version"] = 1.into();
    value.as_object_mut().unwrap().remove("secrets");
    std::fs::write(Store::in_dir(dir.path()).path(), value.to_string()).unwrap();

    let keys = Arc::new(MemoryKeyring::default());
    let store = keyed_store(&dir, &keys);
    let loaded = store.load();

    assert!(loaded.recovery.is_none(), "{:?}", loaded.recovery);
    assert_eq!(tab_password(&loaded.state), "basic-password");
    let on_disk = raw(&store);
    for secret in SECRETS {
        assert!(!on_disk.contains(secret), "{secret} was left in plain text");
    }
}

#[test]
fn without_a_keyring_secrets_are_plain_text_and_that_is_said_once() {
    let dir = tempfile::tempdir().unwrap();
    let keys = Arc::new(MemoryKeyring::default());
    keys.set_down(true);

    let store = keyed_store(&dir, &keys);
    let first = store.load();
    let notice = first.recovery.expect("the user should be told");
    assert!(notice.message.contains("plain text"), "{}", notice.message);
    assert!(notice.message.contains("the keyring is locked"));

    store.save(&state_with_secrets()).unwrap();
    assert!(raw(&store).contains("basic-password"));
    assert!(raw(&store).contains("\"secrets\": \"plainText\""));

    let second = keyed_store(&dir, &keys).load();
    assert!(second.recovery.is_none(), "{:?}", second.recovery);
    assert_eq!(tab_password(&second.state), "basic-password");
}

#[test]
fn encrypted_secrets_survive_a_session_without_the_keyring() {
    let dir = tempfile::tempdir().unwrap();
    let keys = Arc::new(MemoryKeyring::default());
    keyed_store(&dir, &keys)
        .save(&state_with_secrets())
        .unwrap();

    keys.set_down(true);
    let locked = keyed_store(&dir, &keys);
    let mut outcome = locked.load();
    let notice = outcome.recovery.expect("the user should be told");
    assert!(
        notice.message.contains("cannot be read right now"),
        "{}",
        notice.message
    );
    // Not sent as a password, and not shown as one.
    assert_eq!(tab_password(&outcome.state), "");

    // Something new is sent meanwhile, which moves every history row along.
    let mut sent = HttpRequest::get("https://api.example.com/later");
    sent.auth = AuthConfig::Bearer {
        token: "typed-while-locked".into(),
    };
    outcome.state.push_history(HistoryEntry::failure(
        sent,
        &api_client_core::RequestError::other("boom"),
    ));
    locked.save(&outcome.state).unwrap();
    assert!(raw(&locked).contains("typed-while-locked"));

    keys.set_down(false);
    let unlocked = keyed_store(&dir, &keys).load();
    assert!(unlocked.recovery.is_none(), "{:?}", unlocked.recovery);
    assert_eq!(tab_password(&unlocked.state), "basic-password");
    let tokens: Vec<_> = unlocked
        .state
        .history
        .iter()
        .map(|entry| match &entry.request.auth {
            AuthConfig::Bearer { token } => token.as_str(),
            _ => "",
        })
        .collect();
    assert_eq!(tokens, ["typed-while-locked", "history-bearer-token"]);
}

#[test]
fn a_lost_key_clears_what_it_encrypted_and_keeps_a_copy() {
    let dir = tempfile::tempdir().unwrap();
    let old_keys = Arc::new(MemoryKeyring::default());
    keyed_store(&dir, &old_keys)
        .save(&state_with_secrets())
        .unwrap();

    // A new machine, or a keyring that was reset.
    let new_keys = Arc::new(MemoryKeyring::default());
    let outcome = keyed_store(&dir, &new_keys).load();

    let notice = outcome.recovery.expect("the user should be told");
    assert!(
        notice.message.contains("could not be decrypted"),
        "{}",
        notice.message
    );
    let copy = notice.backup_path.expect("the old file should be kept");
    assert!(std::fs::read_to_string(copy).unwrap().contains("enc:v1:"));
    assert_eq!(tab_password(&outcome.state), "");
    // Everything that was not secret is still there.
    assert_eq!(outcome.state.saved_requests.len(), 2);

    let again = keyed_store(&dir, &new_keys).load();
    assert!(again.recovery.is_none(), "{:?}", again.recovery);
}

#[cfg(unix)]
#[test]
fn the_state_file_is_readable_by_its_owner_only() {
    use std::os::unix::fs::PermissionsExt;

    let dir = tempfile::tempdir().unwrap();
    let store = Store::in_dir(dir.path());
    let mode = |store: &Store| {
        std::fs::metadata(store.path())
            .unwrap()
            .permissions()
            .mode()
            & 0o777
    };

    store.save(&AppState::default()).unwrap();
    assert_eq!(mode(&store), 0o600);

    // As every earlier version left it.
    std::fs::set_permissions(store.path(), std::fs::Permissions::from_mode(0o644)).unwrap();
    store.load();
    assert_eq!(mode(&store), 0o600);
}
