use serde::{Deserialize, Serialize};
use ts_rs::TS;

use crate::new_id;

/// HTTP verbs the client can send.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, TS)]
#[serde(rename_all = "UPPERCASE")]
#[ts(export)]
pub enum HttpMethod {
    Get,
    Post,
    Put,
    Patch,
    Delete,
    Head,
    Options,
}

impl HttpMethod {
    pub const ALL: [HttpMethod; 7] = [
        HttpMethod::Get,
        HttpMethod::Post,
        HttpMethod::Put,
        HttpMethod::Patch,
        HttpMethod::Delete,
        HttpMethod::Head,
        HttpMethod::Options,
    ];

    pub fn as_str(self) -> &'static str {
        match self {
            HttpMethod::Get => "GET",
            HttpMethod::Post => "POST",
            HttpMethod::Put => "PUT",
            HttpMethod::Patch => "PATCH",
            HttpMethod::Delete => "DELETE",
            HttpMethod::Head => "HEAD",
            HttpMethod::Options => "OPTIONS",
        }
    }
}

impl std::fmt::Display for HttpMethod {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.as_str())
    }
}

/// One editable row in the params, headers, or form editors.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, TS)]
#[serde(rename_all = "camelCase")]
#[ts(export)]
pub struct KeyValueEntry {
    pub id: String,
    pub enabled: bool,
    pub name: String,
    pub value: String,
}

impl KeyValueEntry {
    pub fn new(name: impl Into<String>, value: impl Into<String>) -> Self {
        Self {
            id: new_id(),
            enabled: true,
            name: name.into(),
            value: value.into(),
        }
    }

    pub fn disabled(name: impl Into<String>, value: impl Into<String>) -> Self {
        Self {
            enabled: false,
            ..Self::new(name, value)
        }
    }

    /// A row only counts once it has a name; blank trailing rows are ignored.
    pub fn is_active(&self) -> bool {
        self.enabled && !self.name.trim().is_empty()
    }
}

/// Auth helper configuration. Anything more exotic can be typed as a header.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, TS, Default)]
#[serde(tag = "type", rename_all = "camelCase")]
#[ts(export)]
pub enum AuthConfig {
    #[default]
    None,
    #[serde(rename_all = "camelCase")]
    Bearer { token: String },
    #[serde(rename_all = "camelCase")]
    Basic { username: String, password: String },
    /// A key in a header or the query string, which is most "API key" auth.
    #[serde(rename_all = "camelCase")]
    ApiKey {
        key: String,
        value: String,
        #[serde(default)]
        placement: ApiKeyPlacement,
    },
}

/// Where an API key goes. A header by default: a query string ends up in logs.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize, TS)]
#[serde(rename_all = "camelCase")]
#[ts(export)]
pub enum ApiKeyPlacement {
    #[default]
    Header,
    Query,
}

/// A single part of a multipart/form-data body.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, TS)]
#[serde(rename_all = "camelCase")]
#[ts(export)]
pub struct MultipartEntry {
    pub id: String,
    pub enabled: bool,
    pub name: String,
    pub value: MultipartValue,
}

impl MultipartEntry {
    pub fn text(name: impl Into<String>, value: impl Into<String>) -> Self {
        Self {
            id: new_id(),
            enabled: true,
            name: name.into(),
            value: MultipartValue::Text {
                value: value.into(),
            },
        }
    }

    pub fn file(name: impl Into<String>, path: impl Into<String>) -> Self {
        Self {
            id: new_id(),
            enabled: true,
            name: name.into(),
            value: MultipartValue::File {
                path: path.into(),
                file_name: None,
                content_type: None,
            },
        }
    }

    pub fn is_active(&self) -> bool {
        self.enabled && !self.name.trim().is_empty()
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, TS)]
#[serde(tag = "kind", rename_all = "camelCase")]
#[ts(export)]
pub enum MultipartValue {
    #[serde(rename_all = "camelCase")]
    Text { value: String },
    #[serde(rename_all = "camelCase")]
    File {
        path: String,
        file_name: Option<String>,
        content_type: Option<String>,
    },
}

/// Request body. `Text` carries its own content type so the user can send
/// XML, CSV, or anything else without reaching for the headers tab.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, TS, Default)]
#[serde(tag = "type", rename_all = "camelCase")]
#[ts(export)]
pub enum RequestBody {
    #[default]
    None,
    #[serde(rename_all = "camelCase")]
    Json { text: String },
    #[serde(rename_all = "camelCase")]
    Text {
        text: String,
        content_type: Option<String>,
    },
    #[serde(rename_all = "camelCase")]
    UrlEncoded { entries: Vec<KeyValueEntry> },
    #[serde(rename_all = "camelCase")]
    Multipart { entries: Vec<MultipartEntry> },
}

impl RequestBody {
    pub fn is_empty(&self) -> bool {
        match self {
            RequestBody::None => true,
            RequestBody::Json { text } | RequestBody::Text { text, .. } => text.trim().is_empty(),
            RequestBody::UrlEncoded { entries } => !entries.iter().any(KeyValueEntry::is_active),
            RequestBody::Multipart { entries } => !entries.iter().any(MultipartEntry::is_active),
        }
    }
}

/// Everything needed to send one request. This is the unit the UI edits,
/// the engine executes, and storage persists.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, TS)]
#[serde(rename_all = "camelCase")]
#[ts(export)]
pub struct HttpRequest {
    pub id: String,
    pub method: HttpMethod,
    pub url: String,
    pub query_params: Vec<KeyValueEntry>,
    pub headers: Vec<KeyValueEntry>,
    pub auth: AuthConfig,
    pub body: RequestBody,
    /// Overrides the default timeout for this request only.
    #[ts(type = "number | null")]
    pub timeout_ms: Option<u64>,
}

impl HttpRequest {
    /// The state the app opens into: a blank GET with nothing filled in.
    pub fn blank() -> Self {
        Self {
            id: new_id(),
            method: HttpMethod::Get,
            url: String::new(),
            query_params: Vec::new(),
            headers: Vec::new(),
            auth: AuthConfig::None,
            body: RequestBody::None,
            timeout_ms: None,
        }
    }

    pub fn get(url: impl Into<String>) -> Self {
        Self {
            url: url.into(),
            ..Self::blank()
        }
    }

    /// True when the request has nothing a user would miss.
    pub fn is_untouched(&self) -> bool {
        self.method == HttpMethod::Get
            && self.url.trim().is_empty()
            && !self.query_params.iter().any(KeyValueEntry::is_active)
            && !self.headers.iter().any(KeyValueEntry::is_active)
            && self.auth == AuthConfig::None
            && self.body.is_empty()
    }

    /// Case-insensitive lookup across enabled header rows.
    pub fn find_header(&self, name: &str) -> Option<&KeyValueEntry> {
        self.headers
            .iter()
            .filter(|h| h.is_active())
            .find(|h| h.name.trim().eq_ignore_ascii_case(name))
    }
}

impl Default for HttpRequest {
    fn default() -> Self {
        Self::blank()
    }
}
