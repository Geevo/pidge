//! Trust and identity.
//!
//! reqwest is built on rustls here, and rustls is configured with
//! `rustls-platform-verifier`, so out of the box the client already trusts
//! whatever the operating system trusts: the Windows certificate store, the
//! macOS keychain, or the system CA bundle on Linux. This module handles the
//! two things the OS store cannot do for us — trusting an extra CA, and
//! presenting a client certificate.

use std::fs;
use std::path::Path;

use api_client_core::{ClientIdentitySettings, RequestError, RequestErrorKind, TlsSettings};
use base64::prelude::{BASE64_STANDARD, Engine as _};
use reqwest::{Certificate, ClientBuilder, Identity};

/// Applies the trust and identity settings to a client builder.
pub fn apply(
    mut builder: ClientBuilder,
    settings: &TlsSettings,
) -> Result<ClientBuilder, RequestError> {
    if settings.accept_invalid_certs {
        builder = builder.danger_accept_invalid_certs(true);
    }

    let extra_roots = load_extra_roots(&settings.extra_ca_files)?;

    builder = match (settings.use_system_roots, extra_roots.is_empty()) {
        // The common case: whatever the OS trusts, and nothing added.
        (true, true) => builder,
        // Extra CAs are merged with the OS store, not swapped for it. A
        // corporate root should not cost you the ability to reach the internet.
        (true, false) => builder.tls_certs_merge(extra_roots),
        // Trust only what was named. Useful for talking to one internal host
        // and nothing else.
        (false, false) => builder.tls_certs_only(extra_roots),
        (false, true) => {
            return Err(RequestError::new(
                RequestErrorKind::Tls,
                "System certificates are turned off and no CA file was added, so nothing would be trusted.",
            ));
        }
    };

    if let Some(identity) = &settings.client_identity {
        builder = builder.identity(load_identity(identity)?);
    }

    Ok(builder)
}

fn load_extra_roots(paths: &[String]) -> Result<Vec<Certificate>, RequestError> {
    let mut roots = Vec::new();
    for path in paths.iter().filter(|path| !path.trim().is_empty()) {
        roots.extend(load_ca_file(path.trim())?);
    }
    Ok(roots)
}

/// Reads one CA file. PEM bundles may hold several certificates; DER holds one.
fn load_ca_file(path: &str) -> Result<Vec<Certificate>, RequestError> {
    let bytes = read_file(path, "certificate")?;

    if looks_like_pem(&bytes) {
        Certificate::from_pem_bundle(&bytes).map_err(|err| {
            RequestError::new(
                RequestErrorKind::Tls,
                format!("`{path}` is not a readable PEM certificate."),
            )
            .with_detail(crate::error::chain(&err))
        })
    } else {
        Certificate::from_der(&bytes)
            .map(|cert| vec![cert])
            .map_err(|err| {
                RequestError::new(
                    RequestErrorKind::Tls,
                    format!("`{path}` is not a readable certificate. Expected PEM or DER."),
                )
                .with_detail(crate::error::chain(&err))
            })
    }
}

/// Reads a client certificate, from PEM or from a PKCS#12 bundle.
fn load_identity(settings: &ClientIdentitySettings) -> Result<Identity, RequestError> {
    let path = settings.path.trim();
    if path.is_empty() {
        return Err(RequestError::new(
            RequestErrorKind::Tls,
            "No client certificate file was given.",
        ));
    }

    let bytes = read_file(path, "client certificate")?;

    let pem = if looks_like_pem(&bytes) {
        bytes
    } else {
        // Windows exports .pfx, and rustls only speaks PEM, so the bundle is
        // unpacked here rather than asking anyone to run openssl first.
        pkcs12_to_pem(&bytes, settings.password.as_deref().unwrap_or(""), path)?
    };

    Identity::from_pem(&pem).map_err(|err| {
        RequestError::new(
            RequestErrorKind::Tls,
            format!("`{path}` did not contain a usable certificate and private key."),
        )
        .with_detail(crate::error::chain(&err))
    })
}

