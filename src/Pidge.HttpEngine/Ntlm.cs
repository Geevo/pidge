using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Pidge.HttpEngine;

/// <summary>
/// NTLM over HTTP: the three messages of MS-NLMP, carried by MS-NTHT.
///
/// The handshake is: a 401 offering NTLM, then a negotiate message, then the
/// server's challenge, then an authenticate message computed from it. NTLMv2
/// only — v1 has been a liability for two decades and no server that refuses
/// v2 should be humoured by a new client.
///
/// The arithmetic is pinned to the worked example in MS-NLMP §4.2.4, because a
/// server that dislikes the response says only 401, which tells you nothing
/// about which step was wrong.
/// </summary>
internal static class Ntlm
{
    internal static readonly byte[] Signature = "NTLMSSP\0"u8.ToArray();

    internal const uint NegotiateUnicode = 0x0000_0001;
    private const uint RequestTarget = 0x0000_0004;
    private const uint NegotiateNtlm = 0x0000_0200;
    private const uint NegotiateAlwaysSign = 0x0000_8000;
    private const uint NegotiateExtendedSessionSecurity = 0x0008_0000;
    private const uint Negotiate128 = 0x2000_0000;
    private const uint Negotiate56 = 0x8000_0000;

    /// <summary>What the client offers. Unicode throughout: OEM encoding is a museum piece.</summary>
    private const uint ClientFlags = NegotiateUnicode
        | RequestTarget
        | NegotiateNtlm
        | NegotiateAlwaysSign
        | NegotiateExtendedSessionSecurity
        | Negotiate128
        | Negotiate56;

    /// <summary>The server's half of the handshake, as far as the client needs it.</summary>
    internal sealed record Challenge(byte[] ServerChallenge, byte[] TargetInfo, uint Flags);

    /// <summary>Thrown with a sentence for the user when a challenge cannot be read.</summary>
    internal sealed class ChallengeException(string message) : Exception(message);

    /// <summary><c>NTLM &lt;base64&gt;</c> for the first leg: what the client can do, and nothing else.</summary>
    public static string NegotiateHeader(string domain, string workstation)
    {
        var domainBytes = Encoding.UTF8.GetBytes(Digest.AsciiUpper(domain));
        var workstationBytes = Encoding.UTF8.GetBytes(Digest.AsciiUpper(workstation));

        var message = new List<byte>(40 + domainBytes.Length + workstationBytes.Length);
        message.AddRange(Signature);
        AddU32(message, 1);
        AddU32(message, ClientFlags);

        // The payload sits after the fixed 32-byte header, domain first.
        const uint domainOffset = 32;
        var workstationOffset = domainOffset + (uint)domainBytes.Length;
        PushField(message, domainBytes.Length, domainOffset);
        PushField(message, workstationBytes.Length, workstationOffset);
        message.AddRange(domainBytes);
        message.AddRange(workstationBytes);

        return "NTLM " + Convert.ToBase64String(message.ToArray());
    }

    /// <summary>Reads the challenge out of a <c>WWW-Authenticate: NTLM &lt;base64&gt;</c> header.</summary>
    /// <exception cref="ChallengeException">The header carries no usable challenge.</exception>
    public static Challenge ParseChallenge(string header)
    {
        string? encoded = null;
        foreach (var scheme in header.Split(',').Select(part => part.Trim()))
        {
            if (scheme.StartsWith("NTLM ", StringComparison.Ordinal) || scheme.StartsWith("ntlm ", StringComparison.Ordinal))
            {
                encoded = scheme[5..];
                break;
            }
        }
        if (encoded is null)
        {
            throw new ChallengeException("The server's NTLM challenge carried no message.");
        }

        byte[] message;
        try
        {
            message = Convert.FromBase64String(encoded.Trim());
        }
        catch (FormatException)
        {
            throw new ChallengeException("The server's NTLM challenge was not valid base64.");
        }

        // Signature, type, target name fields, flags, challenge, reserved, target
        // info fields: 48 bytes before any payload.
        if (message.Length < 48 || !message.AsSpan(0, 8).SequenceEqual(Signature))
        {
            throw new ChallengeException("The server's NTLM challenge was malformed.");
        }
        if (ReadU32(message, 8) != 2)
        {
            throw new ChallengeException("The server sent the wrong kind of NTLM message.");
        }

        var flags = ReadU32(message, 20);
        var serverChallenge = message[24..32];

        var infoLength = (long)ReadU16(message, 40);
        var infoOffset = (long)ReadU32(message, 44);
        // A challenge without target info is legal; NTLMv2 just sends none back.
        var targetInfo = infoOffset + infoLength <= message.Length
            ? message.AsSpan((int)infoOffset, (int)infoLength).ToArray()
            : [];

        return new Challenge(serverChallenge, targetInfo, flags);
    }

