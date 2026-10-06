using Pidge.Core;

namespace Pidge.Variables;

/*
 * `{{variable}}` substitution.
 *
 * Substitution happens once, immediately before a request is built, so both
 * frontends behave identically. An unresolved variable is an error: sending a
 * literal `{{token}}` to a server is never what the user meant.
 */

/// <summary>
/// A named set of variables. Environments are flat and have no relationship to
/// workspaces, projects, or anything else. (<c>Environment</c> on the wire;
/// renamed here so it never collides with <see cref="System.Environment"/>.)
/// </summary>
public sealed class VariableEnvironment
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<KeyValueEntry> Variables { get; set; } = [];

    public VariableEnvironment Clone() =>
        new() { Id = Id, Name = Name, Variables = Variables.Select(v => v.Clone()).ToList() };
}

/// <summary>Resolved name/value pairs, ready to substitute.</summary>
public sealed class VariableSet
{
    // Ordinal, so the order is the same on every machine and in every culture.
    private readonly SortedDictionary<string, string> _values = new(StringComparer.Ordinal);

    public VariableSet() { }

    public VariableSet(IEnumerable<(string Name, string Value)> pairs)
    {
        foreach (var (name, value) in pairs)
        {
            Insert(name, value);
        }
    }

    public static VariableSet From(VariableEnvironment environment)
    {
        var set = new VariableSet();
        foreach (var entry in environment.Variables.Where(e => e.IsActive))
        {
            set.Insert(entry.Name.Trim(), entry.Value);
        }
        return set;
    }

    public void Insert(string name, string value) => _values[name] = value;

    public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;

    public bool IsEmpty => _values.Count == 0;

    public IEnumerable<string> Names => _values.Keys;
}

public static class Substitution
{
    /// <summary>
    /// Replaces every <c>{{name}}</c> in <paramref name="input"/>. Unknown names
    /// are collected rather than failing on the first one, so the user fixes
    /// them all at once. Returns null and fills <paramref name="missing"/> when
    /// any are unknown.
    /// </summary>
    public static string? Substitute(string input, VariableSet variables, out List<string> missing)
    {
        var output = new System.Text.StringBuilder(input.Length);
        missing = [];
        var rest = input.AsSpan();

        while (true)
        {
            var start = rest.IndexOf("{{", StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }
            output.Append(rest[..start]);
            var afterOpen = rest[(start + 2)..];
            var end = afterOpen.IndexOf("}}", StringComparison.Ordinal);
            if (end < 0)
            {
                // No closing braces: treat the rest as literal text.
                output.Append(rest[start..]);
                rest = [];
                break;
            }

            var name = afterOpen[..end].Trim().ToString();
            var value = variables.Get(name);
            if (value is not null)
            {
                output.Append(value);
            }
            else if (!missing.Contains(name))
            {
                missing.Add(name);
            }
            rest = afterOpen[(end + 2)..];
        }

        output.Append(rest);
        return missing.Count == 0 ? output.ToString() : null;
    }

    /// <summary>Substitutes across the whole request: URL, params, headers, body, and auth.</summary>
    /// <exception cref="RequestErrorException">A variable has no value.</exception>
    public static HttpRequest ResolveRequest(HttpRequest request, VariableSet variables)
    {
        if (TryResolveRequest(request, variables, out var resolved, out var error))
        {
            return resolved;
        }
        throw error.AsException();
    }

    public static bool TryResolveRequest(
        HttpRequest request,
        VariableSet variables,
        out HttpRequest resolved,
        out RequestError error)
    {
        (resolved, var missing) = ResolveCollecting(request, variables);
        if (missing.Count == 0)
        {
            error = null!;
            return true;
        }

        var list = string.Join(", ", missing.Select(name => "{{" + name + "}}"));
        var known = variables.IsEmpty ? "(none)" : string.Join(", ", variables.Names);
        error = new RequestError(RequestErrorKind.UnresolvedVariable, $"No value for {list}.")
            .WithDetail(
                "Define these in the active environment, or remove them from the request. Known variables: "
                + known);
        return false;
    }

