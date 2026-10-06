using System.Text.Json;
using Pidge.Core;

namespace Pidge.Core.Tests;

/// <summary>The JSON has to keep its shape, or the UI and old state files stop working.</summary>
public class WireShapeTests
{
    private static readonly CoreJsonContext Json = CoreJsonContext.Wire;

    [Fact]
    public void ABlankRequestSerializesWithEveryField()
    {
        var request = new HttpRequest { Id = "r1", Url = "http://x" };
        Assert.Equal(
            """{"id":"r1","method":"GET","url":"http://x","queryParams":[],"headers":[],"auth":{"type":"none"},"body":{"type":"none"},"timeoutMs":null,"encodeQuery":true}""",
            JsonSerializer.Serialize(request, Json.HttpRequest));
    }

    [Fact]
    public void OAuthSettingsSitBesideTheTag()
    {
        AuthConfig auth = new OAuth2Settings { TokenUrl = "https://t", ClientId = "c" };
        Assert.Equal(
            """{"type":"oauth2","grant":"clientCredentials","tokenUrl":"https://t","clientId":"c","clientSecret":"","scope":"","username":"","password":"","refreshToken":"","clientAuth":"basicHeader"}""",
            JsonSerializer.Serialize(auth, Json.AuthConfig));

        AuthConfig one = new OAuth1Settings { ConsumerKey = "k", SignatureMethod = OAuth1Signature.HmacSha256 };
        Assert.Equal(
            """{"type":"oauth1","consumerKey":"k","consumerSecret":"","token":"","tokenSecret":"","signatureMethod":"hmacSha256","realm":""}""",
            JsonSerializer.Serialize(one, Json.AuthConfig));
    }

    [Fact]
    public void TheTagNeedNotComeFirst()
    {
        var auth = JsonSerializer.Deserialize("""{"token":"t","type":"bearer"}""", Json.AuthConfig);
        Assert.Equal("t", Assert.IsType<BearerAuth>(auth).Token);
    }

    [Fact]
    public void MissingFieldsTakeTheirDefaults()
    {
        var request = JsonSerializer.Deserialize(
            """{"id":"a","method":"POST","url":"u","queryParams":[],"headers":[],"auth":{"type":"oauth1"},"body":{"type":"text","text":"x","contentType":null},"timeoutMs":5}""",
            Json.HttpRequest)!;
        Assert.True(request.EncodeQuery);
        Assert.Equal(RequestMethod.Post, request.Method);
        Assert.Equal(5UL, request.TimeoutMs);
        Assert.Equal(OAuth1Signature.HmacSha1, Assert.IsType<OAuth1Settings>(request.Auth).SignatureMethod);
        Assert.Null(Assert.IsType<TextBody>(request.Body).ContentType);

        var tls = JsonSerializer.Deserialize("{}", Json.TlsSettings)!;
        Assert.True(tls.UseSystemRoots);
        Assert.True(tls.IsDefault);
    }

    [Fact]
    public void MultipartValuesAreTaggedWithKind()
    {
        RequestBody body = new MultipartBody
        {
            Entries = [new MultipartEntry { Id = "e", Enabled = true, Name = "f", Value = new MultipartFile { Path = "/a" } }],
        };
        Assert.Equal(
            """{"type":"multipart","entries":[{"id":"e","enabled":true,"name":"f","value":{"kind":"file","path":"/a","fileName":null,"contentType":null}}]}""",
            JsonSerializer.Serialize(body, Json.RequestBody));
    }

    [Fact]
    public void AResponseBodyIsBase64()
    {
        var response = new HttpResponse { Status = 200, Body = "hi"u8.ToArray(), FinalUrl = "http://x/" };
        var json = JsonSerializer.Serialize(response, Json.HttpResponse);
        Assert.Contains("\"body\":\"aGk=\"", json);
        Assert.Contains("\"tls\":null", json);
        Assert.Equal("hi"u8.ToArray(), JsonSerializer.Deserialize(json, Json.HttpResponse)!.Body);
    }

    [Fact]
    public void TextIsWrittenAsUtf8NotEscaped()
    {
        var entry = new KeyValueEntry { Id = "1", Enabled = true, Name = "<a>", Value = "café 'x' & y" };
        Assert.Equal(
            """{"id":"1","enabled":true,"name":"<a>","value":"café 'x' & y"}""",
            JsonSerializer.Serialize(entry, Json.KeyValueEntry));
    }

    [Fact]
    public void ErrorsSerializeWithTheirKind()
    {
        var error = RequestError.Timeout(30000);
        Assert.Equal(
            """{"kind":"timeout","message":"No response after 30000 ms.","detail":null}""",
            JsonSerializer.Serialize(error, Json.RequestError));
    }
}
