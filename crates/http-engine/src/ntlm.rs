//! NTLM over HTTP: the three messages of MS-NLMP, carried by MS-NTHT.
//!
//! The handshake is: a 401 offering NTLM, then a negotiate message, then the
//! server's challenge, then an authenticate message computed from it. NTLMv2
//! only — v1 has been a liability for two decades and no server that refuses
//! v2 should be humoured by a new client.
//!
//! The arithmetic is pinned to the worked example in MS-NLMP §4.2.4, because a
//! server that dislikes the response says only 401, which tells you nothing
//! about which step was wrong.

use base64::Engine as _;
use hmac::{Hmac, Mac};
use md4::Md4;
use md5::{Digest, Md5};

const SIGNATURE: &[u8; 8] = b"NTLMSSP\0";

const NEGOTIATE_UNICODE: u32 = 0x0000_0001;
const REQUEST_TARGET: u32 = 0x0000_0004;
const NEGOTIATE_NTLM: u32 = 0x0000_0200;
const NEGOTIATE_ALWAYS_SIGN: u32 = 0x0000_8000;
const NEGOTIATE_EXTENDED_SESSIONSECURITY: u32 = 0x0008_0000;
const NEGOTIATE_128: u32 = 0x2000_0000;
const NEGOTIATE_56: u32 = 0x8000_0000;

/// What the client offers. Unicode throughout: OEM encoding is a museum piece.
const CLIENT_FLAGS: u32 = NEGOTIATE_UNICODE
    | REQUEST_TARGET
    | NEGOTIATE_NTLM
    | NEGOTIATE_ALWAYS_SIGN
    | NEGOTIATE_EXTENDED_SESSIONSECURITY
    | NEGOTIATE_128
    | NEGOTIATE_56;

/// The server's half of the handshake, as far as the client needs it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct Challenge {
    pub(crate) server_challenge: [u8; 8],
    /// The AV pair blob, passed back inside the response untouched.
    pub(crate) target_info: Vec<u8>,
    pub(crate) flags: u32,
}

/// `NTLM <base64>` for the first leg: what the client can do, and nothing else.
pub(crate) fn negotiate_header(domain: &str, workstation: &str) -> String {
    let domain = domain.to_ascii_uppercase().into_bytes();
    let workstation = workstation.to_ascii_uppercase().into_bytes();

    let mut message = Vec::with_capacity(40 + domain.len() + workstation.len());
    message.extend_from_slice(SIGNATURE);
    message.extend_from_slice(&1u32.to_le_bytes());
    message.extend_from_slice(&CLIENT_FLAGS.to_le_bytes());

    // The payload sits after the fixed 32-byte header, domain first.
    let domain_offset = 32u32;
    let workstation_offset = domain_offset + domain.len() as u32;
    push_field(&mut message, domain.len(), domain_offset);
    push_field(&mut message, workstation.len(), workstation_offset);
    message.extend_from_slice(&domain);
    message.extend_from_slice(&workstation);

    format!(
        "NTLM {}",
        base64::engine::general_purpose::STANDARD.encode(message)
    )
}

/// Reads the challenge out of a `WWW-Authenticate: NTLM <base64>` header.
pub(crate) fn parse_challenge(header: &str) -> Result<Challenge, String> {
    let encoded = header
        .split(',')
        .map(str::trim)
        .find_map(|scheme| {
            scheme
                .strip_prefix("NTLM ")
                .or(scheme.strip_prefix("ntlm "))
        })
        .ok_or_else(|| "The server's NTLM challenge carried no message.".to_string())?;

    let message = base64::engine::general_purpose::STANDARD
        .decode(encoded.trim())
        .map_err(|_| "The server's NTLM challenge was not valid base64.".to_string())?;

    // Signature, type, target name fields, flags, challenge, reserved, target
    // info fields: 48 bytes before any payload.
    if message.len() < 48 || &message[0..8] != SIGNATURE {
        return Err("The server's NTLM challenge was malformed.".to_string());
    }
    if read_u32(&message, 8) != 2 {
        return Err("The server sent the wrong kind of NTLM message.".to_string());
    }

    let flags = read_u32(&message, 20);
    let mut server_challenge = [0u8; 8];
    server_challenge.copy_from_slice(&message[24..32]);

    let info_len = read_u16(&message, 40) as usize;
    let info_offset = read_u32(&message, 44) as usize;
    let target_info = match message.get(info_offset..info_offset + info_len) {
        Some(slice) => slice.to_vec(),
        // A challenge without target info is legal; NTLMv2 just sends none back.
        None => Vec::new(),
    };

    Ok(Challenge {
        server_challenge,
        target_info,
        flags,
    })
}