/// Unpacks a PKCS#12 bundle into the PEM that rustls wants.
///
/// Done in process, deliberately: shelling out to `openssl` would add a
/// dependency that Windows does not ship, and would put the password in the
/// process list where any other user could read it.
fn pkcs12_to_pem(bytes: &[u8], password: &str, path: &str) -> Result<Vec<u8>, RequestError> {
    use p12_keystore::{KeyStore, Pkcs12ImportPolicy};

    let store = KeyStore::from_pkcs12(bytes, password, Pkcs12ImportPolicy::Strict).map_err(|err| {
        let detail = err.to_string();
        // A wrong password and a corrupt file fail in the same place, and the
        // first is overwhelmingly more likely, so say so.
        RequestError::new(
            RequestErrorKind::Tls,
            format!(
                "Could not open `{path}`. Check the password — a PKCS#12 bundle is always encrypted."
            ),
        )
        .with_detail(detail)
    })?;

    let (_, chain) = store.private_key_chain().ok_or_else(|| {
        RequestError::new(
            RequestErrorKind::Tls,
            format!("`{path}` has no private key, so it cannot be used as a client certificate."),
        )
    })?;

    let mut pem = pem_block("PRIVATE KEY", chain.key().as_der());
    for cert in chain.certs() {
        pem.push_str(&pem_block("CERTIFICATE", cert.as_der()));
    }
    Ok(pem.into_bytes())
}

/// DER to PEM. Base64 at 64 columns between the usual markers.
fn pem_block(label: &str, der: &[u8]) -> String {
    let encoded = BASE64_STANDARD.encode(der);
    let mut out = format!("-----BEGIN {label}-----\n");
    for line in encoded.as_bytes().chunks(64) {
        out.push_str(std::str::from_utf8(line).unwrap_or_default());
        out.push('\n');
    }
    out.push_str(&format!("-----END {label}-----\n"));
    out
}

fn looks_like_pem(bytes: &[u8]) -> bool {
    // PEM is text and always carries a header; PKCS#12 and DER start with a
    // DER sequence tag (0x30) and are not valid UTF-8 in general.
    bytes
        .windows(11)
        .take(4096)
        .any(|window| window == b"-----BEGIN ")
}

fn read_file(path: &str, what: &str) -> Result<Vec<u8>, RequestError> {
    fs::read(Path::new(path)).map_err(|err| {
        RequestError::new(
            RequestErrorKind::Io,
            format!("Could not read the {what} at `{path}`."),
        )
        .with_detail(err.to_string())
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn recognises_pem_by_its_header() {
        assert!(looks_like_pem(b"-----BEGIN CERTIFICATE-----\nabc\n"));
        assert!(looks_like_pem(
            b"# a comment first\n-----BEGIN PRIVATE KEY-----\n"
        ));
        assert!(!looks_like_pem(&[0x30, 0x82, 0x0a, 0x00]));
        assert!(!looks_like_pem(b""));
    }

    #[test]
    fn wraps_der_into_pem_at_64_columns() {
        let pem = pem_block("CERTIFICATE", &[0u8; 100]);
        let lines: Vec<_> = pem.lines().collect();

        assert_eq!(lines[0], "-----BEGIN CERTIFICATE-----");
        assert_eq!(lines[lines.len() - 1], "-----END CERTIFICATE-----");
        assert!(
            lines[1..lines.len() - 1]
                .iter()
                .all(|line| line.len() <= 64)
        );
        assert_eq!(
            BASE64_STANDARD
                .decode(lines[1..lines.len() - 1].concat())
                .unwrap(),
            vec![0u8; 100]
        );
    }

    #[test]
    fn trusting_nothing_at_all_is_refused() {
        let settings = TlsSettings {
            use_system_roots: false,
            ..TlsSettings::default()
        };
        let error = apply(reqwest::Client::builder(), &settings).unwrap_err();

        assert_eq!(error.kind, RequestErrorKind::Tls);
        assert!(error.message.contains("nothing would be trusted"));
    }

    #[test]
    fn a_missing_ca_file_names_the_path() {
        let settings = TlsSettings {
            extra_ca_files: vec!["/definitely/not/here.pem".into()],
            ..TlsSettings::default()
        };
        let error = apply(reqwest::Client::builder(), &settings).unwrap_err();

        assert_eq!(error.kind, RequestErrorKind::Io);
        assert!(error.message.contains("/definitely/not/here.pem"));
    }

    #[test]
    fn blank_paths_are_ignored_rather_than_failing() {
        let settings = TlsSettings {
            extra_ca_files: vec!["   ".into(), String::new()],
            ..TlsSettings::default()
        };
        assert!(apply(reqwest::Client::builder(), &settings).is_ok());
    }
}
