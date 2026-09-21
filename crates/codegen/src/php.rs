//! PHP, through the cURL extension or through Guzzle.
//!
//! `ext-curl` is in every PHP build worth having and needs no `composer
//! require`, which is why it goes first. Guzzle is what a project with a
//! `composer.json` already has, and says most of this far more briefly.

use api_client_core::{MultipartValue, RequestBody};
use api_client_http_engine::{AuthPlan, ChallengeAuth};

use crate::text::{comment, file_name, form_encoded, indent};
use crate::{IdentityKind, Plan};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Library {
    Curl,
    Guzzle,
}

pub fn generate(plan: &Plan, library: Library) -> String {
    match library {
        Library::Curl => curl(plan),
        Library::Guzzle => guzzle(plan),
    }
}

fn curl(plan: &Plan) -> String {
    let mut notes = plan.notes();
    let mut blocks: Vec<String> = Vec::new();
    let mut options: Vec<(String, String)> = vec![
        ("CURLOPT_URL".to_string(), literal(&plan.effective.url)),
        ("CURLOPT_RETURNTRANSFER".to_string(), "true".to_string()),
        ("CURLOPT_CUSTOMREQUEST".to_string(), literal(plan.method())),
    ];

    let headers = plan.headers();
    if !headers.is_empty() {
        let rows = headers
            .iter()
            .map(|(name, value)| format!("{},", literal(&format!("{name}: {value}"))))
            .collect::<Vec<_>>()
            .join("\n");
        options.push((
            "CURLOPT_HTTPHEADER".to_string(),
            format!("[\n{}\n]", indent(&rows, 4)),
        ));
    }

    if let Some((username, password)) = plan.basic() {
        options.push((
            "CURLOPT_USERPWD".to_string(),
            literal(&format!("{username}:{password}")),
        ));
    }
    match &plan.effective.auth {
        AuthPlan::Challenge(ChallengeAuth::Digest { username, password }) => {
            options.push((
                "CURLOPT_USERPWD".to_string(),
                literal(&format!("{username}:{password}")),
            ));
            options.push((
                "CURLOPT_HTTPAUTH".to_string(),
                "CURLAUTH_DIGEST".to_string(),
            ));
        }
        AuthPlan::Challenge(ChallengeAuth::Ntlm {
            username,
            password,
            domain,
            ..
        }) => {
            let account = if domain.trim().is_empty() {
                username.clone()
            } else {
                format!("{domain}\\{username}")
            };
            options.push((
                "CURLOPT_USERPWD".to_string(),
                literal(&format!("{account}:{password}")),
            ));
            options.push(("CURLOPT_HTTPAUTH".to_string(), "CURLAUTH_NTLM".to_string()));
        }
        _ => {}
    }

    options.extend(body(plan, &mut blocks));

    if plan.options.follow_redirects {
        options.push(("CURLOPT_FOLLOWLOCATION".to_string(), "true".to_string()));
    }
    let timeout = plan.timeout_ms();
    if timeout > 0 {
        options.push(("CURLOPT_TIMEOUT_MS".to_string(), timeout.to_string()));
    }
    if plan.accepts_invalid_certs() {
        options.push(("CURLOPT_SSL_VERIFYPEER".to_string(), "false".to_string()));
        options.push(("CURLOPT_SSL_VERIFYHOST".to_string(), "0".to_string()));
    }
    let extra = plan.extra_ca_files();
    if let Some(first) = extra.first() {
        options.push(("CURLOPT_CAINFO".to_string(), literal(first)));
        if extra.len() > 1 {
            notes.push(format!(
                "cURL reads one CA file. {} were set, so only the first is here; concatenate \
                 them into one PEM to use them all.",
                extra.len()
            ));
        }
        if plan.merges_ca_files() {
            notes.push(
                "CURLOPT_CAINFO replaces the default CA bundle rather than adding to it, which \
                 is what the app does."
                    .to_string(),
            );
        }
    }
    if let Some((identity, kind)) = plan.identity() {
        options.push(("CURLOPT_SSLCERT".to_string(), literal(identity.path.trim())));
        if kind == IdentityKind::Pkcs12 {
            options.push(("CURLOPT_SSLCERTTYPE".to_string(), literal("P12")));
        }
        if let Some(password) = &identity.password {
            options.push(("CURLOPT_SSLCERTPASSWD".to_string(), literal(password)));
        }
    }

    let width = options
        .iter()
        .map(|(name, _)| name.len())
        .max()
        .unwrap_or(0);
    let rows = options
        .iter()
        .map(|(name, value)| format!("{name:<width$} => {value},"))
        .collect::<Vec<_>>()
        .join("\n");

    blocks.push(format!(
        "$curl = curl_init();\n\ncurl_setopt_array($curl, [\n{}\n]);",
        indent(&rows, 4)
    ));
    blocks.push(
        "$response = curl_exec($curl);\n\
         $status = curl_getinfo($curl, CURLINFO_RESPONSE_CODE);\n\
         curl_close($curl);\n\n\
         echo $status, PHP_EOL;\necho $response, PHP_EOL;"
            .to_string(),
    );

    assemble(&notes, None, &blocks)
}

