//! Turning the certificate the server presented into something readable.
//!
//! reqwest hands back the leaf certificate as DER and nothing else, so this is
//! deliberately about that one certificate rather than the chain: what it is
//! for, who signed it, when it stops being valid, and its fingerprint.

use std::time::{SystemTime, UNIX_EPOCH};

use api_client_core::{PeerCertificate, TlsDetails};
use sha2::{Digest, Sha256};
use x509_parser::prelude::*;

/// Describes a connection from reqwest's TLS extension.
pub(crate) fn describe(info: &reqwest::tls::TlsInfo) -> TlsDetails {
    TlsDetails {
        protocol: info.version().map(protocol_name),
        certificate: info.peer_certificate().and_then(parse),
    }
}

/// reqwest's `Version` is opaque and its `Debug` prints `Version(Tls1_3)`,
/// which is not a thing to show anyone. Compared rather than matched, because
/// the constants are associated values rather than variants.
fn protocol_name(version: reqwest::tls::Version) -> String {
    use reqwest::tls::Version;

    if version == Version::TLS_1_3 {
        "TLS 1.3".to_string()
    } else if version == Version::TLS_1_2 {
        "TLS 1.2".to_string()
    } else if version == Version::TLS_1_1 {
        "TLS 1.1".to_string()
    } else if version == Version::TLS_1_0 {
        "TLS 1.0".to_string()
    } else {
        format!("{version:?}")
    }
}

/// `None` when the certificate will not parse. The TLS layer has already
/// accepted the connection at this point, so a parse failure is a gap in what
/// we can show, not a reason to fail the response.
fn parse(der: &[u8]) -> Option<PeerCertificate> {
    let (_, cert) = X509Certificate::from_der(der).ok()?;

    let subject = cert.subject().to_string();
    let issuer = cert.issuer().to_string();

    Some(PeerCertificate {
        self_signed: subject == issuer,
        subject,
        issuer,
        subject_alt_names: alt_names(&cert),
        not_before: rfc3339(cert.validity().not_before.timestamp()),
        not_after: rfc3339(cert.validity().not_after.timestamp()),
        serial: fingerprint_style(cert.raw_serial()),
        signature_algorithm: signature_algorithm(&cert),
        sha256_fingerprint: fingerprint_style(&Sha256::digest(der)),
        expired: cert.validity().not_after.timestamp() < now_seconds(),
    })
}

fn alt_names(cert: &X509Certificate<'_>) -> Vec<String> {
    let Ok(Some(extension)) = cert.subject_alternative_name() else {
        return Vec::new();
    };

    extension
        .value
        .general_names
        .iter()
        .map(|name| match name {
            GeneralName::DNSName(value) => (*value).to_string(),
            GeneralName::IPAddress(bytes) => ip_address(bytes),
            GeneralName::RFC822Name(value) => (*value).to_string(),
            GeneralName::URI(value) => (*value).to_string(),
            other => other.to_string(),
        })
        .collect()
}

/// The OID's name when x509-parser knows it, and the OID itself when it does
/// not — which is more use than "unknown".
fn signature_algorithm(cert: &X509Certificate<'_>) -> String {
    let oid = &cert.signature_algorithm.algorithm;
    oid_registry::format_oid(oid, &oid_registry::OidRegistry::default().with_all_crypto())
}

fn ip_address(bytes: &[u8]) -> String {
    match bytes.len() {
        4 => {
            let octets: [u8; 4] = bytes.try_into().expect("four bytes");
            std::net::Ipv4Addr::from(octets).to_string()
        }
        16 => {
            let octets: [u8; 16] = bytes.try_into().expect("sixteen bytes");
            std::net::Ipv6Addr::from(octets).to_string()
        }
        _ => fingerprint_style(bytes),
    }
}

/// `AB:CD:EF…`, the way openssl and every browser print a fingerprint.
fn fingerprint_style(bytes: &[u8]) -> String {
    bytes
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect::<Vec<_>>()
        .join(":")
}

/// Seconds to RFC 3339, without pulling in a date library for two fields.
fn rfc3339(seconds: i64) -> String {
    const DAYS_IN_MONTH: [i64; 12] = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    let days = seconds.div_euclid(86_400);
    let time_of_day = seconds.rem_euclid(86_400);
    let (hour, minute, second) = (
        time_of_day / 3600,
        (time_of_day % 3600) / 60,
        time_of_day % 60,
    );

    let mut year = 1970;
    let mut remaining = days;
    loop {
        let length = if is_leap(year) { 366 } else { 365 };
        if remaining < length {
            break;
        }
        remaining -= length;
        year += 1;
    }

    let mut month = 1;
    for (index, length) in DAYS_IN_MONTH.iter().enumerate() {
        let length = length + i64::from(index == 1 && is_leap(year));
        if remaining < length {
            break;
        }
        remaining -= length;
        month += 1;
    }

    format!(
        "{year:04}-{month:02}-{day:02}T{hour:02}:{minute:02}:{second:02}Z",
        day = remaining + 1
    )
}

fn is_leap(year: i64) -> bool {
    (year % 4 == 0 && year % 100 != 0) || year % 400 == 0
}

fn now_seconds() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|elapsed| elapsed.as_secs() as i64)
        .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn formats_seconds_as_rfc3339() {
        assert_eq!(rfc3339(0), "1970-01-01T00:00:00Z");
        assert_eq!(rfc3339(1_700_000_000), "2023-11-14T22:13:20Z");
        // A leap day, which the month table only gets right by adding to it.
        assert_eq!(rfc3339(1_709_164_800), "2024-02-29T00:00:00Z");
        assert_eq!(rfc3339(4_102_444_800), "2100-01-01T00:00:00Z");
    }

    #[test]
    fn prints_bytes_the_way_openssl_does() {
        assert_eq!(fingerprint_style(&[0x0a, 0xff, 0x10]), "0A:FF:10");
        assert_eq!(fingerprint_style(&[]), "");
    }

    #[test]
    fn reads_both_address_families() {
        assert_eq!(ip_address(&[127, 0, 0, 1]), "127.0.0.1");
        assert_eq!(ip_address(&[0; 16]), "::");
    }
}
