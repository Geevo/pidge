using System.Text;
using Pidge.Core;
using Pidge.HttpEngine;

namespace Pidge.Codegen;

/// <summary>
/// Go, through <c>net/http</c>.
///
/// There is one way to make a request in Go and everybody uses it, so there is
/// nothing to choose between here. The error handling is the plain
/// <c>if err != nil</c> that a program would really be written with rather than
/// anything shorter, because the snippet is meant to be pasted into one.
/// </summary>
internal static class Go
{
    /// <summary>
    /// One tab on every line, leaving the inside of a literal alone. Go is
    /// written with tabs and <c>gofmt</c> would put them back whatever this did.
    /// </summary>
    private static string Tabs(string block) =>
        string.Join(
            "\n",
            Text.Lines(block).Select(line => line.Length == 0 || line[0] == Text.KeepMark ? line : "\t" + line));

    public static string Generate(Plan plan)
    {
        var notes = plan.Notes();
        var imports = new List<string> { "fmt", "io", "net/http" };
        var statements = new List<string>();

        var payload = Body(plan, imports, statements, notes);

        statements.Add(
            $"req, err := http.NewRequest({Literal(plan.Method)}, {Literal(plan.Effective.Url)}, {payload})\n"
            + "if err != nil {\n\tpanic(err)\n}");

        var headers = new List<string>();
        // The writer generated the boundary, so only it can name the content type.
        if (plan.Request.Body is MultipartBody)
        {
            headers.Add("req.Header.Set(\"Content-Type\", writer.FormDataContentType())");
        }
        foreach (var (name, value) in plan.Headers())
        {
            // `Add` rather than `Set`, so two rows of one name stay two headers.
            headers.Add($"req.Header.Add({Literal(name)}, {Literal(value)})");
        }
        if (plan.Basic is (var username, var password))
        {
            headers.Add($"req.SetBasicAuth({Literal(username)}, {Literal(password)})");
        }
        if (headers.Count > 0)
        {
            statements.Add(string.Join("\n", headers));
        }

        if (plan.Effective.Auth is AuthPlan.Challenge { Auth: var challenge })
        {
            notes.Add(
                challenge is ChallengeAuth.Digest
                    ? "net/http does not answer a digest challenge. `github.com/icholy/digest` adds a "
                      + "RoundTripper that does."
                    : "net/http does not answer an NTLM challenge. "
                      + "`github.com/Azure/go-ntlmssp` adds a RoundTripper that does.");
        }

        statements.Add(Client(plan, imports, notes));
        statements.Add("res, err := client.Do(req)\nif err != nil {\n\tpanic(err)\n}\ndefer res.Body.Close()");
        statements.Add(
            "resBody, err := io.ReadAll(res.Body)\nif err != nil {\n\tpanic(err)\n}\n\n"
            + "fmt.Println(res.StatusCode)\nfmt.Println(string(resBody))");

        var output = new StringBuilder();
        foreach (var note in notes)
        {
            output.Append(Text.Comment("// ", note)).Append('\n');
        }
        output.Append("package main\n\nimport (\n");
        foreach (var import in imports.Distinct().Order(StringComparer.Ordinal))
        {
            output.Append($"\t{Literal(import)}\n");
        }
        output.Append(")\n\nfunc main() {\n");
        output.Append(Tabs(string.Join("\n\n", statements)));
        output.Append("\n}\n");
        return Text.Settle(output.ToString());
    }

