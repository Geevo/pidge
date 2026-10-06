using System.Text.Json.Serialization;

namespace Pidge.Core;

/// <summary>
/// How the client establishes trust and identifies itself.
///
/// The defaults are what a developer expects without configuring anything: the
/// operating system's own trust store, no client certificate, and certificate
/// checking on.
/// </summary>
public sealed class TlsSettings
{
    /// <summary>
    /// Trust the OS store. Turning this off trusts only <see cref="ExtraCaFiles"/>.
    /// </summary>
    public bool UseSystemRoots { get; set; } = true;

    /// <summary>
    /// Extra CAs to trust, as paths to PEM bundles or single DER certificates.
    /// These are added to the system roots rather than replacing them.
    /// </summary>
    public List<string> ExtraCaFiles { get; set; } = [];

    /// <summary>A certificate to present when a server asks for one (mutual TLS).</summary>
    public ClientIdentitySettings? ClientIdentity { get; set; }

    /// <summary>
    /// Skips certificate verification entirely. For a development server with
    /// a self-signed certificate; adding its CA above is the better answer.
    /// </summary>
    public bool AcceptInvalidCerts { get; set; }

    /// <summary>
    /// True when nothing here changes the default behaviour, so the engine can
    /// skip loading files it does not need.
    /// </summary>
    [JsonIgnore]
    public bool IsDefault => Equals(new TlsSettings());

    public TlsSettings Clone() => new()
    {
        UseSystemRoots = UseSystemRoots,
        ExtraCaFiles = [.. ExtraCaFiles],
        ClientIdentity = ClientIdentity?.Clone(),
        AcceptInvalidCerts = AcceptInvalidCerts,
    };

    public override bool Equals(object? obj) =>
        obj is TlsSettings other
        && UseSystemRoots == other.UseSystemRoots
        && ExtraCaFiles.SequenceEqual(other.ExtraCaFiles, StringComparer.Ordinal)
        && Equals(ClientIdentity, other.ClientIdentity)
        && AcceptInvalidCerts == other.AcceptInvalidCerts;

    public override int GetHashCode() => HashCode.Combine(UseSystemRoots, ExtraCaFiles.Count, AcceptInvalidCerts);
}

/// <summary>A client certificate and its private key.</summary>
public sealed class ClientIdentitySettings
{
    /// <summary>
    /// A PEM file holding the certificate chain and key, or a PKCS#12
    /// (<c>.p12</c>/<c>.pfx</c>) bundle. The format is detected from the contents.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// Required for PKCS#12, which is always encrypted. An empty string is a
    /// legitimate PKCS#12 password and is not the same as null.
    /// </summary>
    public string? Password { get; set; }

    public ClientIdentitySettings Clone() => new() { Path = Path, Password = Password };

    public override bool Equals(object? obj) =>
        obj is ClientIdentitySettings other
        && string.Equals(Path, other.Path, StringComparison.Ordinal)
        && string.Equals(Password, other.Password, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Path, Password);
}

/// <summary>
/// What the connection turned out to be, reported back with the response.
/// Absent for plain HTTP. The certificate is the leaf the server presented.
/// </summary>
public sealed class TlsDetails
{
    /// <summary>e.g. "TLS 1.3". Null when the backend will not say.</summary>
    public string? Protocol { get; set; }

    /// <summary>
    /// Null when the certificate could not be parsed, which is not a reason
    /// to fail a response the TLS layer already accepted.
    /// </summary>
    public PeerCertificate? Certificate { get; set; }
}

/// <summary>The server's certificate, in the terms a person checking it would use.</summary>
public sealed class PeerCertificate
{
    public string Subject { get; set; } = "";
    public string Issuer { get; set; } = "";

    /// <summary>The names this certificate is actually valid for.</summary>
    public List<string> SubjectAltNames { get; set; } = [];

    /// <summary>RFC 3339, so the UI can format them in the local timezone.</summary>
    public string NotBefore { get; set; } = "";

    public string NotAfter { get; set; } = "";
    public string Serial { get; set; } = "";
    public string SignatureAlgorithm { get; set; } = "";

    /// <summary>Uppercase hex, colon separated, as every other tool prints it.</summary>
    public string Sha256Fingerprint { get; set; } = "";

    /// <summary>Against the clock when the response arrived.</summary>
    public bool Expired { get; set; }

    /// <summary>Subject equals issuer: nothing above it vouched for it.</summary>
    public bool SelfSigned { get; set; }
}
