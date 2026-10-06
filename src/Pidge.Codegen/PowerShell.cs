using System.Text;
using Pidge.Core;
using Pidge.HttpEngine;

namespace Pidge.Codegen;

/// <summary>
/// PowerShell, through <c>Invoke-RestMethod</c>.
///
/// The parameters are splatted rather than written on one long line with
/// backtick continuations. A stray space after a backtick breaks the command in
/// a way that is genuinely hard to see, and a hashtable has neither the problem
/// nor the line length.
/// </summary>
internal static class PowerShell
{
    public static string Generate(Plan plan)
    {
        var notes = plan.Notes();
        var blocks = new List<string>();

        var (headers, collided) = Text.Fold(plan.Headers());
        if (collided)
        {
            notes.Add(Text.FoldedNote);
        }
        var contentType = Text.TakeContentType(headers);

        var basic = plan.Basic;
        if (headers.Count > 0 || basic is not null)
        {
            blocks.Add(Hashtable("$headers", headers));
        }

        if (basic is (var username, var password))
        {
            blocks.Add(
                $"$pair = [Text.Encoding]::UTF8.GetBytes({Quote($"{username}:{password}")})\n"
                + "$headers['Authorization'] = 'Basic ' + [Convert]::ToBase64String($pair)");
        }

        string? credential = null;
        switch (plan.Effective.Auth)
        {
            case AuthPlan.Challenge { Auth: ChallengeAuth.Digest digest }:
                credential = Credential(digest.Username, digest.Password);
                break;
            case AuthPlan.Challenge { Auth: ChallengeAuth.Ntlm ntlm }:
                if (ntlm.Workstation.Trim().Length > 0)
                {
                    notes.Add("PowerShell has nowhere to put an NTLM workstation name, so it is not sent.");
                }
                var account = ntlm.Domain.Trim().Length == 0 ? ntlm.Username : $"{ntlm.Domain}\\{ntlm.Username}";
                credential = Credential(account, ntlm.Password);
                break;
        }
        if (credential is not null)
        {
            blocks.Add(credential);
        }

        var certificate = Certificate(plan, notes);
        if (certificate is not null)
        {
            blocks.Add(certificate);
        }

        var body = Body(plan, notes);
        if (body is not null)
        {
            blocks.Add(body.Statement);
        }

        var parameters = new List<(string, string)>
        {
            ("Uri", Quote(plan.Effective.Url)),
            ("Method", Quote(TitleCase(plan.Method))),
        };
        if (headers.Count > 0 || basic is not null)
        {
            parameters.Add(("Headers", "$headers"));
        }
        if (contentType is not null)
        {
            parameters.Add(("ContentType", Quote(contentType)));
        }
        if (body is not null)
        {
            parameters.Add((body.Parameter, body.Variable));
        }
        if (credential is not null)
        {
            parameters.Add(("Credential", "$credential"));
        }
        if (!plan.Options.FollowRedirects)
        {
            parameters.Add(("MaximumRedirection", "0"));
        }
        if (plan.AcceptsInvalidCerts)
        {
            parameters.Add(("SkipCertificateCheck", "$true"));
        }
        if (certificate is not null)
        {
            parameters.Add(("Certificate", "$certificate"));
        }
        var timeout = plan.TimeoutMs;
        if (timeout > 0)
        {
            parameters.Add(("TimeoutSec", Text.Seconds(timeout)));
        }

        // Parameter names are bare words; only the header keys need quoting.
        blocks.Add(WriteHashtable("$parameters", parameters));
        blocks.Add("$response = Invoke-RestMethod @parameters\n$response");

        var output = new StringBuilder();
        foreach (var note in notes)
        {
            output.Append(Text.Comment("# ", note)).Append('\n');
        }
        output.Append(string.Join("\n\n", blocks));
        output.Append('\n');
        return output.ToString();
    }

    /// <summary>
    /// A body, and the parameter it is passed as: <c>-Body</c> for everything
    /// except a multipart form, which has its own.
    /// </summary>
    private sealed record BodyBlock(string Statement, string Variable, string Parameter);

