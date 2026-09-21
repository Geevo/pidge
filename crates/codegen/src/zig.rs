//! Zig, through `std.http.Client`.
//!
//! `fetch` rather than the `open`/`send`/`wait` sequence underneath it: it is
//! the one part of this API that has stayed still across releases, and it says
//! in one call what the long form says in eight.
//!
//! Zig's standard library moves between versions in a way the others here do
//! not — `std.http` and `ArrayList` have both changed shape across releases —
//! so the snippet says which version it was written against rather than leaving
//! somebody to find that out from a compile error.

use api_client_core::{MultipartValue, RequestBody};
use api_client_http_engine::{AuthPlan, ChallengeAuth, basic_credentials};

use crate::Plan;
use crate::text::{comment, form_encoded, indent};

/// What the generated code is written against. Said out loud in the snippet.
const ZIG_VERSION: &str = "0.14";

pub fn generate(plan: &Plan) -> String {
    let mut notes = plan.notes();
    let mut statements: Vec<String> = Vec::new();
    let mut fields: Vec<String> = vec![
        format!(".method = .{},", plan.method()),
        format!(
            ".location = .{{ .url = {} }},",
            literal(&plan.effective.url)
        ),
    ];

    /*
     * Zig's client takes the content type as its own field and everything else
     * as extra headers, in the same way .NET separates them — so the one that
     * names the body is pulled out of the list.
     */
    let mut headers = plan.headers();
    let content_type = crate::text::take_content_type(&mut headers);

    /*
     * Basic auth is written as the header it becomes rather than as a username
     * and a password, which is the one place this differs from the others.
     * Zig's base64 encoder writes into a buffer whose length has to be known at
     * comptime, and three lines of arithmetic would say less than the header.
     */
    if let Some((username, password)) = plan.basic() {
        notes.push(format!(
            "The Authorization header is `{username}` and its password, base64 encoded, because \
             Zig's base64 encoder needs a buffer sized where it is declared."
        ));
        headers.push((
            "Authorization".to_string(),
            basic_credentials(username, password),
        ));
    }

    let header_rows: Vec<String> = headers
        .iter()
        .map(|(name, value)| {
            format!(
                ".{{ .name = {}, .value = {} }},",
                literal(name),
                literal(value)
            )
        })
        .collect();

    if let Some(value) = &content_type {
        fields.push(format!(
            ".headers = .{{ .content_type = .{{ .override = {} }} }},",
            literal(value)
        ));
    }
    if !header_rows.is_empty() {
        fields.push(format!(
            ".extra_headers = &.{{\n{}\n}},",
            indent(&header_rows.join("\n"), 4)
        ));
    }

    if let Some(payload) = body(plan, &mut statements, &mut notes) {
        fields.push(format!(".payload = {payload},"));
    }
    fields.push(".response_storage = .{ .dynamic = &body },".to_string());

    if let AuthPlan::Challenge(challenge) = &plan.effective.auth {
        let scheme = match challenge {
            ChallengeAuth::Digest { .. } => "digest",
            ChallengeAuth::Ntlm { .. } => "NTLM",
        };
        notes.push(format!(
            "std.http.Client does not answer a {scheme} challenge; it has no support for either \
             scheme."
        ));
    }
    if plan.timeout_ms() > 0 {
        notes.push(
            "std.http.Client has no timeout. The request runs until the server answers or the \
             connection fails."
                .to_string(),
        );
    }
    if !plan.options.follow_redirects {
        notes.push(
            "Redirects are followed. `fetch` takes a redirect budget rather than a switch, and \
             the long form — `client.open` with `.redirect_behavior = .unhandled` — is what \
             turns them off."
                .to_string(),
        );
    }
    if plan.accepts_invalid_certs() || !plan.extra_ca_files().is_empty() {
        notes.push(
            "Trust comes from the system bundle that `Client.ca_bundle` loads. There is no \
             per-request switch for skipping verification or adding one certificate."
                .to_string(),
        );
    }
    if plan.identity().is_some() {
        notes.push(
            "std.http.Client cannot present a client certificate; its TLS layer has no place \
             for one."
                .to_string(),
        );
    }

    let mut out = format!("// Written for Zig {ZIG_VERSION}.\n");
    for note in notes {
        out.push_str(&comment("// ", &note));
        out.push('\n');
    }
    out.push_str(
        "\nconst std = @import(\"std\");\n\npub fn main() !void {\n    \
         var gpa = std.heap.GeneralPurposeAllocator(.{}){};\n    \
         defer _ = gpa.deinit();\n    const allocator = gpa.allocator();\n\n    \
         var client = std.http.Client{ .allocator = allocator };\n    \
         defer client.deinit();\n\n    \
         var body = std.ArrayList(u8).init(allocator);\n    defer body.deinit();\n\n",
    );
    if !statements.is_empty() {
        out.push_str(&indent(&statements.join("\n\n"), 4));
        out.push_str("\n\n");
    }
    out.push_str(&indent(
        &format!(
            "const result = try client.fetch(.{{\n{}\n}});",
            indent(&fields.join("\n"), 4)
        ),
        4,
    ));
    out.push_str(
        "\n\n    std.debug.print(\"{d}\\n\", .{@intFromEnum(result.status)});\n    \
         std.debug.print(\"{s}\\n\", .{body.items});\n}\n",
    );
    out
}

