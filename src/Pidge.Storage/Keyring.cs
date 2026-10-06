using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pidge.Core;

namespace Pidge.Storage;

/*
 * Where the key that encrypts saved secrets is kept.
 *
 * One random key per state file, in `state.key` beside it, protected by
 * whatever the operating system offers that can never ask the user anything:
 *
 * - Windows: DPAPI, which ties it to the user's login.
 * - Linux: both of two things, where it can. A Secret Service keyring
 *   (GNOME Keyring, KDE Wallet), but only one that is already unlocked, which
 *   answers in a fraction of a second. And `systemd-creds --user` (systemd
 *   256 and later), which ties it to the machine and the user the way DPAPI
 *   does and never needs unlocking, but takes two seconds to decrypt: it is
 *   only asked when the keyring is locked.
 * - Anywhere else, macOS included: nothing yet, so secrets are saved as
 *   plain text and the user is told.
 *
 * Something that prompts is treated as not being there. A dialog at startup
 * is worse than a notice.
 */

/// <summary>Somewhere to keep the key. <see cref="SystemKeyring"/> outside of tests.</summary>
public interface IKeySource
{
    /// <summary>
    /// The key, or null when there is no key yet.
    /// </summary>
    /// <exception cref="KeySourceException">
    /// It could not be got at, which is not the same thing as there being none
    /// and never replaces it.
    /// </exception>
    string? Read();

    /// <exception cref="KeySourceException">It could not be kept.</exception>
    void Write(string key);
}

/// <summary>Why a key source could not be used. The message is safe to show to the user.</summary>
public sealed class KeySourceException(string message) : Exception(message);

internal static class Keyring
{
    /// <summary>
    /// Finds the key, creating it the first time.
    /// </summary>
    /// <exception cref="KeySourceException">
    /// The reason the secrets will have to be saved as plain text.
    /// </exception>
    public static Cipher Resolve(IKeySource source)
    {
        if (!Cipher.IsSupported)
        {
            throw new KeySourceException("this system has no ChaCha20-Poly1305 to encrypt with");
        }

        var encoded = source.Read();
        if (encoded is null)
        {
            source.Write(Cipher.GenerateKey());
            // Read back rather than trusting what was written: two processes
            // starting at once have to settle on the same key.
            encoded = source.Read()
                ?? throw new KeySourceException("the new key did not stay where it was put");
        }
        return Cipher.FromKey(encoded)
            ?? throw new KeySourceException("the saved key is not one this build can use");
    }

    /// <summary>Logs the whole error and returns what is worth showing the user.</summary>
    internal static KeySourceException Unavailable(string what, Exception error)
    {
        StorageLog.Warn($"{what}: {error.Message}");
        return new KeySourceException(what);
    }
}

/// <summary>The key file, protected by the operating system.</summary>
public sealed class SystemKeyring : IKeySource
{
    private SystemKeyring(string path) => Path = path;

    /// <summary>Where the key file is.</summary>
    public string Path { get; }

    /// <summary><c>state.key</c> for <c>state.json</c>.</summary>
    public static SystemKeyring Beside(string stateFile) => new(System.IO.Path.ChangeExtension(stateFile, "key"));

