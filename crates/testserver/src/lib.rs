//! A deliberately small HTTP/1.1 server for the engine tests.
//!
//! Tests must never touch the public internet, and the interesting cases here
//! (a body that stalls forever, a deliberately malformed JSON payload, a raw
//! multipart echo) are easier to produce by writing bytes than by configuring
//! a framework.

mod http;
mod routes;

use std::io;
use std::net::SocketAddr;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};

use tokio::net::TcpListener;
use tokio::sync::oneshot;

/// A running server. Dropping it shuts the listener down.
pub struct TestServer {
    addr: SocketAddr,
    shutdown: Option<oneshot::Sender<()>>,
    requests: Arc<AtomicUsize>,
}

impl TestServer {
    /// Binds an ephemeral port on loopback and starts serving.
    pub async fn start() -> io::Result<Self> {
        let listener = TcpListener::bind("127.0.0.1:0").await?;
        let addr = listener.local_addr()?;
        let (shutdown, mut shutdown_rx) = oneshot::channel();
        let requests = Arc::new(AtomicUsize::new(0));
        let counter = Arc::clone(&requests);

        tokio::spawn(async move {
            loop {
                tokio::select! {
                    _ = &mut shutdown_rx => break,
                    accepted = listener.accept() => {
                        let Ok((stream, _)) = accepted else { break };
                        let counter = Arc::clone(&counter);
                        tokio::spawn(async move {
                            counter.fetch_add(1, Ordering::Relaxed);
                            let _ = http::serve_connection(stream).await;
                        });
                    }
                }
            }
        });

        Ok(Self {
            addr,
            shutdown: Some(shutdown),
            requests,
        })
    }

    pub fn addr(&self) -> SocketAddr {
        self.addr
    }

    pub fn base_url(&self) -> String {
        format!("http://{}", self.addr)
    }

    /// `server.url("/status/404")`
    pub fn url(&self, path: &str) -> String {
        format!("http://{}{}", self.addr, path)
    }

    /// Connections accepted so far. Useful for asserting a redirect chain.
    pub fn connection_count(&self) -> usize {
        self.requests.load(Ordering::Relaxed)
    }
}

impl Drop for TestServer {
    fn drop(&mut self) {
        if let Some(shutdown) = self.shutdown.take() {
            let _ = shutdown.send(());
        }
    }
}
