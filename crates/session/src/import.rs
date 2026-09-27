//! Saved requests read back in from a file: the JSON this app exports, or a
//! `.http` file from REST Client, JetBrains, or this app.
//!
//! Importing only ever adds. Every request gets new ids, so the same file
//! twice is two copies to delete, never an edit overwritten, and a file of
//! the wrong kind is refused whole rather than half-imported.

use api_client_codegen::parse_http_file;
use api_client_core::{HttpRequest, MultipartEntry, RequestBody, new_id};
use api_client_storage::SavedRequest;
use serde::Deserialize;

use crate::export::EXPORT_KIND;

/// Larger than any export, and small enough not to hold the app up reading
/// something that was never one.
pub const MAX_IMPORT_BYTES: usize = 20 * 1024 * 1024;

pub struct Parsed {
    pub saved: Vec<SavedRequest>,
    /// A sentence for each request the file held that could not come across.
    pub skipped: Vec<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ImportFile {
    kind: Option<String>,
    version: Option<u32>,
    #[serde(default)]
    saved_requests: Vec<SavedRequest>,
}

/// JSON if it looks like JSON, which a `.http` file never does.
pub fn parse(contents: &str) -> Result<Parsed, String> {
    if contents.len() > MAX_IMPORT_BYTES {
        return Err("The file is too large to be a file of saved requests.".into());
    }
    let contents = contents.trim_start_matches('\u{feff}');

    let parsed = if contents.trim_start().starts_with('{') {
        from_json(contents)?
    } else {
        let file = parse_http_file(contents);
        Parsed {
            saved: file
                .requests
                .into_iter()
                .map(|parsed| SavedRequest::new(parsed.name, parsed.request))
                .collect(),
            skipped: file.skipped,
        }
    };

    if parsed.saved.is_empty() {
        return Err(match parsed.skipped.first() {
            Some(reason) => format!("Nothing in the file could be imported. {reason}"),
            None => "There are no requests in the file.".into(),
        });
    }
    Ok(Parsed {
        saved: parsed.saved.into_iter().map(fresh).collect(),
        skipped: parsed.skipped,
    })
}

fn from_json(contents: &str) -> Result<Parsed, String> {
    let file: ImportFile = serde_json::from_str(contents)
        .map_err(|err| format!("The file is not a file of saved requests: {err}."))?;
    if file.kind.as_deref() != Some(EXPORT_KIND) {
        return Err("This JSON is not a file of saved requests exported from API Client.".into());
    }
    if file.version.unwrap_or(0) > 1 {
        return Err(
            "This file was exported by a newer version of API Client. Update to import it.".into(),
        );
    }
    Ok(Parsed {
        saved: file.saved_requests,
        skipped: Vec::new(),
    })
}

/// New ids throughout, and new timestamps: it is new here.
fn fresh(saved: SavedRequest) -> SavedRequest {
    let mut request = saved.request;
    fresh_ids(&mut request);
    SavedRequest::new(saved.name, request)
}

fn fresh_ids(request: &mut HttpRequest) {
    request.id = new_id();
    for entry in request
        .query_params
        .iter_mut()
        .chain(request.headers.iter_mut())
    {
        entry.id = new_id();
    }
    match &mut request.body {
        RequestBody::UrlEncoded { entries } => {
            for entry in entries {
                entry.id = new_id();
            }
        }
        RequestBody::Multipart { entries } => {
            for MultipartEntry { id, .. } in entries {
                *id = new_id();
            }
        }
        RequestBody::None | RequestBody::Json { .. } | RequestBody::Text { .. } => {}
    }
}

#[cfg(test)]
mod tests {
    use api_client_codegen::ExportFormat;
    use api_client_core::{AuthConfig, KeyValueEntry};

    use super::*;
    use crate::export::export;

    fn saved() -> SavedRequest {
        let mut request = HttpRequest::get("https://api.example.com/users");
        request
            .headers
            .push(KeyValueEntry::new("Accept", "application/json"));
        request.auth = AuthConfig::Bearer {
            token: "tok".into(),
        };
        SavedRequest::new("Users", request)
    }

    #[test]
    fn reads_back_what_was_exported_as_new_requests() {
        let original = saved();
        let json = export(std::slice::from_ref(&original), ExportFormat::Json, true);

        let parsed = parse(&json).unwrap();
        let [back] = parsed.saved.as_slice() else {
            panic!("expected one request");
        };
        assert_eq!(back.name, "Users");
        assert_eq!(back.request.auth, original.request.auth);
        assert_eq!(back.request.url, original.request.url);
        assert_ne!(back.id, original.id);
        assert_ne!(back.request.id, original.request.id);
        assert_ne!(back.request.headers[0].id, original.request.headers[0].id);
    }

    #[test]
    fn reads_a_http_file() {
        let http = export(&[saved()], ExportFormat::Http, false);
        let parsed = parse(&http).unwrap();
        assert_eq!(parsed.saved[0].name, "Users");
        assert_eq!(
            parsed.saved[0].request.auth,
            AuthConfig::Bearer {
                token: "{{token}}".into()
            }
        );
    }

    #[test]
    fn refuses_json_that_is_not_an_export() {
        let error = parse(r#"{"version": 2, "tabs": []}"#).err().unwrap();
        assert!(error.contains("not a file of saved requests"), "{error}");

        let error = parse(r#"{"kind": "api-client/saved-requests", "version": 9}"#)
            .err()
            .unwrap();
        assert!(error.contains("newer version"), "{error}");

        let error = parse("{ not json").err().unwrap();
        assert!(error.contains("not a file of saved requests"), "{error}");
    }

    #[test]
    fn refuses_a_file_with_nothing_in_it() {
        assert_eq!(
            parse("# only a comment\n").err().unwrap(),
            "There are no requests in the file."
        );
        let error = parse("TRACE https://x.test\n").err().unwrap();
        assert!(error.contains("TRACE is not a method"), "{error}");
    }
}
