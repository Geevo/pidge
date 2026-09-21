//! Node.js, through `fetch`.
//!
//! The global `fetch` rather than `axios` or `node:https`: it needs nothing
//! installed, and it is the same call a reader already knows from the browser.
//! The snippet is an ES module with a top-level `await`, so it runs as written
//! from a `.mjs` file.
//!
//! The awkward part is trust. Node's built-in `fetch` takes no TLS options at
//! all; they belong to the connection, and the only way to reach one is an
//! `undici` dispatcher. So a request that needs a certificate, a CA or no
//! verification imports `fetch` from the `undici` package instead, where the
//! dispatcher and the client are guaranteed to be the same version.

use std::collections::BTreeSet;

use api_client_core::{HttpMethod, MultipartValue, RequestBody};
use api_client_http_engine::{AuthPlan, ChallengeAuth};

use crate::text::{FOLDED_NOTE, comment, file_name, fold, form_encoded, indent};
use crate::{IdentityKind, Plan};

pub fn generate(plan: &Plan) -> String {
    let mut notes = plan.notes();
    let mut fs: BTreeSet<&str> = BTreeSet::new();
    let mut blocks: Vec<String> = Vec::new();
    let mut options: Vec<String> = vec![format!("method: {},", literal(plan.method()))];

    blocks.push(format!("const url = {};", literal(&plan.effective.url)));

    let (headers, collided) = fold(&plan.headers());
    if collided {
        notes.push(FOLDED_NOTE.to_string());
    }
    let mut rows: Vec<String> = headers
        .iter()
        .map(|(name, value)| format!("{}: {},", literal(name), literal(value)))
        .collect();
    if let Some((username, password)) = plan.basic() {
        rows.push(format!(
            "\"Authorization\": \"Basic \" + Buffer.from({}).toString(\"base64\"),",
            literal(&format!("{username}:{password}"))
        ));
    }
    if !rows.is_empty() {
        blocks.push(format!(
            "const headers = {{\n{}\n}};",
            indent(&rows.join("\n"), 2)
        ));
        options.push("headers,".to_string());
    }

    // fetch answers no challenge at all. The first response is the 401, and
    // the snippet says so rather than looking as if it would log in.
    match &plan.effective.auth {
        AuthPlan::Challenge(ChallengeAuth::Digest { .. }) => notes.push(
            "fetch cannot answer a Digest challenge, so this request will get the server's 401. \
             Answer it with a Digest package, or send the Authorization header it computes."
                .to_string(),
        ),
        AuthPlan::Challenge(ChallengeAuth::Ntlm { .. }) => notes.push(
            "fetch cannot answer an NTLM challenge, so this request will get the server's 401. \
             NTLM needs a package that holds the connection open across the handshake."
                .to_string(),
        ),
        _ => {}
    }

    let multipart = body(plan, &mut blocks, &mut fs, &mut notes);
    if !matches!(plan.request.body, RequestBody::None) {
        options.push("body,".to_string());
    }

    if !plan.options.follow_redirects {
        // In Node, unlike a browser, "manual" hands back the 3xx itself.
        options.push("redirect: \"manual\",".to_string());
    }
    let timeout = plan.timeout_ms();
    if timeout > 0 {
        options.push(format!("signal: AbortSignal.timeout({timeout}),"));
    }

    let mut roots = false;
    let mut undici: Vec<&str> = Vec::new();
    if let Some(connect) = tls(plan, &mut fs, &mut roots) {
        blocks.push(format!(
            "const dispatcher = new Agent({{\n  connect: {{\n{}\n  }},\n}});",
            indent(&connect.join("\n"), 4)
        ));
        options.push("dispatcher,".to_string());
        notes.push(
            "Node's built-in fetch takes no TLS options, so this uses fetch from the undici \
             package, which does: npm install undici"
                .to_string(),
        );

        // undici's fetch checks a FormData is its own, so the form comes from
        // there too.
        undici = if multipart {
            vec!["Agent", "FormData", "fetch"]
        } else {
            vec!["Agent", "fetch"]
        };
    }

    let mut imports: Vec<String> = Vec::new();
    if !fs.is_empty() {
        let names = fs.iter().copied().collect::<Vec<_>>().join(", ");
        imports.push(format!("import {{ {names} }} from \"node:fs\";"));
    }
    if roots {
        imports.push("import { rootCertificates } from \"node:tls\";".to_string());
    }
    if !undici.is_empty() {
        let names = undici.join(", ");
        imports.push(format!("import {{ {names} }} from \"undici\";"));
    }

    blocks.push(format!(
        "const response = await fetch(url, {{\n{}\n}});\n\
         console.log(response.status);\n\
         console.log(await response.text());",
        indent(&options.join("\n"), 2)
    ));

    let mut out = String::new();
    for note in notes {
        out.push_str(&comment("// ", &note));
        out.push('\n');
    }
    if !imports.is_empty() {
        out.push_str(&imports.join("\n"));
        out.push_str("\n\n");
    }
    out.push_str(&blocks.join("\n\n"));
    out.push('\n');
    out
}

