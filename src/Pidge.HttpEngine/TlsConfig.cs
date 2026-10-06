using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Pidge.Core;

namespace Pidge.HttpEngine;

/// <summary>
/// Trust and identity.
///
/// Out of the box the client trusts whatever the operating system trusts: the
/// Windows certificate store, the macOS keychain, or the system CA bundle on
/// Linux. This handles the two things the OS store cannot do for us — trusting
/// an extra CA, and presenting a client certificate.
/// </summary>
internal sealed class TlsConfig
{
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    private TlsConfig(bool isDefault, bool acceptInvalid, bool useSystemRoots, X509Certificate2Collection extraRoots)
    {
        IsDefault = isDefault;
        AcceptInvalid = acceptInvalid;
        UseSystemRoots = useSystemRoots;
        ExtraRoots = extraRoots;
    }

    /// <summary>Nothing configured: the platform's own verification, untouched.</summary>
    public bool IsDefault { get; }

    public bool AcceptInvalid { get; }

    public bool UseSystemRoots { get; }

    public X509Certificate2Collection ExtraRoots { get; }

    public X509Certificate2? ClientCertificate { get; private set; }

    public X509Certificate2Collection ClientChain { get; } = [];

    /// <summary>Reads every file the settings name, so a bad one is reported before anything is sent.</summary>
    /// <exception cref="RequestErrorException">A file is missing or is not what it should be.</exception>
    public static TlsConfig Load(TlsSettings settings)
    {
        var extraRoots = new X509Certificate2Collection();
        var named = 0;
        CryptographicException? unreadable = null;
        foreach (var path in settings.ExtraCaFiles.Where(path => path.Trim().Length > 0))
        {
            named++;
            try
            {
                extraRoots.AddRange(LoadCaFile(path.Trim()));
            }
            catch (CryptographicException err)
            {
                // A DER file is only checked once everything else is loaded, so
                // a missing client certificate is still the error reported first.
                unreadable ??= err;
            }
        }

        if (!settings.UseSystemRoots && named == 0)
        {
            throw new RequestError(
                    RequestErrorKind.Tls,
                    "System certificates are turned off and no CA file was added, so nothing would be trusted.")
                .AsException();
        }

        var config = new TlsConfig(settings.IsDefault, settings.AcceptInvalidCerts, settings.UseSystemRoots, extraRoots);

        if (settings.ClientIdentity is { } identity)
        {
            config.LoadIdentity(identity);
        }

        if (unreadable is not null)
        {
            throw new RequestError(
                    RequestErrorKind.Tls,
                    "Could not apply the certificate settings. Check that each file is a valid certificate.")
                .WithDetail(Errors.Chain(unreadable))
                .AsException();
        }

        return config;
    }

    /// <summary>The options each new connection is opened with.</summary>
    public SslClientAuthenticationOptions ClientOptions()
    {
        var options = new SslClientAuthenticationOptions();
        if (!IsDefault)
        {
            options.RemoteCertificateValidationCallback = Validate;
        }
        if (ClientCertificate is not null)
        {
            options.ClientCertificateContext =
                SslStreamCertificateContext.Create(ClientCertificate, ClientChain, offline: true);
        }
        return options;
    }

    /// <summary>
    /// Extra CAs are merged with the OS store, not swapped for it: a corporate
    /// root should not cost you the ability to reach the internet. With the
    /// system roots off, only what was named is trusted, which is useful for
    /// talking to one internal host and nothing else.
    /// </summary>
    private bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (AcceptInvalid)
        {
            return true;
        }

