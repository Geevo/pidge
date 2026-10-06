namespace Pidge.Storage.Tests;

public class KeyringTests
{
    [Fact]
    public void TheKeyFileSitsBesideTheStateFile()
    {
        var keyring = SystemKeyring.Beside(Path.Combine("data", "state.json"));
        Assert.Equal(Path.Combine("data", "state.key"), keyring.Path);
    }

    [Fact]
    public void NoKeyFileMeansNoKeyYet()
    {
        using var dir = new TempDir();
        Assert.Null(SystemKeyring.Beside(dir.Join("state.json")).Read());
    }

    [Fact]
    public void AKeyFileThatIsNotJsonIsReportedNotReplaced()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Join("state.key"), "nonsense");

        var error = Assert.Throws<KeySourceException>(() => SystemKeyring.Beside(dir.Join("state.json")).Read());
        Assert.Equal("the key file is not one this build understands", error.Message);
        Assert.Equal("nonsense", File.ReadAllText(dir.Join("state.key")));
    }

    [Fact]
    public void AnEmptyKeyFileHoldsNoKey()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Join("state.key"), "{}");

        var error = Assert.Throws<KeySourceException>(() => SystemKeyring.Beside(dir.Join("state.json")).Read());
        Assert.Equal("the key file holds no key", error.Message);
    }

    [Fact]
    public void AKeyFromAnotherPlatformSaysWhereItIs()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Join("state.key"), """{ "systemdCreds": "abc" }""");

        var keyring = SystemKeyring.Beside(dir.Join("state.json"));
        if (OperatingSystem.IsLinux())
        {
            return;
        }
        var error = Assert.Throws<KeySourceException>(keyring.Read);
        Assert.Equal("the key was protected by systemd on Linux", error.Message);
    }

    [Fact]
    public void TheKeyCanBeWrittenBeforeTheDirectoryExists()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var keyring = SystemKeyring.Beside(Path.Combine(dir.Join("fresh"), "state.json"));
        var key = Cipher.GenerateKey();

        keyring.Write(key);

        Assert.Equal(key, keyring.Read());
    }

    [Fact]
    public void WindowsProtectsTheKeyWithDpapi()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var keyring = SystemKeyring.Beside(dir.Join("state.json"));
        var key = Cipher.GenerateKey();

        keyring.Write(key);

        var raw = File.ReadAllText(keyring.Path);
        Assert.StartsWith("{\n  \"dpapi\": \"", raw);
        Assert.EndsWith("\"\n}", raw);
        Assert.DoesNotContain(key, raw);
        Assert.Equal(key, keyring.Read());
    }

    [Fact]
    public void ADamagedDpapiBlobIsReported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        File.WriteAllText(dir.Join("state.key"), """{ "dpapi": "not base64!" }""");

        var error = Assert.Throws<KeySourceException>(() => SystemKeyring.Beside(dir.Join("state.json")).Read());
        Assert.Equal("the key file is damaged", error.Message);
    }

    [Fact]
    public void ResolveCreatesTheKeyTheFirstTimeAndReusesIt()
    {
        var keys = new MemoryKeyring();

        var first = Keyring.Resolve(keys);
        var key = keys.Read();
        var second = Keyring.Resolve(keys);

        Assert.NotNull(key);
        Assert.Equal(key, keys.Read());
        var sealedValue = first.Seal("hunter2");
        Assert.Equal("hunter2", second.Open(Cipher.Ciphertext(sealedValue)!));
    }

    [Fact]
    public void ResolveRefusesAKeyItCannotUse()
    {
        var keys = new MemoryKeyring();
        keys.Write("too short");

        var error = Assert.Throws<KeySourceException>(() => Keyring.Resolve(keys));
        Assert.Equal("the saved key is not one this build can use", error.Message);
    }
}
