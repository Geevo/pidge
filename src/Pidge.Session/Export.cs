using System.Text;
using System.Text.Json;
using Pidge.Codegen;
using Pidge.Core;
using Pidge.Storage;

namespace Pidge.Session;

/*
 * Saved requests written out to a file, for sharing or moving elsewhere.
 *
 * Unless asked otherwise, every secret is replaced by a {{variable}} named for
 * it, so the file can be pasted into a chat or committed. The app and both
 * .http clients substitute those the same way, so the person it goes to fills
 * in their own and nothing else changes. What counts as a secret is the same
 * list the state file encrypts.
 */
internal static class Export
{
    /// <summary>What an export says it is, so an import can tell.</summary>
    public const string ExportKind = "pidge/saved-requests";

    /// <summary>What exports said they were before the app was called pidge.</summary>
    public const string LegacyExportKind = "api-client/saved-requests";

    private static readonly string[] AuthSchemes = ["bearer", "basic", "token", "digest", "bot"];

    public static string Write(IEnumerable<SavedRequest> saved, ExportFormat format, bool includeSecrets)
    {
        var copies = saved.Select(entry => PidgeJson.Clone(entry, StorageJsonContext.Wire.SavedRequest)).ToList();
        if (!includeSecrets)
        {
            foreach (var entry in copies)
            {
                ReplaceSecrets(entry.Request);
            }
        }

        return format switch
        {
            ExportFormat.Json => JsonSerializer.Serialize(
                new ExportFile { Kind = ExportKind, Version = 1, SavedRequests = copies },
                ExportFileJsonContext.Pretty.ExportFile) + "\n",
            ExportFormat.Http => HttpFile.Write(copies.Select(entry => (entry.Name, entry.Request))),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };
    }

    private static void ReplaceSecrets(HttpRequest request)
    {
        foreach (var secret in Redact.AuthSecrets(request.Auth))
        {
            // An API key's field is called `value`, which says nothing.
            var name = secret.Field == "value" ? "apiKey" : secret.Field;
            if (Hide(secret.Get(), name) is { } hidden)
            {
                secret.Set(hidden);
            }
        }
        foreach (var header in request.Headers)
        {
            if (Redact.IsSecretHeader(header.Name) && Hide(header.Value, CamelCase(header.Name)) is { } hidden)
            {
                header.Value = hidden;
            }
        }
    }

    /// <summary>True when the request carries a secret as it is, not as a <c>{{variable}}</c>.</summary>
    public static bool HoldsPlainSecret(HttpRequest request)
    {
        var auth = Redact.AuthSecrets(request.Auth)
            .Select(secret => secret.Get())
            .Any(value => value.Length > 0 && !OnlyReferences(value));
        return auth || request.Headers.Any(header =>
            Redact.IsSecretHeader(header.Name)
            && header.Value.Length > 0
            && !OnlyReferences(header.Value));
    }

    /// <summary>
    /// The replacement for a secret, or null to leave alone a value that is
    /// already a variable, such as <c>{{token}}</c> or <c>Bearer {{token}}</c>:
    /// the secret is somewhere else, and the reference is what the person
    /// receiving it needs.
    /// </summary>
    private static string? Hide(string value, string name)
    {
        if (value.Length == 0 || OnlyReferences(value))
        {
            return null;
        }
        return "{{" + name + "}}";
    }

    internal static bool OnlyReferences(string value)
    {
        var rest = value.AsSpan();
        var outside = new StringBuilder();
        var found = false;
        while (rest.IndexOf("{{", StringComparison.Ordinal) is var start and >= 0)
        {
            var length = rest[start..].IndexOf("}}", StringComparison.Ordinal);
            if (length < 0)
            {
                break;
            }
            outside.Append(rest[..start]);
            rest = rest[(start + length + 2)..];
            found = true;
        }
        outside.Append(rest);

        // An auth scheme in front of the variable gives nothing away.
        var trimmed = outside.ToString().Trim();
        return found
            && (trimmed.Length == 0
                || AuthSchemes.Any(scheme => string.Equals(trimmed, scheme, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary><c>X-API-Key</c> becomes <c>xApiKey</c>, a name a variable can have.</summary>
    internal static string CamelCase(string name)
    {
        var output = new StringBuilder();
        var words = name.Split(c => !char.IsAsciiLetterOrDigit(c));
        foreach (var word in words.Where(word => word.Length > 0))
        {
            var lower = word.ToLowerInvariant();
            if (output.Length == 0)
            {
                output.Append(lower);
            }
            else
            {
                output.Append(char.ToUpperInvariant(lower[0])).Append(lower.AsSpan(1));
            }
        }
        return output.ToString();
    }

    private static string[] Split(this string text, Func<char, bool> separator)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (separator(text[i]))
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        return [.. parts];
    }
}

/// <summary>The JSON form, labelled so a file can say what it is.</summary>
internal sealed class ExportFile
{
    public string Kind { get; set; } = "";
    public uint Version { get; set; }
    public List<SavedRequest> SavedRequests { get; set; } = [];
}
