//! The shared application service.
//!
//! Everything a frontend can do lives here: send, cancel, persist, save, and
//! clear. The Tauri commands and the sidecar message handlers are both thin
//! wrappers around this type, which is what keeps the two platforms honest.

mod export;
mod import;

use std::collections::BTreeMap;
use std::sync::{Arc, Mutex, MutexGuard};

use api_client_codegen::ClientOptions;
pub use api_client_codegen::{CodeTarget, ExportFormat};
use api_client_core::{HttpRequest, HttpResponse, RequestError, RequestErrorKind};
use api_client_http_engine::{CancellationRegistry, EngineConfig, HttpEngine};
use api_client_storage::{
    AppState, HistoryEntry, LoadOutcome, Recovery, SavedRequest, Settings, StorageError, Store,
};
pub use import::MAX_IMPORT_BYTES;
use serde::Serialize;
use ts_rs::TS;

/// Anything a state change can fail with.
///
/// Settings and storage fail in different ways and the UI shows both the same
/// way, but keeping them apart means a bad certificate path is not reported as
/// a disk problem.
#[derive(Debug, thiserror::Error)]
pub enum SessionError {
    #[error("{0}")]
    Storage(#[from] StorageError),
    #[error("{}", .0.message)]
    Engine(#[from] RequestError),
    /// A file that is not saved requests, or has none that can come across.
    #[error("{0}")]
    Import(String),
}

/// What an import brought in, for the notice that reports it.
#[derive(Debug, Clone, Serialize, TS)]
#[serde(rename_all = "camelCase")]
#[ts(export)]
pub struct ImportOutcome {
    pub state: AppState,
    #[ts(type = "number")]
    pub imported: usize,
    /// `{{names}}` the new requests use that no environment defines, which
    /// is what an export without its secrets leaves to be filled in.
    pub undefined_variables: Vec<String>,
    /// A sentence for each request the file held that could not come across.
    pub skipped: Vec<String>,
    /// The file holds a password or token as it is: worth deleting now.
    pub plain_secrets: bool,
}

/// What a send produced: a response or an error, plus the history row it
/// created. Returning the row lets a frontend keep its history list live
/// without re-fetching the whole state.
#[derive(Debug, Clone, Serialize, TS)]
#[serde(rename_all = "camelCase")]
#[ts(export)]
pub struct SendOutcome {
    pub response: Option<HttpResponse>,
    pub error: Option<RequestError>,
    /// `None` for a cancelled request, which is never recorded.
    pub history_entry: Option<HistoryEntry>,
}

/// A running application: one engine, one state file, one in-memory state.
#[derive(Clone)]
pub struct Session {
    inner: Arc<Inner>,
}

struct Inner {
    /// Rebuilt when the settings that shape it change, so a new CA or client
    /// certificate takes effect without restarting the app.
    engine: Mutex<HttpEngine>,
    cancellations: CancellationRegistry,
    store: Store,
    state: Mutex<AppState>,
    recovery: Option<Recovery>,
}

impl Session {
    /// Loads state from `store` and builds an engine configured from it.
    /// Never fails on a bad state file; see [`Session::recovery`].
    pub fn start(store: Store) -> Result<Self, RequestError> {
        let LoadOutcome {
            mut state,
            recovery,
        } = store.load();
        state.ensure_one_tab();

        let engine = HttpEngine::new(engine_config(&state.settings))?;

        Ok(Self {
            inner: Arc::new(Inner {
                engine: Mutex::new(engine),
                cancellations: CancellationRegistry::new(),
                store,
                state: Mutex::new(state),
                recovery,
            }),
        })
    }

    /// Set when the state file was missing, unreadable, or from a newer build.
    pub fn recovery(&self) -> Option<&Recovery> {
        self.inner.recovery.as_ref()
    }

    pub fn storage_path(&self) -> String {
        self.inner.store.path().display().to_string()
    }

    /// A copy of the current state, for handing to the UI.
    pub fn snapshot(&self) -> AppState {
        self.state().clone()
    }

    /// Sends a request, resolving variables from the active environment and
    /// recording the outcome in history.
    pub async fn send(&self, request: HttpRequest) -> Result<HttpResponse, RequestError> {
        let outcome = self.send_with_overrides(request, BTreeMap::new()).await;
        match (outcome.response, outcome.error) {
            (Some(response), _) => Ok(response),
            (None, Some(error)) => Err(error),
            (None, None) => Err(RequestError::other("the request produced no result")),
        }
    }

    /// As [`Session::send`], with extra variables layered over the active
    /// environment. The sidecar uses this so the webview can be explicit about
    /// what it thinks the variables are.
    pub async fn send_with_overrides(
        &self,
        request: HttpRequest,
        overrides: BTreeMap<String, String>,
    ) -> SendOutcome {
        let mut variables = self.state().active_variables();
        for (name, value) in overrides {
            variables.insert(name, value);
        }
        let handle = self.inner.cancellations.register(&request.id);

        // Cloned out of the lock: the guard must not be held across an await,
        // and the engine is a handle to a shared connection pool anyway.
        let engine = self.engine();
        let result = engine
            .execute_with_variables(request.clone(), &variables, handle)
            .await;
        self.inner.cancellations.finish(&request.id);

        // A cancelled request was never really sent, so it does not earn a
        // history entry.
        let skip_history = matches!(
            &result,
            Err(error) if error.kind == RequestErrorKind::Cancelled
        );

        let history_entry = if skip_history {
            None
        } else {
            let entry = match &result {
                Ok(response) => HistoryEntry::success(request, response),
                Err(error) => HistoryEntry::failure(request, error),
            };
            self.mutate(|state| state.push_history(entry.clone()));

            // History is written straight away, so a crash or a force-quit
            // does not lose what you just sent. A storage failure is worth a
            // log, but it must never turn a good response into an error.
            if let Err(err) = self.persist() {
                tracing::warn!(error = %err, "could not write history");
            }
            Some(entry)
        };

        match result {
            Ok(response) => SendOutcome {
                response: Some(response),
                error: None,
                history_entry,
            },
            Err(error) => SendOutcome {
                response: None,
                error: Some(error),
                history_entry,
            },
        }
    }

    /// Cancels an in-flight request. Returns false if it had already finished.
    /// Writes a request out as code for another client.
    ///
    /// Variables are resolved exactly as a send resolves them, so the snippet
    /// carries the values the request would actually go out with rather than
    /// `{{name}}` for somebody to fill in by hand. An unresolved name is the
    /// same error it would be on Send: there is no honest code to write for it.
    pub fn generate_code(
        &self,
        request: &HttpRequest,
        overrides: BTreeMap<String, String>,
        target: CodeTarget,
    ) -> Result<String, RequestError> {
        let (resolved, options) = {
            let state = self.state();
            let mut variables = state.active_variables();
            for (name, value) in overrides {
                variables.insert(name, value);
            }
            let settings = &state.settings;
            (
                api_client_variables::resolve_request(request, &variables)?,
                ClientOptions {
                    timeout_ms: settings.timeout_ms,
                    follow_redirects: settings.follow_redirects,
                    tls: settings.tls.clone(),
                },
            )
        };

        api_client_codegen::generate(&resolved, &options, target)
    }

    pub fn cancel(&self, request_id: &str) -> bool {
        self.inner.cancellations.cancel(request_id)
    }

    pub fn in_flight(&self) -> usize {
        self.inner.cancellations.in_flight()
    }

    /// Replaces the whole state, as the UI does when tabs or settings change.
    ///
    /// History is deliberately not taken from `next`: it is owned here and only
    /// changes through [`Session::send`] and [`Session::clear_history`]. A UI
    /// saving a snapshot it took before its last send must not erase the row
    /// that send created.
    pub fn replace_state(&self, mut next: AppState) -> Result<(), SessionError> {
        next.ensure_one_tab();

        let wanted = engine_config(&next.settings);
        let rebuild = wanted != *self.engine().config();

        {
            let mut current = self.state();
            next.history = std::mem::take(&mut current.history);
            *current = next;
        }

        // Saved first. If a certificate path is wrong the setting still needs
        // to persist, or the dialog that reported the error would have nothing
        // to correct.
        self.persist()?;

        if rebuild {
            let engine = HttpEngine::new(wanted)?;
            *self.engine_slot() = engine;
        }
        Ok(())
    }

    fn engine(&self) -> HttpEngine {
        self.engine_slot().clone()
    }

    fn engine_slot(&self) -> MutexGuard<'_, HttpEngine> {
        self.inner
            .engine
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// Creates or updates a saved request. Passing `id` updates in place.
    pub fn save_request(
        &self,
        id: Option<String>,
        name: String,
        request: HttpRequest,
    ) -> Result<SavedRequest, StorageError> {
        let mut saved = SavedRequest::new(name, request);
        if let Some(id) = id {
            saved.id = id;
        }
        self.mutate(|state| state.upsert_saved_request(saved.clone()));
        self.persist()?;
        Ok(saved)
    }

    pub fn delete_saved_request(&self, id: &str) -> Result<bool, StorageError> {
        let removed = self.mutate(|state| state.delete_saved_request(id));
        self.persist()?;
        Ok(removed)
    }

    /// The saved requests with these ids, in the order they are listed, as a
    /// file to share. Secrets become `{{variables}}` unless `include_secrets`.
    pub fn export_saved_requests(
        &self,
        ids: &[String],
        format: ExportFormat,
        include_secrets: bool,
    ) -> String {
        let chosen: Vec<SavedRequest> = self
            .state()
            .saved_requests
            .iter()
            .filter(|saved| ids.contains(&saved.id))
            .cloned()
            .collect();
        export::export(&chosen, format, include_secrets)
    }

    /// Adds the saved requests in `contents`, a JSON export or a `.http`
    /// file, as new ones. Nothing already saved is changed.
    pub fn import_saved_requests(&self, contents: &str) -> Result<ImportOutcome, SessionError> {
        let parsed = import::parse(contents).map_err(SessionError::Import)?;

        let known: api_client_variables::VariableSet = self
            .state()
            .environments
            .iter()
            .flat_map(|environment| environment.variables.iter())
            .filter(|variable| !variable.name.trim().is_empty())
            .map(|variable| (variable.name.trim().to_owned(), String::new()))
            .collect();
        let mut undefined_variables: Vec<String> = Vec::new();
        for saved in &parsed.saved {
            for name in api_client_variables::missing_variables(&saved.request, &known) {
                if !undefined_variables.contains(&name) {
                    undefined_variables.push(name);
                }
            }
        }
        let plain_secrets = parsed
            .saved
            .iter()
            .any(|saved| export::holds_plain_secret(&saved.request));

        let imported = parsed.saved.len();
        self.mutate(|state| state.saved_requests.extend(parsed.saved));
        self.persist()?;

        Ok(ImportOutcome {
            state: self.snapshot(),
            imported,
            undefined_variables,
            skipped: parsed.skipped,
            plain_secrets,
        })
    }

    pub fn clear_history(&self) -> Result<(), StorageError> {
        self.mutate(|state| state.history.clear());
        self.persist()
    }

    /// Writes the current state to disk.
    pub fn persist(&self) -> Result<(), StorageError> {
        let state = self.snapshot();
        self.inner.store.save(&state)
    }

    fn mutate<T>(&self, apply: impl FnOnce(&mut AppState) -> T) -> T {
        apply(&mut self.state())
    }

    fn state(&self) -> MutexGuard<'_, AppState> {
        self.inner
            .state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    }
}

fn engine_config(settings: &Settings) -> EngineConfig {
    EngineConfig {
        default_timeout_ms: settings.timeout_ms,
        max_response_bytes: settings.max_response_bytes,
        follow_redirects: settings.follow_redirects,
        tls: settings.tls.clone(),
        ..EngineConfig::default()
    }
}
