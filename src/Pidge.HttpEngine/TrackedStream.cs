using System.Net.Security;

namespace Pidge.HttpEngine;

/// <summary>
/// Which connection carried one request.
///
/// The handler pools connections and says nothing about which one a response
/// came back on, so each connection's stream marks itself here as the request
/// is written. The mark travels with the request's async flow, so two requests
/// in flight at once each see their own connection, and a reused connection
/// reports the TLS it was opened with. An HTTP/2 connection is shared and
/// written from its own loop, so the engine finds that one by origin instead.
/// </summary>
internal sealed class ConnectionCapture
{
    private static readonly AsyncLocal<ConnectionCapture?> CurrentCapture = new();

    public static ConnectionCapture? Current
    {
        get => CurrentCapture.Value;
        set => CurrentCapture.Value = value;
    }

    public TrackedStream? Connection { get; set; }

    /// <summary>The certificate and protocol of the connection used; null for plain HTTP.</summary>
    public Pidge.Core.TlsDetails? Tls()
    {
        if (Connection?.Inner is not SslStream ssl)
        {
            return null;
        }

        try
        {
            return PeerCert.Describe(ssl);
        }
        catch (ObjectDisposedException)
        {
            // A shared HTTP/2 connection can close as its last response
            // arrives. The response stands without its padlock.
            return null;
        }
    }
}

/// <summary>A connection's stream, passed through untouched except to note which request is using it.</summary>
internal sealed class TrackedStream(Stream inner) : Stream
{
    public Stream Inner { get; } = inner;

    private void Mark()
    {
        if (ConnectionCapture.Current is { } capture)
        {
            capture.Connection = this;
        }
    }

    public override bool CanRead => Inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => Inner.CanWrite;
    public override bool CanTimeout => Inner.CanTimeout;

    public override int ReadTimeout
    {
        get => Inner.ReadTimeout;
        set => Inner.ReadTimeout = value;
    }

    public override int WriteTimeout
    {
        get => Inner.WriteTimeout;
        set => Inner.WriteTimeout = value;
    }

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Flush() => Inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => Inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => Inner.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Inner.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
    {
        Mark();
        Inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Mark();
        Inner.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Mark();
        return Inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Mark();
        return Inner.WriteAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await Inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
