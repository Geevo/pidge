using System.Text;
using Pidge.Core;
using Pidge.HttpEngine;

namespace Pidge.Codegen;

/// <summary>
/// Java, through the client in the standard library or through OkHttp.
///
/// <c>java.net.http.HttpClient</c> arrived in Java 11 and needs nothing
/// installed; OkHttp is what a great deal of Java actually uses, and says
/// several of these things far more briefly. Both are written as a single
/// <c>Main.java</c>, which <c>java Main.java</c> will run without compiling it
/// first.
/// </summary>
internal static class Java
{
    public enum Library
    {
        HttpClient,
        OkHttp,
    }

    public static string Generate(Plan plan, Library library) =>
        library == Library.HttpClient ? HttpClient(plan) : OkHttp(plan);

    private static string HttpClient(Plan plan)
    {
        var notes = plan.Notes();
        var imports = new List<string>
        {
            "java.net.URI",
            "java.net.http.HttpClient",
            "java.net.http.HttpRequest",
            "java.net.http.HttpResponse",
        };
        var statements = new List<string>();

        // The client, which carries everything that is a setting.
        var client = new List<string>();
        if (plan.Options.FollowRedirects)
        {
            client.Add(".followRedirects(HttpClient.Redirect.NORMAL)");
        }
        var timeout = plan.TimeoutMs;
        if (timeout > 0)
        {
            imports.Add("java.time.Duration");
            client.Add($".connectTimeout(Duration.ofMillis({timeout}))");
        }
        if (SslContext(plan, imports, notes, statements) is { } context)
        {
            client.Add(context);
        }

        statements.Add(
            client.Count == 0
                ? "HttpClient client = HttpClient.newHttpClient();"
                : $"HttpClient client = HttpClient.newBuilder()\n{Text.Indent(string.Join("\n", client), 4)}\n    .build();");

        var builder = new List<string> { $".uri(URI.create({Literal(plan.Effective.Url)}))" };
        foreach (var (name, value) in plan.Headers())
        {
            builder.Add($".header({Literal(name)}, {Literal(value)})");
        }
        if (plan.Basic is (var username, var password))
        {
            imports.Add("java.util.Base64");
            imports.Add("java.nio.charset.StandardCharsets");
            builder.Add(
                ".header(\"Authorization\", \"Basic \" + Base64.getEncoder()\n        "
                + $".encodeToString({Literal($"{username}:{password}")}.getBytes(StandardCharsets.UTF_8)))");
        }
        ChallengeNote(plan, notes, "HttpClient");

        if (timeout > 0)
        {
            builder.Add($".timeout(Duration.ofMillis({timeout}))");
        }
        builder.Add(Publisher(plan, imports, statements, notes));

        statements.Add(
            $"HttpRequest request = HttpRequest.newBuilder()\n{Text.Indent(string.Join("\n", builder), 4)}\n    .build();");
        statements.Add(
            "HttpResponse<String> response = client.send(request, "
            + "HttpResponse.BodyHandlers.ofString());\n"
            + "System.out.println(response.statusCode());\nSystem.out.println(response.body());");

        return Assemble(notes, imports, statements);
    }

