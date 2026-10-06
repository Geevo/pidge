using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pidge.Storage;

/*
 * Local persistence.
 *
 * One human-readable JSON file per installation, written atomically, with a
 * version number and a migration chain from the very first release. If the
 * file is unreadable we keep it, say so, and start from defaults — losing a
 * scratch tab is annoying, refusing to open is worse.
 *
 * Passwords and tokens inside it are encrypted with a key the operating
 * system protects, when it can without prompting; see Secrets.cs and
 * Keyring.cs.
 */

/// <summary>
/// Why the loaded state is not what was on disk, or what the user should know
/// about how it is kept.
/// </summary>
/// <param name="Message">Safe to show to the user.</param>
/// <param name="BackupPath">Where the unreadable file was moved, if we managed to keep it.</param>
public sealed record Recovery(string Message, string? BackupPath);

/// <summary>The result of a load: always a usable state, plus an optional explanation.</summary>
public sealed record LoadOutcome(AppState State, Recovery? Recovery);

/// <summary>Reads and writes the state file.</summary>
public sealed class Store
{
    public const string StateFileName = "state.json";

    private readonly IKeySource? _keyring;

    /// <summary>Held for the whole of a load or a save, and over <see cref="_secrets"/>.</summary>
    private readonly Lock _gate = new();

    /// <summary>What this store encrypts with, settled on first use.</summary>
    private SecretsState? _secrets;

    /// <summary>A store that keeps secrets as plain text. See <see cref="WithKeyring"/>.</summary>
    public Store(string path)
        : this(path, null)
    {
    }

    private Store(string path, IKeySource? keyring)
    {
        Path = path;
        _keyring = keyring;
    }

    /// <summary><c>&lt;dir&gt;/state.json</c></summary>
    public static Store InDir(string dir) => new(System.IO.Path.Combine(dir, StateFileName));

    public string Path { get; }

    /// <summary>
    /// Encrypts secrets with a key kept in <paramref name="keys"/>. If it cannot
    /// be reached, secrets are saved as plain text and the next load says so.
    /// </summary>
    public Store WithKeyring(IKeySource keys) => new(Path, keys);

    /// <summary>
    /// <see cref="WithKeyring"/> with a key in <c>state.key</c> beside the state
    /// file, protected by the operating system; see <see cref="SystemKeyring"/>.
    /// <c>PIDGE_KEYRING=off</c> skips it, which the tests that start a real
    /// sidecar use to stay out of the user's keyring.
    /// </summary>
    public Store WithSystemKeyring()
    {
        if (Environment.GetEnvironmentVariable("PIDGE_KEYRING") == "off")
        {
            return this;
        }
        return WithKeyring(SystemKeyring.Beside(Path));
    }

    /// <summary>Never fails: a missing or corrupt file yields defaults plus a <see cref="Recovery"/>.</summary>
    public LoadOutcome Load()
    {
        lock (_gate)
        {
            var (state, toldPlain, recovery) = ReadState();
            var messages = new List<string>();
            if (recovery is not null)
            {
                messages.Add(recovery.Message);
            }
            var backupPath = recovery?.BackupPath;
            var rewrite = false;

            var secrets = _secrets ??= Resolve();
            var opened = SecretFields.Open(state, secrets.Cipher);

            if (opened.Unreadable > 0)
            {
                var copy = KeepCopy("undecryptable");
                var keptAt = copy is null ? "" : $" The previous file was kept at {copy}.";
                messages.Add(
                    $"{CountSecrets(opened.Unreadable)} could not be decrypted with the saved key, so "
                    + $"{(opened.Unreadable == 1 ? "it has" : "they have")} been cleared.{keptAt}");
                backupPath ??= copy;
                rewrite = true;
            }

            var reason = secrets.Unavailable ?? "no keyring was asked";
            if (opened.Kept.Count > 0)
            {
                messages.Add(
                    $"Your saved passwords and tokens are encrypted, and their key cannot be read right now ({reason}). "
                    + "They are left as they are in the file and come back once it can be; anything you enter meanwhile is saved as plain text.");
            }
            else if (secrets.Unavailable is not null && !toldPlain)
            {
                messages.Add(
                    "Passwords and tokens are saved as plain text, in a file only your user account can read, "
                    + $"because nothing could protect their key ({reason}).{KeyringHint()}");
                // Written now, so that the file records it has been said.
                rewrite = true;
            }

            if (secrets.Cipher is not null && opened.Plain > 0)
            {
                rewrite = true;
            }
            secrets.Kept = opened.Kept;

            if (rewrite)
            {
                try
                {
                    SaveLocked(state);
                }
                catch (StorageException e)
                {
                    StorageLog.Warn($"could not rewrite the state file: {e.Message}");
                }
            }

            return new LoadOutcome(
                state,
                messages.Count > 0 ? new Recovery(string.Join(" ", messages), backupPath) : null);
        }
    }

