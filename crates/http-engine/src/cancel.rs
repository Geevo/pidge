use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use tokio_util::sync::CancellationToken;

/// Handed to `execute`; resolving it aborts the request wherever it has got to.
#[derive(Debug, Clone, Default)]
pub struct CancellationHandle {
    token: CancellationToken,
}

impl CancellationHandle {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn cancel(&self) {
        self.token.cancel();
    }

    pub(crate) async fn cancelled(&self) {
        self.token.cancelled().await;
    }
}

/// Tracks in-flight requests by id so a platform adapter can implement
/// `cancel_http_request(id)` without inventing its own bookkeeping.
#[derive(Debug, Clone, Default)]
pub struct CancellationRegistry {
    inner: Arc<Mutex<HashMap<String, CancellationHandle>>>,
}

impl CancellationRegistry {
    pub fn new() -> Self {
        Self::default()
    }

    /// Registers `id` and returns its handle. A repeated id replaces the old
    /// entry, which is what you want when a tab re-sends.
    pub fn register(&self, id: impl Into<String>) -> CancellationHandle {
        let handle = CancellationHandle::new();
        self.lock().insert(id.into(), handle.clone());
        handle
    }

    /// Cancels `id` if it is still running. Returns whether anything was cancelled.
    pub fn cancel(&self, id: &str) -> bool {
        match self.lock().remove(id) {
            Some(handle) => {
                handle.cancel();
                true
            }
            None => false,
        }
    }

    pub fn finish(&self, id: &str) {
        self.lock().remove(id);
    }

    pub fn in_flight(&self) -> usize {
        self.lock().len()
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, HashMap<String, CancellationHandle>> {
        // The map holds no user data and nothing panics while it is held.
        self.inner.lock().unwrap_or_else(|err| err.into_inner())
    }
}