    private static BodyBlock? Body(Plan plan, List<string> notes)
    {
        switch (plan.Request.Body)
        {
            case JsonBody json:
                return new BodyBlock($"$body = {Quote(json.Text)}", "$body", "Body");
            case TextBody text:
                return new BodyBlock($"$body = {Quote(text.Text)}", "$body", "Body");

            case UrlEncodedBody form:
                return new BodyBlock($"$body = {Quote(Text.FormEncoded(form.Entries))}", "$body", "Body");

            case MultipartBody multipart:
                var rows = new List<(string, string)>();
                foreach (var entry in multipart.Entries.Where(entry => entry.IsActive))
                {
                    string value;
                    switch (entry.Value)
                    {
                        case MultipartFile file:
                            // `-Form` takes the file's own name and lets the server
                            // sniff the type; neither can be overridden here.
                            if (file.FileName is not null || file.ContentType is not null)
                            {
                                notes.Add(
                                    $"The part `{entry.Name.Trim()}` is sent under the file's own name, and without the "
                                    + "content type set for it: PowerShell's -Form takes neither.");
                            }
                            value = $"Get-Item {Quote(file.Path)}";
                            break;
                        case MultipartText text:
                            value = Quote(text.Value);
                            break;
                        default:
                            value = Quote("");
                            break;
                    }
                    rows.Add((entry.Name.Trim(), value));
                }

                return new BodyBlock(ExpressionHashtable("$form", rows), "$form", "Form");

            default:
                return null;
        }
    }

    /// <summary>
    /// The client certificate, loaded the way .NET loads one.
    ///
    /// The extra CAs have no equivalent at all here: .NET reads trust from the
    /// machine and user stores, and <c>Invoke-RestMethod</c> has no parameter that
    /// adds to it for one call. That is said rather than quietly dropped.
    /// </summary>
    private static string? Certificate(Plan plan, List<string> notes)
    {
        foreach (var path in plan.ExtraCaFiles())
        {
            notes.Add(
                "PowerShell trusts what the machine trusts, with no per-request CA list. Install "
                + $"`{path}` in the certificate store for this to verify.");
        }

        if (plan.Identity is not (var identity, var kind))
        {
            return null;
        }
        var trimmed = identity.Path.Trim();
        const string type = "[System.Security.Cryptography.X509Certificates.X509Certificate2]";

        if (kind == IdentityKind.Pkcs12)
        {
            return $"$certificate = {type}::new({Quote(trimmed)}, {Quote(identity.Password ?? "")})";
        }

        notes.Add(
            "On Windows, a certificate read from PEM has to be exported to PKCS#12 and loaded "
            + "back before SChannel will present it. On Linux and macOS this works as written.");
        return $"$certificate = {type}::CreateFromPemFile({Quote(trimmed)})";
    }

    private static string Credential(string account, string password) =>
        $"$password = ConvertTo-SecureString {Quote(password)} -AsPlainText -Force\n"
        + $"$credential = New-Object System.Management.Automation.PSCredential({Quote(account)}, $password)";

    /// <summary>
    /// <c>$name = @{ 'key' = 'value' }</c>. Every key is quoted because a name
    /// with a dash in it is not a bare word, and every value because it is text.
    /// </summary>
    private static string Hashtable(string name, List<(string Key, string Value)> rows) =>
        WriteHashtable(name, rows.Select(row => (Quote(row.Key), Quote(row.Value))).ToList());

    /// <summary>
    /// The same, where the values are expressions rather than text: a form part
    /// is a <c>Get-Item</c>, not a string.
    /// </summary>
    private static string ExpressionHashtable(string name, List<(string Key, string Value)> rows) =>
        WriteHashtable(name, rows.Select(row => (Quote(row.Key), row.Value)).ToList());

    private static string WriteHashtable(string name, List<(string Key, string Value)> rows)
    {
        if (rows.Count == 0)
        {
            return $"{name} = @{{}}";
        }
        var width = rows.Max(row => Text.Utf8Length(row.Key));
        var body = string.Join("\n", rows.Select(row => $"{Text.PadRight(row.Key, width)} = {row.Value}"));
        return $"{name} = @{{\n{Text.Indent(body, 4)}\n}}";
    }

    /// <summary>
    /// <c>Invoke-RestMethod</c> takes any casing, but its own documentation
    /// writes them this way and so does every example anyone has read.
    /// </summary>
    internal static string TitleCase(string method) =>
        method.Length == 0 ? "" : method[..1] + method[1..].ToLowerInvariant();

    /// <summary>
    /// Single quotes: PowerShell expands <c>$name</c> and a backtick inside
    /// double quotes, and does neither inside single ones. A quote is doubled
    /// to escape itself, and the string may run over as many lines as it likes.
    /// </summary>
    internal static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