    public string? Read()
    {
        string raw;
        try
        {
            raw = File.ReadAllText(Path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new KeySourceException($"the key file could not be read: {e.Message}");
        }

        // An unreadable file is reported, not replaced: it may be the only way
        // back to everything it encrypted.
        KeyFile? file;
        try
        {
            file = JsonSerializer.Deserialize(raw, KeyFileJsonContext.Wire.KeyFile);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            file = null;
        }
        if (file is null)
        {
            throw new KeySourceException("the key file is not one this build understands");
        }

        var problems = new List<string>();
        var gone = false;
        if (file.SecretService is { } item)
        {
            try
            {
                if (Platform.SecretServiceRead(item) is { } key)
                {
                    return key;
                }
                gone = true;
            }
            catch (KeySourceException e)
            {
                problems.Add(e.Message);
            }
        }
        if (file.SystemdCreds is { } sealedKey)
        {
            try
            {
                return Platform.SystemdCredsDecrypt(sealedKey);
            }
            catch (KeySourceException e)
            {
                problems.Add(e.Message);
            }
        }
        if (file.Dpapi is { } blob)
        {
            try
            {
                return Platform.DpapiUnprotect(blob);
            }
            catch (KeySourceException e)
            {
                problems.Add(e.Message);
            }
        }

        if (problems.Count > 0)
        {
            throw new KeySourceException(string.Join("; ", problems));
        }
        if (gone)
        {
            // Deleted from the keyring with nothing else holding it: lost, the
            // same as a lost file, so a new key can take its place.
            return null;
        }
        throw new KeySourceException("the key file holds no key");
    }

    public void Write(string key)
    {
        var file = Platform.Protect(key);
        var json = JsonSerializer.Serialize(file, KeyFileJsonContext.Pretty.KeyFile);
        try
        {
            // On a first run the key is made before the state is first saved,
            // so the directory may not exist yet.
            if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } parent)
            {
                PrivateFiles.CreatePrivateDir(parent);
            }
            PrivateFiles.WriteFile(Path, json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new KeySourceException($"the key file could not be written: {e.Message}");
        }
    }

    public override string ToString() => $"SystemKeyring {{ path: {Path} }}";

    /// <summary>The protections, by operating system.</summary>
    private static class Platform
    {
        public static KeyFile Protect(string key)
        {
            if (OperatingSystem.IsWindows())
            {
                return Windows.Protect(key);
            }
            if (OperatingSystem.IsLinux())
            {
                return Linux.Protect(key);
            }
            throw new KeySourceException("this platform has nothing to protect it with yet");
        }

        public static string DpapiUnprotect(string blob) =>
            OperatingSystem.IsWindows()
                ? Windows.DpapiUnprotect(blob)
                : throw new KeySourceException("the key was protected by Windows");

        public static string SystemdCredsDecrypt(string sealedKey) =>
            OperatingSystem.IsLinux()
                ? Linux.SystemdCredsDecrypt(sealedKey)
                : throw new KeySourceException("the key was protected by systemd on Linux");

        public static string? SecretServiceRead(string item) =>
            OperatingSystem.IsLinux()
                ? Linux.SecretServiceRead(item)
                : throw new KeySourceException("the key is in a Linux keyring");
    }

    [SupportedOSPlatform("windows")]
    private static class Windows
    {
        /// <summary>
        /// Mixed in so that another program running as the same user cannot hand
        /// the blob to DPAPI and get the key back without knowing it. The app's
        /// name before it was pidge, kept: a key protected with it opens only
        /// with it.
        /// </summary>
        private static ReadOnlySpan<byte> Entropy => "api-client state key"u8;

        public static KeyFile Protect(string key)
        {
            byte[] blob;
            try
            {
                blob = Dpapi.Protect(Encoding.UTF8.GetBytes(key), Entropy);
            }
            catch (Win32Exception e)
            {
                throw Keyring.Unavailable("Windows could not protect the key", e);
            }
            return new KeyFile { Dpapi = Convert.ToBase64String(blob) };
        }

        public static string DpapiUnprotect(string encoded)
        {
            var blob = Cipher.DecodeBase64(encoded)
                ?? throw new KeySourceException("the key file is damaged");
            byte[] key;
            try
            {
                key = Dpapi.Unprotect(blob, Entropy);
            }
            catch (Win32Exception e)
            {
                throw Keyring.Unavailable("Windows could not unprotect the key", e);
            }
            return StrictUtf8(key) ?? throw new KeySourceException("the key file is damaged");
        }
    }

    [SupportedOSPlatform("linux")]
    private static class Linux
    {
        /// <summary>
        /// Bound into the credential, so it cannot be passed off as another one.
        /// Named for the app before it was pidge, and kept: a credential sealed
        /// under one name will not open under another.
        /// </summary>
        private const string CredentialName = "api-client-key";

