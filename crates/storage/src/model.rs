use api_client_core::{HttpRequest, HttpResponse, RequestError};
use api_client_variables::Environment;
use serde::{Deserialize, Serialize};
use ts_rs::TS;

use crate::migrate::SCHEMA_VERSION;

/// Milliseconds since the Unix epoch.
pub fn now_ms() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|elapsed| elapsed.as_millis() as i64)
        .unwrap_or_default()
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, TS, Default)]
#[serde(rename_all = "camelCase")]
#[ts(export)]
pub enum Theme {
    #[default]
    System,
    Light,
    Dark,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, TS)]
#[serde(rename_all = "camelCase", default)]
#[ts(export)]
pub struct Settings {
    pub theme: Theme,
    #[ts(type = "number")]
    pub timeout_ms: u64,
    pub follow_redirects: bool,
    #[ts(type = "number")]
    pub max_history: usize,
    #[ts(type = "number")]
    pub max_response_bytes: u64,
    /// Reopen the scratch tabs that were open last time.
    pub restore_tabs: bool,
    pub wrap_response_lines: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            theme: Theme::System,
            timeout_ms: 30_000,
            follow_redirects: true,
            max_history: 500,
            max_response_bytes: 50 * 1024 * 1024,
            restore_tabs: true,
            wrap_response_lines: false,
        }
    }
}

/// A request the user chose to keep. Flat list, no folders, no collections.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, TS)]
#[serde(rename_all = "camelCase")]
#[ts(export)]
pub struct SavedRequest {
    pub id: String,
    pub name: String,
    pub request: HttpRequest,
    #[ts(type = "number")]
    pub created_at: i64,
    #[ts(type = "number")]
    pub updated_at: i64,
}

impl SavedRequest {
    pub fn new(name: impl Into<String>, request: HttpRequest) -> Self {
        let now = now_ms();
        Self {
            id: api_client_core::new_id(),
            name: name.into(),
            request,
            created_at: now,
            updated_at: now,
        }
    }
}

/// One past send. Response bodies are deliberately not kept: history is for
/// getting back to a request, not for archiving payloads.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, TS)]
#[serde(rename_all = "camelCase")]
#[ts(export)]
pub struct HistoryEntry {
    pub id: String,
    #[ts(type = "number")]
    pub timestamp: i64,
    pub request: HttpRequest,
    pub status: Option<u16>,
    pub status_text: Option<String>,
    #[ts(type = "number | null")]
    pub duration_ms: Option<u64>,
    #[ts(type = "number | null")]
    pub size_bytes: Option<u64>,
    /// Set instead of the status fields when the request never completed.
    pub error: Option<String>,
}

impl HistoryEntry {
    pub fn success(request: HttpRequest, response: &HttpResponse) -> Self {
        Self {
            id: api_client_core::new_id(),
            timestamp: now_ms(),
            request,
            status: Some(response.status),
            status_text: Some(response.status_text.clone()),
            duration_ms: Some(response.duration_ms),
            size_bytes: Some(response.size_bytes),
            error: None,
        }
    }

    pub fn failure(request: HttpRequest, error: &RequestError) -> Self {
        Self {
            id: api_client_core::new_id(),
            timestamp: now_ms(),
            request,
            status: None,
            status_text: None,
            duration_ms: None,
            size_bytes: None,
            error: Some(error.message.clone()),
        }
    }
}

/// An open editor tab. Tabs exist whether or not they were ever saved.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, TS)]
#[serde(rename_all = "camelCase")]
#[ts(export)]
pub struct ScratchTab {
    pub id: String,
    /// `None` means the tab is titled from its URL.
    pub name: Option<String>,
    pub request: HttpRequest,
    /// Set when the tab came from, or was written to, a saved request.
    pub saved_request_id: Option<String>,
    /// True when the tab differs from the saved request it is linked to.
    pub dirty: bool,
}

impl ScratchTab {
    pub fn blank() -> Self {
        Self {
            id: api_client_core::new_id(),
            name: None,
            request: HttpRequest::blank(),
            saved_request_id: None,
            dirty: false,
        }
    }
}

impl Default for ScratchTab {
    fn default() -> Self {
        Self::blank()
    }
}

/// Everything persisted, in one file.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, TS)]
#[serde(rename_all = "camelCase", default)]
#[ts(export)]
pub struct AppState {
    pub version: u32,
    pub settings: Settings,
    pub saved_requests: Vec<SavedRequest>,
    pub history: Vec<HistoryEntry>,
    pub tabs: Vec<ScratchTab>,
    pub active_tab_id: Option<String>,
    pub environments: Vec<Environment>,
    pub active_environment_id: Option<String>,
}

impl Default for AppState {
    /// A first launch: exactly one blank request, nothing else.
    fn default() -> Self {
        let tab = ScratchTab::blank();
        Self {
            version: SCHEMA_VERSION,
            settings: Settings::default(),
            saved_requests: Vec::new(),
            history: Vec::new(),
            active_tab_id: Some(tab.id.clone()),
            tabs: vec![tab],
            environments: Vec::new(),
            active_environment_id: None,
        }
    }
}

impl AppState {
    /// Prepends an entry and trims to the configured cap.
    pub fn push_history(&mut self, entry: HistoryEntry) {
        self.history.insert(0, entry);
        let max = self.settings.max_history;
        if self.history.len() > max {
            self.history.truncate(max);
        }
    }

    /// Inserts or replaces a saved request, matching on id.
    pub fn upsert_saved_request(&mut self, mut saved: SavedRequest) {
        match self
            .saved_requests
            .iter_mut()
            .find(|existing| existing.id == saved.id)
        {
            Some(existing) => {
                saved.created_at = existing.created_at;
                saved.updated_at = now_ms();
                *existing = saved;
            }
            None => self.saved_requests.push(saved),
        }
    }

    pub fn delete_saved_request(&mut self, id: &str) -> bool {
        let before = self.saved_requests.len();
        self.saved_requests.retain(|saved| saved.id != id);
        self.saved_requests.len() != before
    }

    pub fn active_variables(&self) -> api_client_variables::VariableSet {
        self.active_environment_id
            .as_ref()
            .and_then(|id| self.environments.iter().find(|env| env.id == *id))
            .map(api_client_variables::VariableSet::from)
            .unwrap_or_default()
    }

    /// Guarantees the invariant the UI relies on: at least one tab, and an
    /// `active_tab_id` that actually points at one of them.
    pub fn ensure_one_tab(&mut self) {
        if self.tabs.is_empty() {
            self.tabs.push(ScratchTab::blank());
        }
        let valid = self
            .active_tab_id
            .as_ref()
            .is_some_and(|id| self.tabs.iter().any(|tab| tab.id == *id));
        if !valid {
            self.active_tab_id = self.tabs.first().map(|tab| tab.id.clone());
        }
    }
}
