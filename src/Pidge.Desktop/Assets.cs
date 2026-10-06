using System.Reflection;
using System.Text;

namespace Pidge.Desktop;

/// <summary>
/// The built frontend, embedded in the executable and served from the
/// <c>app://</c> scheme, so there is no folder beside the program to lose and
/// no local port for anything else to reach.
/// </summary>
internal static class Assets
{
    public const string Scheme = "app";
    public const string Origin = "app://localhost/";
    public const string StartUrl = Origin + "index.html";

    /*
     * What the page may load and talk to: itself, and nothing else. The host
     * channel is not a network request, so it needs no entry here. Inline
     * styles are allowed because index.html paints the boot screen with one.
     */
    private const string ContentSecurityPolicy =
        "default-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self'; img-src 'self' data:; connect-src 'self'";

    private static readonly Assembly Self = typeof(Assets).Assembly;

    /// <summary>The response for <paramref name="url"/>, or null for anything that is not part of the app.</summary>
    public static Stream? Open(string url, out string? contentType)
    {
        contentType = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        if (path.Length == 0)
        {
            path = "index.html";
        }
        if (path.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var stream = Self.GetManifestResourceStream("app/" + path)
            ?? Self.GetManifestResourceStream("app/" + path.Replace('/', '\\'));
        if (stream is null)
        {
            return null;
        }
        contentType = ContentTypeOf(path);
        return path == "index.html" ? WithPolicy(stream) : stream;
    }

    /// <summary>The window icon, written out once to where the window can read it.</summary>
    public static string? IconFile(string directory)
    {
        try
        {
            using var icon = Self.GetManifestResourceStream("icon.png");
            if (icon is null)
            {
                return null;
            }
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "icon.png");
            if (!File.Exists(path) || new FileInfo(path).Length != icon.Length)
            {
                using var file = File.Create(path);
                icon.CopyTo(file);
            }
            return path;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Stream WithPolicy(Stream stream)
    {
        string html;
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            html = reader.ReadToEnd();
        }
        var meta = $"<meta http-equiv=\"Content-Security-Policy\" content=\"{ContentSecurityPolicy}\" />";
        var head = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        html = head < 0 ? meta + html : html.Insert(head + "<head>".Length, "\n    " + meta);
        return new MemoryStream(Encoding.UTF8.GetBytes(html));
    }

    internal static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" or ".map" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".txt" => "text/plain; charset=utf-8",
        ".md" => "text/markdown; charset=utf-8",
        _ => "application/octet-stream",
    };
}
