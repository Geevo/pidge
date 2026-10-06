using System.Text;
using Pidge.Core;
using Pidge.HttpEngine;

namespace Pidge.Codegen;

/// <summary>
/// C#, through <c>HttpClient</c>.
///
/// Top-level statements, so the snippet is the program: everything here runs as
/// written in a <c>Program.cs</c> or a <c>dotnet script</c> file.
///
/// The awkward part of this API is that a content header is not a request
/// header. <c>Content-Type</c> lives on the content object and throws if it is
/// put anywhere else, so the header list is split before it is written out.
/// </summary>
internal static class CSharp
{
    public static string Generate(Plan plan)
    {
        var notes = plan.Notes();
        var usings = new SortedSet<string>(StringComparer.Ordinal) { "System", "System.Net.Http" };

        var handler = new List<string>();
        var client = new List<string>();
        var statements = new List<string>();

        if (!plan.Options.FollowRedirects)
        {
            handler.Add("AllowAutoRedirect = false,");
        }

        switch (plan.Effective.Auth)
        {
            case AuthPlan.Challenge { Auth: ChallengeAuth.Digest digest }:
                usings.Add("System.Net");
                handler.Add($"Credentials = new NetworkCredential({Literal(digest.Username)}, {Literal(digest.Password)}),");
                break;
            case AuthPlan.Challenge { Auth: ChallengeAuth.Ntlm ntlm }:
                usings.Add("System.Net");
                if (ntlm.Workstation.Trim().Length > 0)
                {
                    notes.Add("NetworkCredential has nowhere to put a workstation name, so it is not sent.");
                }
                handler.Add(
                    $"Credentials = new NetworkCredential({Literal(ntlm.Username)}, {Literal(ntlm.Password)}, {Literal(ntlm.Domain)}),");
                break;
        }

        var certificate = Certificate(plan, usings, notes);
        var insecure = plan.AcceptsInvalidCerts;
        if (handler.Count == 0 && !insecure && certificate is null)
        {
            client.Add("using var client = new HttpClient();");
        }
        else
        {
            var fields = handler.Count == 0
                ? "var handler = new HttpClientHandler();"
                : $"var handler = new HttpClientHandler\n{{\n{Text.Indent(string.Join("\n", handler), 4)}\n}};";
            client.Add(fields);
            if (insecure)
            {
                client.Add(
                    "handler.ServerCertificateCustomValidationCallback =\n    "
                    + "HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;");
            }
            if (certificate is not null)
            {
                client.Add(certificate);
            }
            client.Add("using var client = new HttpClient(handler);");
        }

        var timeout = plan.TimeoutMs;
        client.Add(
            timeout == 0
                ? "client.Timeout = Timeout.InfiniteTimeSpan;"
                : $"client.Timeout = TimeSpan.FromSeconds({Text.Seconds(timeout)});");
        if (timeout == 0)
        {
            usings.Add("System.Threading");
        }
        statements.Add(string.Join("\n", client));

        statements.Add(
            $"using var request = new HttpRequestMessage(HttpMethod.{TitleCase(plan.Method)}, {Literal(plan.Effective.Url)});");

        var headers = plan.Headers();
        var contentType = Text.TakeContentType(headers);

        var headerLines = new List<string>();
        if (plan.Basic is (var username, var password))
        {
            usings.Add("System.Net.Http.Headers");
            usings.Add("System.Text");
            headerLines.Add(
                "request.Headers.Authorization = new AuthenticationHeaderValue(\n    "
                + $"\"Basic\", Convert.ToBase64String(Encoding.UTF8.GetBytes({Literal($"{username}:{password}")})));");
        }
        foreach (var (name, value) in headers)
        {
            /*
             * `TryAddWithoutValidation` rather than `Add`: `Add` parses the value
             * against what it believes the header means and throws on anything it
             * does not like, including a User-Agent it considers malformed. What
             * the user typed is what should be sent.
             */
            headerLines.Add($"request.Headers.TryAddWithoutValidation({Literal(name)}, {Literal(value)});");
        }
        if (headerLines.Count > 0)
        {
            statements.Add(string.Join("\n", headerLines));
        }

        if (Content(plan, usings, contentType) is { } block)
        {
            statements.Add(block);
        }
        else if (contentType is not null)
        {
            // A content type with no body to put it on. .NET has nowhere to hang it.
            notes.Add(
                $"The Content-Type header ({contentType}) is not sent: this request has no body, and .NET "
                + "keeps that header on the body.");
        }

        statements.Add(
            "using var response = await client.SendAsync(request);\n"
            + "Console.WriteLine((int)response.StatusCode);\n"
            + "Console.WriteLine(await response.Content.ReadAsStringAsync());");

        var output = new StringBuilder();
        foreach (var note in notes)
        {
            output.Append(Text.Comment("// ", note)).Append('\n');
        }
        foreach (var name in usings)
        {
            output.Append($"using {name};\n");
        }
        output.Append('\n');
        output.Append(string.Join("\n\n", statements));
        output.Append('\n');
        return output.ToString();
    }