/// The payload, as the expression `fetch` takes for it.
fn body(plan: &Plan, statements: &mut Vec<String>, notes: &mut Vec<String>) -> Option<String> {
    match &plan.request.body {
        RequestBody::None => None,

        RequestBody::Json { text } | RequestBody::Text { text, .. } => {
            statements.push(format!("const payload =\n{}\n;", indent(&literal(text), 4)));
            Some("payload".to_string())
        }

        RequestBody::UrlEncoded { entries } => {
            statements.push(format!(
                "const payload = {};",
                literal(&form_encoded(entries))
            ));
            Some("payload".to_string())
        }

        /*
         * Nothing in the standard library writes a multipart body, so it is
         * written out here — the boundary is fixed, as it is in the Java form,
         * so that copying the same request twice gives the same text.
         */
        RequestBody::Multipart { entries } => {
            notes.push(
                "Zig's standard library has no multipart writer, so the body is assembled here. \
                 A file part is read into memory whole."
                    .to_string(),
            );

            let boundary = "----ApiClientBoundary";
            let mut lines: Vec<String> = vec![
                "var payload = std.ArrayList(u8).init(allocator);".to_string(),
                "defer payload.deinit();".to_string(),
                "const writer = payload.writer();".to_string(),
            ];
            for entry in entries.iter().filter(|entry| entry.is_active()) {
                let name = entry.name.trim();
                match &entry.value {
                    MultipartValue::Text { value } => lines.push(format!(
                        "try writer.print(\n    \"--{boundary}\\r\\nContent-Disposition: \
                         form-data; name=\\\"{name}\\\"\\r\\n\\r\\n{{s}}\\r\\n\",\n    .{{{}}},\n);",
                        literal(value)
                    )),
                    MultipartValue::File {
                        path,
                        file_name: given,
                        content_type,
                    } => {
                        let mime = content_type.as_deref().unwrap_or("application/octet-stream");
                        lines.push(format!(
                            "const part = try std.fs.cwd().readFileAlloc(allocator, {}, 1 << 24);\n\
                             defer allocator.free(part);\n\
                             try writer.print(\n    \
                             \"--{boundary}\\r\\nContent-Disposition: form-data; \
                             name=\\\"{name}\\\"; filename=\\\"{}\\\"\\r\\nContent-Type: \
                             {mime}\\r\\n\\r\\n\",\n    .{{}},\n);\n\
                             try writer.writeAll(part);\n\
                             try writer.writeAll(\"\\r\\n\");",
                            literal(path),
                            crate::text::file_name(path, given.as_ref())
                        ));
                    }
                }
            }
            lines.push(format!("try writer.writeAll(\"--{boundary}--\\r\\n\");"));
            statements.push(lines.join("\n"));
            Some("payload.items".to_string())
        }
    }
}

/// A Zig string literal.
///
/// Text that runs over lines becomes a multiline string: every line prefixed
/// with `\\`, which carries anything at all and needs no escaping, since there
/// are no escape sequences inside one.
fn literal(value: &str) -> String {
    if value.contains('\n') && !value.contains('\r') {
        return value
            .lines()
            .map(|line| format!("\\\\{line}"))
            .collect::<Vec<_>>()
            .join("\n");
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

    /// Every line of a multiline string carries its own `\\`, and nothing
    /// inside one is an escape.
    #[test]
    fn a_body_over_several_lines_is_a_multiline_string() {
        assert_eq!(literal("{\n  \"a\": 1\n}"), "\\\\{\n\\\\  \"a\": 1\n\\\\}");
    }
}
