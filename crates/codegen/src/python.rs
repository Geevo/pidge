//! Python, through `requests`.
//!
//! `requests` rather than `http.client` or `urllib`: it is what anybody reading
//! this would reach for, and the standard library equivalent of a multipart
//! body is a page of code nobody wants pasted into theirs.

use api_client_core::{MultipartValue, RequestBody};
use api_client_http_engine::{AuthPlan, ChallengeAuth};

use crate::text::{FOLDED_NOTE, comment, file_name, fold, indent, seconds};
use crate::{IdentityKind, Plan};

pub fn generate(plan: &Plan) -> String {
    let mut notes = plan.notes();
    let mut imports: Vec<String> = vec!["import requests".to_string()];
    let mut blocks: Vec<String> = Vec::new();
    let mut arguments: Vec<String> = vec!["url".to_string()];

    let (headers, collided) = fold(&plan.headers());
    if collided {
        notes.push(FOLDED_NOTE.to_string());
    }

    blocks.push(format!("url = {}", literal(&plan.effective.url)));

    if !headers.is_empty() {
        let rows = headers
            .iter()
            .map(|(name, value)| format!("{}: {},", literal(name), literal(value)))
            .collect::<Vec<_>>()
            .join("\n");
        blocks.push(format!("headers = {{\n{}\n}}", indent(&rows, 4)));
        arguments.push("headers=headers".to_string());
    }

    if let Some((username, password)) = plan.basic() {
        arguments.push(format!(
            "auth=({}, {})",
            literal(username),
            literal(password)
        ));
    }

    match &plan.effective.auth {
        AuthPlan::Challenge(ChallengeAuth::Digest { username, password }) => {
            imports.push("from requests.auth import HTTPDigestAuth".to_string());
            arguments.push(format!(
                "auth=HTTPDigestAuth({}, {})",
                literal(username),
                literal(password)
            ));
        }
        AuthPlan::Challenge(ChallengeAuth::Ntlm {
            username,
            password,
            domain,
            workstation,
        }) => {
            // Not part of `requests`; `pip install requests-ntlm` supplies it.
            imports.push("from requests_ntlm import HttpNtlmAuth".to_string());
            if !workstation.trim().is_empty() {
                notes.push(
                    "requests-ntlm has nowhere to put a workstation name, so it is not sent."
                        .to_string(),
                );
            }
            let account = if domain.trim().is_empty() {
                username.clone()
            } else {
                format!("{domain}\\{username}")
            };
            arguments.push(format!(
                "auth=HttpNtlmAuth({}, {})",
                literal(&account),
                literal(password)
            ));
        }
        _ => {}
    }

    arguments.extend(body(plan, &mut blocks, &mut notes));

    if !plan.options.follow_redirects {
        arguments.push("allow_redirects=False".to_string());
    }
    arguments.extend(tls(plan, &mut notes));
    let timeout = plan.timeout_ms();
    if timeout > 0 {
        arguments.push(format!("timeout={}", seconds(timeout)));
    }

    blocks.push(format!(
        "response = requests.{}({})",
        plan.method().to_lowercase(),
        arguments.join(", ")
    ));
    blocks.push("print(response.status_code)\nprint(response.text)".to_string());

    let mut out = String::new();
    for note in notes {
        out.push_str(&comment("# ", &note));
        out.push('\n');
    }
    out.push_str(&imports.join("\n"));
    out.push_str("\n\n");
    out.push_str(&blocks.join("\n\n"));
    out.push('\n');
    out
}

/// Trust and identity, as the keyword arguments `requests` takes for them.
fn tls(plan: &Plan, notes: &mut Vec<String>) -> Vec<String> {
    let mut arguments = Vec::new();

    if let Some((identity, kind)) = plan.identity() {
        let path = identity.path.trim();
        match kind {
            IdentityKind::Pem => arguments.push(format!("cert={}", literal(path))),
            IdentityKind::Pkcs12 => {
                // requests goes through OpenSSL, which wants PEM on disk.
                let converted = std::path::Path::new(path)
                    .with_extension("pem")
                    .to_string_lossy()
                    .into_owned();
                notes.push(format!(
                    "requests cannot read a PKCS#12 bundle. Convert it first, which will ask for \
                     the bundle's password:\n  openssl pkcs12 -in {path} -out {converted} -nodes"
                ));
                arguments.push(format!("cert={}", literal(&converted)));
            }
        }
    }

    // Verification off wins over a CA file: there is nothing left to check.
    if plan.accepts_invalid_certs() {
        arguments.push("verify=False".to_string());
        return arguments;
    }

    let extra = plan.extra_ca_files();
    if let Some(first) = extra.first() {
        arguments.push(format!("verify={}", literal(first)));

        if extra.len() > 1 {
            notes.push(format!(
                "requests reads one CA file. {} were set, so only the first is here; concatenate \
                 them into one PEM to use them all.",
                extra.len()
            ));
        }
        if plan.merges_ca_files() {
            notes.push(
                "verify= replaces the default CA bundle rather than adding to it, which is what \
                 the app does. A server with a public certificate will not verify against this \
                 file alone."
                    .to_string(),
            );
        }
    }

    arguments
}