    private static string OkHttp(Plan plan)
    {
        var notes = plan.Notes();
        var imports = new List<string> { "okhttp3.OkHttpClient", "okhttp3.Request", "okhttp3.Response" };
        var statements = new List<string>();

        var client = new List<string>();
        var timeout = plan.TimeoutMs;
        if (timeout > 0)
        {
            imports.Add("java.time.Duration");
            client.Add($".callTimeout(Duration.ofMillis({timeout}))");
        }
        if (!plan.Options.FollowRedirects)
        {
            client.Add(".followRedirects(false)");
            client.Add(".followSslRedirects(false)");
        }
        if (plan.AcceptsInvalidCerts || plan.ExtraCaFiles().Count > 0 || plan.Identity is not null)
        {
            notes.Add(
                "OkHttp takes its trust and its client certificate through "
                + "`sslSocketFactory(factory, trustManager)`, built from a KeyStore. The HttpClient "
                + "form shows one being loaded.");
        }

        statements.Add(
            client.Count == 0
                ? "OkHttpClient client = new OkHttpClient();"
                : $"OkHttpClient client = new OkHttpClient.Builder()\n{Text.Indent(string.Join("\n", client), 4)}\n    .build();");

        var method = plan.Method;
        var body = OkHttpBody(plan, imports, statements);
        var builder = new List<string> { $".url({Literal(plan.Effective.Url)})" };
        foreach (var (name, value) in plan.Headers())
        {
            builder.Add($".addHeader({Literal(name)}, {Literal(value)})");
        }
        if (plan.Basic is (var username, var password))
        {
            imports.Add("okhttp3.Credentials");
            builder.Add($".header(\"Authorization\", Credentials.basic({Literal(username)}, {Literal(password)}))");
        }
        ChallengeNote(plan, notes, "OkHttp");

        builder.Add(method switch
        {
            "GET" => ".get()",
            "HEAD" => ".head()",
            _ => $".method({Literal(method)}, {body})",
        });

        statements.Add(
            $"Request request = new Request.Builder()\n{Text.Indent(string.Join("\n", builder), 4)}\n    .build();");
        statements.Add(
            "try (Response response = client.newCall(request).execute()) {\n    "
            + "System.out.println(response.code());\n    "
            + "System.out.println(response.body().string());\n}");

        return Assemble(notes, imports, statements);
    }

    /// <summary>OkHttp's <c>RequestBody</c>, which is one call for every shape but multipart.</summary>
    private static string OkHttpBody(Plan plan, List<string> imports, List<string> statements)
    {
        switch (plan.Request.Body)
        {
            case JsonBody or TextBody:
                {
                    var text = plan.Request.Body is JsonBody json ? json.Text : ((TextBody)plan.Request.Body).Text;
                    imports.Add("okhttp3.MediaType");
                    imports.Add("okhttp3.RequestBody");
                    var contentType = plan.Headers()
                        .Where(header => Text.AsciiEquals(header.Name, "content-type"))
                        .Select(header => header.Value)
                        .FirstOrDefault() ?? "text/plain";
                    statements.Add(
                        $"RequestBody body = RequestBody.create(\n    {Literal(text)},\n    MediaType.parse({Literal(contentType)}));");
                    return "body";
                }

            case UrlEncodedBody form:
                {
                    imports.Add("okhttp3.FormBody");
                    var rows = string.Join(
                        "\n",
                        form.Entries
                            .Where(entry => entry.IsActive)
                            .Select(entry => $".add({Literal(entry.Name.Trim())}, {Literal(entry.Value)})"));
                    statements.Add($"FormBody body = new FormBody.Builder()\n{Text.Indent(rows, 4)}\n    .build();");
                    return "body";
                }

            case MultipartBody multipart:
                {
                    imports.Add("okhttp3.MultipartBody");
                    var rows = new List<string> { ".setType(MultipartBody.FORM)" };
                    foreach (var entry in multipart.Entries.Where(entry => entry.IsActive))
                    {
                        var name = Literal(entry.Name.Trim());
                        switch (entry.Value)
                        {
                            case MultipartText text:
                                rows.Add($".addFormDataPart({name}, {Literal(text.Value)})");
                                break;
                            case MultipartFile file:
                                imports.Add("okhttp3.MediaType");
                                imports.Add("okhttp3.RequestBody");
                                imports.Add("java.io.File");
                                var mime = file.ContentType is { } type ? $"MediaType.parse({Literal(type)})" : "null";
                                rows.Add(
                                    $".addFormDataPart(\n        {name},\n        {Literal(Text.FileName(file.Path, file.FileName))},\n        "
                                    + $"RequestBody.create(new File({Literal(file.Path)}), {mime}))");
                                break;
                        }
                    }
                    statements.Add(
                        $"MultipartBody body = new MultipartBody.Builder()\n{Text.Indent(string.Join("\n", rows), 4)}\n    .build();");
                    return "body";
                }

            default:
                imports.Add("okhttp3.RequestBody");
                return "RequestBody.create(new byte[0], null)";
        }
    }

