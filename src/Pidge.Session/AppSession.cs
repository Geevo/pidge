using System.Diagnostics;
using Pidge.Codegen;
using Pidge.Core;
using Pidge.HttpEngine;
using Pidge.Storage;
using Pidge.Variables;
using Engine = Pidge.HttpEngine.HttpEngine;

namespace Pidge.Session;

/// <summary>
/// The shared application service, and a running application: one engine, one
/// state file, one in-memory state.
///
/// Everything a frontend can do lives here: send, cancel, persist, save, and
/// clear. The desktop shell and the sidecar's message handlers are both thin
/// wrappers around this type, which is what keeps the two platforms honest.
/// Safe to use from any thread.
/// </summary>
public sealed class AppSession : IDisposable
{
    /// <summary>The largest file <see cref="ImportSavedRequests"/> will read.</summary>
    public const int MaxImportBytes = Import.MaxImportBytes;

    private readonly CancellationRegistry _cancellations = new();
    private readonly Store _store;
    private readonly Lock _stateLock = new();
    private readonly Lock _engineLock = new();
    private AppState _state;

    /// <summary>
    /// Rebuilt when the settings that shape it change, so a new CA or client
    /// certificate takes effect without restarting the app.
    /// </summary>
    private Engine _engine;

    private AppSession(Store store, AppState state, Recovery? recovery, Engine engine)
    {
        _store = store;
        _state = state;
        Recovery = recovery;
        _engine = engine;
    }

    /// <summary>
    /// Loads state from <paramref name="store"/> and builds an engine
    /// configured from it. Never fails on a bad state file; see <see cref="Recovery"/>.
    /// </summary>
    /// <exception cref="RequestErrorException">The TLS settings could not be applied.</exception>
    public static AppSession Start(Store store)
    {
        var (state, recovery) = store.Load();
        state.EnsureOneTab();

        var engine = new Engine(EngineConfigFor(state.Settings));
        return new AppSession(store, state, recovery, engine);
    }

    /// <summary>Set when the state file was missing, unreadable, or from a newer build.</summary>
    public Recovery? Recovery { get; }

    public string StoragePath => _store.Path;

    /// <summary>A copy of the current state, for handing to the UI.</summary>
    public AppState Snapshot()
    {
        lock (_stateLock)
        {
            return _state.Clone();
        }
    }

    /// <summary>
    /// Sends a request, resolving variables from the active environment and
    /// recording the outcome in history.
    /// </summary>
    /// <exception cref="RequestErrorException">The request failed or was cancelled.</exception>
    public async Task<HttpResponse> SendAsync(HttpRequest request)
    {
        var outcome = await SendWithOverridesAsync(request).ConfigureAwait(false);
        if (outcome.Response is { } response)
        {
            return response;
        }
        throw (outcome.Error ?? RequestError.Other("the request produced no result")).AsException();
    }

    /// <summary>
    /// As <see cref="SendAsync"/>, with extra variables layered over the
    /// active environment. The sidecar uses this so the webview can be
    /// explicit about what it thinks the variables are. Never throws: a
    /// failure is in the outcome.
    /// </summary>
    public async Task<SendOutcome> SendWithOverridesAsync(
        HttpRequest request,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var variables = Variables(overrides);
        var handle = _cancellations.Register(request.Id);

        // Taken out of the lock: a rebuild must not wait for a send, and the
        // engine it swaps out stays usable until this send is done with it.
        var engine = CurrentEngine();
        HttpResponse? response = null;
        RequestError? error = null;
        try
        {
            response = await engine.ExecuteWithVariablesAsync(request.Clone(), variables, handle).ConfigureAwait(false);
        }
        catch (RequestErrorException e)
        {
            error = e.Error;
        }
        catch (Exception e)
        {
            error = RequestError.Other(e.Message);
        }
        finally
        {
            _cancellations.Finish(request.Id);
        }

        // A cancelled request was never really sent, so it does not earn a
        // history entry.
        HistoryEntry? historyEntry = null;
        if (error?.Kind != RequestErrorKind.Cancelled)
        {
            historyEntry = response is not null
                ? HistoryEntry.Success(request, response)
                : HistoryEntry.Failure(request, error!);
            var kept = PidgeJson.Clone(historyEntry, StorageJsonContext.Wire.HistoryEntry);
            lock (_stateLock)
            {
                _state.PushHistory(kept);
            }

            // History is written straight away, so a crash or a force-quit
            // does not lose what you just sent. A storage failure is worth a
            // log, but it must never turn a good response into an error.
            try
            {
                Persist();
            }
            catch (StorageException e)
            {
                Trace.TraceWarning($"could not write history: {e.Message}");
            }
        }

        return new SendOutcome { Response = response, Error = error, HistoryEntry = historyEntry };
    }

