using System.Text;
using Pidge.Core;
using Pidge.HttpEngine;

namespace Pidge.Codegen;

/// <summary>
/// Rust, through reqwest.
///
/// There is no HTTP client in the standard library, and reqwest is what a Rust
/// program that makes a request almost always uses — which is also why the
/// generated code and the Send button behave alike down to the redirect policy.
/// </summary>
internal static class Rust
{
    /// <summary>Blocking or async, which in reqwest is two different clients.</summary>
    public enum Style
    {
        Blocking,
        Async,
    }

    /// <summary>
    /// <c>reqwest::blocking</c> or <c>reqwest</c>, which is the only difference
    /// in the names; the builders either side of it are spelled the same.
    /// </summary>
    private static string Module(Style style) => style == Style.Blocking ? "reqwest::blocking" : "reqwest";

    private static string Wait(Style style) => style == Style.Blocking ? "" : ".await";

    public static string Generate(Plan plan, Style style)
    {
        var notes = plan.Notes();
        var statements = new List<string> { Client(plan, style, notes) };

        var call = new List<string> { $".{plan.Method.ToLowerInvariant()}({Literal(plan.Effective.Url)})" };

        foreach (var (name, value) in plan.Headers())
        {
            call.Add($".header({Literal(name)}, {Literal(value)})");
        }
        if (plan.Basic is (var username, var password))
        {
            call.Add($".basic_auth({Literal(username)}, Some({Literal(password)}))");
        }
        if (plan.Effective.Auth is AuthPlan.Challenge { Auth: var challenge })
        {
            notes.Add(
                challenge is ChallengeAuth.Digest
                    ? "reqwest cannot answer a digest challenge. The `diqwest` crate adds it, as "
                      + "`send_with_digest_auth`."
                    : "reqwest cannot answer an NTLM challenge; that scheme needs a client that can "
                      + "hold a connection open across it.");
        }

        call.AddRange(Body(plan, statements, style));
        call.Add($".send(){Wait(style)}?");

        statements.Add($"let response = client\n{Text.Indent(string.Join("\n", call), 4)};");
        statements.Add(
            $"println!(\"{{}}\", response.status());\nprintln!(\"{{}}\", response.text(){Wait(style)}?);");

        var output = new StringBuilder();
        foreach (var note in notes)
        {
            output.Append(Text.Comment("// ", note)).Append('\n');
        }
        output.Append(Preamble(style));
        output.Append(Text.Indent(string.Join("\n\n", statements), 4));
        output.Append("\n\n    Ok(())\n}\n");
        return Text.Settle(output.ToString());
    }

    private static string Preamble(Style style) => style switch
    {
        Style.Blocking =>
            "// Cargo.toml: reqwest = { version = \"0.12\", features = "
            + "[\"blocking\", \"json\", \"multipart\"] }\n\nfn main() -> "
            + "Result<(), Box<dyn std::error::Error>> {\n",
        _ =>
            "// Cargo.toml: reqwest = { version = \"0.12\", features = [\"json\", "
            + "\"multipart\"] }, tokio = { version = \"1\", features = "
            + "[\"full\"] }\n\n#[tokio::main]\nasync fn main() -> Result<(), Box<dyn "
            + "std::error::Error>> {\n",
    };