    /// <summary>
    /// The <c>BodyPublisher</c> the standard client takes, and whatever has to
    /// be built before it.
    /// </summary>
    private static string Publisher(Plan plan, List<string> imports, List<string> statements, List<string> notes)
    {
        var method = plan.Method;
        switch (plan.Request.Body)
        {
            case JsonBody json:
                statements.Add($"String payload = {Literal(json.Text)};");
                return $".method({Literal(method)}, HttpRequest.BodyPublishers.ofString(payload))";
            case TextBody text:
                statements.Add($"String payload = {Literal(text.Text)};");
                return $".method({Literal(method)}, HttpRequest.BodyPublishers.ofString(payload))";

            case UrlEncodedBody form:
                statements.Add($"String payload = {Literal(Text.FormEncoded(form.Entries))};");
                return $".method({Literal(method)}, HttpRequest.BodyPublishers.ofString(payload))";

            /*
             * There is no multipart publisher in the standard client, so the body
             * is written out by hand. The boundary is fixed rather than random
             * because a snippet that reads differently every time it is copied is
             * harder to compare with the last one.
             */
            case MultipartBody multipart:
                {
                    imports.Add("java.io.ByteArrayOutputStream");
                    imports.Add("java.nio.charset.StandardCharsets");
                    imports.Add("java.nio.file.Files");
                    imports.Add("java.nio.file.Path");

                    notes.Add(
                        "HttpClient has no multipart publisher, so the body is assembled here. The "
                        + "OkHttp form has one built in.");

                    const string boundary = "----ApiClientBoundary";
                    var lines = new List<string>
                {
                    $"String boundary = {Literal(boundary)};",
                    "ByteArrayOutputStream payload = new ByteArrayOutputStream();",
                };
                    foreach (var entry in multipart.Entries.Where(entry => entry.IsActive))
                    {
                        var name = entry.Name.Trim();
                        switch (entry.Value)
                        {
                            case MultipartText text:
                                lines.Add(
                                    "payload.writeBytes((\"--\" + boundary + \"\\r\\n\"\n    + "
                                    + $"\"Content-Disposition: form-data; name=\\\"{name}\\\"\\r\\n\\r\\n\"\n    "
                                    + $"+ {Literal(text.Value)} + \"\\r\\n\").getBytes(StandardCharsets.UTF_8));");
                                break;
                            case MultipartFile file:
                                var mime = file.ContentType ?? "application/octet-stream";
                                lines.Add(
                                    "payload.writeBytes((\"--\" + boundary + \"\\r\\n\"\n    + "
                                    + $"\"Content-Disposition: form-data; name=\\\"{name}\\\"; "
                                    + $"filename=\\\"{Text.FileName(file.Path, file.FileName)}\\\"\\r\\n\"\n    + \"Content-Type: {mime}\\r\\n\\r\\n\")\n    "
                                    + ".getBytes(StandardCharsets.UTF_8));\n"
                                    + $"payload.writeBytes(Files.readAllBytes(Path.of({Literal(file.Path)})));\n"
                                    + "payload.writeBytes(\"\\r\\n\".getBytes(StandardCharsets.UTF_8));");
                                break;
                        }
                    }
                    lines.Add(
                        "payload.writeBytes((\"--\" + boundary + \"--\\r\\n\")\n    "
                        + ".getBytes(StandardCharsets.UTF_8));");
                    statements.Add(string.Join("\n", lines));

                    return ".header(\"Content-Type\", \"multipart/form-data; boundary=\" + boundary)\n"
                        + $".method({Literal(method)}, "
                        + "HttpRequest.BodyPublishers.ofByteArray(payload.toByteArray()))";
                }

            default:
                return method == "GET"
                    ? ".GET()"
                    : $".method({Literal(method)}, HttpRequest.BodyPublishers.noBody())";
        }
    }

