using System.Text;
using Pidge.Core;
using Pidge.HttpEngine;

namespace Pidge.Codegen;

/// <summary>
/// Python, through <c>requests</c>.
///
/// <c>requests</c> rather than <c>http.client</c> or <c>urllib</c>: it is what
/// anybody reading this would reach for, and the standard library equivalent of
/// a multipart body is a page of code nobody wants pasted into theirs.
/// </summary>
internal static class Python
{
    public static string Generate(Plan plan)
    {
        var notes = plan.Notes();
        var imports = new List<string> { "import requests" };
        var blocks = new List<string>();
        var arguments = new List<string> { "url" };

        var (headers, collided) = Text.Fold(plan.Headers());
        if (collided)
        {
            notes.Add(Text.FoldedNote);
        }

        blocks.Add($"url = {Literal(plan.Effective.Url)}");

        if (headers.Count > 0)
        {
            var rows = string.Join("\n", headers.Select(header => $"{Literal(header.Name)}: {Literal(header.Value)},"));
            blocks.Add($"headers = {{\n{Text.Indent(rows, 4)}\n}}");
            arguments.Add("headers=headers");
        }

        if (plan.Basic is (var username, var password))
        {
            arguments.Add($"auth=({Literal(username)}, {Literal(password)})");
        }

        switch (plan.Effective.Auth)
        {
            case AuthPlan.Challenge { Auth: ChallengeAuth.Digest digest }:
                imports.Add("from requests.auth import HTTPDigestAuth");
                arguments.Add($"auth=HTTPDigestAuth({Literal(digest.Username)}, {Literal(digest.Password)})");
                break;
            case AuthPlan.Challenge { Auth: ChallengeAuth.Ntlm ntlm }:
                // Not part of `requests`; `pip install requests-ntlm` supplies it.
                imports.Add("from requests_ntlm import HttpNtlmAuth");
                if (ntlm.Workstation.Trim().Length > 0)
                {
                    notes.Add("requests-ntlm has nowhere to put a workstation name, so it is not sent.");
                }
                var account = ntlm.Domain.Trim().Length == 0 ? ntlm.Username : $"{ntlm.Domain}\\{ntlm.Username}";
                arguments.Add($"auth=HttpNtlmAuth({Literal(account)}, {Literal(ntlm.Password)})");
                break;
        }

        arguments.AddRange(Body(plan, blocks, notes));

        if (!plan.Options.FollowRedirects)
        {
            arguments.Add("allow_redirects=False");
        }
        arguments.AddRange(Tls(plan, notes));
        var timeout = plan.TimeoutMs;
        if (timeout > 0)
        {
            arguments.Add($"timeout={Text.Seconds(timeout)}");
        }

        blocks.Add($"response = requests.{plan.Method.ToLowerInvariant()}({string.Join(", ", arguments)})");
        blocks.Add("print(response.status_code)\nprint(response.text)");

        var output = new StringBuilder();
        foreach (var note in notes)
        {
            output.Append(Text.Comment("# ", note)).Append('\n');
        }
        output.Append(string.Join("\n", imports));
        output.Append("\n\n");
        output.Append(string.Join("\n\n", blocks));
        output.Append('\n');
        return output.ToString();
    }

    /// <summary>Trust and identity, as the keyword arguments <c>requests</c> takes for them.</summary>
    private static List<string> Tls(Plan plan, List<string> notes)
    {
        var arguments = new List<string>();

        if (plan.Identity is (var identity, var kind))
        {
            var path = identity.Path.Trim();
            if (kind == IdentityKind.Pem)
            {
                arguments.Add($"cert={Literal(path)}");
            }
            else
            {
                // requests goes through OpenSSL, which wants PEM on disk.
                var converted = Text.WithExtension(path, "pem");
                notes.Add(
                    "requests cannot read a PKCS#12 bundle. Convert it first, which will ask for "
                    + $"the bundle's password:\n  openssl pkcs12 -in {path} -out {converted} -nodes");
                arguments.Add($"cert={Literal(converted)}");
            }
        }

        // Verification off wins over a CA file: there is nothing left to check.
        if (plan.AcceptsInvalidCerts)
        {
            arguments.Add("verify=False");
            return arguments;
        }

        var extra = plan.ExtraCaFiles();
        if (extra.Count > 0)
        {
            arguments.Add($"verify={Literal(extra[0])}");

            if (extra.Count > 1)
            {
                notes.Add(
                    $"requests reads one CA file. {extra.Count} were set, so only the first is here; concatenate "
                    + "them into one PEM to use them all.");
            }
            if (plan.MergesCaFiles)
            {
                notes.Add(
                    "verify= replaces the default CA bundle rather than adding to it, which is what "
                    + "the app does. A server with a public certificate will not verify against this "
                    + "file alone.");
            }
        }

        return arguments;
    }

