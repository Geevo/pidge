using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Pidge.Core;

namespace Pidge.Storage;

/*
 * Passwords and tokens, encrypted inside the state file.
 *
 * The file stays one readable JSON document. Only the values that would let
 * someone else call your APIs are replaced, each by its own ciphertext, so a
 * copy of the file on its own — a backup, a synced dotfile, an attachment to
 * a bug report — gives none of them away.
 *
 * XChaCha20-Poly1305 rather than AES-GCM because every save encrypts every
 * secret afresh with a random nonce, and history multiplies how many there
 * are. A 192-bit nonce makes that safe for as long as the key lives.
 *
 * A value on disk is `enc:v1:` and the base64 of the 24-byte nonce, the
 * ciphertext, and the 16-byte tag, in that order.
 */
internal sealed class Cipher
{
    /// <summary>Marks a value as ciphertext. Anything without it is plain text.</summary>
    public const string Prefix = "enc:v1:";

    public const int KeyLength = 32;
    public const int NonceLength = 24;
    public const int TagLength = 16;

    private readonly byte[] _key;

    private Cipher(byte[] key) => _key = key;

    /// <summary>Whether this machine's crypto library can do the inner AEAD at all.</summary>
    public static bool IsSupported => ChaCha20Poly1305.IsSupported;

    /// <summary>A new random key, base64 encoded: KDE Wallet stores text, not bytes.</summary>
    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyLength));

    public static Cipher? FromKey(string encoded)
    {
        var bytes = DecodeBase64(encoded.Trim());
        return bytes is { Length: KeyLength } ? new Cipher(bytes) : null;
    }

    public string Seal(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var sealedBytes = Encrypt(nonce, Encoding.UTF8.GetBytes(plain), []);
        return Prefix + Convert.ToBase64String(sealedBytes);
    }

    /// <summary>The plain text, or null if this key did not seal it.</summary>
    public string? Open(byte[] sealedBytes)
    {
        var plain = Decrypt(sealedBytes, []);
        if (plain is null)
        {
            return null;
        }
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(plain);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>nonce ‖ ciphertext ‖ tag.</summary>
    internal byte[] Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plain, ReadOnlySpan<byte> associated)
    {
        var output = new byte[NonceLength + plain.Length + TagLength];
        nonce.CopyTo(output);
        Span<byte> subkey = stackalloc byte[KeyLength];
        Span<byte> innerNonce = stackalloc byte[12];
        Derive(nonce, subkey, innerNonce);
        using var aead = new ChaCha20Poly1305(subkey);
        aead.Encrypt(
            innerNonce,
            plain,
            output.AsSpan(NonceLength, plain.Length),
            output.AsSpan(NonceLength + plain.Length, TagLength),
            associated);
        CryptographicOperations.ZeroMemory(subkey);
        return output;
    }

    /// <summary>The inverse of <see cref="Encrypt"/>, or null when the tag does not match.</summary>
    internal byte[]? Decrypt(ReadOnlySpan<byte> sealedBytes, ReadOnlySpan<byte> associated)
    {
        if (sealedBytes.Length < NonceLength + TagLength)
        {
            return null;
        }
        var nonce = sealedBytes[..NonceLength];
        var body = sealedBytes[NonceLength..^TagLength];
        var tag = sealedBytes[^TagLength..];
        var plain = new byte[body.Length];
        Span<byte> subkey = stackalloc byte[KeyLength];
        Span<byte> innerNonce = stackalloc byte[12];
        Derive(nonce, subkey, innerNonce);
        try
        {
            using var aead = new ChaCha20Poly1305(subkey);
            aead.Decrypt(innerNonce, body, tag, plain, associated);
            return plain;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(subkey);
        }
    }

    /// <summary>
    /// XChaCha20: HChaCha20 over the first 16 bytes of the nonce gives the key
    /// for ordinary ChaCha20-Poly1305, whose 12-byte nonce is four zero bytes
    /// and the last 8 bytes of ours.
    /// </summary>
    private void Derive(ReadOnlySpan<byte> nonce, Span<byte> subkey, Span<byte> innerNonce)
    {
        HChaCha20(_key, nonce[..16], subkey);
        innerNonce[..4].Clear();
        nonce[16..NonceLength].CopyTo(innerNonce[4..]);
    }

    /// <summary>
    /// The ChaCha20 block function without the final addition, keeping the
    /// first and last rows of the state (draft-irtf-cfrg-xchacha, section 2.2).
    /// </summary>
    internal static void HChaCha20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce16, Span<byte> output)
    {
        Span<uint> s = stackalloc uint[16];
        s[0] = 0x61707865;
        s[1] = 0x3320646e;
        s[2] = 0x79622d32;
        s[3] = 0x6b206574;
        for (var i = 0; i < 8; i++)
        {
            s[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);
        }
        for (var i = 0; i < 4; i++)
        {
            s[12 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce16[(i * 4)..]);
        }

        for (var round = 0; round < 10; round++)
        {
            QuarterRound(s, 0, 4, 8, 12);
            QuarterRound(s, 1, 5, 9, 13);
            QuarterRound(s, 2, 6, 10, 14);
            QuarterRound(s, 3, 7, 11, 15);
            QuarterRound(s, 0, 5, 10, 15);
            QuarterRound(s, 1, 6, 11, 12);
            QuarterRound(s, 2, 7, 8, 13);
            QuarterRound(s, 3, 4, 9, 14);
        }

        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output[(i * 4)..], s[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(output[(16 + i * 4)..], s[12 + i]);
        }
        s.Clear();
    }

    private static void QuarterRound(Span<uint> s, int a, int b, int c, int d)
    {
        s[a] += s[b];
        s[d] = BitOperations.RotateLeft(s[d] ^ s[a], 16);
        s[c] += s[d];
        s[b] = BitOperations.RotateLeft(s[b] ^ s[c], 12);
        s[a] += s[b];
        s[d] = BitOperations.RotateLeft(s[d] ^ s[a], 8);
        s[c] += s[d];
        s[b] = BitOperations.RotateLeft(s[b] ^ s[c], 7);
    }

    /// <summary>
    /// The raw bytes, if <paramref name="value"/> is ciphertext. Something that
    /// merely starts with the prefix but does not decode is somebody's password.
    /// </summary>
    public static byte[]? Ciphertext(string value)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }
        var bytes = DecodeBase64(value[Prefix.Length..]);
        return bytes is not null && bytes.Length >= NonceLength + TagLength ? bytes : null;
    }

    /// <summary>
    /// Standard base64 with padding, and nothing else: no whitespace, no
    /// missing padding, no stray characters.
    /// </summary>
    internal static byte[]? DecodeBase64(string text)
    {
        if (text.Length % 4 != 0)
        {
            return null;
        }
        foreach (var c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))
            {
                return null;
            }
        }
        var buffer = new byte[text.Length / 4 * 3];
        return Convert.TryFromBase64String(text, buffer, out var written) ? buffer[..written] : null;
    }
}

