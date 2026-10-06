using Pidge.Core;

namespace Pidge.Core.Tests;

/// <summary>Expected values are what the URL Standard's parser makes of the same input.</summary>
public class WebUrlTests
{
    [Theory]
    [InlineData("http://example.com", "http://example.com/")]
    [InlineData("HTTP://EXAMPLE.com:80/a/../b?x=1#f", "http://example.com/b?x=1#f")]
    [InlineData("https://example.com:443", "https://example.com/")]
    [InlineData("https://example.com:0443/", "https://example.com/")]
    [InlineData("http://localhost:3000/test", "http://localhost:3000/test")]
    [InlineData("http://[::1]:8080/", "http://[::1]:8080/")]
    [InlineData("http://[0:0:0:0:0:0:0:1]/", "http://[::1]/")]
    [InlineData("http://[2001:db8:0:0:1:0:0:1]/", "http://[2001:db8::1:0:0:1]/")]
    [InlineData("http://[::ffff:192.168.0.1]/", "http://[::ffff:c0a8:1]/")]
    [InlineData("http://0x7f.1/", "http://127.0.0.1/")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080/")]
    [InlineData("http://example.com/a b?c d#e f", "http://example.com/a%20b?c%20d#e%20f")]
    [InlineData("http://example.com/?q='x'", "http://example.com/?q=%27x%27")]
    [InlineData("http://user:pa ss@host/", "http://user:pa%20ss@host/")]
    [InlineData("http://user@host/", "http://user@host/")]
    [InlineData("http://münchen.de/", "http://xn--mnchen-3ya.de/")]
    [InlineData("http://example.com/%7Bx%7D/{y}", "http://example.com/%7Bx%7D/%7By%7D")]
    [InlineData("http:\\\\example.com\\a", "http://example.com/a")]
    [InlineData("http://example.com/a/./b/%2e%2E/c", "http://example.com/a/c")]
    [InlineData("http://example.com/..", "http://example.com/")]
    [InlineData("http://example.com/a/b/..", "http://example.com/a/")]
    [InlineData("http://example.com?x", "http://example.com/?x")]
    [InlineData("http://example.com#top", "http://example.com/#top")]
    [InlineData("  http://example.com/\tpath\n ", "http://example.com/path")]
    [InlineData("http://example.com/é", "http://example.com/%C3%A9")]
    [InlineData("http://example.com/?q=é&r=%20", "http://example.com/?q=%C3%A9&r=%20")]
    [InlineData("http://example.com/a^b|c", "http://example.com/a^b|c")]
    [InlineData("http://example.com:/x", "http://example.com/x")]
    [InlineData("http://user:@host/", "http://user@host/")]
    [InlineData("http://:@host/", "http://host/")]
    [InlineData("http://%41.com/", "http://a.com/")]
    public void SerializesAsTheUrlCrateDoes(string input, string expected) =>
        Assert.Equal(expected, WebUrl.Parse(input).ToString());

    [Theory]
    [InlineData("http://example.com:99999", "invalid port number")]
    [InlineData("http://example.com:8a", "invalid port number")]
    [InlineData("http://a b/", "invalid international domain name")]
    [InlineData("http://a|b/", "invalid international domain name")]
    [InlineData("http://exa%00mple.com/", "invalid international domain name")]
    [InlineData("http://xn--:65536/", "invalid international domain name")]
    [InlineData("http://\u00AD.com:abc/", "invalid port number")]
    [InlineData("http://", "empty host")]
    [InlineData("http://user@/", "empty host")]
    [InlineData("http://[::1/", "invalid IPv6 address")]
    [InlineData("http://256.0.0.1/", "invalid IPv4 address")]
    [InlineData("example.com", "relative URL without a base")]
    public void RefusesWhatTheUrlCrateRefuses(string input, string error)
    {
        Assert.False(WebUrl.TryParse(input, out _, out var message));
        Assert.Equal(error, message);
    }

    [Fact]
    public void QueryPairsDecodeAsAForm()
    {
        var url = WebUrl.Parse("http://x/?a=1&b=two+words&c=%26&&d");
        Assert.Equal([("a", "1"), ("b", "two words"), ("c", "&"), ("d", "")], url.QueryPairs());
    }

    [Fact]
    public void SetQueryEscapesOnlyWhatCannotAppear()
    {
        var url = WebUrl.Parse("http://x/p");
        url.SetQuery("a=b c&d=%2F&e=#");
        Assert.Equal("http://x/p?a=b%20c&d=%2F&e=%23", url.ToString());
    }

    [Fact]
    public void ExposesItsParts()
    {
        var url = WebUrl.Parse("https://u:p@Example.com:8443/a/b?q=1#f");
        Assert.Equal("https", url.Scheme);
        Assert.Equal("u", url.Username);
        Assert.Equal("p", url.Password);
        Assert.Equal("example.com", url.Host);
        Assert.Equal(8443, url.Port);
        Assert.Equal("/a/b", url.Path);
        Assert.Equal("q=1", url.Query);
        Assert.Equal("f", url.Fragment);
        Assert.Equal(443, WebUrl.Parse("https://example.com").PortOrKnownDefault);
    }

    [Theory]
    [InlineData("a b", "a+b")]
    [InlineData("a*b-c._d~", "a*b-c._d%7E")]
    [InlineData("é&=", "%C3%A9%26%3D")]
    public void FormEncodeMatchesByteSerialize(string input, string expected) =>
        Assert.Equal(expected, PercentEncoding.FormEncode(input));

    [Theory]
    [InlineData("http://ＥＸＡＭＰＬＥ。com/", "http://example.com/")]
    [InlineData("http://\u00ADexample.com/", "http://example.com/")]
    [InlineData("http://XN--MNCHEN-3YA.de/", "http://xn--mnchen-3ya.de/")]
    [InlineData("http://faß.de/", "http://xn--fa-hia.de/")]
    [InlineData("http://e\u0301.com/", "http://xn--9ca.com/")]
    [InlineData("http://日本語。ｊｐ/", "http://xn--wgv71a119e.jp/")]
    [InlineData("http://مثال.إختبار/", "http://xn--mgbh0fb.xn--kgbechtv/")]
    [InlineData("http://a.مثال/", "http://a.xn--mgbh0fb/")]
    [InlineData("http://ab--cd.com/", "http://ab--cd.com/")]
    public void MapsInternationalDomainsAsUts46Does(string input, string expected) =>
        Assert.Equal(expected, WebUrl.Parse(input).ToString());

    [Theory]
    [InlineData("http://xn--a/")]
    [InlineData("http://xn--/")]
    [InlineData("http://١.com/")]
    [InlineData("http://1a.مثال/")]
    [InlineData("http://a\u200Db.com/")]
    [InlineData("http://a\u200Cb.com/")]
    [InlineData("http://\u0301a.com/")]
    [InlineData("http://%e2%80%8d.com/")]
    public void RefusesDomainsUts46Refuses(string input)
    {
        Assert.False(WebUrl.TryParse(input, out _, out var message));
        Assert.Equal("invalid international domain name", message);
    }
}
