//! Header names whose values must never reach a log file.

const SECRET_HEADERS: &[&str] = &[
    "authorization",
    "proxy-authorization",
    "cookie",
    "set-cookie",
    "x-api-key",
    "api-key",
    "x-auth-token",
    "x-amz-security-token",
    "x-csrf-token",
];

pub fn is_secret_header(name: &str) -> bool {
    let name = name.trim().to_ascii_lowercase();
    SECRET_HEADERS.contains(&name.as_str())
}

/// Returns the value, or a placeholder if the header is a secret one.
pub fn header_value_for_log<'a>(name: &str, value: &'a str) -> &'a str {
    if is_secret_header(name) {
        "<redacted>"
    } else {
        value
    }
}
