//! The same routes, served over TLS.
//!
//! Used to test trust settings and client certificates against a real
//! handshake rather than against a mock.

use std::io;
use std::sync::Arc;

use rustls::pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer};
use rustls::server::WebPkiClientVerifier;
use rustls::{RootCertStore, ServerConfig};
use tokio_rustls::TlsAcceptor;

use crate::certs::{TestCa, TestCert};

/// Whether the server asks the client to identify itself.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ClientAuth {
    /// Anyone may connect.
    None,
    /// The client must present a certificate signed by the test CA.
    Required,
}

pub(crate) fn acceptor(
    ca: &TestCa,
    server: &TestCert,
    client_auth: ClientAuth,
) -> io::Result<TlsAcceptor> {
    let chain = vec![CertificateDer::from(server.cert_der.clone())];
    let key = PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(server.key_der.clone()));

    // The provider is named rather than taken from process-global state, so the
    // tests do not depend on who called install_default() first.
    let builder = ServerConfig::builder_with_provider(Arc::new(
        rustls::crypto::aws_lc_rs::default_provider(),
    ))
    .with_safe_default_protocol_versions()
    .map_err(io::Error::other)?;

    let config = match client_auth {
        ClientAuth::None => builder.with_no_client_auth(),
        ClientAuth::Required => {
            let mut roots = RootCertStore::empty();
            roots
                .add(CertificateDer::from(ca.der().to_vec()))
                .map_err(io::Error::other)?;
            let verifier = WebPkiClientVerifier::builder(Arc::new(roots))
                .build()
                .map_err(io::Error::other)?;
            builder.with_client_cert_verifier(verifier)
        }
    };

    let config = config
        .with_single_cert(chain, key)
        .map_err(io::Error::other)?;

    Ok(TlsAcceptor::from(Arc::new(config)))
}
