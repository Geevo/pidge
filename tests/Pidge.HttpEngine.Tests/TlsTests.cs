using Pidge.Core;
using Pidge.TestServer;
using LocalServer = Pidge.TestServer.TestServer;

namespace Pidge.HttpEngine.Tests;

/*
 * TLS trust and client-certificate tests.
 *
 * Every certificate is generated for the run, so a request only succeeds
 * because of the settings under test — never because the machine happened to
 * already trust something.
 */
public class TlsTests
{
    private static HttpEngine Engine(TlsSettings tls) => new(new EngineConfig { Tls = tls });

    /// <summary>Writes bytes to a uniquely named temp file that lives as long as the guard.</summary>
    private sealed class TempFile : IDisposable
    {
        public TempFile(string name, byte[] bytes)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{name}{Guid.NewGuid():N}");
            File.WriteAllBytes(Path, bytes);
        }

        public TempFile(string name, string text)
            : this(name, System.Text.Encoding.UTF8.GetBytes(text))
        {
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<HttpResponse> GetAsync(HttpEngine engine, LocalServer server) =>
        await engine.ExecuteAsync(HttpRequest.Get(server.Url("/json")), new CancellationHandle());

    private static async Task<RequestError> FailsAsync(HttpEngine engine, LocalServer server) =>
        (await Assert.ThrowsAsync<RequestErrorException>(() => GetAsync(engine, server))).Error;

    private static RequestError FailsToBuild(TlsSettings tls) =>
        Assert.Throws<RequestErrorException>(() => Engine(tls)).Error;

    [Fact]
    public async Task AnUntrustedCertificateIsRejected()
    {
        var ca = new TestCa();
        await using var server = await LocalServer.StartTlsAsync(ca, ClientAuth.None);

        // Default settings: the OS trust store, which has never heard of this CA.
        using var engine = Engine(new TlsSettings());
        Assert.Equal(RequestErrorKind.Tls, (await FailsAsync(engine, server)).Kind);
    }

    [Fact]
    public async Task AddingACaMakesItsServerReachable()
    {
        var ca = new TestCa();
        await using var server = await LocalServer.StartTlsAsync(ca, ClientAuth.None);
        using var caFile = new TempFile("ca", ca.Pem);

        using var engine = Engine(new TlsSettings { ExtraCaFiles = [caFile.Path] });
        Assert.Equal(200, (await GetAsync(engine, server)).Status);
    }

    [Fact]
    public async Task AnAddedCaIsMergedWithTheSystemStoreNotSwappedForIt()
    {
        var ca = new TestCa();
        using var caFile = new TempFile("ca", ca.Pem);

        // Building succeeds with system roots on and an extra CA, which is the
        // combination that must not be turned into "trust only this one".
        using var engine = Engine(new TlsSettings { UseSystemRoots = true, ExtraCaFiles = [caFile.Path] });

        await using var server = await LocalServer.StartTlsAsync(ca, ClientAuth.None);
        Assert.Equal(200, (await GetAsync(engine, server)).Status);

        // A second, unrelated CA is still not trusted, so the merge did not simply
        // disable verification.
        var other = new TestCa();
        await using var otherServer = await LocalServer.StartTlsAsync(other, ClientAuth.None);
        Assert.Equal(RequestErrorKind.Tls, (await FailsAsync(engine, otherServer)).Kind);
    }

    [Fact]
    public async Task SystemRootsCanBeTurnedOffToTrustOnlyOneCa()
    {
        var ca = new TestCa();
        await using var server = await LocalServer.StartTlsAsync(ca, ClientAuth.None);
        using var caFile = new TempFile("ca", ca.Pem);

        using var engine = Engine(new TlsSettings { UseSystemRoots = false, ExtraCaFiles = [caFile.Path] });
        Assert.Equal(200, (await GetAsync(engine, server)).Status);
    }

    [Fact]
    public async Task AcceptingInvalidCertificatesBypassesVerification()
    {
        var ca = new TestCa();
        await using var server = await LocalServer.StartTlsAsync(ca, ClientAuth.None);

        using var engine = Engine(new TlsSettings { AcceptInvalidCerts = true });
        Assert.Equal(200, (await GetAsync(engine, server)).Status);
    }

    [Fact]
    public async Task AServerWantingAClientCertificateRefusesAClientWithoutOne()
    {
        var ca = new TestCa();
        await using var server = await LocalServer.StartTlsAsync(ca, ClientAuth.Required);
        using var caFile = new TempFile("ca", ca.Pem);

        using var engine = Engine(new TlsSettings { ExtraCaFiles = [caFile.Path] });
        var error = await FailsAsync(engine, server);

        // The server closes the connection during the handshake.
        Assert.True(
            error.Kind is RequestErrorKind.Tls or RequestErrorKind.ConnectionFailed or RequestErrorKind.BodyRead,
            $"unexpected kind: {error.Kind} ({error.Message})");
    }

    [Fact]
    public async Task APemClientCertificateSatisfiesMutualTls()
    {
        var ca = new TestCa();
        await using var server = await LocalServer.StartTlsAsync(ca, ClientAuth.Required);

        using var caFile = new TempFile("ca", ca.Pem);
        var client = ca.IssueClient("api-client test");
        using var identity = new TempFile("client", client.IdentityPem());

        using var engine = Engine(new TlsSettings
        {
            ExtraCaFiles = [caFile.Path],
            ClientIdentity = new ClientIdentitySettings { Path = identity.Path },
        });
        Assert.Equal(200, (await GetAsync(engine, server)).Status);
    }

    [Fact]
    public async Task APkcs12ClientCertificateSatisfiesMutualTls()
    {
        var ca = new TestCa();
        await using var server = await LocalServer.StartTlsAsync(ca, ClientAuth.Required);

        using var caFile = new TempFile("ca", ca.Pem);
        var client = ca.IssueClient("api-client test");
        // The shape Windows exports: one encrypted bundle, no separate key file.
        using var bundle = new TempFile("client", client.ToPkcs12("hunter2", ca));

        using var engine = Engine(new TlsSettings
        {
            ExtraCaFiles = [caFile.Path],
            ClientIdentity = new ClientIdentitySettings { Path = bundle.Path, Password = "hunter2" },
        });
        Assert.Equal(200, (await GetAsync(engine, server)).Status);
    }

    [Fact]
    public void AWrongPkcs12PasswordSaysSo()
    {
        var ca = new TestCa();
        var client = ca.IssueClient("api-client test");
        using var bundle = new TempFile("client", client.ToPkcs12("hunter2", ca));

        var error = FailsToBuild(new TlsSettings
        {
            ClientIdentity = new ClientIdentitySettings { Path = bundle.Path, Password = "wrong" },
        });

        Assert.Equal(RequestErrorKind.Tls, error.Kind);
        Assert.Contains("password", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingClientCertificateFileNamesThePath()
    {
        var error = FailsToBuild(new TlsSettings
        {
            ClientIdentity = new ClientIdentitySettings { Path = "/definitely/not/here.p12", Password = "x" },
        });

        Assert.Equal(RequestErrorKind.Io, error.Kind);
        Assert.Contains("/definitely/not/here.p12", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACertificateFileThatIsNotACertificateIsReportedClearly()
    {
        using var junk = new TempFile("junk", "this is not a certificate");

        var error = FailsToBuild(new TlsSettings { ExtraCaFiles = [junk.Path] });

        Assert.Equal(RequestErrorKind.Tls, error.Kind);
        Assert.Contains("certificate", error.Message.ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainHttpStillWorksWithTlsSettingsPresent()
    {
        var ca = new TestCa();
        using var caFile = new TempFile("ca", ca.Pem);
        await using var server = await LocalServer.StartAsync();

        using var engine = Engine(new TlsSettings { ExtraCaFiles = [caFile.Path] });
        Assert.Equal(200, (await GetAsync(engine, server)).Status);
    }

    [Fact]
    public async Task AResponseCarriesTheCertificateTheServerPresented()
    {
        var ca = new TestCa();
        using var caFile = new TempFile("ca", ca.Pem);
        await using var server = await LocalServer.StartTlsAsync(ca, ClientAuth.None);

        using var engine = Engine(new TlsSettings { ExtraCaFiles = [caFile.Path] });
        var response = await GetAsync(engine, server);

        var tls = response.Tls;
        Assert.NotNull(tls);
        Assert.Equal("TLS 1.3", tls.Protocol);

        var cert = tls.Certificate;
        Assert.NotNull(cert);
        Assert.Contains("localhost", cert.Subject, StringComparison.Ordinal);
        Assert.Contains("api-client test CA", cert.Issuer, StringComparison.Ordinal);
        // Signed by the CA, so it vouches for itself only if something went wrong.
        Assert.False(cert.SelfSigned);
        Assert.False(cert.Expired);

        // The names it is actually valid for, which is what the handshake checked.
        Assert.Contains("localhost", cert.SubjectAltNames);
        Assert.Contains("127.0.0.1", cert.SubjectAltNames);

        // 32 bytes as AB:CD:…
        Assert.Equal((32 * 3) - 1, cert.Sha256Fingerprint.Length);
        Assert.NotEmpty(cert.Serial);
        Assert.NotEmpty(cert.SignatureAlgorithm);
        Assert.True(string.CompareOrdinal(cert.NotAfter, cert.NotBefore) > 0);
    }

    [Fact]
    public async Task PlainHttpHasNoCertificateToShow()
    {
        await using var server = await LocalServer.StartAsync();

        using var engine = Engine(new TlsSettings());
        Assert.Null((await GetAsync(engine, server)).Tls);
    }
}