    /// <summary>
    /// Every <c>{{name}}</c> the request uses that <paramref name="variables"/>
    /// has no value for, in the order they first appear. The same walk a send
    /// makes, so it cannot disagree with the error a send would give.
    /// </summary>
    public static List<string> MissingVariables(HttpRequest request, VariableSet variables) =>
        ResolveCollecting(request, variables).Missing;

    private static (HttpRequest Resolved, List<string> Missing) ResolveCollecting(
        HttpRequest request,
        VariableSet variables)
    {
        var missing = new List<string>();
        string Resolve(string value)
        {
            var resolved = Substitute(value, variables, out var names);
            if (resolved is not null)
            {
                return resolved;
            }
            foreach (var name in names)
            {
                if (!missing.Contains(name))
                {
                    missing.Add(name);
                }
            }
            return "";
        }

        var result = request.Clone();
        result.Url = Resolve(request.Url);
        result.QueryParams = ResolveEntries(request.QueryParams, Resolve);
        result.Headers = ResolveEntries(request.Headers, Resolve);

        result.Auth = request.Auth switch
        {
            NoAuth => new NoAuth(),
            BearerAuth a => new BearerAuth { Token = Resolve(a.Token) },
            BasicAuth a => new BasicAuth { Username = Resolve(a.Username), Password = Resolve(a.Password) },
            DigestAuth a => new DigestAuth { Username = Resolve(a.Username), Password = Resolve(a.Password) },
            NtlmAuth a => new NtlmAuth
            {
                Username = Resolve(a.Username),
                Password = Resolve(a.Password),
                Domain = Resolve(a.Domain),
                Workstation = Resolve(a.Workstation),
            },
            OAuth1Settings s => new OAuth1Settings
            {
                ConsumerKey = Resolve(s.ConsumerKey),
                ConsumerSecret = Resolve(s.ConsumerSecret),
                Token = Resolve(s.Token),
                TokenSecret = Resolve(s.TokenSecret),
                SignatureMethod = s.SignatureMethod,
                Realm = Resolve(s.Realm),
            },
            OAuth2Settings s => new OAuth2Settings
            {
                Grant = s.Grant,
                TokenUrl = Resolve(s.TokenUrl),
                ClientId = Resolve(s.ClientId),
                ClientSecret = Resolve(s.ClientSecret),
                Scope = Resolve(s.Scope),
                Username = Resolve(s.Username),
                Password = Resolve(s.Password),
                RefreshToken = Resolve(s.RefreshToken),
                ClientAuth = s.ClientAuth,
            },
            ApiKeyAuth a => new ApiKeyAuth { Key = Resolve(a.Key), Value = Resolve(a.Value), Placement = a.Placement },
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Auth.GetType().Name),
        };

        result.Body = request.Body switch
        {
            NoBody => new NoBody(),
            JsonBody b => new JsonBody { Text = Resolve(b.Text) },
            TextBody b => new TextBody { Text = Resolve(b.Text), ContentType = b.ContentType },
            UrlEncodedBody b => new UrlEncodedBody { Entries = ResolveEntries(b.Entries, Resolve) },
            MultipartBody b => new MultipartBody
            {
                Entries = b.Entries.Select(entry =>
                {
                    if (!entry.Enabled)
                    {
                        return entry.Clone();
                    }
                    return new MultipartEntry
                    {
                        Id = entry.Id,
                        Enabled = entry.Enabled,
                        Name = Resolve(entry.Name),
                        Value = entry.Value switch
                        {
                            MultipartText t => new MultipartText { Value = Resolve(t.Value) },
                            MultipartFile f => new MultipartFile
                            {
                                Path = Resolve(f.Path),
                                FileName = f.FileName,
                                ContentType = f.ContentType,
                            },
                            _ => entry.Value.Clone(),
                        },
                    };
                }).ToList(),
            },
            _ => request.Body.Clone(),
        };

        return (result, missing);
    }

    private static List<KeyValueEntry> ResolveEntries(List<KeyValueEntry> entries, Func<string, string> resolve) =>
        entries.Select(entry =>
            // Disabled rows are never sent, so never fail on their variables.
            !entry.Enabled
                ? entry.Clone()
                : new KeyValueEntry
                {
                    Id = entry.Id,
                    Enabled = entry.Enabled,
                    Name = resolve(entry.Name),
                    Value = resolve(entry.Value),
                }).ToList();
}