        /// <summary>Both, where both are there; either on its own will do.</summary>
        public static KeyFile Protect(string key)
        {
            var item = Ids.NewId();
            string? keyring = null;
            string? creds = null;
            string? keyringError = null;
            string? credsError = null;

            try
            {
                SecretService.Create(item, key);
                keyring = item;
            }
            catch (SecretServiceException e)
            {
                keyringError = e.InnerException is { } inner ? $"{e.Message} ({inner.Message})" : e.Message;
            }

            try
            {
                var sealedKey = SystemdCreds(["encrypt", "--tpm2-pcrs="], Encoding.UTF8.GetBytes(key));
                creds = Encoding.UTF8.GetString(sealedKey).Trim();
            }
            catch (KeySourceException e)
            {
                credsError = e.Message;
            }

            if (keyring is null && creds is null)
            {
                StorageLog.Warn($"nowhere to protect the key: keyring: {keyringError}; creds: {credsError}");
                throw new KeySourceException("there is neither an unlocked keyring nor systemd-creds");
            }
            return new KeyFile { SecretService = keyring, SystemdCreds = creds };
        }

        public static string SystemdCredsDecrypt(string sealedKey)
        {
            byte[] key;
            try
            {
                key = SystemdCreds(["decrypt"], Encoding.UTF8.GetBytes(sealedKey));
            }
            catch (KeySourceException e)
            {
                throw Keyring.Unavailable("systemd-creds could not decrypt the key", e);
            }
            return StrictUtf8(key) ?? throw new KeySourceException("the key file is damaged");
        }

        /// <summary>
        /// Runs <c>systemd-creds --user</c>. <c>--no-ask-password</c> stops it
        /// asking for authentication; with no PCRs, a firmware update or a
        /// change to Secure Boot does not lock the key away.
        /// </summary>
        private static byte[] SystemdCreds(string[] args, byte[] input)
        {
            var start = new ProcessStartInfo("systemd-creds")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardInputEncoding = new UTF8Encoding(false),
            };
            start.ArgumentList.Add("--user");
            start.ArgumentList.Add("--no-ask-password");
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }
            start.ArgumentList.Add($"--name={CredentialName}");
            start.ArgumentList.Add("-");
            start.ArgumentList.Add("-");

            try
            {
                using var child = Process.Start(start)
                    ?? throw new KeySourceException("systemd-creds: it did not start");
                var stdout = new MemoryStream();
                var reading = child.StandardOutput.BaseStream.CopyToAsync(stdout);
                var stderr = child.StandardError.ReadToEndAsync();

                child.StandardInput.BaseStream.Write(input);
                child.StandardInput.Close();

                Task.WaitAll(reading, stderr);
                child.WaitForExit();
                if (child.ExitCode != 0)
                {
                    throw new KeySourceException($"systemd-creds: {stderr.Result.Trim()}");
                }
                return stdout.ToArray();
            }
            catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException or AggregateException)
            {
                var cause = e is AggregateException { InnerException: { } inner } ? inner : e;
                throw new KeySourceException($"systemd-creds: {cause.Message}");
            }
        }

        /// <summary>Never unlocks anything: unlocking is what shows a password dialog.</summary>
        public static string? SecretServiceRead(string item)
        {
            try
            {
                return SecretService.Read(item);
            }
            catch (SecretServiceException e) when (e.InnerException is { } inner)
            {
                throw Keyring.Unavailable(e.Message, inner);
            }
            catch (SecretServiceException e)
            {
                throw new KeySourceException(e.Message);
            }
        }
    }

    private static string? StrictUtf8(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}

/// <summary>
/// What <c>state.key</c> holds: the key under each protection that was
/// available when it was made, tried in the order of the fields.
/// </summary>
internal sealed class KeyFile
{
    /// <summary>The id of the keyring item holding the key. The file only points at it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecretService { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemdCreds { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Dpapi { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(KeyFile))]
internal sealed partial class KeyFileJsonContext : JsonSerializerContext
{
    public static KeyFileJsonContext Wire { get; } = new(PidgeJson.CreateOptions());

    public static KeyFileJsonContext Pretty { get; } = new(PidgeJson.CreateOptions(indented: true));
}
