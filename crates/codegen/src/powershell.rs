//! PowerShell, through `Invoke-RestMethod`.
//!
//! The parameters are splatted rather than written on one long line with
//! backtick continuations. A stray space after a backtick breaks the command in
//! a way that is genuinely hard to see, and a hashtable has neither the problem
//! nor the line length.

use api_client_core::{MultipartValue, RequestBody};
use api_client_http_engine::{AuthPlan, ChallengeAuth};

use crate::text::{FOLDED_NOTE, comment, fold, form_encoded, indent, seconds, take_content_type};
use crate::{IdentityKind, Plan};

pub fn generate(plan: &Plan) -> String {
    let mut notes = plan.notes();
    let mut blocks: Vec<String> = Vec::new();

    let (mut headers, collided) = fold(&plan.headers());
    if collided {
        notes.push(FOLDED_NOTE.to_string());
    }
    let content_type = take_content_type(&mut headers);

    let basic = plan.basic();
    if !headers.is_empty() || basic.is_some() {
        blocks.push(hashtable("$headers", &headers));
    }

    if let Some((username, password)) = basic {
        blocks.push(format!(
            "$pair = [Text.Encoding]::UTF8.GetBytes({})\n\
             $headers['Authorization'] = 'Basic ' + [Convert]::ToBase64String($pair)",
            quote(&format!("{username}:{password}"))
        ));
    }

    let credential = match &plan.effective.auth {
        AuthPlan::Challenge(ChallengeAuth::Digest { username, password }) => {
            Some(credential(username, password))
        }
        AuthPlan::Challenge(ChallengeAuth::Ntlm {
            username,
            password,
            domain,
            workstation,
        }) => {
            if !workstation.trim().is_empty() {
                notes.push(
                    "PowerShell has nowhere to put an NTLM workstation name, so it is not sent."
                        .to_string(),
                );
            }
            let account = if domain.trim().is_empty() {
                username.clone()
            } else {
                format!("{domain}\\{username}")
            };
            Some(credential(&account, password))
        }
        _ => None,
    };
    if let Some(block) = &credential {
        blocks.push(block.clone());
    }

    let certificate = certificate(plan, &mut notes);
    if let Some(block) = &certificate {
        blocks.push(block.clone());
    }

    let body = body(plan, &mut notes);
    if let Some(block) = &body {
        blocks.push(block.statement.clone());
    }

    let mut parameters: Vec<(String, String)> = vec![
        ("Uri".to_string(), quote(&plan.effective.url)),
        ("Method".to_string(), quote(&title_case(plan.method()))),
    ];
    if !headers.is_empty() || basic.is_some() {
        parameters.push(("Headers".to_string(), "$headers".to_string()));
    }
    if let Some(value) = &content_type {
        parameters.push(("ContentType".to_string(), quote(value)));
    }
    if let Some(block) = &body {
        parameters.push((block.parameter.to_string(), block.variable.to_string()));
    }
    if credential.is_some() {
        parameters.push(("Credential".to_string(), "$credential".to_string()));
    }
    if !plan.options.follow_redirects {
        parameters.push(("MaximumRedirection".to_string(), "0".to_string()));
    }
    if plan.accepts_invalid_certs() {
        parameters.push(("SkipCertificateCheck".to_string(), "$true".to_string()));
    }
    if certificate.is_some() {
        parameters.push(("Certificate".to_string(), "$certificate".to_string()));
    }
    let timeout = plan.timeout_ms();
    if timeout > 0 {
        parameters.push(("TimeoutSec".to_string(), seconds(timeout)));
    }

    // Parameter names are bare words; only the header keys need quoting.
    blocks.push(write_hashtable("$parameters", &parameters));
    blocks.push("$response = Invoke-RestMethod @parameters\n$response".to_string());

    let mut out = String::new();
    for note in notes {
        out.push_str(&comment("# ", &note));
        out.push('\n');
    }
    out.push_str(&blocks.join("\n\n"));
    out.push('\n');
    out
}

/// A body, and the parameter it is passed as: `-Body` for everything except a
/// multipart form, which has its own.
struct Body {
    statement: String,
    variable: &'static str,
    parameter: &'static str,
}

