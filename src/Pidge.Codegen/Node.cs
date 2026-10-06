using System.Text;
using Pidge.Core;
using Pidge.HttpEngine;

namespace Pidge.Codegen;

/// <summary>
/// Node.js, through the built-in <c>fetch</c> or through axios.
///
/// Both are written as an ES module, which is what lets the top-level
/// <c>await</c> stand on its own: save it as <c>request.mjs</c> and run it.
/// </summary>
internal static class Node
{
    public enum Library
    {
        Fetch,
        Axios,
    }

    public static string Generate(Plan plan, Library library) =>
        library == Library.Fetch ? Fetch(plan) : Axios(plan);

    private static string Fetch(Plan plan)
    {
        var notes = plan.Notes();
        var imports = new List<string>();
        var blocks = new List<string>();
        var options = new List<string> { $"method: {Literal(plan.Method)}," };

        var (headers, collided) = Text.Fold(plan.Headers());
        if (collided)
        {
            notes.Add(Text.FoldedNote);
        }

        blocks.Add($"const url = {Literal(plan.Effective.Url)};");

        var headerRows = headers.Select(header => $"{Key(header.Name)}: {Literal(header.Value)},").ToList();
        if (plan.Basic is (var username, var password))
        {
            headerRows.Add(
                $"Authorization: \"Basic \" + Buffer.from({Literal($"{username}:{password}")}).toString(\"base64\"),");
        }
        if (headerRows.Count > 0)
        {
            blocks.Add($"const headers = {{\n{Text.Indent(string.Join("\n", headerRows), 2)}\n}};");
            options.Add("headers,");
        }

        ChallengeNote(plan, notes, "fetch");
        options.AddRange(Body(plan, imports, blocks, notes, "body"));

        if (!plan.Options.FollowRedirects)
        {
            options.Add("redirect: \"manual\",");
        }
        var timeout = plan.TimeoutMs;
        if (timeout > 0)
        {
            options.Add($"signal: AbortSignal.timeout({timeout}),");
        }

        /*
         * `fetch` has no place to put a certificate: the agent that would carry one
         * belongs to undici, and reaching it means installing undici and setting a
         * global dispatcher. The environment variable is the one thing that works
         * without any of that, and it is worth being plain about what it does.
         */
        if (plan.AcceptsInvalidCerts)
        {
            blocks.Insert(
                0,
                "// This turns certificate checking off for the whole process.\n"
                + "process.env.NODE_TLS_REJECT_UNAUTHORIZED = \"0\";");
        }
        foreach (var path in plan.ExtraCaFiles())
        {
            notes.Add(
                "`fetch` has no per-request CA list. Run node with "
                + $"`--use-openssl-ca` and `NODE_EXTRA_CA_CERTS={path}`, or use the axios form, which "
                + "takes an agent.");
        }
        if (plan.Identity is not null)
        {
            notes.Add(
                "`fetch` cannot present a client certificate. The axios form can, through an "
                + "https.Agent.");
        }

        blocks.Add($"const response = await fetch(url, {{\n{Text.Indent(string.Join("\n", options), 2)}\n}});");
        blocks.Add("console.log(response.status);\nconsole.log(await response.text());");

        return Assemble(notes, imports, blocks);
    }

    private static string Axios(Plan plan)
    {
        var notes = plan.Notes();
        var imports = new List<string> { "import axios from \"axios\";" };
        var blocks = new List<string>();
        var options = new List<string>
        {
            $"method: {Literal(plan.Method.ToLowerInvariant())},",
            $"url: {Literal(plan.Effective.Url)},",
        };

        var (headers, collided) = Text.Fold(plan.Headers());
        if (collided)
        {
            notes.Add(Text.FoldedNote);
        }
        if (headers.Count > 0)
        {
            var rows = string.Join("\n", headers.Select(header => $"{Key(header.Name)}: {Literal(header.Value)},"));
            blocks.Add($"const headers = {{\n{Text.Indent(rows, 2)}\n}};");
            options.Add("headers,");
        }

        if (plan.Basic is (var username, var password))
        {
            options.Add($"auth: {{ username: {Literal(username)}, password: {Literal(password)} }},");
        }
        ChallengeNote(plan, notes, "axios");

        options.AddRange(Body(plan, imports, blocks, notes, "data"));

        var timeout = plan.TimeoutMs;
        if (timeout > 0)
        {
            options.Add($"timeout: {timeout},");
        }
        if (!plan.Options.FollowRedirects)
        {
            options.Add("maxRedirects: 0,");
        }
        // A 4xx is an outcome rather than a throw, as it is everywhere else here.
        options.Add("validateStatus: () => true,");

        if (Agent(plan, imports) is { } agent)
        {
            blocks.Add(agent);
            options.Add("httpsAgent: agent,");
        }

        blocks.Add($"const response = await axios({{\n{Text.Indent(string.Join("\n", options), 2)}\n}});");
        blocks.Add("console.log(response.status);\nconsole.log(response.data);");

        return Assemble(notes, imports, blocks);
    }

