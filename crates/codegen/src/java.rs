//! Java, through the client in the standard library or through OkHttp.
//!
//! `java.net.http.HttpClient` arrived in Java 11 and needs nothing installed;
//! OkHttp is what a great deal of Java actually uses, and says several of these
//! things far more briefly. Both are written as a single `Main.java`, which
//! `java Main.java` will run without compiling it first.

use api_client_core::{MultipartValue, RequestBody};
use api_client_http_engine::{AuthPlan, ChallengeAuth};

use crate::text::{comment, file_name, form_encoded, indent, keep, settle};
use crate::{IdentityKind, Plan};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Library {
    HttpClient,
    OkHttp,
}

pub fn generate(plan: &Plan, library: Library) -> String {
    match library {
        Library::HttpClient => http_client(plan),
        Library::OkHttp => ok_http(plan),
    }
}

fn http_client(plan: &Plan) -> String {
    let mut notes = plan.notes();
    let mut imports: Vec<String> = vec![
        "java.net.URI".to_string(),
        "java.net.http.HttpClient".to_string(),
        "java.net.http.HttpRequest".to_string(),
        "java.net.http.HttpResponse".to_string(),
    ];
    let mut statements: Vec<String> = Vec::new();

    // The client, which carries everything that is a setting.
    let mut client: Vec<String> = Vec::new();
    if plan.options.follow_redirects {
        client.push(".followRedirects(HttpClient.Redirect.NORMAL)".to_string());
    }
    let timeout = plan.timeout_ms();
    if timeout > 0 {
        imports.push("java.time.Duration".to_string());
        client.push(format!(".connectTimeout(Duration.ofMillis({timeout}))"));
    }
    if let Some(context) = ssl_context(plan, &mut imports, &mut notes, &mut statements) {
        client.push(context);
    }

    statements.push(if client.is_empty() {
        "HttpClient client = HttpClient.newHttpClient();".to_string()
    } else {
        format!(
            "HttpClient client = HttpClient.newBuilder()\n{}\n    .build();",
            indent(&client.join("\n"), 4)
        )
    });

    let mut builder: Vec<String> = vec![format!(
        ".uri(URI.create({}))",
        literal(&plan.effective.url)
    )];
    for (name, value) in plan.headers() {
        builder.push(format!(".header({}, {})", literal(&name), literal(&value)));
    }
    if let Some((username, password)) = plan.basic() {
        imports.push("java.util.Base64".to_string());
        imports.push("java.nio.charset.StandardCharsets".to_string());
        builder.push(format!(
            ".header(\"Authorization\", \"Basic \" + Base64.getEncoder()\n        \
             .encodeToString({}.getBytes(StandardCharsets.UTF_8)))",
            literal(&format!("{username}:{password}"))
        ));
    }
    challenge_note(plan, &mut notes, "HttpClient");

    if timeout > 0 {
        builder.push(format!(".timeout(Duration.ofMillis({timeout}))"));
    }
    builder.push(publisher(plan, &mut imports, &mut statements, &mut notes));

    statements.push(format!(
        "HttpRequest request = HttpRequest.newBuilder()\n{}\n    .build();",
        indent(&builder.join("\n"), 4)
    ));
    statements.push(
        "HttpResponse<String> response = client.send(request, \
         HttpResponse.BodyHandlers.ofString());\n\
         System.out.println(response.statusCode());\nSystem.out.println(response.body());"
            .to_string(),
    );

    assemble(&notes, &imports, &statements)
}

