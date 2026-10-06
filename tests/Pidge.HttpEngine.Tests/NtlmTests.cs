using System.Text;

namespace Pidge.HttpEngine.Tests;

public class NtlmTests
{
    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>
    /// MS-NLMP §4.2.4.1.1: User / Domain / Password gives this key. Everything
    /// else is derived from it, so a wrong answer starts here.
    /// </summary>
    [Fact]
    public void DerivesTheKeyFromTheSpecsExample() =>
        Assert.Equal("0c868a403bfd7a93a3001ef22ef02e3f", Hex(Ntlm.NtowfV2("User", "Password", "Domain")));

    /// <summary>
    /// The same example, upper-cased: the user name is upper-cased before
    /// hashing and the domain is not, which is easy to get backwards.
    /// </summary>
    [Fact]
    public void UpperCasesTheUserButNotTheDomain()
    {
        var mixed = Ntlm.NtowfV2("user", "Password", "Domain");
        var upper = Ntlm.NtowfV2("USER", "Password", "Domain");
        Assert.Equal(Hex(mixed), Hex(upper));

        var otherDomain = Ntlm.NtowfV2("User", "Password", "DOMAIN");
        Assert.NotEqual(Hex(otherDomain), Hex(upper));
    }

    [Fact]
    public void WritesANegotiateMessageTheServerCanRead()
    {
        var header = Ntlm.NegotiateHeader("domain", "workstation");
        Assert.StartsWith("NTLM ", header, StringComparison.Ordinal);
        var message = Convert.FromBase64String(header["NTLM ".Length..]);

        Assert.Equal(Ntlm.Signature, message[0..8]);
        Assert.Equal(1u, Ntlm.ReadU32(message, 8));
        Assert.Equal(Ntlm.NegotiateUnicode, Ntlm.ReadU32(message, 12) & Ntlm.NegotiateUnicode);

        // The domain is upper-cased and where the field says it is.
        var length = Ntlm.ReadU16(message, 16);
        var offset = (int)Ntlm.ReadU32(message, 20);
        Assert.Equal("DOMAIN"u8.ToArray(), message[offset..(offset + length)]);
    }

    [Fact]
    public void ReadsAChallengeBackOutOfAHeader()
    {
        // A minimal type 2: header, challenge at 24, target info at 48.
        var message = new List<byte>();
        message.AddRange(Ntlm.Signature);
        Ntlm.AddU32(message, 2);
        Ntlm.PushField(message, 0, 48); // target name
        Ntlm.AddU32(message, Ntlm.NegotiateUnicode);
        message.AddRange([1, 2, 3, 4, 5, 6, 7, 8]); // server challenge
        message.AddRange(new byte[8]); // reserved
        Ntlm.PushField(message, 4, 48); // target info
        message.AddRange([0xaa, 0xbb, 0xcc, 0xdd]);

        var encoded = Convert.ToBase64String(message.ToArray());
        var challenge = Ntlm.ParseChallenge($"NTLM {encoded}");

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, challenge.ServerChallenge);
        Assert.Equal(new byte[] { 0xaa, 0xbb, 0xcc, 0xdd }, challenge.TargetInfo);
    }

    [Fact]
    public void FindsNtlmAmongTheSchemesAServerOffers()
    {
        var message = new List<byte>();
        message.AddRange(Ntlm.Signature);
        Ntlm.AddU32(message, 2);
        message.AddRange(new byte[40]);
        var encoded = Convert.ToBase64String(message.ToArray());

        var header = $"Negotiate, NTLM {encoded}, Basic realm=\"x\"";
        Assert.NotNull(Ntlm.ParseChallenge(header));
    }

    [Fact]
    public void RefusesAChallengeThatIsNotOne()
    {
        Assert.Throws<Ntlm.ChallengeException>(() => Ntlm.ParseChallenge("NTLM"));
        Assert.Throws<Ntlm.ChallengeException>(() => Ntlm.ParseChallenge("Negotiate abcdef"));
        Assert.Throws<Ntlm.ChallengeException>(() => Ntlm.ParseChallenge("NTLM not-base64!!"));
    }

    /// <summary>
    /// The response carries the proof string and then the blob it was computed
    /// over, so the server can recompute it without the client's state.
    /// </summary>
    [Fact]
    public void PacksTheProofAndTheBlobTogether()
    {
        byte[] targetInfo = [0x02, 0x00, 0x00, 0x00];
        var clientChallenge = Enumerable.Repeat((byte)0xaa, 8).ToArray();
        var response = Ntlm.Ntlmv2Response(
            "User",
            "Password",
            "Domain",
            [0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef],
            targetInfo,
            clientChallenge,
            0);

        Assert.Equal(16 + 32 + targetInfo.Length, response.Length);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x00, 0x00 }, response[16..20]);
        Assert.Equal(new byte[8], response[24..32]);
        Assert.Equal(clientChallenge, response[32..40]);
    }

    /// <summary>The test suite from RFC 1320 §A.5.</summary>
    [Theory]
    [InlineData("", "31d6cfe0d16ae931b73c59d7e0c089c0")]
    [InlineData("a", "bde52cb31de33e46245e05fbdbd6fb24")]
    [InlineData("abc", "a448017aaf21d8525fc10ae87aa6729d")]
    [InlineData("message digest", "d9130a8164549fe818874806e1c7014b")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "d79e1c308aa5bbcdeea8ed63df412da9")]
    [InlineData(
        "12345678901234567890123456789012345678901234567890123456789012345678901234567890",
        "e33b4ddc9c38f2199c3e7b164fcc0536")]
    public void Md4MatchesTheRfc(string input, string expected) =>
        Assert.Equal(expected, Hex(Md4.Hash(Encoding.ASCII.GetBytes(input))));
}
