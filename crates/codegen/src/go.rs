//! Go, through `net/http`.
//!
//! There is one way to make a request in Go and everybody uses it, so there is
//! nothing to choose between here. The error handling is the plain `if err !=
//! nil` that a program would really be written with rather than anything
//! shorter, because the snippet is meant to be pasted into one.

use api_client_core::{MultipartValue, RequestBody};
use api_client_http_engine::{AuthPlan, ChallengeAuth};

use crate::text::{KEEP, comment, file_name, form_encoded, keep, settle};
use crate::{IdentityKind, Plan};

/// One tab on every line, leaving the inside of a literal alone. Go is written
/// with tabs and `gofmt` would put them back whatever this did.
fn tabs(block: &str) -> String {
    block
        .lines()
        .map(|line| {
            if line.is_empty() || line.starts_with(KEEP) {
                line.to_string()
            } else {
                format!("\t{line}")
            }
        })
        .collect::<Vec<_>>()
        .join("\n")
}

pub fn generate(plan: &Plan) -> String {
    let mut notes = plan.notes();
    let mut imports: Vec<&str> = vec!["fmt", "io", "net/http"];
    let mut statements: Vec<String> = Vec::new();

    let payload = body(plan, &mut imports, &mut statements, &mut notes);

    statements.push(format!(
        "req, err := http.NewRequest({}, {}, {})\nif err != nil {{\n\tpanic(err)\n}}",
        literal(plan.method()),
        literal(&plan.effective.url),
        payload
    ));

    let mut headers: Vec<String> = Vec::new();
    // The writer generated the boundary, so only it can name the content type.
    if matches!(plan.request.body, RequestBody::Multipart { .. }) {
        headers.push("req.Header.Set(\"Content-Type\", writer.FormDataContentType())".to_string());
    }
    for (name, value) in plan.headers() {
        // `Add` rather than `Set`, so two rows of one name stay two headers.
        headers.push(format!(
            "req.Header.Add({}, {})",
            literal(&name),
            literal(&value)
        ));
    }
    if let Some((username, password)) = plan.basic() {
        headers.push(format!(
            "req.SetBasicAuth({}, {})",
            literal(username),
            literal(password)
        ));
    }
    if !headers.is_empty() {
        statements.push(headers.join("\n"));
    }

    if let AuthPlan::Challenge(challenge) = &plan.effective.auth {
        notes.push(match challenge {
            ChallengeAuth::Digest { .. } => {
                "net/http does not answer a digest challenge. `github.com/icholy/digest` adds a \
                 RoundTripper that does."
                    .to_string()
            }
            ChallengeAuth::Ntlm { .. } => "net/http does not answer an NTLM challenge. \
                 `github.com/Azure/go-ntlmssp` adds a RoundTripper that does."
                .to_string(),
        });
    }

    statements.push(client(plan, &mut imports, &mut notes));
    statements.push(
        "res, err := client.Do(req)\nif err != nil {\n\tpanic(err)\n}\ndefer res.Body.Close()"
            .to_string(),
    );
    statements.push(
        "resBody, err := io.ReadAll(res.Body)\nif err != nil {\n\tpanic(err)\n}\n\n\
         fmt.Println(res.StatusCode)\nfmt.Println(string(resBody))"
            .to_string(),
    );

    let mut out = String::new();
    for note in notes {
        out.push_str(&comment("// ", &note));
        out.push('\n');
    }
    out.push_str("package main\n\nimport (\n");
    imports.sort_unstable();
    imports.dedup();
    for import in &imports {
        out.push_str(&format!("\t{}\n", literal(import)));
    }
    out.push_str(")\n\nfunc main() {\n");
    out.push_str(&tabs(&statements.join("\n\n")));
    out.push_str("\n}\n");
    settle(out)
}