    /// <summary>
    /// The client, which carries the timeout, the redirect policy and the TLS
    /// settings — everything that belongs to the client rather than the request.
    /// </summary>
    private static string Client(Plan plan, List<string> imports, List<string> notes)
    {
        var lines = new List<string>();
        var fields = new List<string>();

        var timeout = plan.TimeoutMs;
        if (timeout > 0)
        {
            imports.Add("time");
            fields.Add($"Timeout: {timeout} * time.Millisecond,");
        }

        var tls = new List<string>();
        if (plan.AcceptsInvalidCerts)
        {
            tls.Add("InsecureSkipVerify: true,");
        }

        var extra = plan.ExtraCaFiles();
        if (extra.Count > 0)
        {
            imports.Add("crypto/tls");
            imports.Add("crypto/x509");
            imports.Add("os");

            // Added to the system pool, which is what the app does, rather than
            // replacing it — `SystemCertPool` is the whole difference.
            lines.Add(
                plan.MergesCaFiles
                    ? "pool, err := x509.SystemCertPool()\nif err != nil {\n\tpanic(err)\n}"
                    : "pool := x509.NewCertPool()");
            foreach (var path in extra)
            {
                lines.Add(
                    $"ca, err := os.ReadFile({Literal(path)})\nif err != nil {{\n\tpanic(err)\n}}\n"
                    + "pool.AppendCertsFromPEM(ca)");
            }
            tls.Add("RootCAs: pool,");
        }

        if (plan.Identity is (var identity, var kind))
        {
            imports.Add("crypto/tls");
            var path = identity.Path.Trim();
            if (kind == IdentityKind.Pem)
            {
                lines.Add(
                    $"cert, err := tls.LoadX509KeyPair({Literal(path)}, {Literal(path)})\n"
                    + "if err != nil {\n\tpanic(err)\n}");
                tls.Add("Certificates: []tls.Certificate{cert},");
            }
            else
            {
                // Go's standard library has no PKCS#12 reader that yields a key.
                var stem = Text.TrimEndMatches(Text.TrimEndMatches(path, ".p12"), ".pfx");
                notes.Add(
                    "Go cannot read a PKCS#12 bundle from the standard library. Convert it "
                    + "first, which will ask for the bundle's password:\n  openssl pkcs12 -in "
                    + $"{path} -out {stem}.pem -nodes");
                var converted = $"{stem}.pem";
                lines.Add(
                    $"cert, err := tls.LoadX509KeyPair({Literal(converted)}, {Literal(converted)})\n"
                    + "if err != nil {\n\tpanic(err)\n}");
                tls.Add("Certificates: []tls.Certificate{cert},");
            }
        }

        if (tls.Count > 0)
        {
            imports.Add("crypto/tls");
            fields.Add(
                $"Transport: &http.Transport{{\n\tTLSClientConfig: &tls.Config{{\n{Tabs(Tabs(string.Join("\n", tls)))}\n\t}},\n}},");
        }

        lines.Add(
            fields.Count == 0
                ? "client := &http.Client{}"
                : $"client := &http.Client{{\n{Tabs(string.Join("\n", fields))}\n}}");

        if (!plan.Options.FollowRedirects)
        {
            lines.Add(
                "client.CheckRedirect = func(req *http.Request, via []*http.Request) error {\n\t"
                + "return http.ErrUseLastResponse\n}");
        }

        return string.Join("\n\n", lines);
    }

    /// <summary>
    /// The body, as the reader <c>NewRequest</c> takes. Anything that needs
    /// setting up first is added to <paramref name="statements"/>.
    /// </summary>
    private static string Body(Plan plan, List<string> imports, List<string> statements, List<string> notes)
    {
        switch (plan.Request.Body)
        {
            case JsonBody json:
                imports.Add("strings");
                statements.Add($"payload := strings.NewReader({Literal(json.Text)})");
                return "payload";
            case TextBody text:
                imports.Add("strings");
                statements.Add($"payload := strings.NewReader({Literal(text.Text)})");
                return "payload";

            case UrlEncodedBody form:
                imports.Add("strings");
                statements.Add($"payload := strings.NewReader({Literal(Text.FormEncoded(form.Entries))})");
                return "payload";

            case MultipartBody multipart:
                imports.Add("bytes");
                imports.Add("mime/multipart");

                var lines = new List<string>
                {
                    "payload := &bytes.Buffer{}",
                    "writer := multipart.NewWriter(payload)",
                };
                foreach (var entry in multipart.Entries.Where(entry => entry.IsActive))
                {
                    var name = Literal(entry.Name.Trim());
                    switch (entry.Value)
                    {
                        case MultipartText text:
                            lines.Add(
                                $"if err := writer.WriteField({name}, {Literal(text.Value)}); err != nil {{\n\t"
                                + "panic(err)\n}");
                            break;
                        case MultipartFile file:
                            imports.Add("os");
                            if (file.ContentType is not null)
                            {
                                notes.Add(
                                    $"The part `{entry.Name.Trim()}` is sent without the content type set for it: "
                                    + "`CreateFormFile` writes its own.");
                            }
                            lines.Add(
                                $"part, err := writer.CreateFormFile({name}, {Literal(Text.FileName(file.Path, file.FileName))})\n"
                                + "if err != nil {\n\tpanic(err)\n}\n"
                                + $"file, err := os.Open({Literal(file.Path)})\n"
                                + "if err != nil {\n\tpanic(err)\n}\n"
                                + "if _, err := io.Copy(part, file); err != nil {\n\tpanic(err)\n}\n"
                                + "file.Close()");
                            break;
                    }
                }
                lines.Add("if err := writer.Close(); err != nil {\n\tpanic(err)\n}");
                statements.Add(string.Join("\n\n", lines));
                return "payload";

            default:
                return "nil";
        }
    }

    /// <summary>
    /// A Go string literal.
    ///
    /// A backtick string keeps its newlines and has no escapes at all, which is
    /// what a JSON body wants — unless the body contains a backtick, which such
    /// a string has no way to carry.
    /// </summary>
    internal static string Literal(string value)
    {
        if (value.Contains('\n') && !value.Contains('`') && !value.Contains('\r'))
        {
            return Text.Keep($"`{value}`");
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