fn ok_http(plan: &Plan) -> String {
    let mut notes = plan.notes();
    let mut imports: Vec<String> = vec![
        "okhttp3.OkHttpClient".to_string(),
        "okhttp3.Request".to_string(),
        "okhttp3.Response".to_string(),
    ];
    let mut statements: Vec<String> = Vec::new();

    let mut client: Vec<String> = Vec::new();
    let timeout = plan.timeout_ms();
    if timeout > 0 {
        imports.push("java.time.Duration".to_string());
        client.push(format!(".callTimeout(Duration.ofMillis({timeout}))"));
    }
    if !plan.options.follow_redirects {
        client.push(".followRedirects(false)".to_string());
        client.push(".followSslRedirects(false)".to_string());
    }
    if plan.accepts_invalid_certs()
        || !plan.extra_ca_files().is_empty()
        || plan.identity().is_some()
    {
        notes.push(
            "OkHttp takes its trust and its client certificate through \
             `sslSocketFactory(factory, trustManager)`, built from a KeyStore. The HttpClient \
             form shows one being loaded."
                .to_string(),
        );
    }

    statements.push(if client.is_empty() {
        "OkHttpClient client = new OkHttpClient();".to_string()
    } else {
        format!(
            "OkHttpClient client = new OkHttpClient.Builder()\n{}\n    .build();",
            indent(&client.join("\n"), 4)
        )
    });

    let method = plan.method();
    let body = ok_http_body(plan, &mut imports, &mut statements);
    let mut builder: Vec<String> = vec![format!(".url({})", literal(&plan.effective.url))];
    for (name, value) in plan.headers() {
        builder.push(format!(
            ".addHeader({}, {})",
            literal(&name),
            literal(&value)
        ));
    }
    if let Some((username, password)) = plan.basic() {
        imports.push("okhttp3.Credentials".to_string());
        builder.push(format!(
            ".header(\"Authorization\", Credentials.basic({}, {}))",
            literal(username),
            literal(password)
        ));
    }
    challenge_note(plan, &mut notes, "OkHttp");

    builder.push(match method {
        "GET" => ".get()".to_string(),
        "HEAD" => ".head()".to_string(),
        other => format!(".method({}, {})", literal(other), body),
    });

    statements.push(format!(
        "Request request = new Request.Builder()\n{}\n    .build();",
        indent(&builder.join("\n"), 4)
    ));
    statements.push(
        "try (Response response = client.newCall(request).execute()) {\n    \
         System.out.println(response.code());\n    \
         System.out.println(response.body().string());\n}"
            .to_string(),
    );

    assemble(&notes, &imports, &statements)
}

/// OkHttp's `RequestBody`, which is one call for every shape but multipart.
fn ok_http_body(plan: &Plan, imports: &mut Vec<String>, statements: &mut Vec<String>) -> String {
    match &plan.request.body {
        RequestBody::None => {
            imports.push("okhttp3.RequestBody".to_string());
            "RequestBody.create(new byte[0], null)".to_string()
        }

        RequestBody::Json { text } | RequestBody::Text { text, .. } => {
            imports.push("okhttp3.MediaType".to_string());
            imports.push("okhttp3.RequestBody".to_string());
            let content_type = plan
                .headers()
                .into_iter()
                .find(|(name, _)| name.eq_ignore_ascii_case("content-type"))
                .map(|(_, value)| value)
                .unwrap_or_else(|| "text/plain".to_string());
            statements.push(format!(
                "RequestBody body = RequestBody.create(\n    {},\n    MediaType.parse({}));",
                literal(text),
                literal(&content_type)
            ));
            "body".to_string()
        }

        RequestBody::UrlEncoded { entries } => {
            imports.push("okhttp3.FormBody".to_string());
            let rows = entries
                .iter()
                .filter(|entry| entry.is_active())
                .map(|entry| {
                    format!(
                        ".add({}, {})",
                        literal(entry.name.trim()),
                        literal(&entry.value)
                    )
                })
                .collect::<Vec<_>>()
                .join("\n");
            statements.push(format!(
                "FormBody body = new FormBody.Builder()\n{}\n    .build();",
                indent(&rows, 4)
            ));
            "body".to_string()
        }

        RequestBody::Multipart { entries } => {
            imports.push("okhttp3.MultipartBody".to_string());
            let mut rows: Vec<String> = vec![".setType(MultipartBody.FORM)".to_string()];
            for entry in entries.iter().filter(|entry| entry.is_active()) {
                let name = literal(entry.name.trim());
                match &entry.value {
                    MultipartValue::Text { value } => {
                        rows.push(format!(".addFormDataPart({name}, {})", literal(value)));
                    }
                    MultipartValue::File {
                        path,
                        file_name: given,
                        content_type,
                    } => {
                        imports.push("okhttp3.MediaType".to_string());
                        imports.push("okhttp3.RequestBody".to_string());
                        imports.push("java.io.File".to_string());
                        let mime = content_type
                            .as_deref()
                            .map(|mime| format!("MediaType.parse({})", literal(mime)))
                            .unwrap_or_else(|| "null".to_string());
                        rows.push(format!(
                            ".addFormDataPart(\n        {name},\n        {},\n        \
                             RequestBody.create(new File({}), {mime}))",
                            literal(&file_name(path, given.as_ref())),
                            literal(path)
                        ));
                    }
                }
            }
            statements.push(format!(
                "MultipartBody body = new MultipartBody.Builder()\n{}\n    .build();",
                indent(&rows.join("\n"), 4)
            ));
            "body".to_string()
        }
    }
}