    /// <summary><c>NTLM &lt;base64&gt;</c> for the third leg: the response the server checks.</summary>
    public static string AuthenticateHeader(
        Challenge challenge,
        string username,
        string password,
        string domain,
        string workstation,
        byte[] clientChallenge,
        ulong timestamp)
    {
        var ntResponse = Ntlmv2Response(
            username,
            password,
            domain,
            challenge.ServerChallenge,
            challenge.TargetInfo,
            clientChallenge,
            timestamp);

        // The LM slot: HMAC over both challenges, then the client's own. Servers
        // ignore it under NTLMv2, but a missing field is a malformed message.
        var lmResponse = HMACMD5.HashData(
                NtowfV2(username, password, domain),
                (byte[])[.. challenge.ServerChallenge, .. clientChallenge])
            .Concat(clientChallenge)
            .ToArray();

        var domainBytes = Utf16Le(domain);
        var userBytes = Utf16Le(username);
        var workstationBytes = Utf16Le(workstation);
        byte[][] parts = [lmResponse, ntResponse, domainBytes, userBytes, workstationBytes];

        // Fixed header: signature, type, six field descriptors, flags.
        const uint headerLength = 64;
        var offset = headerLength;
        var message = new List<byte>();
        message.AddRange(Signature);
        AddU32(message, 3);

        foreach (var part in parts)
        {
            PushField(message, part.Length, offset);
            offset += (uint)part.Length;
        }
        // No session key is exchanged: nothing here signs or seals anything.
        PushField(message, 0, offset);
        AddU32(message, (ClientFlags & challenge.Flags) | NegotiateUnicode);

        foreach (var part in parts)
        {
            message.AddRange(part);
        }

        return "NTLM " + Convert.ToBase64String(message.ToArray());
    }

    /// <summary>NTOWFv2: the key everything else is derived from.</summary>
    internal static byte[] NtowfV2(string username, string password, string domain)
    {
        var ntHash = Md4.Hash(Utf16Le(password));
        var identity = Utf16Le(username.ToUpperInvariant() + domain);
        return HMACMD5.HashData(ntHash, identity);
    }

    /// <summary>The NT response: a proof string followed by the blob it was computed over.</summary>
    internal static byte[] Ntlmv2Response(
        string username,
        string password,
        string domain,
        byte[] serverChallenge,
        byte[] targetInfo,
        byte[] clientChallenge,
        ulong timestamp)
    {
        var key = NtowfV2(username, password, domain);

        var blob = new List<byte>(32 + targetInfo.Length);
        blob.AddRange([0x01, 0x01, 0x00, 0x00]); // RespType, HiRespType
        blob.AddRange(new byte[4]); // Reserved
        var stamp = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(stamp, timestamp);
        blob.AddRange(stamp);
        blob.AddRange(clientChallenge);
        blob.AddRange(new byte[4]); // Reserved
        blob.AddRange(targetInfo);
        blob.AddRange(new byte[4]); // Reserved

        var blobBytes = blob.ToArray();
        var proof = HMACMD5.HashData(key, (byte[])[.. serverChallenge, .. blobBytes]);
        return [.. proof, .. blobBytes];
    }

    internal static byte[] Utf16Le(string value) => Encoding.Unicode.GetBytes(value);