    /// <summary>
    /// Writes a request out as code for another client.
    ///
    /// Variables are resolved exactly as a send resolves them, so the snippet
    /// carries the values the request would actually go out with rather than
    /// <c>{{name}}</c> for somebody to fill in by hand. An unresolved name is
    /// the same error it would be on Send: there is no honest code to write for it.
    /// </summary>
    /// <exception cref="RequestErrorException">A variable has no value, or the request cannot be written.</exception>
    public string GenerateCode(
        HttpRequest request,
        IReadOnlyDictionary<string, string>? overrides,
        CodeTarget target)
    {
        var variables = Variables(overrides);
        ClientOptions options;
        lock (_stateLock)
        {
            var settings = _state.Settings;
            options = new ClientOptions
            {
                TimeoutMs = settings.TimeoutMs,
                FollowRedirects = settings.FollowRedirects,
                Tls = settings.Tls.Clone(),
            };
        }

        var resolved = Substitution.ResolveRequest(request, variables);
        return CodeGenerator.Generate(resolved, options, target);
    }

    /// <summary>Cancels an in-flight request. Returns false if it had already finished.</summary>
    public bool Cancel(string requestId) => _cancellations.Cancel(requestId);

    public int InFlight => _cancellations.InFlight;

    /// <summary>
    /// Replaces the whole state, as the UI does when tabs or settings change.
    ///
    /// History is deliberately not taken from <paramref name="next"/>: it is
    /// owned here and only changes through <see cref="SendAsync"/> and
    /// <see cref="ClearHistory"/>. A UI saving a snapshot it took before its
    /// last send must not erase the row that send created.
    ///
    /// The window placement is kept the same way: only the desktop shell moves
    /// the window, through <see cref="SetWindowPlacement"/>.
    /// </summary>
    /// <exception cref="SessionException">The state could not be saved, or the new settings could not be applied.</exception>
    public void ReplaceState(AppState next)
    {
        next = next.Clone();
        next.EnsureOneTab();

        var wanted = EngineConfigFor(next.Settings);
        var rebuild = wanted != CurrentEngine().Config;

        lock (_stateLock)
        {
            next.History = _state.History;
            next.Window = _state.Window;
            _state = next;
        }

        // Saved first. If a certificate path is wrong the setting still needs
        // to persist, or the dialog that reported the error would have nothing
        // to correct.
        try
        {
            Persist();
        }
        catch (StorageException e)
        {
            throw SessionException.Storage(e);
        }

        if (rebuild)
        {
            Engine engine;
            try
            {
                engine = new Engine(wanted);
            }
            catch (RequestErrorException e)
            {
                throw SessionException.Engine(e);
            }
            lock (_engineLock)
            {
                // The old engine is not disposed: a send that started on it
                // may still be using it, and it is released once that is done.
                _engine = engine;
            }
        }
    }

    /// <summary>Creates or updates a saved request. Passing <paramref name="id"/> updates in place.</summary>
    /// <exception cref="StorageException">The state could not be saved.</exception>
    public SavedRequest SaveRequest(string? id, string name, HttpRequest request)
    {
        var saved = new SavedRequest(name, request.Clone());
        if (id is not null)
        {
            saved.Id = id;
        }
        var kept = PidgeJson.Clone(saved, StorageJsonContext.Wire.SavedRequest);
        lock (_stateLock)
        {
            _state.UpsertSavedRequest(kept);
            saved.CreatedAt = kept.CreatedAt;
            saved.UpdatedAt = kept.UpdatedAt;
        }
        Persist();
        return saved;
    }

    /// <exception cref="StorageException">The state could not be saved.</exception>
    public bool DeleteSavedRequest(string id)
    {
        bool removed;
        lock (_stateLock)
        {
            removed = _state.DeleteSavedRequest(id);
        }
        Persist();
        return removed;
    }