/// `NTLM <base64>` for the third leg: the response the server checks.
pub(crate) fn authenticate_header(
    challenge: &Challenge,
    username: &str,
    password: &str,
    domain: &str,
    workstation: &str,
    client_challenge: [u8; 8],
    timestamp: u64,
) -> String {
    let nt_response = ntlmv2_response(
        username,
        password,
        domain,
        &challenge.server_challenge,
        &challenge.target_info,
        client_challenge,
        timestamp,
    );

    // The LM slot: HMAC over both challenges, then the client's own. Servers
    // ignore it under NTLMv2, but a missing field is a malformed message.
    let mut lm_input = Vec::with_capacity(16);
    lm_input.extend_from_slice(&challenge.server_challenge);
    lm_input.extend_from_slice(&client_challenge);
    let mut lm_response = hmac_md5(&ntowf_v2(username, password, domain), &lm_input);
    lm_response.extend_from_slice(&client_challenge);

    let domain_bytes = utf16le(domain);
    let user_bytes = utf16le(username);
    let workstation_bytes = utf16le(workstation);

    // Fixed header: signature, type, six field descriptors, flags.
    let header_len = 64u32;
    let mut offset = header_len;
    let mut message = Vec::new();
    message.extend_from_slice(SIGNATURE);
    message.extend_from_slice(&3u32.to_le_bytes());

    for part in [
        &lm_response,
        &nt_response,
        &domain_bytes,
        &user_bytes,
        &workstation_bytes,
    ] {
        push_field(&mut message, part.len(), offset);
        offset += part.len() as u32;
    }
    // No session key is exchanged: nothing here signs or seals anything.
    push_field(&mut message, 0, offset);
    message.extend_from_slice(&(CLIENT_FLAGS & challenge.flags | NEGOTIATE_UNICODE).to_le_bytes());

    for part in [
        &lm_response,
        &nt_response,
        &domain_bytes,
        &user_bytes,
        &workstation_bytes,
    ] {
        message.extend_from_slice(part);
    }

    format!(
        "NTLM {}",
        base64::engine::general_purpose::STANDARD.encode(message)
    )
}

/// NTOWFv2: the key everything else is derived from.
fn ntowf_v2(username: &str, password: &str, domain: &str) -> Vec<u8> {
    let nt_hash = Md4::digest(utf16le(password));
    let identity = utf16le(&format!("{}{}", username.to_uppercase(), domain));
    hmac_md5(&nt_hash, &identity)
}

/// The NT response: a proof string followed by the blob it was computed over.
fn ntlmv2_response(
    username: &str,
    password: &str,
    domain: &str,
    server_challenge: &[u8; 8],
    target_info: &[u8],
    client_challenge: [u8; 8],
    timestamp: u64,
) -> Vec<u8> {
    let key = ntowf_v2(username, password, domain);

    let mut blob = Vec::with_capacity(32 + target_info.len());
    blob.extend_from_slice(&[0x01, 0x01, 0x00, 0x00]); // Responserversion, HiResponserversion
    blob.extend_from_slice(&[0x00; 4]); // Reserved
    blob.extend_from_slice(&timestamp.to_le_bytes());
    blob.extend_from_slice(&client_challenge);
    blob.extend_from_slice(&[0x00; 4]); // Reserved
    blob.extend_from_slice(target_info);
    blob.extend_from_slice(&[0x00; 4]); // Reserved

    let mut proof_input = Vec::with_capacity(8 + blob.len());
    proof_input.extend_from_slice(server_challenge);
    proof_input.extend_from_slice(&blob);

    let mut response = hmac_md5(&key, &proof_input);
    response.extend_from_slice(&blob);
    response
}

fn hmac_md5(key: &[u8], data: &[u8]) -> Vec<u8> {
    let mut mac = Hmac::<Md5>::new_from_slice(key).expect("HMAC takes a key of any length");
    mac.update(data);
    mac.finalize().into_bytes().to_vec()
}

fn utf16le(value: &str) -> Vec<u8> {
    value.encode_utf16().flat_map(u16::to_le_bytes).collect()
}

/// A length/maximum/offset triple, which is how every variable field is
/// described in these messages.
fn push_field(message: &mut Vec<u8>, len: usize, offset: u32) {
    let len = len as u16;
    message.extend_from_slice(&len.to_le_bytes());
    message.extend_from_slice(&len.to_le_bytes());
    message.extend_from_slice(&offset.to_le_bytes());
}

fn read_u16(bytes: &[u8], at: usize) -> u16 {
    u16::from_le_bytes([bytes[at], bytes[at + 1]])
}

fn read_u32(bytes: &[u8], at: usize) -> u32 {
    u32::from_le_bytes([bytes[at], bytes[at + 1], bytes[at + 2], bytes[at + 3]])
}

/// A fresh challenge per exchange, from the same source the TLS stack uses.
pub(crate) fn client_challenge() -> [u8; 8] {
    let mut bytes = [0u8; 8];
    // Failing to get eight random bytes is not a reason to fall back to
    // something predictable, so a failure here is a panic rather than a guess.
    getrandom::getrandom(&mut bytes).expect("the system random source");
    bytes
}

