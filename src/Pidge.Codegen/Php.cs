using System.Globalization;
using System.Text;
using Pidge.Core;
using Pidge.HttpEngine;

namespace Pidge.Codegen;

/// <summary>
/// PHP, through the cURL extension or through Guzzle.
///
/// <c>ext-curl</c> is in every PHP build worth having and needs no
/// <c>composer require</c>, which is why it goes first. Guzzle is what a
/// project with a <c>composer.json</c> already has, and says most of this far
/// more briefly.
/// </summary>
internal static class Php
{
    public enum Library
    {
        Curl,
        Guzzle,
    }

    public static string Generate(Plan plan, Library library) =>
        library == Library.Curl ? Curl(plan) : Guzzle(plan);

    private static string Curl(Plan plan)
    {
        var notes = plan.Notes();
        var blocks = new List<string>();
        var options = new List<(string Name, string Value)>
        {
            ("CURLOPT_URL", Literal(plan.Effective.Url)),
            ("CURLOPT_RETURNTRANSFER", "true"),
            ("CURLOPT_CUSTOMREQUEST", Literal(plan.Method)),
        };

        var headers = plan.Headers();
        if (headers.Count > 0)
        {
            var rows = string.Join("\n", headers.Select(header => $"{Literal($"{header.Name}: {header.Value}")},"));
            options.Add(("CURLOPT_HTTPHEADER", $"[\n{Text.Indent(rows, 4)}\n]"));
        }

        if (plan.Basic is (var username, var password))
        {
            options.Add(("CURLOPT_USERPWD", Literal($"{username}:{password}")));
        }
        switch (plan.Effective.Auth)
        {
            case AuthPlan.Challenge { Auth: ChallengeAuth.Digest digest }:
                options.Add(("CURLOPT_USERPWD", Literal($"{digest.Username}:{digest.Password}")));
                options.Add(("CURLOPT_HTTPAUTH", "CURLAUTH_DIGEST"));
                break;
            case AuthPlan.Challenge { Auth: ChallengeAuth.Ntlm ntlm }:
                var account = ntlm.Domain.Trim().Length == 0 ? ntlm.Username : $"{ntlm.Domain}\\{ntlm.Username}";
                options.Add(("CURLOPT_USERPWD", Literal($"{account}:{ntlm.Password}")));
                options.Add(("CURLOPT_HTTPAUTH", "CURLAUTH_NTLM"));
                break;
        }

        options.AddRange(Body(plan, blocks));

        if (plan.Options.FollowRedirects)
        {
            options.Add(("CURLOPT_FOLLOWLOCATION", "true"));
        }
        var timeout = plan.TimeoutMs;
        if (timeout > 0)
        {
            options.Add(("CURLOPT_TIMEOUT_MS", timeout.ToString(CultureInfo.InvariantCulture)));
        }
        if (plan.AcceptsInvalidCerts)
        {
            options.Add(("CURLOPT_SSL_VERIFYPEER", "false"));
            options.Add(("CURLOPT_SSL_VERIFYHOST", "0"));
        }
        var extra = plan.ExtraCaFiles();
        if (extra.Count > 0)
        {
            options.Add(("CURLOPT_CAINFO", Literal(extra[0])));
            if (extra.Count > 1)
            {
                notes.Add(
                    $"cURL reads one CA file. {extra.Count} were set, so only the first is here; concatenate "
                    + "them into one PEM to use them all.");
            }
            if (plan.MergesCaFiles)
            {
                notes.Add(
                    "CURLOPT_CAINFO replaces the default CA bundle rather than adding to it, which "
                    + "is what the app does.");
            }
        }
        if (plan.Identity is (var identity, var kind))
        {
            options.Add(("CURLOPT_SSLCERT", Literal(identity.Path.Trim())));
            if (kind == IdentityKind.Pkcs12)
            {
                options.Add(("CURLOPT_SSLCERTTYPE", Literal("P12")));
            }
            if (identity.Password is { } certificatePassword)
            {
                options.Add(("CURLOPT_SSLCERTPASSWD", Literal(certificatePassword)));
            }
        }

        blocks.Add($"$curl = curl_init();\n\ncurl_setopt_array($curl, [\n{Text.Indent(Pairs(options), 4)}\n]);");
        blocks.Add(
            "$response = curl_exec($curl);\n"
            + "$status = curl_getinfo($curl, CURLINFO_RESPONSE_CODE);\n"
            + "curl_close($curl);\n\n"
            + "echo $status, PHP_EOL;\necho $response, PHP_EOL;");

        return Assemble(notes, null, blocks);
    }

