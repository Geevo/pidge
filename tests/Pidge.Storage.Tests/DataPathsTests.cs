namespace Pidge.Storage.Tests;

public class DataPathsTests
{
    [Fact]
    public void MovesTheOldDirectoryIntoPlaceOnce()
    {
        using var root = new TempDir();
        var (legacy, current) = (root.Join("api-client"), root.Join("pidge"));
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "state.json"), "{}");

        Assert.True(DataPaths.AdoptLegacyDir(legacy, current));
        Assert.True(File.Exists(Path.Combine(current, "state.json")));
        Assert.False(Directory.Exists(legacy));

        // Nothing left to move the second time.
        Assert.False(DataPaths.AdoptLegacyDir(legacy, current));
    }

    [Fact]
    public void NeverMovesOverADirectoryThatIsAlreadyThere()
    {
        using var root = new TempDir();
        var (legacy, current) = (root.Join("api-client"), root.Join("pidge"));
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "state.json"), "old");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "state.json"), "new");

        Assert.False(DataPaths.AdoptLegacyDir(legacy, current));
        Assert.Equal("new", File.ReadAllText(Path.Combine(current, "state.json")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(legacy, "state.json")));
    }

    [Fact]
    public void DoesNothingOnAFirstInstall()
    {
        using var root = new TempDir();
        var (legacy, current) = (root.Join("api-client"), root.Join("pidge"));
        Assert.False(DataPaths.AdoptLegacyDir(legacy, current));
        Assert.False(Directory.Exists(current));
    }

    [Fact]
    public void TheDataDirectoryIsNamedForTheApp()
    {
        var dir = DataPaths.DefaultDataDir();
        Assert.Equal("pidge", Path.GetFileName(dir));
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("APPDATA") is { Length: > 0 } appData)
        {
            Assert.Equal(Path.Combine(appData, "pidge"), dir);
        }
    }
}
