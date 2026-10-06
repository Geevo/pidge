using System.Security.Cryptography;
using System.Text;

namespace Pidge.HttpEngine.Tests;

public class DigestTests
{
    [Fact]
    public void DigestsOverThePathAndQueryOnly()
    {
        Assert.Equal("/api/x?a=1", Digest.PathAndQuery("https://example.com/api/x?a=1"));
        Assert.Equal("/", Digest.PathAndQuery("https://example.com"));
    }

    [Fact]
    public void NamesTheSchemeAServerAskedFor()
    {
        Assert.Equal("Negotiate", Digest.SchemeName("Negotiate abcdef"));
        Assert.Equal("unknown", Digest.SchemeName(""));
    }

    /// <summary>The worked example from RFC 2617 §3.5, with its client nonce.</summary>
    [Fact]
    public void AnswersTheRfcExample()
    {
        var challenge = DigestChallenge.Parse(
            "Digest realm=\"testrealm@host.com\", qop=\"auth,auth-int\", "
            + "nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\", opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"");

        var answer = challenge.Respond("Mufasa", "Circle Of Life", "/dir/index.html", "GET", "0a4f113b");

        Assert.Contains("response=\"6629fae49393a05397450978507c4ef1\"", answer, StringComparison.Ordinal);
        Assert.Contains("qop=auth,", answer, StringComparison.Ordinal);
        Assert.Contains("nc=00000001", answer, StringComparison.Ordinal);
    }

    /// <summary>The SHA-512 core, started from the standard values, is SHA-512.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(111)]
    [InlineData(112)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(1000)]
    public void TheSha512CoreMatchesThePlatform(int length)
    {
        var data = new byte[length];
        for (var index = 0; index < length; index++)
        {
            data[index] = (byte)(index * 31);
        }

        Assert.Equal(SHA512.HashData(data), Sha512.Compute(data, Sha512.Sha512Iv));
    }

    /// <summary>FIPS 180-4's own example for SHA-512/256.</summary>
    [Fact]
    public void Sha512_256OfAbcIsTheStandardsAnswer() =>
        Assert.Equal(
            "53048e2681941ef99b2e29b76b4c7dabe4c2d0c634fc6d46e0e2f13107e7af23",
            Convert.ToHexStringLower(Sha512.Hash512_256(Encoding.ASCII.GetBytes("abc"))));
}