/// The `BodyPublisher` the standard client takes, and whatever has to be built
/// before it.
fn publisher(
    plan: &Plan,
    imports: &mut Vec<String>,
    statements: &mut Vec<String>,
    notes: &mut Vec<String>,
) -> String {
    let method = plan.method();
    match &plan.request.body {
        RequestBody::None => match method {
            "GET" => ".GET()".to_string(),
            other => format!(
                ".method({}, HttpRequest.BodyPublishers.noBody())",
                literal(other)
            ),
        },

        RequestBody::Json { text } | RequestBody::Text { text, .. } => {
            statements.push(format!("String payload = {};", literal(text)));
            format!(
                ".method({}, HttpRequest.BodyPublishers.ofString(payload))",
                literal(method)
            )
        }

        RequestBody::UrlEncoded { entries } => {
            statements.push(format!(
                "String payload = {};",
                literal(&form_encoded(entries))
            ));
            format!(
                ".method({}, HttpRequest.BodyPublishers.ofString(payload))",
                literal(method)
            )
        }

        /*
         * There is no multipart publisher in the standard client, so the body
         * is written out by hand. The boundary is fixed rather than random
         * because a snippet that reads differently every time it is copied is
         * harder to compare with the last one.
         */
        RequestBody::Multipart { entries } => {
            imports.push("java.io.ByteArrayOutputStream".to_string());
            imports.push("java.nio.charset.StandardCharsets".to_string());
            imports.push("java.nio.file.Files".to_string());
            imports.push("java.nio.file.Path".to_string());

            notes.push(
                "HttpClient has no multipart publisher, so the body is assembled here. The \
                 OkHttp form has one built in."
                    .to_string(),
            );

            let boundary = "----ApiClientBoundary";
            let mut lines: Vec<String> = vec![
                format!("String boundary = {};", literal(boundary)),
                "ByteArrayOutputStream payload = new ByteArrayOutputStream();".to_string(),
            ];
            for entry in entries.iter().filter(|entry| entry.is_active()) {
                let name = entry.name.trim();
                match &entry.value {
                    MultipartValue::Text { value } => lines.push(format!(
                        "payload.writeBytes((\"--\" + boundary + \"\\r\\n\"\n    + \
                         \"Content-Disposition: form-data; name=\\\"{name}\\\"\\r\\n\\r\\n\"\n    \
                         + {} + \"\\r\\n\").getBytes(StandardCharsets.UTF_8));",
                        literal(value)
                    )),
                    MultipartValue::File {
                        path,
                        file_name: given,
                        content_type,
                    } => {
                        let mime = content_type
                            .as_deref()
                            .unwrap_or("application/octet-stream");
                        lines.push(format!(
                            "payload.writeBytes((\"--\" + boundary + \"\\r\\n\"\n    + \
                             \"Content-Disposition: form-data; name=\\\"{name}\\\"; \
                             filename=\\\"{}\\\"\\r\\n\"\n    + \"Content-Type: {mime}\\r\\n\\r\\n\")\n    \
                             .getBytes(StandardCharsets.UTF_8));\n\
                             payload.writeBytes(Files.readAllBytes(Path.of({})));\n\
                             payload.writeBytes(\"\\r\\n\".getBytes(StandardCharsets.UTF_8));",
                            file_name(path, given.as_ref()),
                            literal(path)
                        ));
                    }
                }
            }
            lines.push(
                "payload.writeBytes((\"--\" + boundary + \"--\\r\\n\")\n    \
                 .getBytes(StandardCharsets.UTF_8));"
                    .to_string(),
            );
            statements.push(lines.join("\n"));

            format!(
                ".header(\"Content-Type\", \"multipart/form-data; boundary=\" + boundary)\n\
                 .method({}, \
                 HttpRequest.BodyPublishers.ofByteArray(payload.toByteArray()))",
                literal(method)
            )
        }
    }
}

