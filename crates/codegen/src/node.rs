//! Node.js, through the built-in `fetch` or through axios.
//!
//! Both are written as an ES module, which is what lets the top-level `await`
//! stand on its own: save it as `request.mjs` and run it.

use api_client_core::{MultipartValue, RequestBody};
use api_client_http_engine::{AuthPlan, ChallengeAuth};

use crate::text::{FOLDED_NOTE, comment, file_name, fold, indent};
use crate::{IdentityKind, Plan};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Library {
    Fetch,
    Axios,
}

pub fn generate(plan: &Plan, library: Library) -> String {
    match library {
        Library::Fetch => fetch(plan),
        Library::Axios => axios(plan),
    }
}

fn fetch(plan: &Plan) -> String {
    let mut notes = plan.notes();
    let mut imports: Vec<String> = Vec::new();
    let mut blocks: Vec<String> = Vec::new();
    let mut options: Vec<String> = vec![format!("method: {},", literal(plan.method()))];

    let (headers, collided) = fold(&plan.headers());
    if collided {
        notes.push(FOLDED_NOTE.to_string());
    }

    blocks.push(format!("const url = {};", literal(&plan.effective.url)));

    let mut header_rows: Vec<String> = headers
        .iter()
        .map(|(name, value)| format!("{}: {},", key(name), literal(value)))
        .collect();
    if let Some((username, password)) = plan.basic() {
        header_rows.push(format!(
            "Authorization: \"Basic \" + Buffer.from({}).toString(\"base64\"),",
            literal(&format!("{username}:{password}"))
        ));
    }
    if !header_rows.is_empty() {
        blocks.push(format!(
            "const headers = {{\n{}\n}};",
            indent(&header_rows.join("\n"), 2)
        ));
        options.push("headers,".to_string());
    }

    challenge_note(plan, &mut notes, "fetch");
    options.extend(body(plan, &mut imports, &mut blocks, &mut notes, "body"));

    if !plan.options.follow_redirects {
        options.push("redirect: \"manual\",".to_string());
    }
    let timeout = plan.timeout_ms();
    if timeout > 0 {
        options.push(format!("signal: AbortSignal.timeout({timeout}),"));
    }

    /*
     * `fetch` has no place to put a certificate: the agent that would carry one
     * belongs to undici, and reaching it means installing undici and setting a
     * global dispatcher. The environment variable is the one thing that works
     * without any of that, and it is worth being plain about what it does.
     */
    if plan.accepts_invalid_certs() {
        blocks.insert(
            0,
            "// This turns certificate checking off for the whole process.\n\
             process.env.NODE_TLS_REJECT_UNAUTHORIZED = \"0\";"
                .to_string(),
        );
    }
    for path in plan.extra_ca_files() {
        notes.push(format!(
            "`fetch` has no per-request CA list. Run node with \
             `--use-openssl-ca` and `NODE_EXTRA_CA_CERTS={path}`, or use the axios form, which \
             takes an agent."
        ));
    }
    if plan.identity().is_some() {
        notes.push(
            "`fetch` cannot present a client certificate. The axios form can, through an \
             https.Agent."
                .to_string(),
        );
    }

    blocks.push(format!(
        "const response = await fetch(url, {{\n{}\n}});",
        indent(&options.join("\n"), 2)
    ));
    blocks.push("console.log(response.status);\nconsole.log(await response.text());".to_string());

    assemble(&notes, &imports, &blocks)
}

