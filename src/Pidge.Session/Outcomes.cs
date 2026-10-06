using Pidge.Core;
using Pidge.Storage;

namespace Pidge.Session;

/// <summary>What an import brought in, for the notice that reports it.</summary>
public sealed class ImportOutcome
{
    public AppState State { get; set; } = new();

    public int Imported { get; set; }

    /// <summary>
    /// <c>{{names}}</c> the new requests use that no environment defines, which
    /// is what an export without its secrets leaves to be filled in.
    /// </summary>
    public List<string> UndefinedVariables { get; set; } = [];

    /// <summary>A sentence for each request the file held that could not come across.</summary>
    public List<string> Skipped { get; set; } = [];

    /// <summary>The file holds a password or token as it is: worth deleting now.</summary>
    public bool PlainSecrets { get; set; }
}

/// <summary>
/// What a send produced: a response or an error, plus the history row it
/// created. Returning the row lets a frontend keep its history list live
/// without re-fetching the whole state.
/// </summary>
public sealed class SendOutcome
{
    public HttpResponse? Response { get; set; }

    public RequestError? Error { get; set; }

    /// <summary>Null for a cancelled request, which is never recorded.</summary>
    public HistoryEntry? HistoryEntry { get; set; }
}

public enum SessionErrorKind
{
    Storage,
    Engine,

    /// <summary>A file that is not saved requests, or has none that can come across.</summary>
    Import,
}

/// <summary>
/// Anything a state change can fail with.
///
/// Settings and storage fail in different ways and the UI shows both the same
/// way, but keeping them apart means a bad certificate path is not reported as
/// a disk problem.
/// </summary>
public sealed class SessionException : Exception
{
    private SessionException(SessionErrorKind kind, string message, Exception? inner)
        : base(message, inner)
    {
        Kind = kind;
    }

    public SessionErrorKind Kind { get; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="SessionErrorKind.Storage"/>.</summary>
    public StorageException? StorageError => InnerException as StorageException;

    /// <summary>Set when <see cref="Kind"/> is <see cref="SessionErrorKind.Engine"/>.</summary>
    public RequestError? EngineError => (InnerException as RequestErrorException)?.Error;

    public static SessionException Storage(StorageException error) => new(SessionErrorKind.Storage, error.Message, error);

    public static SessionException Engine(RequestErrorException error) =>
        new(SessionErrorKind.Engine, error.Error.Message, error);

    public static SessionException Import(string message) => new(SessionErrorKind.Import, message, null);
}
