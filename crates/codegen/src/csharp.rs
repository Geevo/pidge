//! C#, through `HttpClient`.
//!
//! Top-level statements, so the snippet is the program: everything here runs as
//! written in a `Program.cs` or a `dotnet script` file.
//!
//! The awkward part of this API is that a content header is not a request
//! header. `Content-Type` lives on the content object and throws if it is put
//! anywhere else, so the header list is split before it is written out.

use std::collections::BTreeSet;

use api_client_core::{MultipartValue, RequestBody};
use api_client_http_engine::{AuthPlan, ChallengeAuth};

use crate::text::{comment, file_name, form_encoded, indent, seconds, take_content_type};
use crate::{IdentityKind, Plan};

pub fn generate(plan: &Plan) -> String {
    let mut notes = plan.notes();
    let mut usings: BTreeSet<&str> = BTreeSet::new();
    usings.insert("System");
    usings.insert("System.Net.Http");

    let mut handler: Vec<String> = Vec::new();
    let mut client: Vec<String> = Vec::new();
    let mut statements: Vec<String> = Vec::new();

    if !plan.options.follow_redirects {
        handler.push("AllowAutoRedirect = false,".to_string());
    }

    match &plan.effective.auth {
        AuthPlan::Challenge(ChallengeAuth::Digest { username, password }) => {
            usings.insert("System.Net");
            handler.push(format!(
                "Credentials = new NetworkCredential({}, {}),",
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
            usings.insert("System.Net");
            if !workstation.trim().is_empty() {
                notes.push(
                    "NetworkCredential has nowhere to put a workstation name, so it is not sent."
                        .to_string(),
                );
            }
            handler.push(format!(
                "Credentials = new NetworkCredential({}, {}, {}),",
                literal(username),
                literal(password),
                literal(domain)
            ));
        }
        _ => {}
    }

    let certificate = certificate(plan, &mut usings, &mut notes);
    let insecure = plan.accepts_invalid_certs();
    if handler.is_empty() && !insecure && certificate.is_none() {
        client.push("using var client = new HttpClient();".to_string());
    } else {
        let fields = if handler.is_empty() {
            "var handler = new HttpClientHandler();".to_string()
        } else {
            format!(
                "var handler = new HttpClientHandler\n{{\n{}\n}};",
                indent(&handler.join("\n"), 4)
            )
        };
        client.push(fields);
        if insecure {
            client.push(
                "handler.ServerCertificateCustomValidationCallback =\n    \
                 HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;"
                    .to_string(),
            );
        }
        if let Some(block) = &certificate {
            client.push(block.clone());
        }
        client.push("using var client = new HttpClient(handler);".to_string());
    }

    let timeout = plan.timeout_ms();
    client.push(if timeout == 0 {
        "client.Timeout = Timeout.InfiniteTimeSpan;".to_string()
    } else {
        format!(
            "client.Timeout = TimeSpan.FromSeconds({});",
            seconds(timeout)
        )
    });
    if timeout == 0 {
        usings.insert("System.Threading");
    }
    statements.push(client.join("\n"));

    statements.push(format!(
        "using var request = new HttpRequestMessage(HttpMethod.{}, {});",
        title_case(plan.method()),
        literal(&plan.effective.url)
    ));

    let mut headers = plan.headers();
    let content_type = take_content_type(&mut headers);

    let mut header_lines: Vec<String> = Vec::new();
    if let Some((username, password)) = plan.basic() {
        usings.insert("System.Net.Http.Headers");
        usings.insert("System.Text");
        header_lines.push(format!(
            "request.Headers.Authorization = new AuthenticationHeaderValue(\n    \
             \"Basic\", Convert.ToBase64String(Encoding.UTF8.GetBytes({})));",
            literal(&format!("{username}:{password}"))
        ));
    }
    for (name, value) in &headers {
        /*
         * `TryAddWithoutValidation` rather than `Add`: `Add` parses the value
         * against what it believes the header means and throws on anything it
         * does not like, including a User-Agent it considers malformed. What
         * the user typed is what should be sent.
         */
        header_lines.push(format!(
            "request.Headers.TryAddWithoutValidation({}, {});",
            literal(name),
            literal(value)
        ));
    }
    if !header_lines.is_empty() {
        statements.push(header_lines.join("\n"));
    }

    if let Some(block) = content(plan, &mut usings, content_type.as_deref()) {
        statements.push(block);
    } else if let Some(value) = &content_type {
        // A content type with no body to put it on. .NET has nowhere to hang it.
        notes.push(format!(
            "The Content-Type header ({value}) is not sent: this request has no body, and .NET \
             keeps that header on the body."
        ));
    }

    statements.push(
        "using var response = await client.SendAsync(request);\n\
         Console.WriteLine((int)response.StatusCode);\n\
         Console.WriteLine(await response.Content.ReadAsStringAsync());"
            .to_string(),
    );

    let mut out = String::new();
    for note in notes {
        out.push_str(&comment("// ", &note));
        out.push('\n');
    }
    for using in &usings {
        out.push_str(&format!("using {using};\n"));
    }
    out.push('\n');
    out.push_str(&statements.join("\n\n"));
    out.push('\n');
    out
}

/// The client certificate, added to the handler that will present it.
///
/// The extra CAs have no equivalent: .NET reads trust from the machine and user
/// stores, and the only per-request hook is the validation callback, which
/// replaces the whole check rather than adding one root to it.
fn certificate(
    plan: &Plan,
    usings: &mut BTreeSet<&str>,
    notes: &mut Vec<String>,
) -> Option<String> {
    for path in plan.extra_ca_files() {
        notes.push(format!(
            ".NET trusts what the machine trusts, with no per-request CA list. Install `{path}` \
             in the certificate store for this to verify."
        ));
    }

    let (identity, kind) = plan.identity()?;
    let path = identity.path.trim();
    usings.insert("System.Security.Cryptography.X509Certificates");

    let load = match kind {
        IdentityKind::Pkcs12 => {
            // `X509CertificateLoader` arrived in .NET 9, and the constructor it
            // replaced is obsolete from the same version, so there is no one
            // spelling that is current everywhere.
            notes.push(
                "X509CertificateLoader needs .NET 9 or later. On .NET 8 and earlier, use \
                 new X509Certificate2(path, password) instead."
                    .to_string(),
            );
            format!(
                "X509CertificateLoader.LoadPkcs12FromFile({}, {})",
                literal(path),
                literal(identity.password.as_deref().unwrap_or(""))
            )
        }
        IdentityKind::Pem => {
            notes.push(
                "On Windows, a certificate read from PEM has to be exported to PKCS#12 and loaded \
                 back before SChannel will present it. On Linux and macOS this works as written."
                    .to_string(),
            );
            format!("X509Certificate2.CreateFromPemFile({})", literal(path))
        }
    };

    Some(format!(
        "handler.ClientCertificateOptions = ClientCertificateOption.Manual;\n\
         handler.ClientCertificates.Add({load});"
    ))
}

/// The `request.Content` assignment, and the content type set on it.
fn content(plan: &Plan, usings: &mut BTreeSet<&str>, content_type: Option<&str>) -> Option<String> {
    let mut lines: Vec<String> = Vec::new();

    match &plan.request.body {
        RequestBody::None => return None,

        RequestBody::Json { text } | RequestBody::Text { text, .. } => {
            usings.insert("System.Text");
            lines.push(format!(
                "request.Content = new StringContent({}, Encoding.UTF8);",
                literal(text)
            ));
        }

        RequestBody::UrlEncoded { entries } => {
            // The pairs already encoded, as one string, rather than through
            // `FormUrlEncodedContent`: that class escapes to its own taste, and
            // this way the bytes are the ones the app would send.
            usings.insert("System.Text");
            lines.push(format!(
                "request.Content = new StringContent({}, Encoding.UTF8);",
                literal(&form_encoded(entries))
            ));
        }

        RequestBody::Multipart { entries } => {
            lines.push("var content = new MultipartFormDataContent();".to_string());
            let mut parts = 0;
            for entry in entries.iter().filter(|entry| entry.is_active()) {
                let name = literal(entry.name.trim());
                match &entry.value {
                    MultipartValue::Text { value } => {
                        lines.push(format!(
                            "content.Add(new StringContent({}), {name});",
                            literal(value)
                        ));
                    }
                    MultipartValue::File {
                        path,
                        file_name: given,
                        content_type: part_type,
                    } => {
                        usings.insert("System.IO");
                        parts += 1;
                        let variable = format!("part{parts}");
                        lines.push(format!(
                            "var {variable} = new StreamContent(File.OpenRead({}));",
                            literal(path)
                        ));
                        if let Some(mime) = part_type {
                            usings.insert("System.Net.Http.Headers");
                            lines.push(format!(
                                "{variable}.Headers.ContentType = MediaTypeHeaderValue.Parse({});",
                                literal(mime)
                            ));
                        }
                        lines.push(format!(
                            "content.Add({variable}, {name}, {});",
                            literal(&file_name(path, given.as_ref()))
                        ));
                    }
                }
            }
            lines.push("request.Content = content;".to_string());
            // Multipart writes its own content type, boundary and all.
            return Some(lines.join("\n"));
        }
    }

    if let Some(value) = content_type {
        usings.insert("System.Net.Http.Headers");
        lines.push(format!(
            "request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse({});",
            literal(value)
        ));
    }

    Some(lines.join("\n"))
}

/// `HttpMethod.Get`, which is how the class spells its own members.
fn title_case(method: &str) -> String {
    let mut characters = method.chars();
    match characters.next() {
        Some(first) => format!("{first}{}", characters.as_str().to_lowercase()),
        None => String::new(),
    }
}

/// A string literal, verbatim only when it has to be.
///
/// `@"..."` keeps newlines and backslashes as they are — a Windows path in an
/// ordinary literal is a run of escapes — and doubles an embedded quote. A
/// header name gains nothing from the `@`, so it does not get one.
fn literal(value: &str) -> String {
    if value.contains(['\\', '"', '\n', '\r', '\t']) {
        return format!("@\"{}\"", value.replace('"', "\"\""));
    }
    format!("\"{value}\"")
}

#[cfg(test)]
mod tests {
    use super::literal;

    #[test]
    fn a_quote_is_doubled() {
        assert_eq!(literal(r#"{"a": 1}"#), r#"@"{""a"": 1}""#);
    }

    /// Nothing to escape, nothing to explain.
    #[test]
    fn a_plain_value_is_a_plain_literal() {
        assert_eq!(literal("application/json"), r#""application/json""#);
    }

    /// The reason for verbatim strings: this is a path, not four escapes.
    #[test]
    fn a_windows_path_survives() {
        assert_eq!(literal(r"C:\temp\a.png"), r#"@"C:\temp\a.png""#);
    }
}
