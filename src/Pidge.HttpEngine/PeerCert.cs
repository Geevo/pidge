using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Pidge.Core;

namespace Pidge.HttpEngine;

/// <summary>
/// Turning the certificate the server presented into something readable.
///
/// This is deliberately about the leaf certificate rather than the chain: what
/// it is for, who signed it, when it stops being valid, and its fingerprint.
/// </summary>
internal static class PeerCert
{
    /// <summary>Describes a finished handshake.</summary>
    public static TlsDetails Describe(SslStream stream)
    {
        byte[]? der = null;
        try
        {
            der = stream.RemoteCertificate?.GetRawCertData();
        }
        catch (Exception err) when (err is CryptographicException or InvalidOperationException
                                       or ObjectDisposedException)
        {
        }

        return new TlsDetails
        {
            Protocol = ProtocolName(stream),
            Certificate = der is null ? null : Parse(der),
        };
    }

    private static string? ProtocolName(SslStream stream)
    {
        try
        {
            return ProtocolName(stream.SslProtocol);
        }
        catch (Exception err) when (err is InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>The enum's own names are not a thing to show anyone.</summary>
    internal static string ProtocolName(SslProtocols protocol) => protocol switch
    {
        SslProtocols.Tls13 => "TLS 1.3",
        SslProtocols.Tls12 => "TLS 1.2",
#pragma warning disable CS0618, SYSLIB0039 // Named, not negotiated: an old server still has to be described.
        SslProtocols.Tls11 => "TLS 1.1",
        SslProtocols.Tls => "TLS 1.0",
#pragma warning restore CS0618, SYSLIB0039
        _ => protocol.ToString(),
    };

    /// <summary>
    /// Null when the certificate will not parse. The TLS layer has already
    /// accepted the connection at this point, so a parse failure is a gap in what
    /// we can show, not a reason to fail the response.
    /// </summary>
    internal static PeerCertificate? Parse(byte[] der)
    {
        try
        {
            using var cert = X509CertificateLoader.LoadCertificate(der);

            var subject = NameToString(cert.SubjectName.RawData);
            var issuer = NameToString(cert.IssuerName.RawData);
            var notBefore = UnixSeconds(cert.NotBefore);
            var notAfter = UnixSeconds(cert.NotAfter);

            return new PeerCertificate
            {
                SelfSigned = subject == issuer,
                Subject = subject,
                Issuer = issuer,
                SubjectAltNames = AltNames(cert),
                NotBefore = Rfc3339(notBefore),
                NotAfter = Rfc3339(notAfter),
                Serial = FingerprintStyle(cert.SerialNumberBytes.Span),
                SignatureAlgorithm = SignatureAlgorithm(cert.SignatureAlgorithm.Value ?? ""),
                Sha256Fingerprint = FingerprintStyle(SHA256.HashData(der)),
                Expired = notAfter < DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
        }
        catch (Exception err) when (err is CryptographicException or AsnContentException or ArgumentException)
        {
            return null;
        }
    }

    private static long UnixSeconds(DateTime local) =>
        new DateTimeOffset(local.ToUniversalTime()).ToUnixTimeSeconds();

    // ---- Names --------------------------------------------------------------

    /// <summary>The short names everyone recognises, tried before the registry.</summary>
    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.Ordinal)
    {
        ["2.5.4.3"] = "CN",
        ["2.5.4.6"] = "C",
        ["2.5.4.7"] = "L",
        ["2.5.4.8"] = "ST",
        ["2.5.4.10"] = "O",
        ["2.5.4.11"] = "OU",
        ["0.9.2342.19200300.100.1.25"] = "DC",
        ["1.2.840.113549.1.9.1"] = "Email",
    };

    /// <summary>The rest of the X.500 attribute types, by their registered short names.</summary>
    private static readonly Dictionary<string, string> AttributeNames = new(StringComparer.Ordinal)
    {
        ["2.5.4.4"] = "surname",
        ["2.5.4.5"] = "serialNumber",
        ["2.5.4.9"] = "street",
        ["2.5.4.12"] = "title",
        ["2.5.4.15"] = "businessCategory",
        ["2.5.4.17"] = "postalCode",
        ["2.5.4.41"] = "name",
        ["2.5.4.42"] = "givenName",
        ["2.5.4.43"] = "initials",
        ["2.5.4.44"] = "generationQualifier",
        ["2.5.4.46"] = "dnQualifier",
        ["2.5.4.65"] = "pseudonym",
        ["0.9.2342.19200300.100.1.1"] = "uid",
    };

    /// <summary>
    /// <c>CN=localhost, O=Example</c>: relative names joined by commas, the
    /// attributes within one joined by <c>+</c>.
    /// </summary>
    internal static string NameToString(byte[] encoded)
    {
        try
        {
            var reader = new AsnReader(encoded, AsnEncodingRules.BER);
            var sequence = reader.ReadSequence();
            reader.ThrowIfNotEmpty();

            var rdns = new List<string>();
            while (sequence.HasData)
            {
                var set = sequence.ReadSetOf(skipSortOrderValidation: true);
                var attributes = new List<string>();
                while (set.HasData)
                {
                    var attribute = set.ReadSequence();
                    var oid = attribute.ReadObjectIdentifier();
                    var value = attribute.ReadEncodedValue();
                    attributes.Add($"{AttributeName(oid)}={AttributeValue(value)}");
                }
                rdns.Add(string.Join(" + ", attributes));
            }
            return string.Join(", ", rdns);
        }
        catch (Exception err) when (err is AsnContentException or CryptographicException or DecoderFallbackException
                                       or ArgumentException)
        {
            return "<X509Error: Invalid X.509 name>";
        }
    }

    private static string AttributeName(string oid) =>
        Abbreviations.TryGetValue(oid, out var abbreviation) ? abbreviation
        : AttributeNames.TryGetValue(oid, out var name) ? name
        : $"OID({oid})";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The string types as text; anything else as hex, which at least shows what is there.</summary>
    private static string AttributeValue(ReadOnlyMemory<byte> encoded)
    {
        var tag = Asn1Tag.Decode(encoded.Span, out _);
        AsnDecoder.ReadEncodedValue(encoded.Span, AsnEncodingRules.BER, out var offset, out var length, out _);
        var content = encoded.Span.Slice(offset, length);

        return tag.TagValue switch
        {
            // Numeric, Visible, Printable, General, ObjectDescriptor, Graphic,
            // T61, Videotex, UTF8 and IA5.
            18 or 26 or 19 or 27 or 7 or 25 or 20 or 21 or 12 or 22 => StrictUtf8.GetString(content),
            30 => Encoding.BigEndianUnicode.GetString(content),
            _ => Convert.ToHexString(content),
        };
    }

    // ---- Alternative names --------------------------------------------------

    private const string SubjectAltNameOid = "2.5.29.17";

    private static List<string> AltNames(X509Certificate2 cert)
    {
        var extension = cert.Extensions.FirstOrDefault(e => e.Oid?.Value == SubjectAltNameOid);
        if (extension is null)
        {
            return [];
        }

        try
        {
            var reader = new AsnReader(extension.RawData, AsnEncodingRules.BER);
            var names = reader.ReadSequence();
            var result = new List<string>();
            while (names.HasData)
            {
                result.Add(GeneralName(names));
            }
            return result;
        }
        catch (Exception err) when (err is AsnContentException or CryptographicException or DecoderFallbackException
                                       or ArgumentException)
        {
            return [];
        }
    }

    private static string GeneralName(AsnReader names)
    {
        var tag = names.PeekTag();
        if (tag.TagClass != TagClass.ContextSpecific)
        {
            var other = names.ReadEncodedValue();
            return $"Invalid({tag.TagValue}, {FingerprintStyle(other.Span)})";
        }

        switch (tag.TagValue)
        {
            case 1:
                return names.ReadCharacterString(UniversalTagNumber.IA5String, tag);
            case 2:
                return names.ReadCharacterString(UniversalTagNumber.IA5String, tag);
            case 6:
                return names.ReadCharacterString(UniversalTagNumber.IA5String, tag);
            case 7:
                return IpAddress(names.ReadOctetString(tag));
            case 0:
                {
                    var otherName = names.ReadSequence(tag);
                    var oid = otherName.ReadObjectIdentifier();
                    return $"OtherName({oid}, [...])";
                }
            case 3:
                names.ReadEncodedValue();
                return "X400Address(<unparsed>)";
            case 4:
                {
                    var directory = names.ReadSequence(tag);
                    return $"DirectoryName({NameToString(directory.ReadEncodedValue().ToArray())})";
                }
            case 5:
                names.ReadEncodedValue();
                return "EDIPartyName(<unparsed>)";
            case 8:
                return $"RegisteredID({names.ReadObjectIdentifier(tag)})";
            default:
                {
                    var unknown = names.ReadEncodedValue();
                    return $"Invalid({tag.TagValue}, {FingerprintStyle(unknown.Span)})";
                }
        }
    }

    internal static string IpAddress(byte[] bytes) => bytes.Length switch
    {
        4 or 16 => new IPAddress(bytes).ToString(),
        _ => FingerprintStyle(bytes),
    };

    // ---- Signature algorithm ------------------------------------------------

    /// <summary>The ones a certificate is actually signed with, by their registered names.</summary>
    private static readonly Dictionary<string, string> SignatureNames = new(StringComparer.Ordinal)
    {
        ["1.2.840.113549.1.1.1"] = "rsaEncryption",
        ["1.2.840.113549.1.1.5"] = "sha1WithRSAEncryption",
        ["1.2.840.113549.1.1.10"] = "rsassa-pss",
        ["1.2.840.113549.1.1.11"] = "sha256WithRSAEncryption",
        ["1.2.840.113549.1.1.12"] = "sha384WithRSAEncryption",
        ["1.2.840.113549.1.1.13"] = "sha512WithRSAEncryption",
        ["1.2.840.10045.4.3.2"] = "ecdsa-with-SHA256",
        ["1.2.840.10045.4.3.3"] = "ecdsa-with-SHA384",
        ["1.2.840.10045.4.3.4"] = "ecdsa-with-SHA512",
        ["1.3.101.112"] = "ed25519",
    };

    /// <summary>
    /// The OID's name when it is a known one, and the OID itself when it is
    /// not — which is more use than "unknown".
    /// </summary>
    internal static string SignatureAlgorithm(string oid) =>
        SignatureNames.TryGetValue(oid, out var name) ? $"{name} ({oid})" : oid;

    // ---- Formatting ---------------------------------------------------------

    /// <summary><c>AB:CD:EF…</c>, the way openssl and every browser print a fingerprint.</summary>
    internal static string FingerprintStyle(ReadOnlySpan<byte> bytes)
    {
        var output = new StringBuilder(bytes.Length * 3);
        for (var index = 0; index < bytes.Length; index++)
        {
            if (index > 0)
            {
                output.Append(':');
            }
            output.Append(bytes[index].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return output.ToString();
    }

    private static readonly long[] DaysInMonth = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    /// <summary>Seconds to RFC 3339, the same arithmetic the other platforms use.</summary>
    internal static string Rfc3339(long seconds)
    {
        var days = DivEuclid(seconds, 86_400);
        var timeOfDay = seconds - days * 86_400;
        var (hour, minute, second) = (timeOfDay / 3600, timeOfDay % 3600 / 60, timeOfDay % 60);

        long year = 1970;
        var remaining = days;
        while (true)
        {
            var length = IsLeap(year) ? 366 : 365;
            if (remaining < length)
            {
                break;
            }
            remaining -= length;
            year++;
        }

        var month = 1;
        for (var index = 0; index < DaysInMonth.Length; index++)
        {
            var length = DaysInMonth[index] + (index == 1 && IsLeap(year) ? 1 : 0);
            if (remaining < length)
            {
                break;
            }
            remaining -= length;
            month++;
        }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return string.Create(inv, $"{year:0000}-{month:00}-{remaining + 1:00}T{hour:00}:{minute:00}:{second:00}Z");
    }

    private static long DivEuclid(long value, long divisor)
    {
        var quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }

    private static bool IsLeap(long year) => (year % 4 == 0 && year % 100 != 0) || year % 400 == 0;
}
