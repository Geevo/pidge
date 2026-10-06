using Pidge.Core;

namespace Pidge.HttpEngine.Tests;

public class OAuth1Tests
{
    /// <summary>The worked example from RFC 5849 §3.4.1.1, base string and all.</summary>
    [Fact]
    public void BuildsTheBaseStringFromTheRfc()
    {
        var url = WebUrl.Parse("http://example.com/request?b5=%3D%253D&a3=a&c%40=&a2=r%20b");
        var body = new UrlEncodedBody { Entries = [KeyValueEntry.New("c2", ""), KeyValueEntry.New("a3", "2 q")] };
        (string, string)[] oauth =
        [
            ("oauth_consumer_key", "9djdj82h48djs9d2"),
            ("oauth_token", "kkk9d7dh3k39sjv7"),
            ("oauth_signature_method", "HMAC-SHA1"),
            ("oauth_timestamp", "137131201"),
            ("oauth_nonce", "7d8f3e4a"),
        ];

        Assert.Equal(
            "POST&http%3A%2F%2Fexample.com%2Frequest&a2%3Dr%2520b%26a3%3D2%2520q%26a3%3Da%26b5%3D%253D%25253D%26c%2540%3D%26c2%3D%26oauth_consumer_key%3D9djdj82h48djs9d2%26oauth_nonce%3D7d8f3e4a%26oauth_signature_method%3DHMAC-SHA1%26oauth_timestamp%3D137131201%26oauth_token%3Dkkk9d7dh3k39sjv7",
            OAuth1.BaseString("POST", url, body, oauth));
    }

    [Fact]
    public void DropsADefaultPortFromTheBaseUrl()
    {
        static string Parse(string raw) => OAuth1.BaseUrl(WebUrl.Parse(raw));

        Assert.Equal("http://example.com/Path", Parse("http://Example.com:80/Path"));
        Assert.Equal("https://example.com/p", Parse("https://example.com:443/p"));
        Assert.Equal("https://example.com:8443/p", Parse("https://example.com:8443/p?x=1"));
    }

    [Fact]
    public void EncodesWhatTheRfcSaysToEncode()
    {
        Assert.Equal("a-b_c.d~e", OAuth1.Encode("a-b_c.d~e"));
        Assert.Equal("r%20b", OAuth1.Encode("r b"));
        Assert.Equal("%3D", OAuth1.Encode("="));
    }

    [Fact]
    public void TheSigningKeyJoinsBothSecretsEncoded() =>
        Assert.Equal(
            "con%20sumer&tok%26en",
            OAuth1.SigningKey(new OAuth1Settings { ConsumerSecret = "con sumer", TokenSecret = "tok&en" }));

    /// <summary>No token yet is a legitimate state, and the key still ends with <c>&amp;</c>.</summary>
    [Fact]
    public void SignsWithNoTokenSecret() =>
        Assert.Equal("secret&", OAuth1.SigningKey(new OAuth1Settings { ConsumerSecret = "secret" }));
}

public class OAuth2Tests
{
    private static OAuth2Settings Settings() => new() { TokenUrl = "https://example.com/token", ClientId = "id" };

    [Fact]
    public void ACachedTokenIsReusedUntilItIsNearlyExpired()
    {
        var cache = new TokenCache();
        var key = OAuth2.CacheKey(Settings());

        cache.Put(key, new CachedToken("fresh", TokenCache.After(TimeSpan.FromSeconds(600))));
        Assert.Equal("fresh", cache.Get(key));

        cache.Put(key, new CachedToken("nearly-gone", TokenCache.After(TimeSpan.FromSeconds(5))));
        Assert.Null(cache.Get(key));
    }

    [Fact]
    public void ATokenWithNoExpiryIsNotKept()
    {
        var cache = new TokenCache();
        var key = OAuth2.CacheKey(Settings());
        cache.Put(key, new CachedToken("unknown-life", null));
        Assert.Null(cache.Get(key));
    }

    [Fact]
    public void TheKeyChangesWithAnythingThatChangesTheToken()
    {
        var baseKey = OAuth2.CacheKey(Settings());

        var otherScope = Settings();
        otherScope.Scope = "read:things";
        Assert.NotEqual(baseKey, OAuth2.CacheKey(otherScope));

        var otherClient = Settings();
        otherClient.ClientId = "someone-else";
        Assert.NotEqual(baseKey, OAuth2.CacheKey(otherClient));
    }
}