fn axios(plan: &Plan) -> String {
    let mut notes = plan.notes();
    let mut imports: Vec<String> = vec!["import axios from \"axios\";".to_string()];
    let mut blocks: Vec<String> = Vec::new();
    let mut options: Vec<String> = vec![
        format!("method: {},", literal(&plan.method().to_lowercase())),
        format!("url: {},", literal(&plan.effective.url)),
    ];

    let (headers, collided) = fold(&plan.headers());
    if collided {
        notes.push(FOLDED_NOTE.to_string());
    }
    if !headers.is_empty() {
        let rows = headers
            .iter()
            .map(|(name, value)| format!("{}: {},", key(name), literal(value)))
            .collect::<Vec<_>>()
            .join("\n");
        blocks.push(format!("const headers = {{\n{}\n}};", indent(&rows, 2)));
        options.push("headers,".to_string());
    }

    if let Some((username, password)) = plan.basic() {
        options.push(format!(
            "auth: {{ username: {}, password: {} }},",
            literal(username),
            literal(password)
        ));
    }
    challenge_note(plan, &mut notes, "axios");

    options.extend(body(plan, &mut imports, &mut blocks, &mut notes, "data"));

    let timeout = plan.timeout_ms();
    if timeout > 0 {
        options.push(format!("timeout: {timeout},"));
    }
    if !plan.options.follow_redirects {
        options.push("maxRedirects: 0,".to_string());
    }
    // A 4xx is an outcome rather than a throw, as it is everywhere else here.
    options.push("validateStatus: () => true,".to_string());

    if let Some(agent) = agent(plan, &mut imports) {
        blocks.push(agent);
        options.push("httpsAgent: agent,".to_string());
    }

    blocks.push(format!(
        "const response = await axios({{\n{}\n}});",
        indent(&options.join("\n"), 2)
    ));
    blocks.push("console.log(response.status);\nconsole.log(response.data);".to_string());

    assemble(&notes, &imports, &blocks)
}

/// The agent axios takes its TLS settings through.
fn agent(plan: &Plan, imports: &mut Vec<String>) -> Option<String> {
    let mut fields: Vec<String> = Vec::new();

    if plan.accepts_invalid_certs() {
        fields.push("rejectUnauthorized: false,".to_string());
    }
    for path in plan.extra_ca_files() {
        fields.push(format!("ca: readFileSync({}),", literal(path)));
    }
    if let Some((identity, kind)) = plan.identity() {
        let path = identity.path.trim();
        match kind {
            IdentityKind::Pem => {
                fields.push(format!("cert: readFileSync({}),", literal(path)));
                fields.push(format!("key: readFileSync({}),", literal(path)));
            }
            IdentityKind::Pkcs12 => {
                fields.push(format!("pfx: readFileSync({}),", literal(path)));
                fields.push(format!(
                    "passphrase: {},",
                    literal(identity.password.as_deref().unwrap_or(""))
                ));
            }
        }
    }

    if fields.is_empty() {
        return None;
    }
    imports.push("import https from \"node:https\";".to_string());
    if fields.iter().any(|field| field.contains("readFileSync")) {
        imports.push("import { readFileSync } from \"node:fs\";".to_string());
    }
    Some(format!(
        "const agent = new https.Agent({{\n{}\n}});",
        indent(&fields.join("\n"), 2)
    ))
}

