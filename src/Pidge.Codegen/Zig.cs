using System.Text;
using Pidge.Core;
using Pidge.HttpEngine;

namespace Pidge.Codegen;

/// <summary>
/// Zig, through <c>std.http.Client</c>.
///
/// <c>fetch</c> rather than the <c>open</c>/<c>send</c>/<c>wait</c> sequence
/// underneath it: it is the one part of this API that has stayed still across
/// releases, and it says in one call what the long form says in eight.
///
/// Zig's standard library moves between versions in a way the others here do
/// not — <c>std.http</c> and <c>ArrayList</c> have both changed shape across
/// releases — so the snippet says which version it was written against rather
/// than leaving somebody to find that out from a compile error.
/// </summary>
internal static class Zig
{
    /// <summary>What the generated code is written against. Said out loud in the snippet.</summary>
    private const string ZigVersion = "0.14";

    private const string Boundary = "----ApiClientBoundary";

    public static string Generate(Plan plan)
    {
        var notes = plan.Notes();
        var statements = new List<string>();
        var fields = new List<string>
        {
            $".method = .{plan.Method},",
            $".location = .{{ .url = {Literal(plan.Effective.Url)} }},",
        };

        /*
         * Zig's client takes the content type as its own field and everything else
         * as extra headers, in the same way .NET separates them — so the one that
         * names the body is pulled out of the list.
         */
        var headers = plan.Headers();
        var contentType = Text.TakeContentType(headers);

        /*
         * Basic auth is written as the header it becomes rather than as a username
         * and a password, which is the one place this differs from the others.
         * Zig's base64 encoder writes into a buffer whose length has to be known at
         * comptime, and three lines of arithmetic would say less than the header.
         */
        if (plan.Basic is (var username, var password))
        {
            notes.Add(
                $"The Authorization header is `{username}` and its password, base64 encoded, because "
                + "Zig's base64 encoder needs a buffer sized where it is declared.");
            headers.Add(("Authorization", RequestPlanning.BasicCredentials(username, password)));
        }

        var headerRows = headers
            .Select(header => $".{{ .name = {Literal(header.Name)}, .value = {Literal(header.Value)} }},")
            .ToList();

        if (contentType is not null)
        {
            fields.Add($".headers = .{{ .content_type = .{{ .override = {Literal(contentType)} }} }},");
        }
        if (headerRows.Count > 0)
        {
            fields.Add($".extra_headers = &.{{\n{Text.Indent(string.Join("\n", headerRows), 4)}\n}},");
        }

        if (Body(plan, statements, notes) is { } payload)
        {
            fields.Add($".payload = {payload},");
        }
        fields.Add(".response_storage = .{ .dynamic = &body },");

        if (plan.Effective.Auth is AuthPlan.Challenge { Auth: var challenge })
        {
            var scheme = challenge is ChallengeAuth.Digest ? "digest" : "NTLM";
            notes.Add(
                $"std.http.Client does not answer a {scheme} challenge; it has no support for either "
                + "scheme.");
        }
        if (plan.TimeoutMs > 0)
        {
            notes.Add(
                "std.http.Client has no timeout. The request runs until the server answers or the "
                + "connection fails.");
        }
        if (!plan.Options.FollowRedirects)
        {
            notes.Add(
                "Redirects are followed. `fetch` takes a redirect budget rather than a switch, and "
                + "the long form — `client.open` with `.redirect_behavior = .unhandled` — is what "
                + "turns them off.");
        }
        if (plan.AcceptsInvalidCerts || plan.ExtraCaFiles().Count > 0)
        {
            notes.Add(
                "Trust comes from the system bundle that `Client.ca_bundle` loads. There is no "
                + "per-request switch for skipping verification or adding one certificate.");
        }
        if (plan.Identity is not null)
        {
            notes.Add(
                "std.http.Client cannot present a client certificate; its TLS layer has no place "
                + "for one.");
        }

        var output = new StringBuilder($"// Written for Zig {ZigVersion}.\n");
        foreach (var note in notes)
        {
            output.Append(Text.Comment("// ", note)).Append('\n');
        }
        output.Append(
            "\nconst std = @import(\"std\");\n\npub fn main() !void {\n    "
            + "var gpa = std.heap.GeneralPurposeAllocator(.{}){};\n    "
            + "defer _ = gpa.deinit();\n    const allocator = gpa.allocator();\n\n    "
            + "var client = std.http.Client{ .allocator = allocator };\n    "
            + "defer client.deinit();\n\n    "
            + "var body = std.ArrayList(u8).init(allocator);\n    defer body.deinit();\n\n");
        if (statements.Count > 0)
        {
            output.Append(Text.Indent(string.Join("\n\n", statements), 4));
            output.Append("\n\n");
        }
        output.Append(
            Text.Indent($"const result = try client.fetch(.{{\n{Text.Indent(string.Join("\n", fields), 4)}\n}});", 4));
        output.Append(
            "\n\n    std.debug.print(\"{d}\\n\", .{@intFromEnum(result.status)});\n    "
            + "std.debug.print(\"{s}\\n\", .{body.items});\n}\n");
        return output.ToString();
    }

