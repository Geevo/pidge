using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Pidge.TestServer;

/// <summary>Whether the server asks the client to identify itself.</summary>
public enum ClientAuth
{
    /// <summary>Anyone may connect.</summary>
    None,

    /// <summary>The client must present a certificate signed by the test CA.</summary>
    Required,
}

/// <summary>
/// A deliberately small HTTP/1.1 server for the engine tests.
///
/// Tests must never touch the public internet, and the interesting cases here
/// (a body that stalls forever, a deliberately malformed JSON payload, a raw
/// multipart echo) are easier to produce by writing bytes than by configuring
/// a framework.
///
/// Disposing it shuts the listener down.
/// </summary>
public sealed class TestServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _scheme;
    private readonly Func<Stream, CancellationToken, Task<Stream?>> _wrap;
    private readonly Task _acceptLoop;
    private int _connections;

    private TestServer(string scheme, Func<Stream, CancellationToken, Task<Stream?>> wrap)
    {
        _scheme = scheme;
        _wrap = wrap;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Address = (IPEndPoint)_listener.LocalEndpoint;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Binds an ephemeral port on loopback and starts serving.</summary>
    public static Task<TestServer> StartAsync() =>
        Task.FromResult(new TestServer("http", (stream, _) => Task.FromResult<Stream?>(stream)));

    /// <summary>
    /// The same routes over TLS, with a certificate signed by <paramref name="ca"/>.
    ///
    /// The CA is generated per test, so a request only succeeds if the client
    /// was told to trust it — which is the point of the trust tests.
    /// </summary>
    public static Task<TestServer> StartTlsAsync(TestCa ca, ClientAuth clientAuth)
    {
        var serverCertificate = ca.IssueServer().WithKey();
        var context = SslStreamCertificateContext.Create(serverCertificate, null, offline: true);
        var root = X509CertificateLoader.LoadCertificate(ca.Der);

        async Task<Stream?> HandshakeAsync(Stream stream, CancellationToken cancellationToken)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            var options = new SslServerAuthenticationOptions
            {
                ServerCertificateContext = context,
                ClientCertificateRequired = clientAuth == ClientAuth.Required,
                RemoteCertificateValidationCallback = clientAuth == ClientAuth.Required
                    ? (_, certificate, _, _) => SignedBy(certificate, root)
                    : (_, _, _, _) => true,
            };
            try
            {
                await ssl.AuthenticateAsServerAsync(options, cancellationToken).ConfigureAwait(false);
                return ssl;
            }
            catch (Exception)
            {
                // A rejected handshake is a normal outcome here.
                await ssl.DisposeAsync().ConfigureAwait(false);
                return null;
            }
        }

        return Task.FromResult(new TestServer("https", HandshakeAsync));
    }

    /// <summary>Whether the client presented a certificate that chains to the test CA.</summary>
    private static bool SignedBy(X509Certificate? certificate, X509Certificate2 root)
    {
        if (certificate is null)
        {
            return false;
        }

        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        return chain.Build(leaf);
    }

    public IPEndPoint Address { get; }

    public string BaseUrl => $"{_scheme}://{Address}";

    /// <summary><c>server.Url("/status/404")</c></summary>
    public string Url(string path) => BaseUrl + path;

    /// <summary>Connections accepted so far. Useful for asserting a redirect chain.</summary>
    public int ConnectionCount => Volatile.Read(ref _connections);

    private async Task AcceptLoopAsync()
    {
        var token = _shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            Interlocked.Increment(ref _connections);
            _ = Task.Run(() => ServeAsync(client, token), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            try
            {
                var stream = await _wrap(client.GetStream(), cancellationToken).ConfigureAwait(false);
                if (stream is null)
                {
                    return;
                }
                await using (stream.ConfigureAwait(false))
                {
                    await Http.ServeConnectionAsync(stream, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // The client went away, or the server is shutting down.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        // The source is left undisposed: connections still being served hold its token.
    }
}