/// Windows FILETIME: 100-nanosecond ticks since 1601.
pub(crate) fn timestamp() -> u64 {
    const TICKS_TO_UNIX_EPOCH: u64 = 116_444_736_000_000_000;

    let since_epoch = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|elapsed| elapsed.as_nanos() / 100)
        .unwrap_or_default() as u64;
    since_epoch + TICKS_TO_UNIX_EPOCH
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hex(bytes: &[u8]) -> String {
        bytes.iter().map(|byte| format!("{byte:02x}")).collect()
    }

    /// MS-NLMP §4.2.4.1.1: User / Domain / Password gives this key. Everything
    /// else is derived from it, so a wrong answer starts here.
    #[test]
    fn derives_the_key_from_the_specs_example() {
        assert_eq!(
            hex(&ntowf_v2("User", "Password", "Domain")),
            "0c868a403bfd7a93a3001ef22ef02e3f"
        );
    }

    /// The same example, upper-cased: the user name is upper-cased before
    /// hashing and the domain is not, which is easy to get backwards.
    #[test]
    fn upper_cases_the_user_but_not_the_domain() {
        let mixed = ntowf_v2("user", "Password", "Domain");
        let upper = ntowf_v2("USER", "Password", "Domain");
        assert_eq!(hex(&mixed), hex(&upper));

        let other_domain = ntowf_v2("User", "Password", "DOMAIN");
        assert_ne!(hex(&other_domain), hex(&upper));
    }

    #[test]
    fn writes_a_negotiate_message_the_server_can_read() {
        let header = negotiate_header("domain", "workstation");
        let message = base64::engine::general_purpose::STANDARD
            .decode(header.strip_prefix("NTLM ").expect("scheme"))
            .expect("base64");

        assert_eq!(&message[0..8], SIGNATURE);
        assert_eq!(read_u32(&message, 8), 1);
        assert_eq!(
            read_u32(&message, 12) & NEGOTIATE_UNICODE,
            NEGOTIATE_UNICODE
        );

        // The domain is upper-cased and where the field says it is.
        let len = read_u16(&message, 16) as usize;
        let offset = read_u32(&message, 20) as usize;
        assert_eq!(&message[offset..offset + len], b"DOMAIN");
    }

    #[test]
    fn reads_a_challenge_back_out_of_a_header() {
        // A minimal type 2: header, challenge at 24, target info at 48.
        let mut message = Vec::new();
        message.extend_from_slice(SIGNATURE);
        message.extend_from_slice(&2u32.to_le_bytes());
        push_field(&mut message, 0, 48); // target name
        message.extend_from_slice(&NEGOTIATE_UNICODE.to_le_bytes());
        message.extend_from_slice(&[1, 2, 3, 4, 5, 6, 7, 8]); // server challenge
        message.extend_from_slice(&[0u8; 8]); // reserved
        push_field(&mut message, 4, 48); // target info
        message.extend_from_slice(&[0xaa, 0xbb, 0xcc, 0xdd]);

        let encoded = base64::engine::general_purpose::STANDARD.encode(&message);
        let challenge = parse_challenge(&format!("NTLM {encoded}")).expect("challenge");

        assert_eq!(challenge.server_challenge, [1, 2, 3, 4, 5, 6, 7, 8]);
        assert_eq!(challenge.target_info, vec![0xaa, 0xbb, 0xcc, 0xdd]);
    }

    #[test]
    fn finds_ntlm_among_the_schemes_a_server_offers() {
        let mut message = Vec::new();
        message.extend_from_slice(SIGNATURE);
        message.extend_from_slice(&2u32.to_le_bytes());
        message.extend_from_slice(&[0u8; 40]);
        let encoded = base64::engine::general_purpose::STANDARD.encode(&message);

        let header = format!("Negotiate, NTLM {encoded}, Basic realm=\"x\"");
        assert!(parse_challenge(&header).is_ok());
    }

    #[test]
    fn refuses_a_challenge_that_is_not_one() {
        assert!(parse_challenge("NTLM").is_err());
        assert!(parse_challenge("Negotiate abcdef").is_err());
        assert!(parse_challenge("NTLM not-base64!!").is_err());
    }

    /// The response carries the proof string and then the blob it was computed
    /// over, so the server can recompute it without the client's state.
    #[test]
    fn packs_the_proof_and_the_blob_together() {
        let target_info = vec![0x02, 0x00, 0x00, 0x00];
        let response = ntlmv2_response(
            "User",
            "Password",
            "Domain",
            &[0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef],
            &target_info,
            [0xaa; 8],
            0,
        );

        assert_eq!(response.len(), 16 + 32 + target_info.len());
        assert_eq!(&response[16..20], &[0x01, 0x01, 0x00, 0x00]);
        assert_eq!(&response[24..32], &[0u8; 8], "the timestamp was not zero");
        assert_eq!(&response[32..40], &[0xaa; 8]);
    }
}