    /// <summary>
    /// Writes to a sibling temp file and renames, so a crash mid-write cannot
    /// leave a half-written state file behind.
    /// </summary>
    /// <exception cref="StorageException">The file or its directory could not be written.</exception>
    public void Save(AppState state)
    {
        lock (_gate)
        {
            SaveLocked(state);
        }
    }

    private void SaveLocked(AppState original)
    {
        var state = original.Clone();
        state.Version = Migration.SchemaVersion;

        var secrets = _secrets ??= Resolve();
        SecretFields.Seal(state, secrets.Cipher, secrets.Kept);
        var storage = secrets.Cipher is not null ? "keyring" : "plainText";

        if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } parent)
        {
            try
            {
                PrivateFiles.CreatePrivateDir(parent);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw StorageException.Write(parent, e);
            }
        }

        // The state, and a note of how its secrets are kept.
        var onDisk = JsonSerializer.SerializeToNode(state, StorageJsonContext.Wire.AppState)!.AsObject();
        onDisk["secrets"] = storage;
        var json = onDisk.ToJsonString(StorageJsonContext.Pretty.Options);

        try
        {
            PrivateFiles.WriteFile(Path, json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw StorageException.Write(Path, e);
        }
    }

    /// <summary>
    /// The state on disk, whether it says its plain-text secrets have been
    /// announced, and what went wrong reading it.
    /// </summary>
    private (AppState State, bool ToldPlain, Recovery? Recovery) ReadState()
    {
        string raw;
        try
        {
            raw = File.ReadAllText(Path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return (new AppState(), false, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var recovery = Recovered($"Could not read your saved data ({e.Message}). Starting fresh.", keepFile: false);
            return (new AppState(), false, recovery);
        }

        // A file from before the permissions were tightened is fixed on sight.
        PrivateFiles.MakePrivate(Path);

        string message;
        try
        {
            var value = JsonNode.Parse(raw);
            var toldPlain = value is JsonObject obj
                && obj["secrets"] is JsonValue secrets
                && secrets.GetValueKind() == JsonValueKind.String
                && secrets.GetValue<string>() == "plainText";
            return (Migration.Migrate(value), toldPlain, null);
        }
        catch (JsonException e)
        {
            message = e.Message;
        }
        catch (StorageException e)
        {
            message = e.Message;
        }

        return (
            new AppState(),
            false,
            Recovered($"Your saved data could not be loaded ({message}). Starting fresh.", keepFile: true));
    }

    /// <summary>Asks the keyring for the key, once per store.</summary>
    private SecretsState Resolve()
    {
        if (_keyring is null)
        {
            return new SecretsState(null, null);
        }
        try
        {
            return new SecretsState(Keyring.Resolve(_keyring), null);
        }
        catch (KeySourceException e)
        {
            StorageLog.Warn($"secrets will be saved as plain text: {e.Message}");
            return new SecretsState(null, e.Message);
        }
    }

    /// <summary>Copies the state file aside before something in it is given up on.</summary>
    private string? KeepCopy(string why)
    {
        var copy = System.IO.Path.ChangeExtension(Path, $"{why}-{Clock.NowMs()}.json");
        try
        {
            File.Copy(Path, copy, overwrite: true);
            PrivateFiles.MakePrivate(copy);
            return copy;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StorageLog.Warn($"could not keep a copy of the state file: {e.Message}");
            return null;
        }
    }

    /// <summary>Moves the unreadable file aside so the user can recover it by hand.</summary>
    private Recovery Recovered(string message, bool keepFile)
    {
        string? backupPath = null;
        if (keepFile)
        {
            var backup = System.IO.Path.ChangeExtension(Path, $"corrupt-{Clock.NowMs()}.json");
            try
            {
                File.Move(Path, backup, overwrite: true);
                PrivateFiles.MakePrivate(backup);
                backupPath = backup;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                StorageLog.Warn($"could not preserve the unreadable state file: {e.Message}");
            }
        }

        return new Recovery(
            backupPath is null ? message : $"{message} The previous file was kept at {backupPath}.",
            backupPath);
    }

    private static string CountSecrets(int n) =>
        n == 1 ? "A saved password or token" : $"{n} saved passwords and tokens";

    /// <summary>What to install, where the answer is not obvious.</summary>
    private static string KeyringHint() =>
        OperatingSystem.IsLinux()
            ? " That needs systemd 256 or later, or an unlocked GNOME Keyring or KDE Wallet."
            : "";

    public override string ToString() => $"Store {{ path: {Path}, keyring: {_keyring} }}";

    private sealed class SecretsState(Cipher? cipher, string? unavailable)
    {
        /// <summary>Null saves secrets as plain text.</summary>
        public Cipher? Cipher { get; } = cipher;

        /// <summary>Why there is no cipher, when a keyring was asked for one and failed.</summary>
        public string? Unavailable { get; } = unavailable;

        /// <summary>Ciphertext that could not be opened, written back until replaced.</summary>
        public IReadOnlyDictionary<string, string> Kept { get; set; } = new Dictionary<string, string>();
    }
}
