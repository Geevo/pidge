//! Throwaway certificates, generated per test run.
//!
//! Nothing is committed and nothing is read from the machine's own trust store,
//! so the TLS tests prove that our settings did the work rather than that the
//! host happened to be configured a certain way.

use p12_keystore::{EncryptionAlgorithm, KeyStore, KeyStoreEntry, PrivateKey, PrivateKeyChain};
use rcgen::{
    BasicConstraints, CertificateParams, DnType, IsCa, Issuer, KeyPair, KeyUsagePurpose, SanType,
};

/// A certificate authority that can sign server and client certificates.
pub struct TestCa {
    issuer: Issuer<'static, KeyPair>,
    pem: String,
    der: Vec<u8>,
}

/// A signed leaf certificate and its key.
pub struct TestCert {
    pub cert_pem: String,
    pub key_pem: String,
    pub cert_der: Vec<u8>,
    pub key_der: Vec<u8>,
}

impl TestCa {
    pub fn new() -> Self {
        let mut params = CertificateParams::new(Vec::new()).expect("ca params");
        params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
        params.key_usages = vec![
            KeyUsagePurpose::KeyCertSign,
            KeyUsagePurpose::CrlSign,
            KeyUsagePurpose::DigitalSignature,
        ];
        params
            .distinguished_name
            .push(DnType::CommonName, "api-client test CA");

        let key = KeyPair::generate().expect("ca key");
        let cert = params.self_signed(&key).expect("ca cert");

        Self {
            pem: cert.pem(),
            der: cert.der().to_vec(),
            issuer: Issuer::new(params, key),
        }
    }

    /// The CA certificate as PEM, for pointing `extraCaFiles` at.
    pub fn pem(&self) -> &str {
        &self.pem
    }

    pub fn der(&self) -> &[u8] {
        &self.der
    }

    /// Signs a certificate valid for `127.0.0.1` and `localhost`.
    pub fn issue_server(&self) -> TestCert {
        let mut params = CertificateParams::new(Vec::new()).expect("server params");
        params.subject_alt_names = vec![
            SanType::IpAddress(std::net::IpAddr::from([127, 0, 0, 1])),
            SanType::DnsName("localhost".try_into().expect("dns name")),
        ];
        params
            .distinguished_name
            .push(DnType::CommonName, "localhost");
        self.issue(params)
    }

    /// Signs a certificate for a client to present.
    pub fn issue_client(&self, common_name: &str) -> TestCert {
        let mut params = CertificateParams::new(Vec::new()).expect("client params");
        params
            .distinguished_name
            .push(DnType::CommonName, common_name);
        self.issue(params)
    }

    fn issue(&self, params: CertificateParams) -> TestCert {
        let key = KeyPair::generate().expect("leaf key");
        let cert = params.signed_by(&key, &self.issuer).expect("leaf cert");

        TestCert {
            cert_pem: cert.pem(),
            key_pem: key.serialize_pem(),
            cert_der: cert.der().to_vec(),
            key_der: key.serialize_der(),
        }
    }
}

impl Default for TestCa {
    fn default() -> Self {
        Self::new()
    }
}

impl TestCert {
    /// Certificate and key in one PEM, the shape `Identity::from_pem` wants.
    pub fn identity_pem(&self) -> String {
        format!("{}{}", self.key_pem, self.cert_pem)
    }

    /// The same material as an encrypted PKCS#12 bundle, the way Windows
    /// exports a client certificate.
    pub fn to_pkcs12(&self, password: &str, ca: &TestCa) -> Vec<u8> {
        let key = PrivateKey::from_der(&self.key_der).expect("pkcs8 key");
        let leaf = p12_keystore::Certificate::from_der(&self.cert_der).expect("leaf der");
        let root = p12_keystore::Certificate::from_der(ca.der()).expect("ca der");

        let chain = PrivateKeyChain::new(vec![1, 2, 3, 4], key, vec![leaf, root]);
        let mut store = KeyStore::new();
        store.add_entry("client", KeyStoreEntry::PrivateKeyChain(chain));

        store
            .writer(password)
            .encryption_algorithm(EncryptionAlgorithm::PbeWithHmacSha256AndAes256)
            .write()
            .expect("pkcs12")
    }
}
