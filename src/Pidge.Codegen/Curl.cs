using System.Text;
using Pidge.Core;
using Pidge.HttpEngine;

namespace Pidge.Codegen;

/// <summary>
/// curl, in long options.
///
/// <c>--request</c> and <c>--header</c> rather than <c>-X</c> and <c>-H</c>: a
/// snippet is read more often than it is typed, and the long names say what
/// they do.
/// </summary>
internal static class Curl
{
    public static string Generate(Plan plan)
    {
        var notes = plan.Notes();
        var arguments = new List<string>();

        /*
         * `-X HEAD` leaves curl waiting for a body that a HEAD response never has,
         * which looks exactly like a hung server. `--head` is the same request,
         * correctly.
         */
        if (plan.Request.Method == RequestMethod.Head)
        {
            arguments.Add("--head");
        }
        else
        {
            arguments.Add($"--request {plan.Method}");
        }

        arguments.Add($"--url {Quote(plan.Effective.Url)}");

        foreach (var (name, value) in plan.Headers())
        {
            arguments.Add($"--header {Quote($"{name}: {value}")}");
        }

        arguments.AddRange(Auth(plan));
        arguments.AddRange(Body(plan));

        // The app follows redirects unless it is told not to; curl does not unless
        // it is told to.
        if (plan.Options.FollowRedirects)
        {
            arguments.Add("--location");
        }
        if (plan.AcceptsInvalidCerts)
        {
            arguments.Add("--insecure");
        }
        arguments.AddRange(Tls(plan, notes));
        var timeout = plan.TimeoutMs;
        if (timeout > 0)
        {
            arguments.Add($"--max-time {Text.Seconds(timeout)}");
        }

        var output = new StringBuilder();
        foreach (var note in notes)
        {
            output.Append(Text.Comment("# ", note)).Append('\n');
        }
        output.Append("curl ");
        output.Append(string.Join(" \\\n  ", arguments));
        output.Append('\n');
        return output.ToString();
    }

    private static List<string> Auth(Plan plan)
    {
        if (plan.Basic is (var username, var password))
        {
            return [$"--user {Quote($"{username}:{password}")}"];
        }

        switch (plan.Effective.Auth)
        {
            case AuthPlan.Challenge { Auth: ChallengeAuth.Digest digest }:
                return ["--digest", $"--user {Quote($"{digest.Username}:{digest.Password}")}"];

            // curl carries the domain in the user field, and has nowhere at all to
            // put a workstation name.
            case AuthPlan.Challenge { Auth: ChallengeAuth.Ntlm ntlm }:
                var account = ntlm.Domain.Trim().Length == 0 ? ntlm.Username : $"{ntlm.Domain}\\{ntlm.Username}";
                return ["--ntlm", $"--user {Quote($"{account}:{ntlm.Password}")}"];

            default:
                return [];
        }
    }

    /// <summary>
    /// The trust and identity settings, which belong to the client rather than
    /// to the request, and which a snippet therefore has to state for itself.
    /// </summary>
    private static List<string> Tls(Plan plan, List<string> notes)
    {
        var arguments = new List<string>();

        if (plan.Identity is (var identity, var kind))
        {
            if (kind == IdentityKind.Pkcs12)
            {
                arguments.Add("--cert-type P12");
            }
            arguments.Add($"--cert {Quote(identity.Path.Trim())}");
            // `--pass` rather than curl's `certificate:password` form, which cannot
            // be told from the colon in a Windows path.
            if (identity.Password is { } password)
            {
                arguments.Add($"--pass {Quote(password)}");
            }
        }

        var extra = plan.ExtraCaFiles();
        if (extra.Count > 0)
        {
            arguments.Add($"--cacert {Quote(extra[0])}");

            if (extra.Count > 1)
            {
                notes.Add(
                    $"curl reads one CA file. {extra.Count} were set, so only the first is here; concatenate them "
                    + "into one PEM to use them all.");
            }
            if (plan.MergesCaFiles)
            {
                notes.Add(
                    "--cacert replaces the system trust store rather than adding to it, which is what "
                    + "the app does. A server with a public certificate will not verify against this "
                    + "file alone.");
            }
        }

        return arguments;
    }

    private static List<string> Body(Plan plan)
    {
        switch (plan.Request.Body)
        {
            // `--data-raw` rather than `--data`: the latter reads a leading `@` as
            // a file name, and strips the newlines out of what it is given.
            case JsonBody json:
                return [$"--data-raw {Quote(json.Text)}"];
            case TextBody text:
                return [$"--data-raw {Quote(text.Text)}"];

            /*
             * Written out already encoded rather than as `--data-urlencode` pairs.
             * That option escapes the value and leaves the name alone, which is not
             * what a form body is, and the difference only shows up on the one name
             * that needed escaping.
             */
            case UrlEncodedBody form:
                return [$"--data-raw {Quote(Text.FormEncoded(form.Entries))}"];

            case MultipartBody multipart:
                return multipart.Entries
                    .Where(entry => entry.IsActive)
                    .Select(entry =>
                    {
                        var name = entry.Name.Trim();
                        string part;
                        switch (entry.Value)
                        {
                            case MultipartFile file:
                                var builder = new StringBuilder($"{name}=@{file.Path}");
                                if (file.ContentType is { } mime)
                                {
                                    builder.Append($";type={mime}");
                                }
                                builder.Append($";filename={Text.FileName(file.Path, file.FileName)}");
                                part = builder.ToString();
                                break;
                            case MultipartText text:
                                part = $"{name}={text.Value}";
                                break;
                            default:
                                part = $"{name}=";
                                break;
                        }
                        return $"--form {Quote(part)}";
                    })
                    .ToList();

            default:
                return [];
        }
    }

    /// <summary>
    /// Single quotes, which the shell leaves entirely alone. A quote inside the
    /// value ends the string, escapes itself, and opens a new one — the usual
    /// <c>'\''</c> — so anything at all can be written this way.
    /// </summary>
    internal static string Quote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}