fn body(plan: &Plan, notes: &mut Vec<String>) -> Option<Body> {
    match &plan.request.body {
        RequestBody::None => None,

        RequestBody::Json { text } | RequestBody::Text { text, .. } => Some(Body {
            statement: format!("$body = {}", quote(text)),
            variable: "$body",
            parameter: "Body",
        }),

        RequestBody::UrlEncoded { entries } => Some(Body {
            statement: format!("$body = {}", quote(&form_encoded(entries))),
            variable: "$body",
            parameter: "Body",
        }),

        RequestBody::Multipart { entries } => {
            let mut rows: Vec<(String, String)> = Vec::new();
            for entry in entries.iter().filter(|entry| entry.is_active()) {
                let value = match &entry.value {
                    MultipartValue::Text { value } => quote(value),
                    MultipartValue::File {
                        path,
                        file_name,
                        content_type,
                    } => {
                        // `-Form` takes the file's own name and lets the server
                        // sniff the type; neither can be overridden here.
                        if file_name.is_some() || content_type.is_some() {
                            notes.push(format!(
                                "The part `{}` is sent under the file's own name, and without the \
                                 content type set for it: PowerShell's -Form takes neither.",
                                entry.name.trim()
                            ));
                        }
                        format!("Get-Item {}", quote(path))
                    }
                };
                rows.push((entry.name.trim().to_string(), value));
            }

            Some(Body {
                statement: expression_hashtable("$form", &rows),
                variable: "$form",
                parameter: "Form",
            })
        }
    }
}

/// The client certificate, loaded the way .NET loads one.
///
/// The extra CAs have no equivalent at all here: .NET reads trust from the
/// machine and user stores, and `Invoke-RestMethod` has no parameter that adds
/// to it for one call. That is said rather than quietly dropped.
fn certificate(plan: &Plan, notes: &mut Vec<String>) -> Option<String> {
    for path in plan.extra_ca_files() {
        notes.push(format!(
            "PowerShell trusts what the machine trusts, with no per-request CA list. Install \
             `{path}` in the certificate store for this to verify."
        ));
    }

    let (identity, kind) = plan.identity()?;
    let path = identity.path.trim();
    const CLASS: &str = "[System.Security.Cryptography.X509Certificates.X509Certificate2]";

    Some(match kind {
        IdentityKind::Pkcs12 => format!(
            "$certificate = {CLASS}::new({}, {})",
            quote(path),
            quote(identity.password.as_deref().unwrap_or(""))
        ),
        IdentityKind::Pem => {
            notes.push(
                "On Windows, a certificate read from PEM has to be exported to PKCS#12 and loaded \
                 back before SChannel will present it. On Linux and macOS this works as written."
                    .to_string(),
            );
            format!("$certificate = {CLASS}::CreateFromPemFile({})", quote(path))
        }
    })
}

fn credential(account: &str, password: &str) -> String {
    format!(
        "$password = ConvertTo-SecureString {} -AsPlainText -Force\n\
         $credential = New-Object System.Management.Automation.PSCredential({}, $password)",
        quote(password),
        quote(account)
    )
}

/// `$name = @{ 'key' = 'value' }`. Every key is quoted because a name with a
/// dash in it is not a bare word, and every value because it is text.
fn hashtable(name: &str, rows: &[(String, String)]) -> String {
    let quoted: Vec<(String, String)> = rows
        .iter()
        .map(|(key, value)| (quote(key), quote(value)))
        .collect();
    write_hashtable(name, &quoted)
}

/// The same, where the values are expressions rather than text: a form part is
/// a `Get-Item`, not a string.
fn expression_hashtable(name: &str, rows: &[(String, String)]) -> String {
    let quoted: Vec<(String, String)> = rows
        .iter()
        .map(|(key, value)| (quote(key), value.clone()))
        .collect();
    write_hashtable(name, &quoted)
}

fn write_hashtable(name: &str, rows: &[(String, String)]) -> String {
    if rows.is_empty() {
        return format!("{name} = @{{}}");
    }
    let width = rows.iter().map(|(key, _)| key.len()).max().unwrap_or(0);
    let body = rows
        .iter()
        .map(|(key, value)| format!("{key:<width$} = {value}"))
        .collect::<Vec<_>>()
        .join("\n");
    format!("{name} = @{{\n{}\n}}", indent(&body, 4))
}

/// `Invoke-RestMethod` takes any casing, but its own documentation writes them
/// this way and so does every example anyone has read.
fn title_case(method: &str) -> String {
    let mut characters = method.chars();
    match characters.next() {
        Some(first) => format!("{first}{}", characters.as_str().to_lowercase()),
        None => String::new(),
    }
}

/// Single quotes: PowerShell expands `$name` and a backtick inside double
/// quotes, and does neither inside single ones. A quote is doubled to escape
/// itself, and the string may run over as many lines as it likes.
fn quote(value: &str) -> String {
    format!("'{}'", value.replace('\'', "''"))
}

#[cfg(test)]
mod tests {
    use super::{quote, title_case};

    #[test]
    fn a_quote_is_doubled() {
        assert_eq!(quote("it's"), "'it''s'");
    }

    /// A dollar sign is a variable in a double-quoted string and plain text in
    /// a single-quoted one, which is why every string here is single-quoted.
    #[test]
    fn a_dollar_sign_is_left_alone() {
        assert_eq!(quote("$body"), "'$body'");
    }

    #[test]
    fn methods_are_written_as_the_documentation_writes_them() {
        assert_eq!(title_case("DELETE"), "Delete");
    }
}
