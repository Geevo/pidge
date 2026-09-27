//! What must never reach a log file, and what an export must not give away.

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

/// Every secret in `auth`, by the name of its field, for whatever has to hide
/// them: the state file encrypts them, an export can replace them.
///
/// Exhaustive on purpose: a new kind of auth has to decide what in it is secret.
/// Identifiers — usernames, client ids, OAuth 1 tokens — are not.
pub fn auth_secrets_mut(auth: &mut crate::AuthConfig) -> Vec<(&'static str, &mut String)> {
    use crate::AuthConfig;

    match auth {
        AuthConfig::None => Vec::new(),
        AuthConfig::Bearer { token } => vec![("token", token)],
        AuthConfig::Basic { password, .. }
        | AuthConfig::Digest { password, .. }
        | AuthConfig::Ntlm { password, .. } => vec![("password", password)],
        AuthConfig::OAuth1(settings) => vec![
            ("consumerSecret", &mut settings.consumer_secret),
            ("tokenSecret", &mut settings.token_secret),
        ],
        AuthConfig::OAuth2(settings) => vec![
            ("clientSecret", &mut settings.client_secret),
            ("password", &mut settings.password),
            ("refreshToken", &mut settings.refresh_token),
        ],
        AuthConfig::ApiKey { value, .. } => vec![("value", value)],
    }
}

/// The scheme's name as the state file spells it, e.g. `oauth2`.
pub fn auth_kind(auth: &crate::AuthConfig) -> &'static str {
    use crate::AuthConfig;

    match auth {
        AuthConfig::None => "none",
        AuthConfig::Bearer { .. } => "bearer",
        AuthConfig::Basic { .. } => "basic",
        AuthConfig::Digest { .. } => "digest",
        AuthConfig::Ntlm { .. } => "ntlm",
        AuthConfig::OAuth1(_) => "oauth1",
        AuthConfig::OAuth2(_) => "oauth2",
        AuthConfig::ApiKey { .. } => "apiKey",
    }
}