fn guzzle(plan: &Plan) -> String {
    let mut notes = plan.notes();
    let mut blocks: Vec<String> = Vec::new();
    let mut client: Vec<(String, String)> = Vec::new();
    let mut options: Vec<(String, String)> = Vec::new();

    let timeout = plan.timeout_ms();
    if timeout > 0 {
        client.push((literal("timeout"), format!("{}", timeout as f64 / 1000.0)));
    }
    client.push((
        literal("allow_redirects"),
        if plan.options.follow_redirects {
            "true".to_string()
        } else {
            "false".to_string()
        },
    ));
    // A 4xx is an outcome rather than an exception, as it is everywhere else.
    client.push((literal("http_errors"), "false".to_string()));

    if plan.accepts_invalid_certs() {
        client.push((literal("verify"), "false".to_string()));
    } else if let Some(first) = plan.extra_ca_files().first() {
        client.push((literal("verify"), literal(first)));
        if plan.merges_ca_files() {
            notes.push(
                "`verify` replaces the default CA bundle rather than adding to it, which is what \
                 the app does."
                    .to_string(),
            );
        }
    }
    if let Some((identity, _)) = plan.identity() {
        let path = literal(identity.path.trim());
        client.push((
            literal("cert"),
            match &identity.password {
                Some(password) => format!("[{path}, {}]", literal(password)),
                None => path,
            },
        ));
    }

    let headers = plan.headers();
    if !headers.is_empty() {
        let rows = headers
            .iter()
            .map(|(name, value)| format!("{} => {},", literal(name), literal(value)))
            .collect::<Vec<_>>()
            .join("\n");
        options.push((literal("headers"), format!("[\n{}\n]", indent(&rows, 4))));
    }

    if let Some((username, password)) = plan.basic() {
        options.push((
            literal("auth"),
            format!("[{}, {}]", literal(username), literal(password)),
        ));
    }
    if let AuthPlan::Challenge(challenge) = &plan.effective.auth {
        match challenge {
            ChallengeAuth::Digest { username, password } => options.push((
                literal("auth"),
                format!(
                    "[{}, {}, {}]",
                    literal(username),
                    literal(password),
                    literal("digest")
                ),
            )),
            ChallengeAuth::Ntlm {
                username,
                password,
                domain,
                ..
            } => {
                let account = if domain.trim().is_empty() {
                    username.clone()
                } else {
                    format!("{domain}\\{username}")
                };
                options.push((
                    literal("auth"),
                    format!(
                        "[{}, {}, {}]",
                        literal(&account),
                        literal(password),
                        literal("ntlm")
                    ),
                ));
            }
        }
    }

    options.extend(guzzle_body(plan, &mut blocks));

    let mut out = format!(
        "$client = new Client([\n{}\n]);",
        indent(&pairs(&client), 4)
    );
    if options.is_empty() {
        out.push_str(&format!(
            "\n\n$response = $client->request({}, {});",
            literal(plan.method()),
            literal(&plan.effective.url)
        ));
    } else {
        out.push_str(&format!(
            "\n\n$response = $client->request({}, {}, [\n{}\n]);",
            literal(plan.method()),
            literal(&plan.effective.url),
            indent(&pairs(&options), 4)
        ));
    }
    blocks.push(out);
    blocks.push(
        "echo $response->getStatusCode(), PHP_EOL;\necho $response->getBody(), PHP_EOL;"
            .to_string(),
    );

    assemble(&notes, Some("use GuzzleHttp\\Client;"), &blocks)
}

fn pairs(rows: &[(String, String)]) -> String {
    let width = rows.iter().map(|(name, _)| name.len()).max().unwrap_or(0);
    rows.iter()
        .map(|(name, value)| format!("{name:<width$} => {value},"))
        .collect::<Vec<_>>()
        .join("\n")
}

