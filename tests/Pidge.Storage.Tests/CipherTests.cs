using System.Text;

namespace Pidge.Storage.Tests;

public class CipherTests
{
    private static Cipher NewCipher() => Cipher.FromKey(Cipher.GenerateKey())!;

    [Fact]
    public void RoundTripsThroughCiphertext()
    {
        var cipher = NewCipher();
        var sealedValue = cipher.Seal("hunter2");

        Assert.StartsWith(Cipher.Prefix, sealedValue);
        Assert.DoesNotContain("hunter2", sealedValue);
        Assert.Equal("hunter2", cipher.Open(Cipher.Ciphertext(sealedValue)!));
    }

    [Fact]
    public void TheSameSecretSealsDifferentlyEachTime()
    {
        var cipher = NewCipher();
        Assert.NotEqual(cipher.Seal("hunter2"), cipher.Seal("hunter2"));
    }

    [Fact]
    public void AnotherKeyCannotOpenIt()
    {
        var ours = NewCipher();
        var theirs = NewCipher();
        var sealedValue = ours.Seal("hunter2");

        Assert.Null(theirs.Open(Cipher.Ciphertext(sealedValue)!));
    }

    [Fact]
    public void APasswordThatOnlyLooksLikeCiphertextIsAPassword()
    {
        Assert.Null(Cipher.Ciphertext("enc:v1:hunter2"));
        Assert.Null(Cipher.Ciphertext("enc:v1:aGk="));
    }

    [Fact]
    public void RefusesAKeyOfTheWrongSize()
    {
        Assert.Null(Cipher.FromKey(Convert.ToBase64String(new byte[16])));
        Assert.Null(Cipher.FromKey("not base64"));
    }

    [Fact]
    public void AcceptsAKeyWithSurroundingWhitespace()
    {
        Assert.NotNull(Cipher.FromKey(" " + Cipher.GenerateKey() + "\n"));
    }

    /// <summary>draft-irtf-cfrg-xchacha, section 2.2.1.</summary>
    [Fact]
    public void HChaCha20MatchesTheDraftsTestVector()
    {
        var key = Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        var nonce = Convert.FromHexString("000000090000004a0000000031415927");
        var output = new byte[32];

        Cipher.HChaCha20(key, nonce, output);

        Assert.Equal(
            "82413b4227b27bfed30e42508a877d73a0f9e4d58a74a853c12ec41326d3ecdc",
            Convert.ToHexStringLower(output));
    }

    /// <summary>draft-irtf-cfrg-xchacha, appendix A.3.1.</summary>
    [Fact]
    public void XChaCha20Poly1305MatchesTheDraftsTestVector()
    {
        var key = Convert.FromHexString("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f");
        var nonce = Convert.FromHexString("404142434445464748494a4b4c4d4e4f5051525354555657");
        var associated = Convert.FromHexString("50515253c0c1c2c3c4c5c6c7");
        var plain = Encoding.ASCII.GetBytes(
            "Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");
        var cipher = Cipher.FromKey(Convert.ToBase64String(key))!;

        var sealedBytes = cipher.Encrypt(nonce, plain, associated);

        var hex = Convert.ToHexStringLower(sealedBytes);
        Assert.StartsWith("404142434445464748494a4b4c4d4e4f5051525354555657", hex);
        Assert.StartsWith("bd6d179d3e83d43b9576579493c0e939", hex[48..]);
        Assert.EndsWith("c0875924c1c7987947deafd8780acf49", hex);
        Assert.Equal(24 + plain.Length + 16, sealedBytes.Length);
        Assert.Equal(plain, cipher.Decrypt(sealedBytes, associated));
        Assert.Null(cipher.Decrypt(sealedBytes, []));
    }
}
