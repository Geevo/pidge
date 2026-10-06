namespace Pidge.HttpEngine;

/// <summary>Handed to <c>ExecuteAsync</c>; cancelling it aborts the request wherever it has got to.</summary>
public sealed class CancellationHandle
{
    private readonly CancellationTokenSource _source = new();

    public void Cancel()
    {
        try
        {
            _source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public bool IsCancelled => _source.IsCancellationRequested;

    public CancellationToken Token => _source.Token;
}

/// <summary>
/// Tracks in-flight requests by id so a platform adapter can implement
/// <c>cancel_http_request(id)</c> without inventing its own bookkeeping.
/// </summary>
public sealed class CancellationRegistry
{
    private readonly Dictionary<string, CancellationHandle> _inner = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>
    /// Registers <paramref name="id"/> and returns its handle. A repeated id
    /// replaces the old entry, which is what you want when a tab re-sends.
    /// </summary>
    public CancellationHandle Register(string id)
    {
        var handle = new CancellationHandle();
        lock (_lock)
        {
            _inner[id] = handle;
        }
        return handle;
    }

    /// <summary>Cancels <paramref name="id"/> if it is still running. Returns whether anything was cancelled.</summary>
    public bool Cancel(string id)
    {
        CancellationHandle? handle;
        lock (_lock)
        {
            if (!_inner.Remove(id, out handle))
            {
                return false;
            }
        }
        handle.Cancel();
        return true;
    }

    public void Finish(string id)
    {
        lock (_lock)
        {
            _inner.Remove(id);
        }
    }

    public int InFlight
    {
        get
        {
            lock (_lock)
            {
                return _inner.Count;
            }
        }
    }
}