/// The client, which carries the timeout, the redirect policy and the TLS
/// settings — everything that belongs to the client rather than the request.
fn client(plan: &Plan, imports: &mut Vec<&'static str>, notes: &mut Vec<String>) -> String {
    let mut lines: Vec<String> = Vec::new();
    let mut fields: Vec<String> = Vec::new();

    let timeout = plan.timeout_ms();
    if timeout > 0 {
        imports.push("time");
        fields.push(format!("Timeout: {} * time.Millisecond,", timeout));
    }

    let mut tls: Vec<String> = Vec::new();
    if plan.accepts_invalid_certs() {
        tls.push("InsecureSkipVerify: true,".to_string());
    }

    let extra = plan.extra_ca_files();
    if !extra.is_empty() {
        imports.push("crypto/tls");
        imports.push("crypto/x509");
        imports.push("os");

        // Added to the system pool, which is what the app does, rather than
        // replacing it — `SystemCertPool` is the whole difference.
        let pool = if plan.merges_ca_files() {
            "pool, err := x509.SystemCertPool()\nif err != nil {\n\tpanic(err)\n}".to_string()
        } else {
            "pool := x509.NewCertPool()".to_string()
        };
        lines.push(pool);
        for path in &extra {
            lines.push(format!(
                "ca, err := os.ReadFile({})\nif err != nil {{\n\tpanic(err)\n}}\n\
                 pool.AppendCertsFromPEM(ca)",
                literal(path)
            ));
        }
        tls.push("RootCAs: pool,".to_string());
    }

    if let Some((identity, kind)) = plan.identity() {
        imports.push("crypto/tls");
        let path = identity.path.trim();
        match kind {
            IdentityKind::Pem => {
                lines.push(format!(
                    "cert, err := tls.LoadX509KeyPair({}, {})\nif err != nil {{\n\tpanic(err)\n}}",
                    literal(path),
                    literal(path)
                ));
                tls.push("Certificates: []tls.Certificate{cert},".to_string());
            }
            IdentityKind::Pkcs12 => {
                // Go's standard library has no PKCS#12 reader that yields a key.
                notes.push(format!(
                    "Go cannot read a PKCS#12 bundle from the standard library. Convert it \
                     first, which will ask for the bundle's password:\n  openssl pkcs12 -in \
                     {path} -out {}.pem -nodes",
                    path.trim_end_matches(".p12").trim_end_matches(".pfx")
                ));
                let converted = format!(
                    "{}.pem",
                    path.trim_end_matches(".p12").trim_end_matches(".pfx")
                );
                lines.push(format!(
                    "cert, err := tls.LoadX509KeyPair({}, {})\nif err != nil {{\n\tpanic(err)\n}}",
                    literal(&converted),
                    literal(&converted)
                ));
                tls.push("Certificates: []tls.Certificate{cert},".to_string());
            }
        }
    }

    if !tls.is_empty() {
        imports.push("crypto/tls");
        fields.push(format!(
            "Transport: &http.Transport{{\n\tTLSClientConfig: &tls.Config{{\n{}\n\t}},\n}},",
            tabs(&tabs(&tls.join("\n")))
        ));
    }

    let declaration = if fields.is_empty() {
        "client := &http.Client{}".to_string()
    } else {
        format!("client := &http.Client{{\n{}\n}}", tabs(&fields.join("\n")))
    };
    lines.push(declaration);

    if !plan.options.follow_redirects {
        lines.push(
            "client.CheckRedirect = func(req *http.Request, via []*http.Request) error {\n\t\
             return http.ErrUseLastResponse\n}"
                .to_string(),
        );
    }

    lines.join("\n\n")
}

/// The body, as the reader `NewRequest` takes. Anything that needs setting up
/// first is pushed onto `statements`.
fn body(
    plan: &Plan,
    imports: &mut Vec<&'static str>,
    statements: &mut Vec<String>,
    notes: &mut Vec<String>,
) -> String {
    match &plan.request.body {
        RequestBody::None => "nil".to_string(),

        RequestBody::Json { text } | RequestBody::Text { text, .. } => {
            imports.push("strings");
            statements.push(format!("payload := strings.NewReader({})", literal(text)));
            "payload".to_string()
        }

        RequestBody::UrlEncoded { entries } => {
            imports.push("strings");
            statements.push(format!(
                "payload := strings.NewReader({})",
                literal(&form_encoded(entries))
            ));
            "payload".to_string()
        }

        RequestBody::Multipart { entries } => {
            imports.push("bytes");
            imports.push("mime/multipart");

            let mut lines = vec![
                "payload := &bytes.Buffer{}".to_string(),
                "writer := multipart.NewWriter(payload)".to_string(),
            ];
            for entry in entries.iter().filter(|entry| entry.is_active()) {
                let name = literal(entry.name.trim());
                match &entry.value {
                    MultipartValue::Text { value } => {
                        lines.push(format!(
                            "if err := writer.WriteField({name}, {}); err != nil {{\n\t\
                             panic(err)\n}}",
                            literal(value)
                        ));
                    }
                    MultipartValue::File {
                        path,
                        file_name: given,
                        content_type,
                    } => {
                        imports.push("os");
                        if content_type.is_some() {
                            notes.push(format!(
                                "The part `{}` is sent without the content type set for it: \
                                 `CreateFormFile` writes its own.",
                                entry.name.trim()
                            ));
                        }
                        lines.push(format!(
                            "part, err := writer.CreateFormFile({name}, {})\n\
                             if err != nil {{\n\tpanic(err)\n}}\n\
                             file, err := os.Open({})\n\
                             if err != nil {{\n\tpanic(err)\n}}\n\
                             if _, err := io.Copy(part, file); err != nil {{\n\tpanic(err)\n}}\n\
                             file.Close()",
                            literal(&file_name(path, given.as_ref())),
                            literal(path)
                        ));
                    }
                }
            }
            lines.push("if err := writer.Close(); err != nil {\n\tpanic(err)\n}".to_string());
            statements.push(lines.join("\n\n"));

            "payload".to_string()
        }
    }
}

/// A Go string literal.
///
/// A backtick string keeps its newlines and has no escapes at all, which is
/// what a JSON body wants — unless the body contains a backtick, which such a
/// string has no way to carry.
fn literal(value: &str) -> String {
    if value.contains('\n') && !value.contains('`') && !value.contains('\r') {
        return keep(&format!("`{value}`"));
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
    fn a_body_over_several_lines_is_a_raw_string() {
        assert_eq!(
            crate::text::settle(literal("{\n  \"a\": 1\n}")),
            "`{\n  \"a\": 1\n}`"
        );
    }

    /// A raw string cannot carry a backtick, so that one is escaped instead.
    #[test]
    fn a_backtick_forces_the_quoted_form() {
        assert_eq!(literal("a\n`b"), "\"a\\n`b\"");
    }
}
