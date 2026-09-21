//! Rust, through reqwest.
//!
//! There is no HTTP client in the standard library, and reqwest is what a Rust
//! program that makes a request almost always uses — including this one, which
//! is the reason the generated code and the Send button behave alike down to
//! the redirect policy.

use api_client_core::{MultipartValue, RequestBody};
use api_client_http_engine::{AuthPlan, ChallengeAuth};

use crate::text::{comment, file_name, form_encoded, indent, keep, settle};
use crate::{IdentityKind, Plan};

/// Blocking or async, which in reqwest is two different clients.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Style {
    Blocking,
    Async,
}

impl Style {
    /// `reqwest::blocking` or `reqwest`, which is the only difference in the
    /// names; the builders either side of it are spelled the same.
    fn module(self) -> &'static str {
        match self {
            Style::Blocking => "reqwest::blocking",
            Style::Async => "reqwest",
        }
    }

    fn wait(self) -> &'static str {
        match self {
            Style::Blocking => "",
            Style::Async => ".await",
        }
    }
}

pub fn generate(plan: &Plan, style: Style) -> String {
    let mut notes = plan.notes();
    let mut statements: Vec<String> = Vec::new();

    statements.push(client(plan, style, &mut notes));

    let mut call: Vec<String> = vec![format!(
        ".{}({})",
        plan.method().to_lowercase(),
        literal(&plan.effective.url)
    )];

    for (name, value) in plan.headers() {
        call.push(format!(".header({}, {})", literal(&name), literal(&value)));
    }
    if let Some((username, password)) = plan.basic() {
        call.push(format!(
            ".basic_auth({}, Some({}))",
            literal(username),
            literal(password)
        ));
    }
    if let AuthPlan::Challenge(challenge) = &plan.effective.auth {
        notes.push(match challenge {
            ChallengeAuth::Digest { .. } => "reqwest cannot answer a digest challenge. The \
                 `diqwest` crate adds it, as `send_with_digest_auth`."
                .to_string(),
            ChallengeAuth::Ntlm { .. } => {
                "reqwest cannot answer an NTLM challenge; that scheme needs a client that can \
                 hold a connection open across it."
                    .to_string()
            }
        });
    }

    call.extend(body(plan, &mut statements, &mut notes, style));
    call.push(format!(".send(){}?", style.wait()));

    statements.push(format!(
        "let response = client\n{};",
        indent(&call.join("\n"), 4)
    ));
    statements.push(format!(
        "println!(\"{{}}\", response.status());\nprintln!(\"{{}}\", response.text(){}?);",
        style.wait()
    ));

    let mut out = String::new();
    for note in notes {
        out.push_str(&comment("// ", &note));
        out.push('\n');
    }
    out.push_str(&preamble(style));
    out.push_str(&indent(&statements.join("\n\n"), 4));
    out.push_str("\n\n    Ok(())\n}\n");
    settle(out)
}

fn preamble(style: Style) -> String {
    match style {
        Style::Blocking => "// Cargo.toml: reqwest = { version = \"0.12\", features = \
                            [\"blocking\", \"json\", \"multipart\"] }\n\nfn main() -> \
                            Result<(), Box<dyn std::error::Error>> {\n"
            .to_string(),
        Style::Async => "// Cargo.toml: reqwest = { version = \"0.12\", features = [\"json\", \
                         \"multipart\"] }, tokio = { version = \"1\", features = \
                         [\"full\"] }\n\n#[tokio::main]\nasync fn main() -> Result<(), Box<dyn \
                         std::error::Error>> {\n"
            .to_string(),
    }
}

/// The client, which is where everything that is a setting rather than a part
/// of the request ends up.
fn client(plan: &Plan, style: Style, notes: &mut Vec<String>) -> String {
    let mut builder: Vec<String> = Vec::new();

    let timeout = plan.timeout_ms();
    if timeout > 0 {
        builder.push(format!(
            ".timeout(std::time::Duration::from_millis({timeout}))"
        ));
    }
    if !plan.options.follow_redirects {
        builder.push(".redirect(reqwest::redirect::Policy::none())".to_string());
    }
    if plan.accepts_invalid_certs() {
        builder.push(".danger_accept_invalid_certs(true)".to_string());
    }

    for path in plan.extra_ca_files() {
        builder.push(format!(
            ".add_root_certificate(reqwest::Certificate::from_pem(&std::fs::read({})?)?)",
            literal(path)
        ));
    }
    if !plan.merges_ca_files() && !plan.extra_ca_files().is_empty() {
        builder.push(".tls_built_in_root_certs(false)".to_string());
    }

    if let Some((identity, kind)) = plan.identity() {
        let path = identity.path.trim();
        match kind {
            IdentityKind::Pem => builder.push(format!(
                ".identity(reqwest::Identity::from_pem(&std::fs::read({})?)?)",
                literal(path)
            )),
            IdentityKind::Pkcs12 => {
                // reqwest reads PKCS#12 only through its native-tls backend.
                notes.push(
                    "`Identity::from_pkcs12_der` needs reqwest's `native-tls` feature; the \
                     default rustls backend reads PEM only."
                        .to_string(),
                );
                builder.push(format!(
                    ".identity(reqwest::Identity::from_pkcs12_der(&std::fs::read({})?, {})?)",
                    literal(path),
                    literal(identity.password.as_deref().unwrap_or(""))
                ));
            }
        }
    }

    if builder.is_empty() {
        return format!("let client = {}::Client::new();", style.module());
    }
    format!(
        "let client = {}::Client::builder()\n{}\n    .build()?;",
        style.module(),
        indent(&builder.join("\n"), 4)
    )
}