    /// <summary>
    /// A length/maximum/offset triple, which is how every variable field is
    /// described in these messages.
    /// </summary>
    internal static void PushField(List<byte> message, int length, uint offset)
    {
        var len = (ushort)length;
        Span<byte> field = stackalloc byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(field, len);
        BinaryPrimitives.WriteUInt16LittleEndian(field[2..], len);
        BinaryPrimitives.WriteUInt32LittleEndian(field[4..], offset);
        message.AddRange(field);
    }

    internal static void AddU32(List<byte> message, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        message.AddRange(bytes);
    }

    internal static ushort ReadU16(byte[] bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));

    internal static uint ReadU32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));

    /// <summary>A fresh challenge per exchange, from the system's secure random source.</summary>
    public static byte[] ClientChallenge() => RandomNumberGenerator.GetBytes(8);

    /// <summary>Windows FILETIME: 100-nanosecond ticks since 1601.</summary>
    public static ulong Timestamp()
    {
        const ulong ticksToUnixEpoch = 116_444_736_000_000_000;
        var sinceEpoch = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
        return (ulong)Math.Max(0, sinceEpoch) + ticksToUnixEpoch;
    }
}

/// <summary>
/// MD4 (RFC 1320). Broken as a hash and kept only because the NT password
/// hash is defined in terms of it; nothing else here should reach for it.
/// </summary>
internal static class Md4
{
    public static byte[] Hash(byte[] data)
    {
        uint a0 = 0x67452301, b0 = 0xefcdab89, c0 = 0x98badcfe, d0 = 0x10325476;

        var paddedLength = (data.Length + 8 + 64) / 64 * 64;
        var message = new byte[paddedLength];
        data.CopyTo(message, 0);
        message[data.Length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(paddedLength - 8), (ulong)data.Length * 8);

        var x = new uint[16];
        for (var block = 0; block < paddedLength; block += 64)
        {
            for (var i = 0; i < 16; i++)
            {
                x[i] = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(block + i * 4));
            }

            uint a = a0, b = b0, c = c0, d = d0;

            static uint F(uint x, uint y, uint z) => (x & y) | (~x & z);
            static uint G(uint x, uint y, uint z) => (x & y) | (x & z) | (y & z);
            static uint H(uint x, uint y, uint z) => x ^ y ^ z;

            // Round 1.
            foreach (var k in (int[])[0, 4, 8, 12])
            {
                a = uint.RotateLeft(a + F(b, c, d) + x[k], 3);
                d = uint.RotateLeft(d + F(a, b, c) + x[k + 1], 7);
                c = uint.RotateLeft(c + F(d, a, b) + x[k + 2], 11);
                b = uint.RotateLeft(b + F(c, d, a) + x[k + 3], 19);
            }

            // Round 2.
            foreach (var k in (int[])[0, 1, 2, 3])
            {
                a = uint.RotateLeft(a + G(b, c, d) + x[k] + 0x5a827999, 3);
                d = uint.RotateLeft(d + G(a, b, c) + x[k + 4] + 0x5a827999, 5);
                c = uint.RotateLeft(c + G(d, a, b) + x[k + 8] + 0x5a827999, 9);
                b = uint.RotateLeft(b + G(c, d, a) + x[k + 12] + 0x5a827999, 13);
            }

            // Round 3.
            foreach (var k in (int[])[0, 2, 1, 3])
            {
                a = uint.RotateLeft(a + H(b, c, d) + x[k] + 0x6ed9eba1, 3);
                d = uint.RotateLeft(d + H(a, b, c) + x[k + 8] + 0x6ed9eba1, 9);
                c = uint.RotateLeft(c + H(d, a, b) + x[k + 4] + 0x6ed9eba1, 11);
                b = uint.RotateLeft(b + H(c, d, a) + x[k + 12] + 0x6ed9eba1, 15);
            }

            a0 += a;
            b0 += b;
            c0 += c;
            d0 += d;
        }

        var output = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(output, a0);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), b0);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), c0);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12), d0);
        return output;
    }
}