    /// <summary>The agent axios takes its TLS settings through.</summary>
    private static string? Agent(Plan plan, List<string> imports)
    {
        var fields = new List<string>();

        if (plan.AcceptsInvalidCerts)
        {
            fields.Add("rejectUnauthorized: false,");
        }
        foreach (var path in plan.ExtraCaFiles())
        {
            fields.Add($"ca: readFileSync({Literal(path)}),");
        }
        if (plan.Identity is (var identity, var kind))
        {
            var path = identity.Path.Trim();
            if (kind == IdentityKind.Pem)
            {
                fields.Add($"cert: readFileSync({Literal(path)}),");
                fields.Add($"key: readFileSync({Literal(path)}),");
            }
            else
            {
                fields.Add($"pfx: readFileSync({Literal(path)}),");
                fields.Add($"passphrase: {Literal(identity.Password ?? "")},");
            }
        }

        if (fields.Count == 0)
        {
            return null;
        }
        imports.Add("import https from \"node:https\";");
        if (fields.Any(field => field.Contains("readFileSync", StringComparison.Ordinal)))
        {
            imports.Add("import { readFileSync } from \"node:fs\";");
        }
        return $"const agent = new https.Agent({{\n{Text.Indent(string.Join("\n", fields), 2)}\n}});";
    }

    /// <summary>
    /// The body, as the options that carry it. Both libraries take the same
    /// three shapes — a string, a <c>URLSearchParams</c> and a <c>FormData</c> —
    /// under different names: <c>fetch</c> calls the field <c>body</c> and axios
    /// calls it <c>data</c>.
    /// </summary>
    private static List<string> Body(
        Plan plan,
        List<string> imports,
        List<string> blocks,
        List<string> notes,
        string field)
    {
        var carry = field == "body" ? "body," : $"{field}: body,";

        switch (plan.Request.Body)
        {
            case JsonBody json:
                blocks.Add($"const body = {Literal(json.Text)};");
                return [carry];
            case TextBody text:
                blocks.Add($"const body = {Literal(text.Text)};");
                return [carry];

            // A list of pairs rather than an object: two rows may share a name.
            case UrlEncodedBody form:
                {
                    var rows = string.Join(
                        "\n",
                        form.Entries
                            .Where(entry => entry.IsActive)
                            .Select(entry => $"[{Literal(entry.Name.Trim())}, {Literal(entry.Value)}],"));
                    blocks.Add($"const body = new URLSearchParams([\n{Text.Indent(rows, 2)}\n]);");
                    return [carry];
                }

            case MultipartBody multipart:
                {
                    var lines = new List<string> { "const body = new FormData();" };
                    var readsFiles = false;
                    foreach (var entry in multipart.Entries.Where(entry => entry.IsActive))
                    {
                        var name = Literal(entry.Name.Trim());
                        switch (entry.Value)
                        {
                            case MultipartText text:
                                lines.Add($"body.append({name}, {Literal(text.Value)});");
                                break;
                            case MultipartFile file:
                                readsFiles = true;
                                var mime = file.ContentType is { } type ? $", {{ type: {Literal(type)} }}" : "";
                                lines.Add(
                                    $"body.append(\n  {name},\n  new Blob([await readFile({Literal(file.Path)})]{mime}),\n  "
                                    + $"{Literal(Text.FileName(file.Path, file.FileName))},\n);");
                                break;
                        }
                    }
                    if (readsFiles)
                    {
                        imports.Add("import { readFile } from \"node:fs/promises\";");
                    }
                    if (!multipart.Entries.Any(entry => entry.IsActive && entry.Value is MultipartFile))
                    {
                        notes.Add(
                            "This multipart body has no file in it. A FormData with only text parts is "
                            + "still sent as multipart, so this is the same request the app makes.");
                    }
                    blocks.Add(string.Join("\n", lines));
                    return [carry];
                }

            default:
                return [];
        }
    }

    private static void ChallengeNote(Plan plan, List<string> notes, string library)
    {
        if (plan.Effective.Auth is AuthPlan.Challenge { Auth: var challenge })
        {
            var scheme = challenge is ChallengeAuth.Digest ? "digest" : "NTLM";
            notes.Add(
                $"{library} cannot answer a {scheme} challenge on its own. Node has no built-in "
                + "support for either scheme.");
        }
    }

    private static string Assemble(List<string> notes, List<string> imports, List<string> blocks)
    {
        var output = new StringBuilder();
        foreach (var note in notes)
        {
            output.Append(Text.Comment("// ", note)).Append('\n');
        }
        if (imports.Count > 0)
        {
            // Repeats next to each other are dropped; the order is the order they were added in.
            var kept = new List<string>();
            foreach (var import in imports)
            {
                if (kept.Count == 0 || kept[^1] != import)
                {
                    kept.Add(import);
                }
            }
            output.Append(string.Join("\n", kept));
            output.Append("\n\n");
        }
        output.Append(string.Join("\n\n", blocks));
        output.Append('\n');
        return output.ToString();
    }

    /// <summary>An object key, quoted only when it has to be.</summary>
    internal static string Key(string name)
    {
        if (name.Length > 0
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '$')
            && !char.IsAsciiDigit(name[0]))
        {
            return name;
        }
        return Literal(name);
    }

    /// <summary>
    /// A JavaScript string literal.
    ///
    /// A body that runs over lines is written as a template literal, which keeps
    /// them; anything else is a quoted string. The backtick and <c>${</c> are
    /// what a template literal has to escape, and neither is common in a request.
    /// </summary>
    internal static string Literal(string value)
    {
        if (value.Contains('\n') && !value.Contains('\r'))
        {
            var escaped = value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("`", "\\`", StringComparison.Ordinal)
                .Replace("${", "\\${", StringComparison.Ordinal);
            return $"`{escaped}`";
        }

        var quoted = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        return $"\"{quoted}\"";
    }
}