/// The body, as the calls that add it to the request being built.
fn body(
    plan: &Plan,
    statements: &mut Vec<String>,
    notes: &mut Vec<String>,
    style: Style,
) -> Vec<String> {
    match &plan.request.body {
        RequestBody::None => Vec::new(),

        RequestBody::Json { text } | RequestBody::Text { text, .. } => {
            vec![format!(".body({})", literal(text))]
        }

        // Written out already encoded rather than through `.form()`, which
        // would encode a second time to its own taste.
        RequestBody::UrlEncoded { entries } => {
            vec![format!(".body({})", literal(&form_encoded(entries)))]
        }

        RequestBody::Multipart { entries } => {
            let mut lines = vec![format!(
                "let form = {}::multipart::Form::new()",
                module(style)
            )];
            for entry in entries.iter().filter(|entry| entry.is_active()) {
                let name = literal(entry.name.trim());
                match &entry.value {
                    MultipartValue::Text { value } => {
                        lines.push(format!("    .text({name}, {})", literal(value)));
                    }
                    MultipartValue::File {
                        path,
                        file_name: given,
                        content_type,
                    } => {
                        let mut part = format!(
                            "{}::multipart::Part::bytes(std::fs::read({})?)\n        \
                             .file_name({})",
                            module(style),
                            literal(path),
                            literal(&file_name(path, given.as_ref()))
                        );
                        if let Some(mime) = content_type {
                            part.push_str(&format!("\n        .mime_str({})?", literal(mime)));
                        }
                        lines.push(format!(
                            "    .part(\n        {name},\n        {part},\n    )"
                        ));
                    }
                }
            }
            lines.push("    ;".to_string());
            statements.push(lines.join("\n").replace("\n    ;", ";"));
            let _ = notes;
            vec![".multipart(form)".to_string()]
        }
    }
}

fn module(style: Style) -> &'static str {
    style.module()
}

/// A Rust string literal.
///
/// A raw string, because a JSON body is full of quotes and backslashes and a
/// path on Windows is nothing else. The hashes are counted so that a body
/// carrying `"#` of its own still closes in the right place.
///
/// Anything that runs over lines goes the same way even when it has neither, so
/// that it carries the mark keeping its own indentation out of the indenter's
/// hands. An ordinary Rust string would hold those lines perfectly well and
/// then be laid out along with the code around it.
fn literal(value: &str) -> String {
    if !value.contains('"') && !value.contains('\\') && !value.contains('\n') {
        return format!("\"{value}\"");
    }

    let mut hashes = 1;
    while value.contains(&format!("\"{}", "#".repeat(hashes))) {
        hashes += 1;
    }
    let fence = "#".repeat(hashes);
    keep(&format!("r{fence}\"{value}\"{fence}"))
}

#[cfg(test)]
mod tests {
    use super::literal;

    #[test]
    fn a_plain_value_is_a_plain_literal() {
        assert_eq!(literal("application/json"), "\"application/json\"");
    }

    #[test]
    fn anything_with_a_quote_or_a_backslash_is_raw() {
        assert_eq!(literal(r#"{"a": 1}"#), r##"r#"{"a": 1}"#"##);
        assert_eq!(literal(r"C:\temp"), r##"r#"C:\temp"#"##);
    }

    /// A body over several lines carries the mark that keeps its own
    /// indentation out of the indenter's hands.
    #[test]
    fn a_body_over_several_lines_is_left_where_it_is() {
        assert_eq!(
            crate::text::settle(literal("{\n  \"a\": 1\n}")),
            "r#\"{\n  \"a\": 1\n}\"#"
        );
    }

    /// A body carrying the closing fence needs a longer one.
    #[test]
    fn the_fence_grows_past_what_the_body_contains() {
        assert_eq!(literal(r##"say "#"##), r###"r##"say "#"##"###);
    }
}