    /// <summary>
    /// The client, which is where everything that is a setting rather than a
    /// part of the request ends up.
    /// </summary>
    private static string Client(Plan plan, Style style, List<string> notes)
    {
        var builder = new List<string>();

        var timeout = plan.TimeoutMs;
        if (timeout > 0)
        {
            builder.Add($".timeout(std::time::Duration::from_millis({timeout}))");
        }
        if (!plan.Options.FollowRedirects)
        {
            builder.Add(".redirect(reqwest::redirect::Policy::none())");
        }
        if (plan.AcceptsInvalidCerts)
        {
            builder.Add(".danger_accept_invalid_certs(true)");
        }

        var extra = plan.ExtraCaFiles();
        foreach (var path in extra)
        {
            builder.Add($".add_root_certificate(reqwest::Certificate::from_pem(&std::fs::read({Literal(path)})?)?)");
        }
        if (!plan.MergesCaFiles && extra.Count > 0)
        {
            builder.Add(".tls_built_in_root_certs(false)");
        }

        if (plan.Identity is (var identity, var kind))
        {
            var path = identity.Path.Trim();
            if (kind == IdentityKind.Pem)
            {
                builder.Add($".identity(reqwest::Identity::from_pem(&std::fs::read({Literal(path)})?)?)");
            }
            else
            {
                // reqwest reads PKCS#12 only through its native-tls backend.
                notes.Add(
                    "`Identity::from_pkcs12_der` needs reqwest's `native-tls` feature; the "
                    + "default rustls backend reads PEM only.");
                builder.Add(
                    $".identity(reqwest::Identity::from_pkcs12_der(&std::fs::read({Literal(path)})?, "
                    + $"{Literal(identity.Password ?? "")})?)");
            }
        }

        if (builder.Count == 0)
        {
            return $"let client = {Module(style)}::Client::new();";
        }
        return $"let client = {Module(style)}::Client::builder()\n{Text.Indent(string.Join("\n", builder), 4)}\n    .build()?;";
    }

    /// <summary>The body, as the calls that add it to the request being built.</summary>
    private static List<string> Body(Plan plan, List<string> statements, Style style)
    {
        switch (plan.Request.Body)
        {
            case JsonBody json:
                return [$".body({Literal(json.Text)})"];
            case TextBody text:
                return [$".body({Literal(text.Text)})"];

            // Written out already encoded rather than through `.form()`, which
            // would encode a second time to its own taste.
            case UrlEncodedBody form:
                return [$".body({Literal(Text.FormEncoded(form.Entries))})"];

            case MultipartBody multipart:
                var lines = new List<string> { $"let form = {Module(style)}::multipart::Form::new()" };
                foreach (var entry in multipart.Entries.Where(entry => entry.IsActive))
                {
                    var name = Literal(entry.Name.Trim());
                    switch (entry.Value)
                    {
                        case MultipartText text:
                            lines.Add($"    .text({name}, {Literal(text.Value)})");
                            break;
                        case MultipartFile file:
                            var part =
                                $"{Module(style)}::multipart::Part::bytes(std::fs::read({Literal(file.Path)})?)\n        "
                                + $".file_name({Literal(Text.FileName(file.Path, file.FileName))})";
                            if (file.ContentType is { } mime)
                            {
                                part += $"\n        .mime_str({Literal(mime)})?";
                            }
                            lines.Add($"    .part(\n        {name},\n        {part},\n    )");
                            break;
                    }
                }
                lines.Add("    ;");
                statements.Add(string.Join("\n", lines).Replace("\n    ;", ";", StringComparison.Ordinal));
                return [".multipart(form)"];

            default:
                return [];
        }
    }

    /// <summary>
    /// A Rust string literal.
    ///
    /// A raw string, because a JSON body is full of quotes and backslashes and a
    /// path on Windows is nothing else. The hashes are counted so that a body
    /// carrying <c>"#</c> of its own still closes in the right place.
    ///
    /// Anything that runs over lines goes the same way even when it has neither,
    /// so that it carries the mark keeping its own indentation out of the
    /// indenter's hands. An ordinary Rust string would hold those lines perfectly
    /// well and then be laid out along with the code around it.
    /// </summary>
    internal static string Literal(string value)
    {
        if (!value.Contains('"') && !value.Contains('\\') && !value.Contains('\n'))
        {
            return $"\"{value}\"";
        }

        var hashes = 1;
        while (value.Contains("\"" + new string('#', hashes), StringComparison.Ordinal))
        {
            hashes++;
        }
        var fence = new string('#', hashes);
        return Text.Keep($"r{fence}\"{value}\"{fence}");
    }
}