    /// <summary>
    /// The saved requests with these ids, in the order they are listed, as a
    /// file to share. Secrets become <c>{{variables}}</c> unless <paramref name="includeSecrets"/>.
    /// </summary>
    public string ExportSavedRequests(IReadOnlyCollection<string> ids, ExportFormat format, bool includeSecrets)
    {
        List<SavedRequest> chosen;
        lock (_stateLock)
        {
            chosen = _state.SavedRequests
                .Where(saved => ids.Contains(saved.Id))
                .Select(saved => PidgeJson.Clone(saved, StorageJsonContext.Wire.SavedRequest))
                .ToList();
        }
        return Export.Write(chosen, format, includeSecrets);
    }

    /// <summary>
    /// Adds the saved requests in <paramref name="contents"/>, a JSON export or
    /// a <c>.http</c> file, as new ones. Nothing already saved is changed.
    /// </summary>
    /// <exception cref="SessionException">
    /// The file is not one that can be imported (<see cref="SessionErrorKind.Import"/>),
    /// or the state could not be saved.
    /// </exception>
    public ImportOutcome ImportSavedRequests(string contents)
    {
        Import.Parsed parsed;
        try
        {
            parsed = Import.Parse(contents);
        }
        catch (ImportRefusedException e)
        {
            throw SessionException.Import(e.Message);
        }

        var known = new VariableSet();
        lock (_stateLock)
        {
            foreach (var variable in _state.Environments.SelectMany(environment => environment.Variables))
            {
                var name = variable.Name.Trim();
                if (name.Length > 0)
                {
                    known.Insert(name, "");
                }
            }
        }

        var undefinedVariables = new List<string>();
        foreach (var saved in parsed.Saved)
        {
            foreach (var name in Substitution.MissingVariables(saved.Request, known))
            {
                if (!undefinedVariables.Contains(name))
                {
                    undefinedVariables.Add(name);
                }
            }
        }
        var plainSecrets = parsed.Saved.Any(saved => Export.HoldsPlainSecret(saved.Request));

        var imported = parsed.Saved.Count;
        lock (_stateLock)
        {
            _state.SavedRequests.AddRange(parsed.Saved);
        }
        try
        {
            Persist();
        }
        catch (StorageException e)
        {
            throw SessionException.Storage(e);
        }

        return new ImportOutcome
        {
            State = Snapshot(),
            Imported = imported,
            UndefinedVariables = undefinedVariables,
            Skipped = parsed.Skipped,
            PlainSecrets = plainSecrets,
        };
    }

    /// <exception cref="StorageException">The state could not be saved.</exception>
    public void ClearHistory()
    {
        lock (_stateLock)
        {
            _state.History.Clear();
        }
        Persist();
    }

    /// <summary>
    /// Where the desktop window was last. Kept in memory as the window moves;
    /// it reaches the disk with the next save.
    /// </summary>
    public void SetWindowPlacement(WindowPlacement placement)
    {
        lock (_stateLock)
        {
            _state.Window = placement;
        }
    }

    public WindowPlacement? GetWindowPlacement()
    {
        lock (_stateLock)
        {
            return _state.Window;
        }
    }

    /// <summary>Writes the current state to disk.</summary>
    /// <exception cref="StorageException">The state could not be saved.</exception>
    public void Persist() => _store.Save(Snapshot());

    /// <summary>Closes the engine's connections.</summary>
    public void Dispose() => CurrentEngine().Dispose();

    private VariableSet Variables(IReadOnlyDictionary<string, string>? overrides)
    {
        VariableSet variables;
        lock (_stateLock)
        {
            variables = _state.ActiveVariables();
        }
        if (overrides is not null)
        {
            foreach (var (name, value) in overrides)
            {
                variables.Insert(name, value);
            }
        }
        return variables;
    }

    private Engine CurrentEngine()
    {
        lock (_engineLock)
        {
            return _engine;
        }
    }

    private static EngineConfig EngineConfigFor(Settings settings) => new()
    {
        DefaultTimeout = settings.TimeoutMs,
        MaxResponseBytes = settings.MaxResponseBytes,
        FollowRedirects = settings.FollowRedirects,
        Tls = settings.Tls.Clone(),
    };
}