    private static string Guzzle(Plan plan)
    {
        var notes = plan.Notes();
        var blocks = new List<string>();
        var client = new List<(string Name, string Value)>();
        var options = new List<(string Name, string Value)>();

        var timeout = plan.TimeoutMs;
        if (timeout > 0)
        {
            client.Add((Literal("timeout"), Text.Float(timeout / 1000.0)));
        }
        client.Add((Literal("allow_redirects"), plan.Options.FollowRedirects ? "true" : "false"));
        // A 4xx is an outcome rather than an exception, as it is everywhere else.
        client.Add((Literal("http_errors"), "false"));

        var extra = plan.ExtraCaFiles();
        if (plan.AcceptsInvalidCerts)
        {
            client.Add((Literal("verify"), "false"));
        }
        else if (extra.Count > 0)
        {
            client.Add((Literal("verify"), Literal(extra[0])));
            if (plan.MergesCaFiles)
            {
                notes.Add(
                    "`verify` replaces the default CA bundle rather than adding to it, which is what "
                    + "the app does.");
            }
        }
        if (plan.Identity is (var identity, _))
        {
            var path = Literal(identity.Path.Trim());
            client.Add((
                Literal("cert"),
                identity.Password is { } certificatePassword ? $"[{path}, {Literal(certificatePassword)}]" : path));
        }

        var headers = plan.Headers();
        if (headers.Count > 0)
        {
            var rows = string.Join("\n", headers.Select(header => $"{Literal(header.Name)} => {Literal(header.Value)},"));
            options.Add((Literal("headers"), $"[\n{Text.Indent(rows, 4)}\n]"));
        }

        if (plan.Basic is (var username, var password))
        {
            options.Add((Literal("auth"), $"[{Literal(username)}, {Literal(password)}]"));
        }
        switch (plan.Effective.Auth)
        {
            case AuthPlan.Challenge { Auth: ChallengeAuth.Digest digest }:
                options.Add((
                    Literal("auth"),
                    $"[{Literal(digest.Username)}, {Literal(digest.Password)}, {Literal("digest")}]"));
                break;
            case AuthPlan.Challenge { Auth: ChallengeAuth.Ntlm ntlm }:
                var account = ntlm.Domain.Trim().Length == 0 ? ntlm.Username : $"{ntlm.Domain}\\{ntlm.Username}";
                options.Add((
                    Literal("auth"),
                    $"[{Literal(account)}, {Literal(ntlm.Password)}, {Literal("ntlm")}]"));
                break;
        }

        options.AddRange(GuzzleBody(plan, blocks));

        var output = $"$client = new Client([\n{Text.Indent(Pairs(client), 4)}\n]);";
        if (options.Count == 0)
        {
            output += $"\n\n$response = $client->request({Literal(plan.Method)}, {Literal(plan.Effective.Url)});";
        }
        else
        {
            output +=
                $"\n\n$response = $client->request({Literal(plan.Method)}, {Literal(plan.Effective.Url)}, [\n"
                + $"{Text.Indent(Pairs(options), 4)}\n]);";
        }
        blocks.Add(output);
        blocks.Add("echo $response->getStatusCode(), PHP_EOL;\necho $response->getBody(), PHP_EOL;");

        return Assemble(notes, "use GuzzleHttp\\Client;", blocks);
    }

    private static string Pairs(List<(string Name, string Value)> rows)
    {
        var width = rows.Count == 0 ? 0 : rows.Max(row => Text.Utf8Length(row.Name));
        return string.Join("\n", rows.Select(row => $"{Text.PadRight(row.Name, width)} => {row.Value},"));
    }