    /// <summary>The payload, as the expression <c>fetch</c> takes for it.</summary>
    private static string? Body(Plan plan, List<string> statements, List<string> notes)
    {
        switch (plan.Request.Body)
        {
            case JsonBody json:
                statements.Add($"const payload =\n{Text.Indent(Literal(json.Text), 4)}\n;");
                return "payload";
            case TextBody text:
                statements.Add($"const payload =\n{Text.Indent(Literal(text.Text), 4)}\n;");
                return "payload";

            case UrlEncodedBody form:
                statements.Add($"const payload = {Literal(Text.FormEncoded(form.Entries))};");
                return "payload";

            /*
             * Nothing in the standard library writes a multipart body, so it is
             * written out here — the boundary is fixed, as it is in the Java form,
             * so that copying the same request twice gives the same text.
             */
            case MultipartBody multipart:
                {
                    notes.Add(
                        "Zig's standard library has no multipart writer, so the body is assembled here. "
                        + "A file part is read into memory whole.");

                    var lines = new List<string>
                {
                    "var payload = std.ArrayList(u8).init(allocator);",
                    "defer payload.deinit();",
                    "const writer = payload.writer();",
                };
                    foreach (var entry in multipart.Entries.Where(entry => entry.IsActive))
                    {
                        var name = entry.Name.Trim();
                        switch (entry.Value)
                        {
                            case MultipartText text:
                                lines.Add(
                                    $"try writer.print(\n    \"--{Boundary}\\r\\nContent-Disposition: "
                                    + $"form-data; name=\\\"{name}\\\"\\r\\n\\r\\n{{s}}\\r\\n\",\n    .{{{Literal(text.Value)}}},\n);");
                                break;
                            case MultipartFile file:
                                var mime = file.ContentType ?? "application/octet-stream";
                                lines.Add(
                                    $"const part = try std.fs.cwd().readFileAlloc(allocator, {Literal(file.Path)}, 1 << 24);\n"
                                    + "defer allocator.free(part);\n"
                                    + "try writer.print(\n    "
                                    + $"\"--{Boundary}\\r\\nContent-Disposition: form-data; "
                                    + $"name=\\\"{name}\\\"; filename=\\\"{Text.FileName(file.Path, file.FileName)}\\\"\\r\\nContent-Type: "
                                    + $"{mime}\\r\\n\\r\\n\",\n    .{{}},\n);\n"
                                    + "try writer.writeAll(part);\n"
                                    + "try writer.writeAll(\"\\r\\n\");");
                                break;
                        }
                    }
                    lines.Add($"try writer.writeAll(\"--{Boundary}--\\r\\n\");");
                    statements.Add(string.Join("\n", lines));
                    return "payload.items";
                }

            default:
                return null;
        }
    }

    /// <summary>
    /// A Zig string literal.
    ///
    /// Text that runs over lines becomes a multiline string: every line
    /// prefixed with <c>\\</c>, which carries anything at all and needs no
    /// escaping, since there are no escape sequences inside one.
    /// </summary>
    internal static string Literal(string value)
    {
        if (value.Contains('\n') && !value.Contains('\r'))
        {
            return string.Join("\n", Text.Lines(value).Select(line => "\\\\" + line));
        }

        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}
