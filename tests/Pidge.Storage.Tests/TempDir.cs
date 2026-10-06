namespace Pidge.Storage.Tests;

/// <summary>A directory that lives as long as the test.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pidge-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Join(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
