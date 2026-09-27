//! Saved requests written out to a file, for sharing or moving elsewhere.
//!
//! Unless asked otherwise, every secret is replaced by a `{{variable}}` named
//! for it, so the file can be pasted into a chat or committed. The app and
//! both `.http` clients substitute those the same way, so the person it goes
//! to fills in their own and nothing else changes. What counts as a secret is
//! the same list the state file encrypts.

use api_client_codegen::ExportFormat;
use api_client_core::HttpRequest;
use api_client_core::redact::{auth_secrets_mut, is_secret_header};
use api_client_storage::SavedRequest;
use serde::Serialize;

/// The JSON form, labelled so a file can say what it is.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ExportFile<'a> {
    kind: &'static str,
    version: u32,
    saved_requests: &'a [SavedRequest],
}

pub fn export(saved: &[SavedRequest], format: ExportFormat, include_secrets: bool) -> String {
    let mut saved = saved.to_vec();
    if !include_secrets {
        for entry in &mut saved {
            replace_secrets(&mut entry.request);
        }
    }

    match format {
        ExportFormat::Json => {
            let file = ExportFile {
                kind: "api-client/saved-requests",
                version: 1,
                saved_requests: &saved,
            };
            let mut json = serde_json::to_string_pretty(&file)
                .expect("saved requests always serialise; they came from a JSON file");
            json.push('\n');
            json
        }
        ExportFormat::Http => api_client_codegen::http_file(
            saved
                .iter()
                .map(|entry| (entry.name.as_str(), &entry.request)),
        ),
    }
}

fn replace_secrets(request: &mut HttpRequest) {
    for (field, value) in auth_secrets_mut(&mut request.auth) {
        // An API key's field is called `value`, which says nothing.
        let name = if field == "value" { "apiKey" } else { field };
        hide(value, name);
    }
    for header in &mut request.headers {
        if is_secret_header(&header.name) {
            let name = camel_case(&header.name);
            hide(&mut header.value, &name);
        }
    }
}

/// Leaves alone a value that is already a variable, such as `{{token}}` or
/// `Bearer {{token}}`: the secret is somewhere else, and the reference is
/// what the person receiving it needs.
fn hide(value: &mut String, name: &str) {
    if value.is_empty() || only_references(value) {
        return;
    }
    *value = format!("{{{{{name}}}}}");
}

fn only_references(value: &str) -> bool {
    let mut rest = value;
    let mut outside = String::new();
    let mut found = false;
    while let Some(start) = rest.find("{{") {
        let Some(length) = rest[start..].find("}}") else {
            break;
        };
        outside.push_str(&rest[..start]);
        rest = &rest[start + length + 2..];
        found = true;
    }
    outside.push_str(rest);

    // An auth scheme in front of the variable gives nothing away.
    let outside = outside.trim();
    found
        && (outside.is_empty()
            || ["bearer", "basic", "token", "digest", "bot"]
                .iter()
                .any(|scheme| outside.eq_ignore_ascii_case(scheme)))
}

/// `X-API-Key` becomes `xApiKey`, a name a variable can have.
fn camel_case(name: &str) -> String {
    let mut out = String::new();
    for word in name
        .split(|c: char| !c.is_ascii_alphanumeric())
        .filter(|word| !word.is_empty())
    {
        let word = word.to_ascii_lowercase();
        if out.is_empty() {
            out.push_str(&word);
        } else {
            let mut chars = word.chars();
            if let Some(first) = chars.next() {
                out.push(first.to_ascii_uppercase());
                out.extend(chars);
            }
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use api_client_core::{AuthConfig, KeyValueEntry, OAuth2Settings};

    use super::*;

    fn saved_with(auth: AuthConfig, headers: Vec<KeyValueEntry>) -> SavedRequest {
        let mut request = HttpRequest::get("https://api.example.com/users");
        request.auth = auth;
        request.headers = headers;
        SavedRequest::new("Users", request)
    }

    #[test]
    fn secrets_become_variables_named_for_them() {
        let saved = saved_with(
            AuthConfig::OAuth2(OAuth2Settings {
                client_id: "my-client".into(),
                client_secret: "shh".into(),
                refresh_token: "rt-1".into(),
                ..OAuth2Settings::default()
            }),
            vec![
                KeyValueEntry::new("X-API-Key", "k-123"),
                KeyValueEntry::new("Accept", "application/json"),
            ],
        );

        let json = export(&[saved], ExportFormat::Json, false);
        for secret in ["shh", "rt-1", "k-123"] {
            assert!(!json.contains(secret), "{secret} was exported");
        }
        for kept in [
            "{{clientSecret}}",
            "{{refreshToken}}",
            "{{xApiKey}}",
            "my-client",
            "application/json",
        ] {
            assert!(json.contains(kept), "{kept} is missing");
        }
    }

    #[test]
    fn asked_to_include_them_it_does() {
        let saved = saved_with(
            AuthConfig::Bearer {
                token: "tok-1".into(),
            },
            vec![],
        );
        assert!(export(&[saved], ExportFormat::Http, true).contains("Authorization: Bearer tok-1"));
    }

    #[test]
    fn a_value_that_is_already_a_variable_is_left_alone() {
        let saved = saved_with(
            AuthConfig::Bearer {
                token: "{{myToken}}".into(),
            },
            vec![KeyValueEntry::new("Authorization", "Bearer {{other}}")],
        );
        let json = export(&[saved], ExportFormat::Json, false);
        assert!(json.contains("{{myToken}}"));
        assert!(json.contains("Bearer {{other}}"));
    }

    #[test]
    fn a_secret_beside_a_variable_is_still_a_secret() {
        let saved = saved_with(
            AuthConfig::None,
            vec![KeyValueEntry::new("Cookie", "session=abc; theme={{theme}}")],
        );
        let json = export(&[saved], ExportFormat::Json, false);
        assert!(!json.contains("session=abc"));
        assert!(json.contains("{{cookie}}"));
    }

    #[test]
    fn the_json_says_what_it_is_and_round_trips() {
        let saved = saved_with(AuthConfig::None, vec![]);
        let json = export(std::slice::from_ref(&saved), ExportFormat::Json, true);
        let value: serde_json::Value = serde_json::from_str(&json).unwrap();

        assert_eq!(value["kind"], "api-client/saved-requests");
        let back: Vec<SavedRequest> =
            serde_json::from_value(value["savedRequests"].clone()).unwrap();
        assert_eq!(back, vec![saved]);
    }
}
