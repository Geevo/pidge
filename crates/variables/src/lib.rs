//! `{{variable}}` substitution.
//!
//! Substitution happens in Rust, once, immediately before a request is built,
//! so both frontends behave identically. An unresolved variable is an error:
//! sending a literal `{{token}}` to a server is never what the user meant.

use std::collections::BTreeMap;

use api_client_core::{
    AuthConfig, HttpRequest, KeyValueEntry, MultipartEntry, MultipartValue, OAuth2Settings,
    RequestBody, RequestError, RequestErrorKind,
};
use serde::{Deserialize, Serialize};
use ts_rs::TS;

/// A named set of variables. Environments are flat and have no relationship to
/// workspaces, projects, or anything else.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, TS, Default)]
#[serde(rename_all = "camelCase")]
#[ts(export)]
pub struct Environment {
    pub id: String,
    pub name: String,
    pub variables: Vec<KeyValueEntry>,
}

/// Resolved name/value pairs, ready to substitute.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct VariableSet {
    values: BTreeMap<String, String>,
}

impl VariableSet {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn insert(&mut self, name: impl Into<String>, value: impl Into<String>) {
        self.values.insert(name.into(), value.into());
    }

    pub fn get(&self, name: &str) -> Option<&str> {
        self.values.get(name).map(String::as_str)
    }

    pub fn is_empty(&self) -> bool {
        self.values.is_empty()
    }

    pub fn names(&self) -> impl Iterator<Item = &str> {
        self.values.keys().map(String::as_str)
    }
}

impl From<&Environment> for VariableSet {
    fn from(environment: &Environment) -> Self {
        let mut set = VariableSet::new();
        for entry in environment.variables.iter().filter(|e| e.is_active()) {
            set.insert(entry.name.trim(), entry.value.clone());
        }
        set
    }
}

impl<K: Into<String>, V: Into<String>> FromIterator<(K, V)> for VariableSet {
    fn from_iter<I: IntoIterator<Item = (K, V)>>(iter: I) -> Self {
        let mut set = VariableSet::new();
        for (k, v) in iter {
            set.insert(k, v);
        }
        set
    }
}

/// Replaces every `{{name}}` in `input`. Unknown names are collected rather
/// than failing on the first one, so the user fixes them all at once.
pub fn substitute(input: &str, variables: &VariableSet) -> Result<String, Vec<String>> {
    let mut out = String::with_capacity(input.len());
    let mut missing: Vec<String> = Vec::new();
    let mut rest = input;

    while let Some(start) = rest.find("{{") {
        out.push_str(&rest[..start]);
        let after_open = &rest[start + 2..];
        let Some(end) = after_open.find("}}") else {
            // No closing braces: treat the rest as literal text.
            out.push_str(&rest[start..]);
            return finish(out, missing);
        };

        let raw_name = &after_open[..end];
        let name = raw_name.trim();
        match variables.get(name) {
            Some(value) => out.push_str(value),
            None => {
                if !missing.iter().any(|existing| existing.as_str() == name) {
                    missing.push(name.to_string());
                }
            }
        }
        rest = &after_open[end + 2..];
    }

    out.push_str(rest);
    finish(out, missing)
}

fn finish(out: String, missing: Vec<String>) -> Result<String, Vec<String>> {
    if missing.is_empty() {
        Ok(out)
    } else {
        Err(missing)
    }
}

/// Substitutes across the whole request: URL, params, headers, body, and auth.
pub fn resolve_request(
    request: &HttpRequest,
    variables: &VariableSet,
) -> Result<HttpRequest, RequestError> {
    let mut missing: Vec<String> = Vec::new();
    let mut resolve = |value: &str| -> String {
        match substitute(value, variables) {
            Ok(resolved) => resolved,
            Err(names) => {
                for name in names {
                    if !missing.contains(&name) {
                        missing.push(name);
                    }
                }
                String::new()
            }
        }
    };

    let mut resolved = request.clone();
    resolved.url = resolve(&request.url);
    resolved.query_params = resolve_entries(&request.query_params, &mut resolve);
    resolved.headers = resolve_entries(&request.headers, &mut resolve);

    resolved.auth = match &request.auth {
        AuthConfig::None => AuthConfig::None,
        AuthConfig::Bearer { token } => AuthConfig::Bearer {
            token: resolve(token),
        },
        AuthConfig::Basic { username, password } => AuthConfig::Basic {
            username: resolve(username),
            password: resolve(password),
        },
        AuthConfig::Digest { username, password } => AuthConfig::Digest {
            username: resolve(username),
            password: resolve(password),
        },
        AuthConfig::OAuth2(settings) => AuthConfig::OAuth2(OAuth2Settings {
            grant: settings.grant,
            token_url: resolve(&settings.token_url),
            client_id: resolve(&settings.client_id),
            client_secret: resolve(&settings.client_secret),
            scope: resolve(&settings.scope),
            username: resolve(&settings.username),
            password: resolve(&settings.password),
            refresh_token: resolve(&settings.refresh_token),
            client_auth: settings.client_auth,
        }),
        AuthConfig::ApiKey {
            key,
            value,
            placement,
        } => AuthConfig::ApiKey {
            key: resolve(key),
            value: resolve(value),
            placement: *placement,
        },
    };

    resolved.body = match &request.body {
        RequestBody::None => RequestBody::None,
        RequestBody::Json { text } => RequestBody::Json {
            text: resolve(text),
        },
        RequestBody::Text { text, content_type } => RequestBody::Text {
            text: resolve(text),
            content_type: content_type.clone(),
        },
        RequestBody::UrlEncoded { entries } => RequestBody::UrlEncoded {
            entries: resolve_entries(entries, &mut resolve),
        },
        RequestBody::Multipart { entries } => RequestBody::Multipart {
            entries: entries
                .iter()
                .map(|entry| {
                    if !entry.enabled {
                        return entry.clone();
                    }
                    MultipartEntry {
                        id: entry.id.clone(),
                        enabled: entry.enabled,
                        name: resolve(&entry.name),
                        value: match &entry.value {
                            MultipartValue::Text { value } => MultipartValue::Text {
                                value: resolve(value),
                            },
                            MultipartValue::File {
                                path,
                                file_name,
                                content_type,
                            } => MultipartValue::File {
                                path: resolve(path),
                                file_name: file_name.clone(),
                                content_type: content_type.clone(),
                            },
                        },
                    }
                })
                .collect(),
        },
    };

    if missing.is_empty() {
        Ok(resolved)
    } else {
        let list = missing
            .iter()
            .map(|name| format!("{{{{{name}}}}}"))
            .collect::<Vec<_>>()
            .join(", ");
        Err(RequestError::new(
            RequestErrorKind::UnresolvedVariable,
            format!("No value for {list}."),
        )
        .with_detail(format!(
            "Define these in the active environment, or remove them from the request. Known variables: {}",
            if variables.is_empty() {
                "(none)".to_string()
            } else {
                variables.names().collect::<Vec<_>>().join(", ")
            }
        )))
    }
}

fn resolve_entries(
    entries: &[KeyValueEntry],
    resolve: &mut impl FnMut(&str) -> String,
) -> Vec<KeyValueEntry> {
    entries
        .iter()
        .map(|entry| {
            // Disabled rows are never sent, so never fail on their variables.
            if !entry.enabled {
                return entry.clone();
            }
            KeyValueEntry {
                id: entry.id.clone(),
                enabled: entry.enabled,
                name: resolve(&entry.name),
                value: resolve(&entry.value),
            }
        })
        .collect()
}
