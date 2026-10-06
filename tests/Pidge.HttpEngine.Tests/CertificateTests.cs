using Pidge.Core;

namespace Pidge.HttpEngine.Tests;

public class PeerCertTests
{
    [Fact]
    public void FormatsSecondsAsRfc3339()
    {
        Assert.Equal("1970-01-01T00:00:00Z", PeerCert.Rfc3339(0));
        Assert.Equal("2023-11-14T22:13:20Z", PeerCert.Rfc3339(1_700_000_000));
        // A leap day, which the month table only gets right by adding to it.
        Assert.Equal("2024-02-29T00:00:00Z", PeerCert.Rfc3339(1_709_164_800));
        Assert.Equal("2100-01-01T00:00:00Z", PeerCert.Rfc3339(4_102_444_800));
    }

    [Fact]
    public void PrintsBytesTheWayOpensslDoes()
    {
        Assert.Equal("0A:FF:10", PeerCert.FingerprintStyle([0x0a, 0xff, 0x10]));
        Assert.Equal("", PeerCert.FingerprintStyle([]));
    }

    [Fact]
    public void ReadsBothAddressFamilies()
    {
        Assert.Equal("127.0.0.1", PeerCert.IpAddress([127, 0, 0, 1]));
        Assert.Equal("::", PeerCert.IpAddress(new byte[16]));
    }
}

public class TlsConfigTests
{
    [Fact]
    public void RecognisesPemByItsHeader()
    {
        Assert.True(TlsConfig.LooksLikePem("-----BEGIN CERTIFICATE-----\nabc\n"u8.ToArray()));
        Assert.True(TlsConfig.LooksLikePem("# a comment first\n-----BEGIN PRIVATE KEY-----\n"u8.ToArray()));
        Assert.False(TlsConfig.LooksLikePem([0x30, 0x82, 0x0a, 0x00]));
        Assert.False(TlsConfig.LooksLikePem([]));
    }

    [Fact]
    public void WrapsDerIntoPemAt64Columns()
    {
        var pem = TlsConfig.PemBlock("CERTIFICATE", new byte[100]);
        var lines = pem.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("-----BEGIN CERTIFICATE-----", lines[0]);
        Assert.Equal("-----END CERTIFICATE-----", lines[^1]);
        Assert.All(lines[1..^1], line => Assert.True(line.Length <= 64));
        Assert.Equal(new byte[100], Convert.FromBase64String(string.Concat(lines[1..^1])));
    }

    [Fact]
    public void TrustingNothingAtAllIsRefused()
    {
        var error = Assert.Throws<RequestErrorException>(
            () => TlsConfig.Load(new TlsSettings { UseSystemRoots = false })).Error;

        Assert.Equal(RequestErrorKind.Tls, error.Kind);
        Assert.Contains("nothing would be trusted", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingCaFileNamesThePath()
    {
        var error = Assert.Throws<RequestErrorException>(
            () => TlsConfig.Load(new TlsSettings { ExtraCaFiles = ["/definitely/not/here.pem"] })).Error;

        Assert.Equal(RequestErrorKind.Io, error.Kind);
        Assert.Contains("/definitely/not/here.pem", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankPathsAreIgnoredRatherThanFailing() =>
        Assert.NotNull(TlsConfig.Load(new TlsSettings { ExtraCaFiles = ["   ", ""] }));
}