    /// <summary>The body, as the cURL options that carry it.</summary>
    private static List<(string Name, string Value)> Body(Plan plan, List<string> blocks)
    {
        switch (plan.Request.Body)
        {
            case JsonBody json:
                blocks.Add($"$payload = {Literal(json.Text)};");
                return [("CURLOPT_POSTFIELDS", "$payload")];
            case TextBody text:
                blocks.Add($"$payload = {Literal(text.Text)};");
                return [("CURLOPT_POSTFIELDS", "$payload")];

            case UrlEncodedBody form:
                blocks.Add($"$payload = {Literal(Text.FormEncoded(form.Entries))};");
                return [("CURLOPT_POSTFIELDS", "$payload")];

            /*
             * An array of fields, which is how the extension is told to write a
             * multipart body: it generates the boundary and the headers itself, so
             * no Content-Type is set here.
             */
            case MultipartBody multipart:
                {
                    var rows = string.Join(
                        "\n",
                        multipart.Entries
                            .Where(entry => entry.IsActive)
                            .Select(entry =>
                            {
                                var name = Literal(entry.Name.Trim());
                                return entry.Value switch
                                {
                                    MultipartFile file =>
                                        $"{name} => new CURLFile({Literal(file.Path)}, "
                                        + $"{Literal(file.ContentType ?? "application/octet-stream")}, "
                                        + $"{Literal(Text.FileName(file.Path, file.FileName))}),",
                                    MultipartText text => $"{name} => {Literal(text.Value)},",
                                    _ => $"{name} => {Literal("")},",
                                };
                            }));
                    blocks.Add($"$payload = [\n{Text.Indent(rows, 4)}\n];");
                    return [("CURLOPT_POSTFIELDS", "$payload")];
                }

            default:
                return [];
        }
    }

    /// <summary>The same for Guzzle, which names each shape rather than taking one field.</summary>
    private static List<(string Name, string Value)> GuzzleBody(Plan plan, List<string> blocks)
    {
        switch (plan.Request.Body)
        {
            case JsonBody json:
                blocks.Add($"$payload = {Literal(json.Text)};");
                return [(Literal("body"), "$payload")];
            case TextBody text:
                blocks.Add($"$payload = {Literal(text.Text)};");
                return [(Literal("body"), "$payload")];

            // `form_params` encodes the pairs itself, the same way the engine does.
            case UrlEncodedBody form:
                {
                    var rows = string.Join(
                        "\n",
                        form.Entries
                            .Where(entry => entry.IsActive)
                            .Select(entry => $"{Literal(entry.Name.Trim())} => {Literal(entry.Value)},"));
                    blocks.Add($"$payload = [\n{Text.Indent(rows, 4)}\n];");
                    return [(Literal("form_params"), "$payload")];
                }

            case MultipartBody multipart:
                {
                    var rows = string.Join(
                        "\n",
                        multipart.Entries
                            .Where(entry => entry.IsActive)
                            .Select(entry =>
                            {
                                var name = Literal(entry.Name.Trim());
                                switch (entry.Value)
                                {
                                    case MultipartFile file:
                                        var part =
                                            $"[\n    {Literal("name")} => {name},\n    {Literal("contents")} => "
                                            + $"Utils::tryFopen({Literal(file.Path)}, 'r'),\n    "
                                            + $"{Literal("filename")} => {Literal(Text.FileName(file.Path, file.FileName))},";
                                        if (file.ContentType is { } mime)
                                        {
                                            part +=
                                                $"\n    {Literal("headers")} => [{Literal("Content-Type")} => {Literal(mime)}],";
                                        }
                                        return part + "\n],";
                                    case MultipartText text:
                                        return $"[\n    {Literal("name")} => {name},\n    {Literal("contents")} => {Literal(text.Value)},\n],";
                                    default:
                                        return $"[\n    {Literal("name")} => {name},\n    {Literal("contents")} => {Literal("")},\n],";
                                }
                            }));
                    blocks.Add($"$payload = [\n{Text.Indent(rows, 4)}\n];");
                    return [(Literal("multipart"), "$payload")];
                }

            default:
                return [];
        }
    }

    private static string Assemble(List<string> notes, string? import, List<string> blocks)
    {
        var output = new StringBuilder("<?php\n\n");
        foreach (var note in notes)
        {
            output.Append(Text.Comment("// ", note)).Append('\n');
        }
        if (notes.Count > 0)
        {
            output.Append('\n');
        }
        if (import is not null)
        {
            output.Append("require 'vendor/autoload.php';\n\n");
            output.Append(import);
            output.Append("\n\n");
        }
        output.Append(string.Join("\n\n", blocks));
        output.Append('\n');
        return output.ToString();
    }

    /// <summary>
    /// A PHP string literal.
    ///
    /// Single quotes, in which nothing is interpolated — <c>$</c> and <c>\n</c>
    /// are text — and the only two escapes are the quote itself and the
    /// backslash. It may run over as many lines as it likes.
    /// </summary>
    internal static string Literal(string value) =>
        $"'{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}'";
}