/// The `SSLContext` the client is built with, when anything about trust or
/// identity has been changed from the default.
fn ssl_context(
    plan: &Plan,
    imports: &mut Vec<String>,
    notes: &mut Vec<String>,
    statements: &mut Vec<String>,
) -> Option<String> {
    let identity = plan.identity();
    let extra = plan.extra_ca_files();

    if plan.accepts_invalid_certs() {
        notes.push(
            "Certificate checking is off in Settings. Java has no switch for that: it needs a \
             TrustManager that accepts everything, which is not written out here on purpose."
                .to_string(),
        );
    }
    for path in &extra {
        notes.push(format!(
            "Java reads trust from a KeyStore rather than a PEM file. Import `{path}` with \
             `keytool -importcert`, or build a TrustManagerFactory from it."
        ));
    }

    let (identity, kind) = identity?;
    let path = identity.path.trim();
    if kind == IdentityKind::Pem {
        notes.push(
            "Java loads a client certificate from a PKCS#12 keystore, not from PEM. Convert it \
             with `openssl pkcs12 -export`."
                .to_string(),
        );
        return None;
    }

    imports.push("java.io.FileInputStream".to_string());
    imports.push("java.security.KeyStore".to_string());
    imports.push("javax.net.ssl.KeyManagerFactory".to_string());
    imports.push("javax.net.ssl.SSLContext".to_string());

    let password = identity.password.as_deref().unwrap_or("");
    statements.push(format!(
        "char[] password = {}.toCharArray();\n\
         KeyStore keyStore = KeyStore.getInstance(\"PKCS12\");\n\
         try (FileInputStream in = new FileInputStream({})) {{\n    \
         keyStore.load(in, password);\n}}\n\
         KeyManagerFactory keyManagers = KeyManagerFactory.getInstance(\n    \
         KeyManagerFactory.getDefaultAlgorithm());\n\
         keyManagers.init(keyStore, password);\n\
         SSLContext sslContext = SSLContext.getInstance(\"TLS\");\n\
         sslContext.init(keyManagers.getKeyManagers(), null, null);",
        literal(password),
        literal(path)
    ));

    Some(".sslContext(sslContext)".to_string())
}

fn challenge_note(plan: &Plan, notes: &mut Vec<String>, library: &str) {
    if let AuthPlan::Challenge(challenge) = &plan.effective.auth {
        let scheme = match challenge {
            ChallengeAuth::Digest { .. } => "digest",
            ChallengeAuth::Ntlm { .. } => "NTLM",
        };
        notes.push(format!(
            "{library} does not answer a {scheme} challenge on its own."
        ));
    }
}

fn assemble(notes: &[String], imports: &[String], statements: &[String]) -> String {
    let mut out = String::new();
    for note in notes {
        out.push_str(&comment("// ", note));
        out.push('\n');
    }

    let mut sorted = imports.to_vec();
    sorted.sort();
    sorted.dedup();
    for import in &sorted {
        out.push_str(&format!("import {import};\n"));
    }

    out.push_str(
        "\npublic class Main {\n    public static void main(String[] args) throws Exception {\n",
    );
    out.push_str(&indent(&statements.join("\n\n"), 8));
    out.push_str("\n    }\n}\n");
    settle(out)
}

/// A Java string literal.
///
/// A text block for anything that runs over lines, with its content at the left
/// margin: Java strips the smallest indentation it finds across the content
/// lines, and at the margin there is none to strip, so the body comes out
/// exactly as it went in.
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
        return keep(&format!("\"\"\"\n{escaped}\"\"\""));
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
    fn a_plain_value_is_a_quoted_string() {
        assert_eq!(literal("application/json"), "\"application/json\"");
    }

    /// The content sits at the margin so that nothing is stripped from it.
    #[test]
    fn a_body_over_several_lines_is_a_text_block() {
        assert_eq!(
            crate::text::settle(literal("{\n  \"a\": 1\n}")),
            "\"\"\"\n{\n  \"a\": 1\n}\"\"\""
        );
    }
}
