using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pidge.TestServer;

/*
 * Throwaway certificates, generated per test run.
 *
 * Nothing is committed and nothing is read from the machine's own trust store,
 * so the TLS tests prove that our settings did the work rather than that the
 * host happened to be configured a certain way.
 */

/// <summary>A certificate authority that can sign server and client certificates.</summary>
public sealed class TestCa
{
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";

    private readonly X509Certificate2 _certificate;

    // Kept for the life of the CA: the certificate signs with it.
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public TestCa()
    {
        var request = new CertificateRequest(Name("api-client test CA"), _key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature,
            true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        _certificate = request.CreateSelfSigned(NotBefore, NotAfter);
        Der = _certificate.RawData;
        Pem = _certificate.ExportCertificatePem() + "\n";
    }

    /// <summary>The CA certificate as PEM, for pointing <c>ExtraCaFiles</c> at.</summary>
    public string Pem { get; }

    public byte[] Der { get; }

    internal X509Certificate2 Certificate => _certificate;

    private static DateTimeOffset NotBefore => DateTimeOffset.UtcNow.AddDays(-1);

    private static DateTimeOffset NotAfter => DateTimeOffset.UtcNow.AddYears(10);

    /// <summary>Signs a certificate valid for <c>127.0.0.1</c> and <c>localhost</c>.</summary>
    public TestCert IssueServer()
    {
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        return Issue("localhost", ServerAuthentication, names.Build());
    }

    /// <summary>Signs a certificate for a client to present.</summary>
    public TestCert IssueClient(string commonName) => Issue(commonName, ClientAuthentication, null);

    private TestCert Issue(string commonName, string usage, X509Extension? altNames)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(Name(commonName), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(_certificate, true, false));
        if (altNames is not null)
        {
            request.CertificateExtensions.Add(altNames);
        }

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var certificate = request.Create(_certificate, NotBefore, NotAfter.AddDays(-1), serial);

        return new TestCert(
            certificate.RawData,
            key.ExportPkcs8PrivateKey(),
            certificate.ExportCertificatePem() + "\n",
            key.ExportPkcs8PrivateKeyPem() + "\n");
    }

    private static X500DistinguishedName Name(string commonName)
    {
        var builder = new X500DistinguishedNameBuilder();
        builder.AddCommonName(commonName);
        return builder.Build();
    }
}

/// <summary>A signed leaf certificate and its key.</summary>
public sealed class TestCert(byte[] certDer, byte[] keyDer, string certPem, string keyPem)
{
    public string CertPem { get; } = certPem;

    /// <summary>PKCS#8.</summary>
    public string KeyPem { get; } = keyPem;

    public byte[] CertDer { get; } = certDer;

    public byte[] KeyDer { get; } = keyDer;

    /// <summary>Certificate and key in one PEM, key first.</summary>
    public string IdentityPem() => KeyPem + CertPem;

    /// <summary>
    /// The same material as an encrypted PKCS#12 bundle, the way Windows
    /// exports a client certificate.
    /// </summary>
    public byte[] ToPkcs12(string password, TestCa ca)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(KeyDer, out _);
        using var leaf = X509CertificateLoader.LoadCertificate(CertDer);
        using var certificate = leaf.CopyWithPrivateKey(key);
        using var root = X509CertificateLoader.LoadCertificate(ca.Der);
        var collection = new X509Certificate2Collection { certificate, root };
        return collection.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password)
            ?? throw new CryptographicException("The bundle could not be exported.");
    }

    /// <summary>The certificate with its private key attached, in a form TLS can use on every platform.</summary>
    internal X509Certificate2 WithKey()
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(KeyDer, out _);
        using var certificate = X509CertificateLoader.LoadCertificate(CertDer);
        using var withKey = certificate.CopyWithPrivateKey(key);

        // An ephemeral key cannot be used by every platform's TLS stack, so
        // it is round-tripped through PKCS#12 into one that can.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }
}