/// Adds whatever the body needs as its own statements, and returns the keyword
/// arguments that refer to them.
fn body(plan: &Plan, blocks: &mut Vec<String>, notes: &mut Vec<String>) -> Vec<String> {
    match &plan.request.body {
        RequestBody::None => Vec::new(),

        /*
         * `data=` with the text exactly as typed, rather than `json=` with a
         * dictionary. The editor holds text, which is not always valid JSON and
         * is not always meant to be; re-encoding it through a dictionary would
         * reorder keys and reformat numbers, and send something the user never
         * wrote.
         */
        RequestBody::Json { text } | RequestBody::Text { text, .. } => {
            blocks.push(format!("payload = {}", literal(text)));
            vec!["data=payload".to_string()]
        }

        // A list of pairs rather than a dictionary: two rows may share a name,
        // and `requests` encodes the list the same way the engine does.
        RequestBody::UrlEncoded { entries } => {
            let rows = entries
                .iter()
                .filter(|entry| entry.is_active())
                .map(|entry| {
                    format!(
                        "({}, {}),",
                        literal(entry.name.trim()),
                        literal(&entry.value)
                    )
                })
                .collect::<Vec<_>>()
                .join("\n");
            blocks.push(format!("payload = [\n{}\n]", indent(&rows, 4)));
            vec!["data=payload".to_string()]
        }

        RequestBody::Multipart { entries } => {
            let mut text_rows: Vec<String> = Vec::new();
            let mut file_rows: Vec<String> = Vec::new();

            for entry in entries.iter().filter(|entry| entry.is_active()) {
                let name = literal(entry.name.trim());
                match &entry.value {
                    MultipartValue::Text { value } => {
                        text_rows.push(format!("({name}, {}),", literal(value)));
                    }
                    MultipartValue::File {
                        path,
                        file_name: given,
                        content_type,
                    } => {
                        let shown = literal(&file_name(path, given.as_ref()));
                        let handle = format!("open({}, \"rb\")", literal(path));
                        let part = match content_type {
                            Some(mime) => {
                                format!("({name}, ({shown}, {handle}, {})),", literal(mime))
                            }
                            None => format!("({name}, ({shown}, {handle})),"),
                        };
                        file_rows.push(part);
                    }
                }
            }

            let mut arguments = Vec::new();
            if !text_rows.is_empty() {
                blocks.push(format!(
                    "payload = [\n{}\n]",
                    indent(&text_rows.join("\n"), 4)
                ));
                arguments.push("data=payload".to_string());
            }
            if file_rows.is_empty() {
                // Without a file, `requests` sends a form rather than a
                // multipart body, which is not what the request says.
                notes.push(
                    "This multipart body has no file in it. requests sends a form-encoded body \
                     unless `files` is given, so add a part with a file to keep it multipart."
                        .to_string(),
                );
            } else {
                blocks.push(format!(
                    "files = [\n{}\n]",
                    indent(&file_rows.join("\n"), 4)
                ));
                arguments.push("files=files".to_string());
            }
            arguments
        }
    }
}

/// A Python string literal.
///
/// Text that runs over lines is written as it reads, in triple quotes. A
/// carriage return has no spelling inside those — the source file's own line
/// endings would swallow it — so anything carrying one is escaped onto a single
/// line instead, where every character is explicit.
fn literal(value: &str) -> String {
    if value.contains('\n') && !value.contains('\r') {
        let mut escaped = value
            .replace('\\', "\\\\")
            .replace("\"\"\"", "\\\"\\\"\\\"");
        // A quote against the closing delimiter would make four in a row.
        if escaped.ends_with('"') {
            escaped.pop();
            escaped.push_str("\\\"");
        }
        return format!("\"\"\"{escaped}\"\"\"");
    }

    let escaped = value
        .replace('\\', "\\\\")
        .replace('"', "\\\"")
        .replace('\r', "\\r")
        .replace('\n', "\\n")
        .replace('\t', "\\t");
    format!("\"{escaped}\"")
}

#[cfg(test)]
mod tests {
    use super::literal;

    #[test]
    fn a_backslash_and_a_quote_are_escaped() {
        assert_eq!(literal(r#"a\b"c"#), r#""a\\b\"c""#);
    }

    #[test]
    fn a_body_over_several_lines_is_written_as_it_reads() {
        assert_eq!(literal("{\n  \"a\": 1\n}"), "\"\"\"{\n  \"a\": 1\n}\"\"\"");
    }

    /// A carriage return inside triple quotes is the source file's line ending,
    /// not data, so that text is escaped onto one line instead.
    #[test]
    fn a_carriage_return_is_escaped_rather_than_written() {
        assert_eq!(literal("a\r\nb"), r#""a\r\nb""#);
    }
}