    /// <summary>
    /// The <c>SSLContext</c> the client is built with, when anything about trust
    /// or identity has been changed from the default.
    /// </summary>
    private static string? SslContext(Plan plan, List<string> imports, List<string> notes, List<string> statements)
    {
        var configured = plan.Identity;
        var extra = plan.ExtraCaFiles();

        if (plan.AcceptsInvalidCerts)
        {
            notes.Add(
                "Certificate checking is off in Settings. Java has no switch for that: it needs a "
                + "TrustManager that accepts everything, which is not written out here on purpose.");
        }
        foreach (var path in extra)
        {
            notes.Add(
                $"Java reads trust from a KeyStore rather than a PEM file. Import `{path}` with "
                + "`keytool -importcert`, or build a TrustManagerFactory from it.");
        }

        if (configured is not (var identity, var kind))
        {
            return null;
        }
        var trimmed = identity.Path.Trim();
        if (kind == IdentityKind.Pem)
        {
            notes.Add(
                "Java loads a client certificate from a PKCS#12 keystore, not from PEM. Convert it "
                + "with `openssl pkcs12 -export`.");
            return null;
        }

        imports.Add("java.io.FileInputStream");
        imports.Add("java.security.KeyStore");
        imports.Add("javax.net.ssl.KeyManagerFactory");
        imports.Add("javax.net.ssl.SSLContext");

        var password = identity.Password ?? "";
        statements.Add(
            $"char[] password = {Literal(password)}.toCharArray();\n"
            + "KeyStore keyStore = KeyStore.getInstance(\"PKCS12\");\n"
            + $"try (FileInputStream in = new FileInputStream({Literal(trimmed)})) {{\n    "
            + "keyStore.load(in, password);\n}\n"
            + "KeyManagerFactory keyManagers = KeyManagerFactory.getInstance(\n    "
            + "KeyManagerFactory.getDefaultAlgorithm());\n"
            + "keyManagers.init(keyStore, password);\n"
            + "SSLContext sslContext = SSLContext.getInstance(\"TLS\");\n"
            + "sslContext.init(keyManagers.getKeyManagers(), null, null);");

        return ".sslContext(sslContext)";
    }

    private static void ChallengeNote(Plan plan, List<string> notes, string library)
    {
        if (plan.Effective.Auth is AuthPlan.Challenge { Auth: var challenge })
        {
            var scheme = challenge is ChallengeAuth.Digest ? "digest" : "NTLM";
            notes.Add($"{library} does not answer a {scheme} challenge on its own.");
        }
    }

    private static string Assemble(List<string> notes, List<string> imports, List<string> statements)
    {
        var output = new StringBuilder();
        foreach (var note in notes)
        {
            output.Append(Text.Comment("// ", note)).Append('\n');
        }

        foreach (var import in imports.Distinct().Order(StringComparer.Ordinal))
        {
            output.Append($"import {import};\n");
        }

        output.Append("\npublic class Main {\n    public static void main(String[] args) throws Exception {\n");
        output.Append(Text.Indent(string.Join("\n\n", statements), 8));
        output.Append("\n    }\n}\n");
        return Text.Settle(output.ToString());
    }

    /// <summary>
    /// A Java string literal.
    ///
    /// A text block for anything that runs over lines, with its content at the
    /// left margin: Java strips the smallest indentation it finds across the
    /// content lines, and at the margin there is none to strip, so the body
    /// comes out exactly as it went in.
    /// </summary>
    internal static string Literal(string value)
    {
        if (value.Contains('\n') && !value.Contains('\r'))
        {
            var block = value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"\"\"", "\\\"\\\"\\\"", StringComparison.Ordinal);
            // A quote against the closing delimiter would make four in a row.
            if (block.EndsWith('"'))
            {
                block = block[..^1] + "\\\"";
            }
            return Text.Keep($"\"\"\"\n{block}\"\"\"");
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
