//! curl, in long options.
//!
//! `--request` and `--header` rather than `-X` and `-H`: a snippet is read more
//! often than it is typed, and the long names say what they do.

use api_client_core::{HttpMethod, MultipartValue, RequestBody};
use api_client_http_engine::{AuthPlan, ChallengeAuth};

use crate::text::{comment, file_name, form_encoded, seconds};
use crate::{IdentityKind, Plan};

pub fn generate(plan: &Plan) -> String {
    let mut notes = plan.notes();
    let mut arguments: Vec<String> = Vec::new();

    /*
     * `-X HEAD` leaves curl waiting for a body that a HEAD response never has,
     * which looks exactly like a hung server. `--head` is the same request,
     * correctly.
     */
    if plan.request.method == HttpMethod::Head {
        arguments.push("--head".to_string());
    } else {
        arguments.push(format!("--request {}", plan.method()));
    }

    arguments.push(format!("--url {}", quote(&plan.effective.url)));

    for (name, value) in plan.headers() {
        arguments.push(format!("--header {}", quote(&format!("{name}: {value}"))));
    }

    arguments.extend(auth(plan));
    arguments.extend(body(plan));

    // The app follows redirects unless it is told not to; curl does not unless
    // it is told to.
    if plan.options.follow_redirects {
        arguments.push("--location".to_string());
    }
    if plan.accepts_invalid_certs() {
        arguments.push("--insecure".to_string());
    }
    arguments.extend(tls(plan, &mut notes));
    let timeout = plan.timeout_ms();
    if timeout > 0 {
        arguments.push(format!("--max-time {}", seconds(timeout)));
    }

    let mut out = String::new();
    for note in notes {
        out.push_str(&comment("# ", &note));
        out.push('\n');
    }
    out.push_str("curl ");
    out.push_str(&arguments.join(" \\\n  "));
    out.push('\n');
    out
}

fn auth(plan: &Plan) -> Vec<String> {
    if let Some((username, password)) = plan.basic() {
        return vec![format!(
            "--user {}",
            quote(&format!("{username}:{password}"))
        )];
    }

    match &plan.effective.auth {
        AuthPlan::Challenge(ChallengeAuth::Digest { username, password }) => vec![
            "--digest".to_string(),
            format!("--user {}", quote(&format!("{username}:{password}"))),
        ],
        // curl carries the domain in the user field, and has nowhere at all to
        // put a workstation name.
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
            vec![
                "--ntlm".to_string(),
                format!("--user {}", quote(&format!("{account}:{password}"))),
            ]
        }
        _ => Vec::new(),
    }
}

/// The trust and identity settings, which belong to the client rather than to
/// the request, and which a snippet therefore has to state for itself.
fn tls(plan: &Plan, notes: &mut Vec<String>) -> Vec<String> {
    let mut arguments = Vec::new();

    if let Some((identity, kind)) = plan.identity() {
        if kind == IdentityKind::Pkcs12 {
            arguments.push("--cert-type P12".to_string());
        }
        arguments.push(format!("--cert {}", quote(identity.path.trim())));
        // `--pass` rather than curl's `certificate:password` form, which cannot
        // be told from the colon in a Windows path.
        if let Some(password) = &identity.password {
            arguments.push(format!("--pass {}", quote(password)));
        }
    }

    let extra = plan.extra_ca_files();
    if let Some(first) = extra.first() {
        arguments.push(format!("--cacert {}", quote(first)));

        if extra.len() > 1 {
            notes.push(format!(
                "curl reads one CA file. {} were set, so only the first is here; concatenate them \
                 into one PEM to use them all.",
                extra.len()
            ));
        }
        if plan.merges_ca_files() {
            notes.push(
                "--cacert replaces the system trust store rather than adding to it, which is what \
                 the app does. A server with a public certificate will not verify against this \
                 file alone."
                    .to_string(),
            );
        }
    }

    arguments
}

fn body(plan: &Plan) -> Vec<String> {
    match &plan.request.body {
        RequestBody::None => Vec::new(),

        // `--data-raw` rather than `--data`: the latter reads a leading `@` as
        // a file name, and strips the newlines out of what it is given.
        RequestBody::Json { text } | RequestBody::Text { text, .. } => {
            vec![format!("--data-raw {}", quote(text))]
        }

        /*
         * Written out already encoded rather than as `--data-urlencode` pairs.
         * That option escapes the value and leaves the name alone, which is not
         * what a form body is, and the difference only shows up on the one name
         * that needed escaping.
         */
        RequestBody::UrlEncoded { entries } => {
            vec![format!("--data-raw {}", quote(&form_encoded(entries)))]
        }

        RequestBody::Multipart { entries } => entries
            .iter()
            .filter(|entry| entry.is_active())
            .map(|entry| {
                let name = entry.name.trim();
                let part = match &entry.value {
                    MultipartValue::Text { value } => format!("{name}={value}"),
                    MultipartValue::File {
                        path,
                        file_name: given,
                        content_type,
                    } => {
                        let mut part = format!("{name}=@{path}");
                        if let Some(mime) = content_type {
                            part.push_str(&format!(";type={mime}"));
                        }
                        part.push_str(&format!(";filename={}", file_name(path, given.as_ref())));
                        part
                    }
                };
                format!("--form {}", quote(&part))
            })
            .collect(),
    }
}

/// Single quotes, which the shell leaves entirely alone. A quote inside the
/// value ends the string, escapes itself, and opens a new one — the usual
/// `'\''` — so anything at all can be written this way.
fn quote(value: &str) -> String {
    format!("'{}'", value.replace('\'', "'\\''"))
}

#[cfg(test)]
mod tests {
    use super::quote;

    #[test]
    fn a_quote_inside_a_value_survives() {
        assert_eq!(quote("it's"), r"'it'\''s'");
    }
}