/// The body, as the cURL options that carry it.
fn body(plan: &Plan, blocks: &mut Vec<String>) -> Vec<(String, String)> {
    match &plan.request.body {
        RequestBody::None => Vec::new(),

        RequestBody::Json { text } | RequestBody::Text { text, .. } => {
            blocks.push(format!("$payload = {};", literal(text)));
            vec![("CURLOPT_POSTFIELDS".to_string(), "$payload".to_string())]
        }

        RequestBody::UrlEncoded { entries } => {
            blocks.push(format!("$payload = {};", literal(&form_encoded(entries))));
            vec![("CURLOPT_POSTFIELDS".to_string(), "$payload".to_string())]
        }

        /*
         * An array of fields, which is how the extension is told to write a
         * multipart body: it generates the boundary and the headers itself, so
         * no Content-Type is set here.
         */
        RequestBody::Multipart { entries } => {
            let rows = entries
                .iter()
                .filter(|entry| entry.is_active())
                .map(|entry| {
                    let name = literal(entry.name.trim());
                    match &entry.value {
                        MultipartValue::Text { value } => {
                            format!("{name} => {},", literal(value))
                        }
                        MultipartValue::File {
                            path,
                            file_name: given,
                            content_type,
                        } => format!(
                            "{name} => new CURLFile({}, {}, {}),",
                            literal(path),
                            literal(
                                content_type
                                    .as_deref()
                                    .unwrap_or("application/octet-stream")
                            ),
                            literal(&file_name(path, given.as_ref()))
                        ),
                    }
                })
                .collect::<Vec<_>>()
                .join("\n");
            blocks.push(format!("$payload = [\n{}\n];", indent(&rows, 4)));
            vec![("CURLOPT_POSTFIELDS".to_string(), "$payload".to_string())]
        }
    }
}

/// The same for Guzzle, which names each shape rather than taking one field.
fn guzzle_body(plan: &Plan, blocks: &mut Vec<String>) -> Vec<(String, String)> {
    match &plan.request.body {
        RequestBody::None => Vec::new(),

        RequestBody::Json { text } | RequestBody::Text { text, .. } => {
            blocks.push(format!("$payload = {};", literal(text)));
            vec![(literal("body"), "$payload".to_string())]
        }

        // `form_params` encodes the pairs itself, the same way the engine does.
        RequestBody::UrlEncoded { entries } => {
            let rows = entries
                .iter()
                .filter(|entry| entry.is_active())
                .map(|entry| {
                    format!(
                        "{} => {},",
                        literal(entry.name.trim()),
                        literal(&entry.value)
                    )
                })
                .collect::<Vec<_>>()
                .join("\n");
            blocks.push(format!("$payload = [\n{}\n];", indent(&rows, 4)));
            vec![(literal("form_params"), "$payload".to_string())]
        }

        RequestBody::Multipart { entries } => {
            let rows = entries
                .iter()
                .filter(|entry| entry.is_active())
                .map(|entry| {
                    let name = literal(entry.name.trim());
                    match &entry.value {
                        MultipartValue::Text { value } => format!(
                            "[\n    {} => {name},\n    {} => {},\n],",
                            literal("name"),
                            literal("contents"),
                            literal(value)
                        ),
                        MultipartValue::File {
                            path,
                            file_name: given,
                            content_type,
                        } => {
                            let mut part = format!(
                                "[\n    {} => {name},\n    {} => Utils::tryFopen({}, 'r'),\n    \
                                 {} => {},",
                                literal("name"),
                                literal("contents"),
                                literal(path),
                                literal("filename"),
                                literal(&file_name(path, given.as_ref()))
                            );
                            if let Some(mime) = content_type {
                                part.push_str(&format!(
                                    "\n    {} => [{} => {}],",
                                    literal("headers"),
                                    literal("Content-Type"),
                                    literal(mime)
                                ));
                            }
                            part.push_str("\n],");
                            part
                        }
                    }
                })
                .collect::<Vec<_>>()
                .join("\n");
            blocks.push(format!("$payload = [\n{}\n];", indent(&rows, 4)));
            vec![(literal("multipart"), "$payload".to_string())]
        }
    }
}

fn assemble(notes: &[String], import: Option<&str>, blocks: &[String]) -> String {
    let mut out = String::from("<?php\n\n");
    for note in notes {
        out.push_str(&comment("// ", note));
        out.push('\n');
    }
    if !notes.is_empty() {
        out.push('\n');
    }
    if let Some(import) = import {
        out.push_str("require 'vendor/autoload.php';\n\n");
        out.push_str(import);
        out.push_str("\n\n");
    }
    out.push_str(&blocks.join("\n\n"));
    out.push('\n');
    out
}

/// A PHP string literal.
///
/// Single quotes, in which nothing is interpolated — `$` and `\n` are text —
/// and the only two escapes are the quote itself and the backslash. It may run
/// over as many lines as it likes.
fn literal(value: &str) -> String {
    format!("'{}'", value.replace('\\', "\\\\").replace('\'', "\\'"))
}

#[cfg(test)]
mod tests {
    use super::literal;

    /// A dollar sign is a variable in a double-quoted string and text in a
    /// single-quoted one, which is why every string here is single-quoted.
    #[test]
    fn a_dollar_sign_is_left_alone() {
        assert_eq!(literal("$payload"), "'$payload'");
    }

    #[test]
    fn a_quote_and_a_backslash_are_escaped() {
        assert_eq!(literal(r"it's C:\a"), r"'it\'s C:\\a'");
    }
}