/// The `connect` options for an undici `Agent`, or `None` when the defaults
/// will do and the built-in `fetch` can be used as it is.
///
/// `roots` is set when the CA list starts from Node's bundled roots.
fn tls(plan: &Plan, fs: &mut BTreeSet<&str>, roots: &mut bool) -> Option<Vec<String>> {
    let mut connect: Vec<String> = Vec::new();

    if let Some((identity, kind)) = plan.identity() {
        let file = format!("readFileSync({})", literal(identity.path.trim()));
        fs.insert("readFileSync");
        match kind {
            IdentityKind::Pkcs12 => {
                connect.push(format!("pfx: {file},"));
                if let Some(password) = &identity.password {
                    connect.push(format!("passphrase: {},", literal(password)));
                }
            }
            // The engine's PEM holds the certificate and the key together, and
            // Node picks each out of the same file.
            IdentityKind::Pem => {
                connect.push(format!("cert: {file},"));
                connect.push(format!("key: {file},"));
            }
        }
    }

    // Verification off wins over a CA file: there is nothing left to check.
    if plan.accepts_invalid_certs() {
        connect.push("rejectUnauthorized: false,".to_string());
    } else {
        let extra = plan.extra_ca_files();
        if !extra.is_empty() {
            fs.insert("readFileSync");
            /*
             * `ca` replaces Node's bundled roots. The app adds to the system's
             * instead, so when it does, the bundled roots go back in first —
             * the nearest Node comes to the same list.
             */
            let mut files: Vec<String> = Vec::new();
            if plan.merges_ca_files() {
                *roots = true;
                files.push("...rootCertificates".to_string());
            }
            for path in extra {
                files.push(format!("readFileSync({})", literal(path)));
            }
            connect.push(format!("ca: [{}],", files.join(", ")));
        }
    }

    (!connect.is_empty()).then_some(connect)
}

/// Adds `const body = ...` to the blocks, and says whether it is a form.
fn body(
    plan: &Plan,
    blocks: &mut Vec<String>,
    fs: &mut BTreeSet<&str>,
    notes: &mut Vec<String>,
) -> bool {
    if !matches!(plan.request.body, RequestBody::None)
        && matches!(plan.request.method, HttpMethod::Get | HttpMethod::Head)
    {
        notes.push(format!(
            "fetch will not send a body with {}, and throws rather than drop it. Remove the \
             body or change the method.",
            plan.method()
        ));
    }

    match &plan.request.body {
        RequestBody::None => false,

        // The text exactly as typed, not `JSON.stringify` of a parsed copy,
        // which would reformat numbers and send something nobody wrote.
        RequestBody::Json { text } | RequestBody::Text { text, .. } => {
            blocks.push(format!("const body = {};", literal(text)));
            false
        }

        // Already encoded, rather than through `URLSearchParams`, which
        // escapes to its own taste.
        RequestBody::UrlEncoded { entries } => {
            let encoded = form_encoded(entries);
            blocks.push(format!("const body = {};", literal(&encoded)));
            false
        }

        RequestBody::Multipart { entries } => {
            let mut lines = vec!["const body = new FormData();".to_string()];
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
                        fs.insert("openAsBlob");
                        let blob = match content_type {
                            Some(mime) => format!(
                                "await openAsBlob({}, {{ type: {} }})",
                                literal(path),
                                literal(mime)
                            ),
                            None => format!("await openAsBlob({})", literal(path)),
                        };
                        lines.push(format!(
                            "body.append({name}, {blob}, {});",
                            literal(&file_name(path, given.as_ref()))
                        ));
                    }
                }
            }
            // FormData writes its own content type, boundary and all.
            blocks.push(lines.join("\n"));
            true
        }
    }
}

/// A JavaScript string literal.
///
/// Text that runs over lines is written as it reads, in a template literal. A
/// carriage return inside one is normalised away by the parser, so anything
/// carrying one is escaped onto a single line instead, where every character
/// is explicit.
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
    use super::literal;

    #[test]
    fn a_backslash_and_a_quote_are_escaped() {
        assert_eq!(literal(r#"a\b"c"#), r#""a\\b\"c""#);
    }

    #[test]
    fn a_body_over_several_lines_is_written_as_it_reads() {
        assert_eq!(literal("{\n  \"a\": 1\n}"), "`{\n  \"a\": 1\n}`");
    }

    /// A template literal would read `${name}` as code, and a backtick as its end.
    #[test]
    fn a_template_literal_holds_its_own_delimiters_as_text() {
        assert_eq!(literal("a `b`\n${c}"), "`a \\`b\\`\n\\${c}`");
    }

    #[test]
    fn a_carriage_return_is_escaped_rather_than_written() {
        assert_eq!(literal("a\r\nb"), r#""a\r\nb""#);
    }
}
