using System.Text.Json.Nodes;
using Pidge.Core;
using Pidge.Variables;

namespace Pidge.Storage.Tests;

/// <summary>A keyring that lives as long as the test, and can be switched off.</summary>
internal sealed class MemoryKeyring : IKeySource
{
    private readonly Lock _gate = new();
    private string? _key;
    private volatile bool _down;

    public void SetDown(bool down) => _down = down;

    public string? Read()
    {
        if (_down)
        {
            throw new KeySourceException("the keyring is locked");
        }
        lock (_gate)
        {
            return _key;
        }
    }

    public void Write(string key)
    {
        if (_down)
        {
            throw new KeySourceException("the keyring is locked");
        }
        lock (_gate)
        {
            _key = key;
        }
    }
}

public class SecretsTests
{
    private static readonly string[] Secrets =
    [
        "basic-password",
        "header-api-key",
        "history-bearer-token",
        "environment-token",
        "p12-password",
        "oauth1-consumer-secret",
        "oauth1-token-secret",
    ];

    private static AppState StateWithSecrets()
    {
        var request = HttpRequest.Get("https://api.example.com/users");
        request.Auth = new BasicAuth { Username = "ada", Password = "basic-password" };
        request.Headers.Add(new KeyValueEntry("X-Api-Key", "header-api-key"));
        request.Headers.Add(new KeyValueEntry("Accept", "application/json"));

        var signed = HttpRequest.Get("https://api.example.com/timeline");
        signed.Auth = new OAuth1Settings
        {
            ConsumerKey = "consumer-key",
            ConsumerSecret = "oauth1-consumer-secret",
            Token = "visible-oauth1-token",
            TokenSecret = "oauth1-token-secret",
        };

        var sent = HttpRequest.Get("https://api.example.com/me");
        sent.Auth = new BearerAuth { Token = "history-bearer-token" };

        var state = new AppState();
        state.Tabs[0].Request = request.Clone();
        state.UpsertSavedRequest(new SavedRequest("Users", request));
        state.UpsertSavedRequest(new SavedRequest("Timeline", signed));
        state.PushHistory(HistoryEntry.Failure(sent, RequestError.Other("boom")));
        state.Environments.Add(new VariableEnvironment
        {
            Id = Ids.NewId(),
            Name = "Local",
            Variables = [new KeyValueEntry("token", "environment-token")],
        });
        state.Settings.Tls.ClientIdentity = new ClientIdentitySettings
        {
            Path = "/home/ada/client.p12",
            Password = "p12-password",
        };
        return state;
    }

    private static string TabPassword(AppState state) =>
        Assert.IsType<BasicAuth>(state.Tabs[0].Request.Auth).Password;

    private static Store KeyedStore(TempDir dir, MemoryKeyring keys) => Store.InDir(dir.Path).WithKeyring(keys);

    private static string Raw(Store store) => File.ReadAllText(store.Path);

    [Fact]
    public void SecretsAreEncryptedOnDiskAndComeBackOnLoad()
    {
        using var dir = new TempDir();
        var keys = new MemoryKeyring();
        var state = StateWithSecrets();

        KeyedStore(dir, keys).Save(state);

        var onDisk = Raw(KeyedStore(dir, keys));
        foreach (var secret in Secrets)
        {
            Assert.DoesNotContain(secret, onDisk);
        }
        Assert.Contains("enc:v1:", onDisk);
        Assert.Contains("\"secrets\": \"keyring\"", onDisk);
        // Identifiers and ordinary headers stay readable.
        foreach (var visible in new[] { "ada", "application/json", "consumer-key", "visible-oauth1-token" })
        {
            Assert.Contains(visible, onDisk);
        }

        var loaded = KeyedStore(dir, keys).Load();
        Assert.Null(loaded.Recovery);
        Assert.True(PidgeJson.Same(state, loaded.State, StorageJsonContext.Wire.AppState));
    }

    [Fact]
    public void SavingDoesNotTouchTheStateItWasGiven()
    {
        using var dir = new TempDir();
        var state = StateWithSecrets();

        KeyedStore(dir, new MemoryKeyring()).Save(state);

        Assert.Equal("basic-password", TabPassword(state));
    }

    [Fact]
    public void AFileFromBeforeEncryptionIsEncryptedAsSoonAsItLoads()
    {
        using var dir = new TempDir();
        var state = StateWithSecrets();

        // Written the way version 1 wrote it.
        Store.InDir(dir.Path).Save(state);
        var value = JsonNode.Parse(Raw(Store.InDir(dir.Path)))!.AsObject();
        value["version"] = 1;
        value.Remove("secrets");
        File.WriteAllText(Store.InDir(dir.Path).Path, value.ToJsonString());

        var keys = new MemoryKeyring();
        var store = KeyedStore(dir, keys);
        var loaded = store.Load();

        Assert.Null(loaded.Recovery);
        Assert.Equal("basic-password", TabPassword(loaded.State));
        var onDisk = Raw(store);
        foreach (var secret in Secrets)
        {
            Assert.DoesNotContain(secret, onDisk);
        }
    }

