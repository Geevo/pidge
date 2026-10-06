namespace Pidge.Storage;

public enum StorageErrorKind
{
    Write,
    Read,
    Migration,
    Parse,
}

/// <summary>A state file that could not be written, read, migrated or parsed.</summary>
public sealed class StorageException : Exception
{
    private StorageException(StorageErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public StorageErrorKind Kind { get; }

    public static StorageException Write(string path, Exception source) =>
        new(StorageErrorKind.Write, $"could not write {path}: {source.Message}", source);

    public static StorageException Read(string path, Exception source) =>
        new(StorageErrorKind.Read, $"could not read {path}: {source.Message}", source);

    public static StorageException Migration(string message) => new(StorageErrorKind.Migration, message);

    public static StorageException Parse(string detail) =>
        new(StorageErrorKind.Parse, $"state file is not valid JSON: {detail}");
}