    /// <summary>
    /// The client certificate, added to the handler that will present it.
    ///
    /// The extra CAs have no equivalent: .NET reads trust from the machine and
    /// user stores, and the only per-request hook is the validation callback,
    /// which replaces the whole check rather than adding one root to it.
    /// </summary>
    private static string? Certificate(Plan plan, SortedSet<string> usings, List<string> notes)
    {
        foreach (var path in plan.ExtraCaFiles())
        {
            notes.Add(
                $".NET trusts what the machine trusts, with no per-request CA list. Install `{path}` "
                + "in the certificate store for this to verify.");
        }

        if (plan.Identity is not (var identity, var kind))
        {
            return null;
        }
        var trimmed = identity.Path.Trim();
        usings.Add("System.Security.Cryptography.X509Certificates");

        string load;
        if (kind == IdentityKind.Pkcs12)
        {
            // `X509CertificateLoader` arrived in .NET 9, and the constructor it
            // replaced is obsolete from the same version, so there is no one
            // spelling that is current everywhere.
            notes.Add(
                "X509CertificateLoader needs .NET 9 or later. On .NET 8 and earlier, use "
                + "new X509Certificate2(path, password) instead.");
            load = $"X509CertificateLoader.LoadPkcs12FromFile({Literal(trimmed)}, {Literal(identity.Password ?? "")})";
        }
        else
        {
            notes.Add(
                "On Windows, a certificate read from PEM has to be exported to PKCS#12 and loaded "
                + "back before SChannel will present it. On Linux and macOS this works as written.");
            load = $"X509Certificate2.CreateFromPemFile({Literal(trimmed)})";
        }

        return "handler.ClientCertificateOptions = ClientCertificateOption.Manual;\n"
            + $"handler.ClientCertificates.Add({load});";
    }

    /// <summary>The <c>request.Content</c> assignment, and the content type set on it.</summary>
    private static string? Content(Plan plan, SortedSet<string> usings, string? contentType)
    {
        var lines = new List<string>();

        switch (plan.Request.Body)
        {
            case JsonBody json:
                usings.Add("System.Text");
                lines.Add($"request.Content = new StringContent({Literal(json.Text)}, Encoding.UTF8);");
                break;
            case TextBody text:
                usings.Add("System.Text");
                lines.Add($"request.Content = new StringContent({Literal(text.Text)}, Encoding.UTF8);");
                break;

            case UrlEncodedBody form:
                // The pairs already encoded, as one string, rather than through
                // `FormUrlEncodedContent`: that class escapes to its own taste, and
                // this way the bytes are the ones the app would send.
                usings.Add("System.Text");
                lines.Add($"request.Content = new StringContent({Literal(Text.FormEncoded(form.Entries))}, Encoding.UTF8);");
                break;

            case MultipartBody multipart:
                lines.Add("var content = new MultipartFormDataContent();");
                var parts = 0;
                foreach (var entry in multipart.Entries.Where(entry => entry.IsActive))
                {
                    var name = Literal(entry.Name.Trim());
                    switch (entry.Value)
                    {
                        case MultipartText text:
                            lines.Add($"content.Add(new StringContent({Literal(text.Value)}), {name});");
                            break;
                        case MultipartFile file:
                            usings.Add("System.IO");
                            parts++;
                            var variable = $"part{parts}";
                            lines.Add($"var {variable} = new StreamContent(File.OpenRead({Literal(file.Path)}));");
                            if (file.ContentType is { } mime)
                            {
                                usings.Add("System.Net.Http.Headers");
                                lines.Add($"{variable}.Headers.ContentType = MediaTypeHeaderValue.Parse({Literal(mime)});");
                            }
                            lines.Add($"content.Add({variable}, {name}, {Literal(Text.FileName(file.Path, file.FileName))});");
                            break;
                    }
                }
                lines.Add("request.Content = content;");
                // Multipart writes its own content type, boundary and all.
                return string.Join("\n", lines);

            default:
                return null;
        }

        if (contentType is not null)
        {
            usings.Add("System.Net.Http.Headers");
            lines.Add($"request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse({Literal(contentType)});");
        }

        return string.Join("\n", lines);
    }

    /// <summary><c>HttpMethod.Get</c>, which is how the class spells its own members.</summary>
    private static string TitleCase(string method) =>
        method.Length == 0 ? "" : method[..1] + method[1..].ToLowerInvariant();

    /// <summary>
    /// A string literal, verbatim only when it has to be.
    ///
    /// <c>@"..."</c> keeps newlines and backslashes as they are — a Windows path
    /// in an ordinary literal is a run of escapes — and doubles an embedded
    /// quote. A header name gains nothing from the <c>@</c>, so it does not get one.
    /// </summary>
    internal static string Literal(string value)
    {
        if (value.AsSpan().IndexOfAny("\\\"\n\r\t") >= 0)
        {
            return $"@\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }
        return $"\"{value}\"";
    }
}
