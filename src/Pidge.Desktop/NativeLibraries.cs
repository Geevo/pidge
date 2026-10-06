using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Photino.NET;

namespace Pidge.Desktop;

/// <summary>
/// The window host's native libraries. A published build carries them inside
/// the executable rather than beside it, so the executable is the only file;
/// they are written out on first start, to a folder named for their contents,
/// and PhotinoX is pointed at them there. A build that carries none (a plain
/// <c>dotnet run</c>, the tests) finds them beside itself as usual.
/// </summary>
internal static class NativeLibraries
{
    private const string ResourcePrefix = "native/";
    private const string WindowHost = "PhotinoX.Native";
    private const string WebView2Loader = "WebView2Loader.dll";

    private static readonly Assembly Self = typeof(NativeLibraries).Assembly;

    /// <summary>
    /// Writes the libraries under <paramref name="root"/> unless they are
    /// already there, and loads them. Null once they are loaded; otherwise
    /// why the app cannot start, in words for whoever launched it.
    /// </summary>
    public static string? Load(string root)
    {
        var names = Self.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (names.Length == 0)
        {
            return null;
        }

        var files = names.Select(name => (Name: name[ResourcePrefix.Length..], Bytes: Read(name))).ToArray();
        var dir = Path.Combine(root, Fingerprint(files));
        try
        {
            CreateDir(dir);
            foreach (var (name, bytes) in files)
            {
                WriteUnlessSame(Path.Combine(dir, name), bytes);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"could not write the window host to {dir}: {e.Message}");
            return "the window host could not be written out";
        }

        RemoveOtherVersions(root, dir);
        try
        {
            Register(dir, files.Select(file => file.Name));
        }
        catch (DllNotFoundException e)
        {
            // The usual cause on Linux is a system library the host links to
            // that isn't installed; the loader's message names it.
            Log.Warn($"could not load the window host: {e.Message}");
            return OperatingSystem.IsLinux()
                ? "pidge needs GTK 3, WebKitGTK 4.1 and libnotify, and one of them is not installed"
                : "the window host could not be loaded";
        }
        return null;
    }

    private static void Register(string dir, IEnumerable<string> names)
    {
        // The window host links to the WebView2 loader by name. Loaded first,
        // from here, it is the copy Windows hands the host.
        var loader = Path.Combine(dir, WebView2Loader);
        if (File.Exists(loader))
        {
            NativeLibrary.Load(loader);
        }

        var host = NativeLibrary.Load(Path.Combine(dir, names.Single(name => Path.GetFileNameWithoutExtension(name) == WindowHost)));
        NativeLibrary.SetDllImportResolver(typeof(PhotinoWindow).Assembly,
            (name, _, _) => name == WindowHost ? host : IntPtr.Zero);
    }

    private static byte[] Read(string resource)
    {
        using var stream = Self.GetManifestResourceStream(resource)!;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    /// A new folder for every new set of libraries, so an upgrade never writes
    /// over files an older copy of the app still has loaded.
    /// </summary>
    private static string Fingerprint((string Name, byte[] Bytes)[] files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, bytes) in files)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(name + "\0"));
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }

    /// <summary>
    /// Compared byte for byte rather than trusted by name, so a damaged or
    /// replaced file is written again rather than loaded. Written beside and
    /// renamed into place, so a crash never leaves half a library behind.
    /// </summary>
    private static void WriteUnlessSame(string path, byte[] bytes)
    {
        if (Same(path, bytes))
        {
            return;
        }
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllBytes(temp, bytes);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && Same(path, bytes))
        {
            // Another window started at the same moment and got there first.
            File.Delete(temp);
        }
    }

    private static bool Same(string path, byte[] bytes)
    {
        try
        {
            return File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CreateDir(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
        }
        else
        {
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// Clears out what earlier versions wrote. One still running keeps its
    /// files loaded, which Windows will not let go of; those are left for the
    /// next start.
    /// </summary>
    private static void RemoveOtherVersions(string root, string current)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (!string.Equals(dir, current, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(dir);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"could not look for old window hosts in {root}: {e.Message}");
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"left {dir} for now: {e.Message}");
        }
    }
}