    /// <summary>
    /// Adds whatever the body needs as its own statements, and returns the
    /// keyword arguments that refer to them.
    /// </summary>
    private static List<string> Body(Plan plan, List<string> blocks, List<string> notes)
    {
        switch (plan.Request.Body)
        {
            /*
             * `data=` with the text exactly as typed, rather than `json=` with a
             * dictionary. The editor holds text, which is not always valid JSON and
             * is not always meant to be; re-encoding it through a dictionary would
             * reorder keys and reformat numbers, and send something the user never
             * wrote.
             */
            case JsonBody json:
                blocks.Add($"payload = {Literal(json.Text)}");
                return ["data=payload"];
            case TextBody text:
                blocks.Add($"payload = {Literal(text.Text)}");
                return ["data=payload"];

            // A list of pairs rather than a dictionary: two rows may share a name,
            // and `requests` encodes the list the same way the engine does.
            case UrlEncodedBody form:
                {
                    var rows = string.Join(
                        "\n",
                        form.Entries
                            .Where(entry => entry.IsActive)
                            .Select(entry => $"({Literal(entry.Name.Trim())}, {Literal(entry.Value)}),"));
                    blocks.Add($"payload = [\n{Text.Indent(rows, 4)}\n]");
                    return ["data=payload"];
                }

            case MultipartBody multipart:
                {
                    var textRows = new List<string>();
                    var fileRows = new List<string>();

                    foreach (var entry in multipart.Entries.Where(entry => entry.IsActive))
                    {
                        var name = Literal(entry.Name.Trim());
                        switch (entry.Value)
                        {
                            case MultipartText text:
                                textRows.Add($"({name}, {Literal(text.Value)}),");
                                break;
                            case MultipartFile file:
                                var shown = Literal(Text.FileName(file.Path, file.FileName));
                                var handle = $"open({Literal(file.Path)}, \"rb\")";
                                fileRows.Add(
                                    file.ContentType is { } mime
                                        ? $"({name}, ({shown}, {handle}, {Literal(mime)})),"
                                        : $"({name}, ({shown}, {handle})),");
                                break;
                        }
                    }

                    var arguments = new List<string>();
                    if (textRows.Count > 0)
                    {
                        blocks.Add($"payload = [\n{Text.Indent(string.Join("\n", textRows), 4)}\n]");
                        arguments.Add("data=payload");
                    }
                    if (fileRows.Count == 0)
                    {
                        // Without a file, `requests` sends a form rather than a
                        // multipart body, which is not what the request says.
                        notes.Add(
                            "This multipart body has no file in it. requests sends a form-encoded body "
                            + "unless `files` is given, so add a part with a file to keep it multipart.");
                    }
                    else
                    {
                        blocks.Add($"files = [\n{Text.Indent(string.Join("\n", fileRows), 4)}\n]");
                        arguments.Add("files=files");
                    }
                    return arguments;
                }

            default:
                return [];
        }
    }

    /// <summary>
    /// A Python string literal.
    ///
    /// Text that runs over lines is written as it reads, in triple quotes. A
    /// carriage return has no spelling inside those — the source file's own
    /// line endings would swallow it — so anything carrying one is escaped onto
    /// a single line instead, where every character is explicit.
    /// </summary>
    internal static string Literal(string value)
    {
        if (value.Contains('\n') && !value.Contains('\r'))
        {
            var tripled = value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"\"\"", "\\\"\\\"\\\"", StringComparison.Ordinal);
            // A quote against the closing delimiter would make four in a row.
            if (tripled.EndsWith('"'))
            {
                tripled = tripled[..^1] + "\\\"";
            }
            return $"\"\"\"{tripled}\"\"\"";
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