/// <summary>What decrypting a loaded state found.</summary>
internal sealed class Opened
{
    /// <summary>Secrets that were in plain text, which the next save will encrypt.</summary>
    public int Plain { get; set; }

    /// <summary>Ciphertext the key could not open, now cleared.</summary>
    public int Unreadable { get; set; }

    /// <summary>Ciphertext left unopened because there was no key, by where it lives.</summary>
    public Dictionary<string, string> Kept { get; } = new(StringComparer.Ordinal);
}

internal static class SecretFields
{
    private enum Pass
    {
        Open,
        Seal,
    }

    /// <summary>
    /// Decrypts every secret in place. Without a key, ciphertext is taken out of
    /// the state, so it is never sent as a password, and handed back to be
    /// written again by <see cref="Seal"/>.
    /// </summary>
    public static Opened Open(AppState state, Cipher? cipher)
    {
        var opened = new Opened();

        ForEachSecret(state, Pass.Open, (at, value) =>
        {
            var sealedBytes = Cipher.Ciphertext(value);
            if (sealedBytes is null)
            {
                if (value.Length > 0)
                {
                    opened.Plain += 1;
                }
                return value;
            }
            if (cipher is null)
            {
                opened.Kept[at] = value;
                return "";
            }
            if (cipher.Open(sealedBytes) is { } plain)
            {
                return plain;
            }
            opened.Unreadable += 1;
            return "";
        });

        return opened;
    }

    /// <summary>
    /// Encrypts every secret in place, or leaves it as plain text without a key.
    ///
    /// A secret that is still empty where <paramref name="kept"/> has ciphertext
    /// for it gets that ciphertext back: it was cleared by <see cref="Open"/>,
    /// not by the user, and it is readable again as soon as the keyring is.
    /// </summary>
    public static void Seal(AppState state, Cipher? cipher, IReadOnlyDictionary<string, string> kept)
    {
        ForEachSecret(state, Pass.Seal, (at, value) =>
        {
            if (value.Length == 0)
            {
                return kept.TryGetValue(at, out var sealedValue) ? sealedValue : value;
            }
            return cipher is null ? value : cipher.Seal(value);
        });
    }

    /// <summary>
    /// Calls <paramref name="visit"/> with every secret in <paramref name="state"/>
    /// and a name for where it lives, and stores what it returns in its place.
    ///
    /// The names are built from ids, not positions, so they still point at the
    /// same value after history has grown or the tabs have been reordered.
    /// </summary>
    private static void ForEachSecret(AppState state, Pass pass, Func<string, string, string> visit)
    {
        if (state.Settings.Tls.ClientIdentity is { Password: { } password } identity)
        {
            identity.Password = visit("settings/tls/clientIdentity/password", password);
        }

        foreach (var saved in state.SavedRequests)
        {
            RequestSecrets(saved.Request, $"savedRequests/{saved.Id}", pass, visit);
        }
        foreach (var entry in state.History)
        {
            RequestSecrets(entry.Request, $"history/{entry.Id}", pass, visit);
        }
        foreach (var tab in state.Tabs)
        {
            RequestSecrets(tab.Request, $"tabs/{tab.Id}", pass, visit);
        }

        // Every variable, not only the ones that look secret: a variable is where
        // a token goes to be kept out of the requests that use it.
        foreach (var environment in state.Environments)
        {
            foreach (var variable in environment.Variables)
            {
                variable.Value = visit($"environments/{environment.Id}/{variable.Id}", variable.Value);
            }
        }
    }

    private static void RequestSecrets(HttpRequest request, string at, Pass pass, Func<string, string, string> visit)
    {
        var kind = Redact.AuthKind(request.Auth);
        foreach (var secret in Redact.AuthSecrets(request.Auth))
        {
            secret.Set(visit($"{at}/auth/{kind}/{secret.Field}", secret.Get()));
        }

        // Opening looks at every header, so a value still decrypts after its
        // header was renamed to something that does not look secret.
        foreach (var header in request.Headers)
        {
            if (pass == Pass.Open || Redact.IsSecretHeader(header.Name))
            {
                header.Value = visit($"{at}/headers/{header.Id}", header.Value);
            }
        }
    }
}