        if ((errors & (SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
        {
            return false;
        }

        if (UseSystemRoots && errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (certificate is null || ExtraRoots.Count == 0)
        {
            return false;
        }

        var leaf = certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());

        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.AddRange(ExtraRoots);
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        custom.ChainPolicy.DisableCertificateDownloads = true;
        custom.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthentication));
        if (chain is not null)
        {
            // Whatever intermediates the server sent along.
            foreach (var element in chain.ChainElements)
            {
                custom.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        return custom.Build(leaf);
    }

    /// <summary>
    /// Reads one CA file. PEM bundles may hold several certificates; DER holds
    /// one, and throws <see cref="CryptographicException"/> if it will not parse.
    /// </summary>
    private static X509Certificate2Collection LoadCaFile(string path)
    {
        var bytes = ReadFile(path, "certificate");

        if (LooksLikePem(bytes))
        {
            try
            {
                var bundle = new X509Certificate2Collection();
                bundle.ImportFromPem(Encoding.UTF8.GetString(bytes));
                return bundle;
            }
            catch (Exception err) when (err is CryptographicException or ArgumentException)
            {
                throw new RequestError(RequestErrorKind.Tls, $"`{path}` is not a readable PEM certificate.")
                    .WithDetail(Errors.Chain(err))
                    .AsException();
            }
        }

        return [X509CertificateLoader.LoadCertificate(bytes)];
    }

    /// <summary>Reads a client certificate, from PEM or from a PKCS#12 bundle.</summary>
    private void LoadIdentity(ClientIdentitySettings settings)
    {
        var path = settings.Path.Trim();
        if (path.Length == 0)
        {
            throw new RequestError(RequestErrorKind.Tls, "No client certificate file was given.").AsException();
        }

        var bytes = ReadFile(path, "client certificate");

        if (LooksLikePem(bytes))
        {
            LoadPemIdentity(bytes, path);
        }
        else
        {
            // Windows exports .pfx, so the bundle is opened here rather than
            // asking anyone to run openssl first.
            LoadPkcs12Identity(bytes, settings.Password ?? "", path);
        }
    }

    private void LoadPemIdentity(byte[] bytes, string path)
    {
        var text = Encoding.UTF8.GetString(bytes);
        try
        {
            using var fromPem = X509Certificate2.CreateFromPem(text, text);
            ClientCertificate = Persisted(fromPem);

            var all = new X509Certificate2Collection();
            all.ImportFromPem(text);
            foreach (var cert in all)
            {
                if (cert.Thumbprint != fromPem.Thumbprint)
                {
                    ClientChain.Add(cert);
                }
            }
        }
        catch (Exception err) when (err is CryptographicException or ArgumentException)
        {
            throw new RequestError(
                    RequestErrorKind.Tls,
                    $"`{path}` did not contain a usable certificate and private key.")
                .WithDetail(Errors.Chain(err))
                .AsException();
        }
    }

    /// <summary>
    /// Windows cannot present a key that only lives in memory, which is what a
    /// PEM key becomes; a round trip through PKCS#12 gives it one it can use.
    /// </summary>
    private static X509Certificate2 Persisted(X509Certificate2 certificate)
    {
        var exported = certificate.Export(X509ContentType.Pkcs12);
        return X509CertificateLoader.LoadPkcs12(exported, null);
    }

    private void LoadPkcs12Identity(byte[] bytes, string password, string path)
    {
        X509Certificate2Collection store;
        try
        {
            store = X509CertificateLoader.LoadPkcs12Collection(bytes, password);
        }
        catch (CryptographicException err)
        {
            // A wrong password and a corrupt file fail in the same place, and the
            // first is overwhelmingly more likely, so say so.
            throw new RequestError(
                    RequestErrorKind.Tls,
                    $"Could not open `{path}`. Check the password — a PKCS#12 bundle is always encrypted.")
                .WithDetail(Errors.Chain(err))
                .AsException();
        }

        var leaf = store.FirstOrDefault(cert => cert.HasPrivateKey)
            ?? throw new RequestError(
                    RequestErrorKind.Tls,
                    $"`{path}` has no private key, so it cannot be used as a client certificate.")
                .AsException();

        ClientCertificate = leaf;
        foreach (var cert in store)
        {
            if (!ReferenceEquals(cert, leaf))
            {
                ClientChain.Add(cert);
            }
        }
    }

    /// <summary>DER to PEM. Base64 at 64 columns between the usual markers.</summary>
    internal static string PemBlock(string label, byte[] der)
    {
        var encoded = Convert.ToBase64String(der);
        var output = new StringBuilder($"-----BEGIN {label}-----\n");
        for (var start = 0; start < encoded.Length; start += 64)
        {
            output.Append(encoded, start, Math.Min(64, encoded.Length - start)).Append('\n');
        }
        output.Append($"-----END {label}-----\n");
        return output.ToString();
    }

    /// <summary>
    /// PEM is text and always carries a header; PKCS#12 and DER start with a
    /// DER sequence tag (0x30) and are not valid UTF-8 in general.
    /// </summary>
    internal static bool LooksLikePem(byte[] bytes)
    {
        ReadOnlySpan<byte> marker = "-----BEGIN "u8;
        var windows = bytes.Length - marker.Length + 1;
        for (var start = 0; start < Math.Min(windows, 4096); start++)
        {
            if (bytes.AsSpan(start, marker.Length).SequenceEqual(marker))
            {
                return true;
            }
        }
        return false;
    }

    private static byte[] ReadFile(string path, string what)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception err) when (err is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            throw new RequestError(RequestErrorKind.Io, $"Could not read the {what} at `{path}`.")
                .WithDetail(err.Message)
                .AsException();
        }
    }
}