/// The body, as the options that carry it. Both libraries take the same three
/// shapes — a string, a `URLSearchParams` and a `FormData` — under different
/// names: `fetch` calls the field `body` and axios calls it `data`.
fn body(
    plan: &Plan,
    imports: &mut Vec<String>,
    blocks: &mut Vec<String>,
    notes: &mut Vec<String>,
    field: &str,
) -> Vec<String> {
    let carry = || {
        if field == "body" {
            "body,".to_string()
        } else {
            format!("{field}: body,")
        }
    };

    match &plan.request.body {
        RequestBody::None => Vec::new(),

        RequestBody::Json { text } | RequestBody::Text { text, .. } => {
            blocks.push(format!("const body = {};", literal(text)));
            vec![carry()]
        }

        // A list of pairs rather than an object: two rows may share a name.
        RequestBody::UrlEncoded { entries } => {
            let rows = entries
                .iter()
                .filter(|entry| entry.is_active())
                .map(|entry| {
                    format!(
                        "[{}, {}],",
                        literal(entry.name.trim()),
                        literal(&entry.value)
                    )
                })
                .collect::<Vec<_>>()
                .join("\n");
            blocks.push(format!(
                "const body = new URLSearchParams([\n{}\n]);",
                indent(&rows, 2)
            ));
            vec![carry()]
        }

        RequestBody::Multipart { entries } => {
            let mut lines = vec!["const body = new FormData();".to_string()];
            let mut reads_files = false;
            for entry in entries.iter().filter(|entry| entry.is_active()) {
                let name = literal(entry.name.trim());
                match &entry.value {
                    MultipartValue::Text { value } => {
                        lines.push(format!("body.append({name}, {});", literal(value)));
                    }
                    MultipartValue::File {
                        path,
                        file_name: given,
                        content_type,
                    } => {
                        reads_files = true;
                        let mime = content_type
                            .as_ref()
                            .map(|mime| format!(", {{ type: {} }}", literal(mime)))
                            .unwrap_or_default();
                        lines.push(format!(
                            "body.append(\n  {name},\n  new Blob([await readFile({})]{mime}),\n  \
                             {},\n);",
                            literal(path),
                            literal(&file_name(path, given.as_ref()))
                        ));
                    }
                }
            }
            if reads_files {
                imports.push("import { readFile } from \"node:fs/promises\";".to_string());
            }
            if !entries.iter().any(|entry| {
                entry.is_active() && matches!(entry.value, MultipartValue::File { .. })
            }) {
                notes.push(
                    "This multipart body has no file in it. A FormData with only text parts is \
                     still sent as multipart, so this is the same request the app makes."
                        .to_string(),
                );
            }
            blocks.push(lines.join("\n"));
            vec![carry()]
        }
    }
}

fn challenge_note(plan: &Plan, notes: &mut Vec<String>, library: &str) {
    if let AuthPlan::Challenge(challenge) = &plan.effective.auth {
        let scheme = match challenge {
            ChallengeAuth::Digest { .. } => "digest",
            ChallengeAuth::Ntlm { .. } => "NTLM",
        };
        notes.push(format!(
            "{library} cannot answer a {scheme} challenge on its own. Node has no built-in \
             support for either scheme."
        ));
    }
}

fn assemble(notes: &[String], imports: &[String], blocks: &[String]) -> String {
    let mut out = String::new();
    for note in notes {
        out.push_str(&comment("// ", note));
        out.push('\n');
    }
    if !imports.is_empty() {
        let mut sorted = imports.to_vec();
        sorted.dedup();
        out.push_str(&sorted.join("\n"));
        out.push_str("\n\n");
    }
    out.push_str(&blocks.join("\n\n"));
    out.push('\n');
    out
}

/// An object key, quoted only when it has to be.
fn key(name: &str) -> String {
    if name
        .chars()
        .all(|c| c.is_ascii_alphanumeric() || c == '_' || c == '$')
        && !name.starts_with(|c: char| c.is_ascii_digit())
        && !name.is_empty()
    {
        return name.to_string();
    }
    literal(name)
}

/// A JavaScript string literal.
///
/// A body that runs over lines is written as a template literal, which keeps
/// them; anything else is a quoted string. The backtick and `${` are what a
/// template literal has to escape, and neither is common in a request.
fn literal(value: &str) -> String {
    if value.contains('\n') && !value.contains('\r') {
        let escaped = value
            .replace('\\', "\\\\")
            .replace('`', "\\`")
            .replace("${", "\\${");
        return format!("`{escaped}`");
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
    use super::{key, literal};

    #[test]
    fn a_body_over_several_lines_keeps_them() {
        assert_eq!(literal("{\n  \"a\": 1\n}"), "`{\n  \"a\": 1\n}`");
    }

    #[test]
    fn a_template_literal_escapes_what_would_end_it() {
        assert_eq!(literal("a\n`${x}"), "`a\n\\`\\${x}`");
    }

    #[test]
    fn a_header_name_with_a_dash_is_quoted() {
        assert_eq!(key("Accept"), "Accept");
        assert_eq!(key("X-Trace"), "\"X-Trace\"");
    }
}
