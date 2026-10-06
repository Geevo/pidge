using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Pidge.Core;

namespace Pidge.HttpEngine;

/// <summary>
/// OAuth 1.0a request signing, RFC 5849.
///
/// Unlike the others this is arithmetic rather than a conversation: every
/// request carries a signature over its own method, URL and parameters, so
/// there is nothing to fetch and nothing to cache.
///
/// The fiddly part is the signature base string. Percent-encoding is applied
/// twice — once to each parameter, once to the whole sorted list — and a
/// form-encoded body is signed along with the query. Getting any of that wrong
/// produces a signature the server rejects with no explanation of why, which is
/// exactly why the base string is tested against the example in the RFC.
/// </summary>
internal static class OAuth1
{
    /// <summary>Builds the <c>Authorization: OAuth ...</c> value for one request.</summary>
    /// <exception cref="RequestErrorException">The signing key could not be used.</exception>
    public static string Authorization(
        string method,
        WebUrl url,
        RequestBody body,
        OAuth1Settings settings,
        string nonce,
        ulong timestamp)
    {
        var oauth = new List<(string Name, string Value)>
        {
            ("oauth_consumer_key", settings.ConsumerKey),
            ("oauth_nonce", nonce),
            ("oauth_signature_method", settings.SignatureMethod.AsString()),
            ("oauth_timestamp", timestamp.ToString(CultureInfo.InvariantCulture)),
            ("oauth_version", "1.0"),
        };
        if (settings.Token.Trim().Length > 0)
        {
            oauth.Add(("oauth_token", settings.Token));
        }

        var signature = settings.SignatureMethod switch
        {
            // PLAINTEXT is the signing key itself; there is no base string.
            OAuth1Signature.Plaintext => SigningKey(settings),
            var kind => Sign(kind, SigningKey(settings), BaseString(method, url, body, oauth)),
        };

        var parts = new List<string>();
        if (settings.Realm.Trim().Length > 0)
        {
            parts.Add($"realm=\"{Encode(settings.Realm.Trim())}\"");
        }
        foreach (var (name, value) in oauth.Append(("oauth_signature", signature)))
        {
            parts.Add($"{Encode(name)}=\"{Encode(value)}\"");
        }

        return "OAuth " + string.Join(", ", parts);
    }

    /// <summary>
    /// <c>METHOD&amp;url&amp;params</c>, each part encoded, with the params
    /// sorted and encoded a second time as a whole.
    /// </summary>
    internal static string BaseString(
        string method,
        WebUrl url,
        RequestBody body,
        IEnumerable<(string Name, string Value)> oauth)
    {
        var parameters = url.QueryPairs();

        // A form-encoded body is part of what is being signed (§3.4.1.3.1).
        if (body is UrlEncodedBody form)
        {
            parameters.AddRange(form.Entries.Where(e => e.IsActive).Select(e => (e.Name.Trim(), e.Value)));
        }

        parameters.AddRange(oauth);

        // Sorted by encoded name, then encoded value.
        var encoded = parameters.Select(p => (Name: Encode(p.Name), Value: Encode(p.Value))).ToList();
        encoded.Sort((left, right) =>
        {
            var byName = string.CompareOrdinal(left.Name, right.Name);
            return byName != 0 ? byName : string.CompareOrdinal(left.Value, right.Value);
        });

        var joined = string.Join("&", encoded.Select(p => $"{p.Name}={p.Value}"));

        return $"{Digest.AsciiUpper(method)}&{Encode(BaseUrl(url))}&{Encode(joined)}";
    }

    /// <summary>The URL without query, fragment, or a default port (§3.4.1.2).</summary>
    internal static string BaseUrl(WebUrl url)
    {
        var port = url.Port switch
        {
            null => "",
            80 when url.Scheme == "http" => "",
            443 when url.Scheme == "https" => "",
            { } explicitPort => ":" + explicitPort.ToString(CultureInfo.InvariantCulture),
        };
        return $"{url.Scheme}://{Digest.AsciiLower(url.Host)}{port}{url.Path}";
    }

    internal static string SigningKey(OAuth1Settings settings) =>
        $"{Encode(settings.ConsumerSecret)}&{Encode(settings.TokenSecret)}";

    private static string Sign(OAuth1Signature method, string key, string baseString)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var data = Encoding.UTF8.GetBytes(baseString);
        try
        {
            var digest = method switch
            {
                OAuth1Signature.HmacSha1 => HMACSHA1.HashData(keyBytes, data),
                OAuth1Signature.HmacSha256 => HMACSHA256.HashData(keyBytes, data),
                // Handled by the caller; there is no base string to sign.
                _ => keyBytes,
            };
            return Convert.ToBase64String(digest);
        }
        catch (CryptographicException err)
        {
            throw new RequestError(RequestErrorKind.Auth, "The OAuth 1 signing key could not be used.")
                .WithDetail(err.Message)
                .AsException();
        }
    }

    /// <summary>RFC 5849 §3.6: everything but unreserved characters is encoded.</summary>
    internal static string Encode(string value) => PercentEncoding.Encode(value, EncodeSet.Unreserved);

    /// <summary>Unique per request, which is all the specification asks of it.</summary>
    public static string Nonce()
    {
        var nanos = (UInt128)(DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100;
        return nanos.ToString("x", CultureInfo.InvariantCulture)
            + Environment.ProcessId.ToString("x", CultureInfo.InvariantCulture);
    }

    public static ulong Timestamp() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