    [Fact]
    public void WithoutAKeyringSecretsArePlainTextAndThatIsSaidOnce()
    {
        using var dir = new TempDir();
        var keys = new MemoryKeyring();
        keys.SetDown(true);

        var store = KeyedStore(dir, keys);
        var first = store.Load();
        var notice = first.Recovery;
        Assert.NotNull(notice);
        Assert.Contains("plain text", notice.Message);
        Assert.Contains("the keyring is locked", notice.Message);

        store.Save(StateWithSecrets());
        Assert.Contains("basic-password", Raw(store));
        Assert.Contains("\"secrets\": \"plainText\"", Raw(store));

        var second = KeyedStore(dir, keys).Load();
        Assert.Null(second.Recovery);
        Assert.Equal("basic-password", TabPassword(second.State));
    }

    [Fact]
    public void EncryptedSecretsSurviveASessionWithoutTheKeyring()
    {
        using var dir = new TempDir();
        var keys = new MemoryKeyring();
        KeyedStore(dir, keys).Save(StateWithSecrets());

        keys.SetDown(true);
        var locked = KeyedStore(dir, keys);
        var outcome = locked.Load();
        var notice = outcome.Recovery;
        Assert.NotNull(notice);
        Assert.Contains("cannot be read right now", notice.Message);
        // Not sent as a password, and not shown as one.
        Assert.Equal("", TabPassword(outcome.State));

        // Something new is sent meanwhile, which moves every history row along.
        var sent = HttpRequest.Get("https://api.example.com/later");
        sent.Auth = new BearerAuth { Token = "typed-while-locked" };
        outcome.State.PushHistory(HistoryEntry.Failure(sent, RequestError.Other("boom")));
        locked.Save(outcome.State);
        Assert.Contains("typed-while-locked", Raw(locked));

        keys.SetDown(false);
        var unlocked = KeyedStore(dir, keys).Load();
        Assert.Null(unlocked.Recovery);
        Assert.Equal("basic-password", TabPassword(unlocked.State));
        var tokens = unlocked.State.History
            .Select(entry => entry.Request.Auth is BearerAuth bearer ? bearer.Token : "")
            .ToList();
        Assert.Equal(["typed-while-locked", "history-bearer-token"], tokens);
    }

    [Fact]
    public void ALostKeyClearsWhatItEncryptedAndKeepsACopy()
    {
        using var dir = new TempDir();
        var oldKeys = new MemoryKeyring();
        KeyedStore(dir, oldKeys).Save(StateWithSecrets());

        // A new machine, or a keyring that was reset.
        var newKeys = new MemoryKeyring();
        var outcome = KeyedStore(dir, newKeys).Load();

        var notice = outcome.Recovery;
        Assert.NotNull(notice);
        Assert.Contains("could not be decrypted", notice.Message);
        Assert.StartsWith("9 saved passwords and tokens could not be decrypted with the saved key, so they have been cleared.", notice.Message);
        var copy = notice.BackupPath;
        Assert.NotNull(copy);
        Assert.StartsWith("state.undecryptable-", Path.GetFileName(copy));
        Assert.Contains("enc:v1:", File.ReadAllText(copy));
        Assert.Equal("", TabPassword(outcome.State));
        // Everything that was not secret is still there.
        Assert.Equal(2, outcome.State.SavedRequests.Count);

        var again = KeyedStore(dir, newKeys).Load();
        Assert.Null(again.Recovery);
    }

    [Fact]
    public void ThePlainTextNoticeNamesTheReason()
    {
        using var dir = new TempDir();
        var keys = new MemoryKeyring();
        keys.SetDown(true);

        var notice = KeyedStore(dir, keys).Load().Recovery;

        Assert.NotNull(notice);
        Assert.StartsWith(
            "Passwords and tokens are saved as plain text, in a file only your user account can read, because nothing could protect their key (the keyring is locked).",
            notice.Message);
        Assert.Null(notice.BackupPath);
    }

    [Fact]
    public void TheStateFileIsReadableByItsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var store = Store.InDir(dir.Path);
        UnixFileMode Mode() => File.GetUnixFileMode(store.Path);

        store.Save(new AppState());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, Mode());

        // As every earlier version left it.
        File.SetUnixFileMode(store.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        store.Load();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, Mode());
    }

    [Fact]
    public void PidgeKeyringOffLeavesTheStoreWithoutAKeyring()
    {
        using var dir = new TempDir();
        var previous = Environment.GetEnvironmentVariable("PIDGE_KEYRING");
        Environment.SetEnvironmentVariable("PIDGE_KEYRING", "off");
        try
        {
            var store = Store.InDir(dir.Path).WithSystemKeyring();
            Assert.Null(store.Load().Recovery);
            Assert.False(File.Exists(dir.Join("state.key")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PIDGE_KEYRING", previous);
        }
    }
}
