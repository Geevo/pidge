using Pidge.Core;

namespace Pidge.Codegen.Tests;

/// <summary>
/// What the generators have to agree about.
///
/// These are deliberately about behaviour rather than layout: a test that
/// pinned every line of output would fail on a reflowed comment and say
/// nothing about whether the request is right.
/// </summary>
public class GenerateTests
{
    private static string Code(HttpRequest request, CodeTarget target) =>
        CodeGenerator.Generate(request, ClientOptions.Standard(), target);

    private static List<(CodeTarget Target, string Code)> Everywhere(HttpRequest request) =>
        CodeTargets.All.Select(target => (target, Code(request, target))).ToList();

    private static HttpRequest Get(string url) => new() { Url = url };

    private static int Occurrences(string text, string fragment)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += fragment.Length;
        }
        return count;
    }

    private static ClientOptions WithTls(TlsSettings tls) => ClientOptions.Standard() with { Tls = tls };

    /// <summary>
    /// The URL in the snippet is the URL that would be requested, not the text in
    /// the field: <c>localhost:3000</c> is a host and a port here as it is
    /// everywhere else in the app.
    /// </summary>
    [Fact]
    public void ABareHostGainsTheSchemeTheEngineWouldGiveIt()
    {
        foreach (var (target, code) in Everywhere(Get("localhost:3000/api/test")))
        {
            Assert.True(
                code.Contains("http://localhost:3000/api/test", StringComparison.Ordinal),
                $"{target} did not normalize the URL:\n{code}");
        }
    }

    /// <summary>
    /// The params table and the URL are one thing shown twice, and the snippet
    /// shows it once — encoded as the engine encodes it, with <c>%20</c> for a space.
    /// </summary>
    [Fact]
    public void QueryRowsAreFoldedIntoTheUrlOnce()
    {
        var request = Get("https://example.com/lookup");
        request.QueryParams = [KeyValueEntry.New("postcode", "SW1A 1AA")];

        foreach (var (target, code) in Everywhere(request))
        {
            Assert.True(
                code.Contains("https://example.com/lookup?postcode=SW1A%201AA", StringComparison.Ordinal),
                $"{target} did not fold the query in:\n{code}");
            Assert.True(Occurrences(code, "postcode=") == 1, $"{target} wrote the parameter twice:\n{code}");
        }
    }

    /// <summary>A key placed in the query string is a query parameter, exactly as the engine treats it.</summary>
    [Fact]
    public void AnApiKeyInTheQueryStringIsInTheUrl()
    {
        var request = Get("https://example.com/things");
        request.Auth = new ApiKeyAuth { Key = "api_key", Value = "abc123", Placement = ApiKeyPlacement.Query };

        foreach (var (target, code) in Everywhere(request))
        {
            Assert.True(
                code.Contains("https://example.com/things?api_key=abc123", StringComparison.Ordinal),
                $"{target} lost the key:\n{code}");
        }
    }

    /// <summary>
    /// Every one of these languages says a username and a password better than
    /// the base64 the header would carry, so none of them shows the blob.
    /// </summary>
    [Fact]
    public void BasicAuthIsWrittenInEachLanguageRatherThanAsABlob()
    {
        var request = Get("https://example.com/");
        request.Auth = new BasicAuth { Username = "ada", Password = "lovelace" };

        (CodeTarget, string)[] expected =
        [
            (CodeTarget.Curl, "--user 'ada:lovelace'"),
            (CodeTarget.PowerShell, "'ada:lovelace'"),
            (CodeTarget.Python, "auth=(\"ada\", \"lovelace\")"),
            (CodeTarget.CSharp, "\"ada:lovelace\""),
        ];

        foreach (var (target, fragment) in expected)
        {
            var code = Code(request, target);
            Assert.True(
                code.Contains(fragment, StringComparison.Ordinal),
                $"{target} did not write the credentials as `{fragment}`:\n{code}");
            Assert.False(
                code.Contains("YWRhOmxvdmVsYWNl", StringComparison.Ordinal),
                $"{target} wrote the base64 rather than the pair:\n{code}");
        }
    }

    /// <summary>
    /// An Authorization header the user typed wins over the Auth tab, in the
    /// snippet as in the engine, and the code says why.
    /// </summary>
    [Fact]
    public void ATypedAuthorizationHeaderBeatsTheAuthTab()
    {
        var request = Get("https://example.com/");
        request.Headers = [KeyValueEntry.New("Authorization", "Bearer mine")];
        request.Auth = new BasicAuth { Username = "ada", Password = "lovelace" };

        foreach (var (target, code) in Everywhere(request))
        {
            Assert.True(code.Contains("Bearer mine", StringComparison.Ordinal), $"{target} dropped the header:\n{code}");
            Assert.False(code.Contains("lovelace", StringComparison.Ordinal), $"{target} sent the Auth tab as well:\n{code}");
            Assert.True(
                code.Contains("Auth tab was ignored", StringComparison.Ordinal),
                $"{target} did not say the tab was ignored:\n{code}");
        }
    }

    /// <summary>
    /// Digest and NTLM are a second request after a challenge, which each of
    /// these clients can do on its own — so each is told to, rather than left silent.
    /// </summary>
    [Fact]
    public void AChallengeSchemeUsesTheClientThatCanAnswerIt()
    {
        var request = Get("https://example.com/");
        request.Auth = new DigestAuth { Username = "ada", Password = "lovelace" };

        (CodeTarget, string)[] expected =
        [
            (CodeTarget.Curl, "--digest"),
            (CodeTarget.PowerShell, "PSCredential"),
            (CodeTarget.Python, "HTTPDigestAuth"),
            (CodeTarget.CSharp, "NetworkCredential"),
        ];

        foreach (var (target, fragment) in expected)
        {
            var code = Code(request, target);
            Assert.True(code.Contains(fragment, StringComparison.Ordinal), $"{target} did not use `{fragment}`:\n{code}");
        }
    }

    /// <summary>
    /// The body is the text in the editor, character for character. Re-encoding
    /// it through a parser would reorder keys and reformat numbers, and send
    /// something nobody wrote.
    /// </summary>
    [Fact]
    public void AJsonBodyIsPassedThroughAsTyped()
    {
        var request = Get("https://example.com/");
        request.Method = RequestMethod.Post;
        request.Body = new JsonBody { Text = "{\n  \"b\": 1,\n  \"a\": 2.50\n}" };

        foreach (var (target, code) in Everywhere(request))
        {
            Assert.True(
                code.Contains("\"b\"", StringComparison.Ordinal) && code.Contains("2.50", StringComparison.Ordinal),
                $"{target} rewrote the body:\n{code}");
            Assert.True(
                code.Contains("application/json", StringComparison.Ordinal),
                $"{target} lost the content type:\n{code}");
        }
    }

    /// <summary>
    /// A form body is encoded once, here, the way the engine encodes it — so the
    /// bytes in the snippet are the bytes on the wire.
    /// </summary>
    [Fact]
    public void AFormBodyIsEncodedTheWayTheEngineEncodesIt()
    {
        var request = Get("https://example.com/");
        request.Method = RequestMethod.Post;
        request.Body = new UrlEncodedBody { Entries = [KeyValueEntry.New("full name", "Ada Lovelace")] };

        foreach (var (target, code) in Everywhere(request))
        {
            Assert.True(
                code.Contains("application/x-www-form-urlencoded", StringComparison.Ordinal),
                $"{target} lost the content type:\n{code}");
        }

        // Python hands `requests` the pairs and lets it encode them, which is the
        // same encoding; the other three write the bytes out.
        foreach (var target in new[] { CodeTarget.Curl, CodeTarget.PowerShell, CodeTarget.CSharp })
        {
            var code = Code(request, target);
            Assert.True(
                code.Contains("full+name=Ada+Lovelace", StringComparison.Ordinal),
                $"{target} did not encode the form:\n{code}");
        }
    }

    /// <summary>
    /// Multipart writes its own Content-Type, boundary and all. Anybody else who
    /// writes that header breaks the body.
    /// </summary>
    [Fact]
    public void NothingSetsAContentTypeForAMultipartBody()
    {
        var request = Get("https://example.com/upload");
        request.Method = RequestMethod.Post;
        request.Headers = [KeyValueEntry.New("Content-Type", "multipart/form-data")];
        request.Body = new MultipartBody
        {
            Entries =
            [
                MultipartEntry.Text("caption", "A photo"),
                MultipartEntry.File("photo", "/home/ada/cat.png"),
            ],
        };

        foreach (var (target, code) in Everywhere(request))
        {
            /*
             * A client that writes the body itself has to name the boundary it
             * generated; one whose library writes the body must not name the type
             * at all. What is never right is the user's own row, which has no
             * boundary in it and would break the body.
             */
            var index = code.IndexOf("multipart/form-data", StringComparison.Ordinal);
            if (index >= 0)
            {
                Assert.True(
                    code[index..].Contains("boundary", StringComparison.Ordinal),
                    $"{target} wrote a content type with no boundary:\n{code}");
            }
            Assert.True(
                code.Contains("caption", StringComparison.Ordinal) && code.Contains("cat.png", StringComparison.Ordinal),
                $"{target} lost a part:\n{code}");
            Assert.True(
                code.Contains("Content-Type", StringComparison.Ordinal),
                $"{target} did not say the header was replaced:\n{code}");
        }
    }

    /// <summary><c>-X HEAD</c> leaves curl waiting for a body a HEAD response never has.</summary>
    [Fact]
    public void CurlAsksForAHeadRequestTheWayCurlWantsToBeAsked()
    {
        var request = Get("https://example.com/");
        request.Method = RequestMethod.Head;
        var code = Code(request, CodeTarget.Curl);
        Assert.Contains("--head", code, StringComparison.Ordinal);
        Assert.DoesNotContain("--request HEAD", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// What cannot be reproduced is said, not left out. A signature is a library,
    /// and a snippet that quietly omitted it would look complete.
    /// </summary>
    [Fact]
    public void AnUnsignableRequestSaysSo()
    {
        var oauth1 = Get("https://example.com/");
        oauth1.Auth = new OAuth1Settings();
        foreach (var (target, code) in Everywhere(oauth1))
        {
            Assert.True(
                code.Contains("OAuth 1.0a", StringComparison.Ordinal),
                $"{target} said nothing about the signature:\n{code}");
        }

        var oauth2 = Get("https://example.com/");
        oauth2.Auth = new OAuth2Settings { TokenUrl = "https://id.example.com/token" };
        foreach (var (target, code) in Everywhere(oauth2))
        {
            Assert.True(
                code.Contains("https://id.example.com/token", StringComparison.Ordinal),
                $"{target} said nothing about the token:\n{code}");
        }
    }

    /// <summary>
    /// The app's own settings are not in the generated program, so they are
    /// written into it. Without this, a snippet copied from a client that follows
    /// redirects stops at the first one.
    /// </summary>
    [Fact]
    public void TheClientSettingsAreWrittenIntoTheSnippet()
    {
        var request = Get("https://example.com/");
        var strict = new ClientOptions
        {
            TimeoutMs = 5_000,
            FollowRedirects = false,
            Tls = new TlsSettings { AcceptInvalidCerts = true },
        };

        (CodeTarget, string[])[] expected =
        [
            (CodeTarget.Curl, ["--insecure", "--max-time 5"]),
            (CodeTarget.PowerShell, ["SkipCertificateCheck", "MaximumRedirection", "TimeoutSec"]),
            (CodeTarget.Python, ["verify=False", "allow_redirects=False", "timeout=5"]),
            (CodeTarget.CSharp, ["AllowAutoRedirect = false", "DangerousAccept"]),
        ];

        foreach (var (target, fragments) in expected)
        {
            var code = CodeGenerator.Generate(request, strict, target);
            foreach (var fragment in fragments)
            {
                Assert.True(
                    code.Contains(fragment, StringComparison.Ordinal),
                    $"{target} did not carry `{fragment}`:\n{code}");
            }
        }

        // The other way round: curl does not follow redirects unless it is told
        // to, and the app does.
        var standard = CodeGenerator.Generate(request, ClientOptions.Standard(), CodeTarget.Curl);
        Assert.Contains("--location", standard, StringComparison.Ordinal);
    }

    /// <summary>
    /// A request with no URL cannot be described, and the error says the same
    /// thing the Send button would.
    /// </summary>
    [Fact]
    public void AnEmptyUrlIsAnErrorRatherThanASnippet()
    {
        foreach (var target in CodeTargets.All)
        {
            var error = Assert.Throws<RequestErrorException>(
                () => CodeGenerator.Generate(Get("   "), ClientOptions.Standard(), target));
            Assert.Contains("Enter a URL", error.Error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>A disabled row is not sent, so it is not written either.</summary>
    [Fact]
    public void DisabledRowsAreLeftOut()
    {
        var caption = MultipartEntry.Text("caption", "not-this");
        caption.Enabled = false;

        var request = Get("https://example.com/");
        request.Method = RequestMethod.Post;
        request.Headers = [KeyValueEntry.Disabled("X-Draft", "not-this")];
        request.QueryParams = [KeyValueEntry.Disabled("page", "not-this")];
        request.Body = new MultipartBody { Entries = [caption, MultipartEntry.File("photo", "/home/ada/cat.png")] };

        foreach (var (target, code) in Everywhere(request))
        {
            Assert.False(code.Contains("not-this", StringComparison.Ordinal), $"{target} wrote a disabled row:\n{code}");
        }
    }

    /// <summary>
    /// A file part keeps the name and the type the request gave it, wherever the
    /// language has somewhere to put them.
    /// </summary>
    [Fact]
    public void AFilePartKeepsTheNameItWasGiven()
    {
        var photo = MultipartEntry.File("photo", "/home/ada/IMG_0001.png");
        photo.Value = new MultipartFile
        {
            Path = "/home/ada/IMG_0001.png",
            FileName = "cat.png",
            ContentType = "image/png",
        };

        var request = Get("https://example.com/upload");
        request.Method = RequestMethod.Post;
        request.Body = new MultipartBody { Entries = [photo] };

        foreach (var target in new[] { CodeTarget.Curl, CodeTarget.Python, CodeTarget.CSharp })
        {
            var code = Code(request, target);
            Assert.True(
                code.Contains("cat.png", StringComparison.Ordinal) && code.Contains("image/png", StringComparison.Ordinal),
                $"{target} lost the part's name or type:\n{code}");
        }

        // PowerShell's -Form takes neither, and says so rather than pretending.
        Assert.Contains("-Form takes neither", Code(request, CodeTarget.PowerShell), StringComparison.Ordinal);
    }

    /// <summary>
    /// A client certificate is the app's setting, not the request's, so the
    /// snippet has to present it itself or it will be turned away at the handshake.
    /// </summary>
    [Fact]
    public void AClientCertificateIsPresentedInEachLanguage()
    {
        var options = WithTls(new TlsSettings
        {
            ClientIdentity = new ClientIdentitySettings { Path = "/home/ada/client.p12", Password = "hunter2" },
        });

        (CodeTarget, string[])[] expected =
        [
            (CodeTarget.Curl, ["--cert-type P12", "--cert '/home/ada/client.p12'", "--pass 'hunter2'"]),
            (CodeTarget.PowerShell, ["X509Certificate2", "'/home/ada/client.p12'", "'hunter2'"]),
            (CodeTarget.Python, ["cert="]),
            (CodeTarget.CSharp, ["ClientCertificates.Add", "LoadPkcs12FromFile", "\"hunter2\""]),
        ];

        foreach (var (target, fragments) in expected)
        {
            var code = CodeGenerator.Generate(Get("https://example.com/"), options, target);
            foreach (var fragment in fragments)
            {
                Assert.True(
                    code.Contains(fragment, StringComparison.Ordinal),
                    $"{target} did not present the certificate with `{fragment}`:\n{code}");
            }
        }
    }

    /// <summary>
    /// The file's extension says which kind it is, because nothing here can read
    /// it — and a PEM and a PKCS#12 bundle are loaded by different calls.
    /// </summary>
    [Fact]
    public void APemCertificateIsLoadedAsAPem()
    {
        var options = WithTls(new TlsSettings
        {
            ClientIdentity = new ClientIdentitySettings { Path = "/home/ada/client.pem" },
        });

        var curl = CodeGenerator.Generate(Get("https://example.com/"), options, CodeTarget.Curl);
        Assert.Contains("--cert '/home/ada/client.pem'", curl, StringComparison.Ordinal);
        Assert.DoesNotContain("--cert-type P12", curl, StringComparison.Ordinal);
        Assert.DoesNotContain("--pass", curl, StringComparison.Ordinal);

        (CodeTarget, string)[] expected =
        [
            (CodeTarget.PowerShell, "CreateFromPemFile"),
            (CodeTarget.CSharp, "CreateFromPemFile"),
            (CodeTarget.Python, "cert=\"/home/ada/client.pem\""),
        ];
        foreach (var (target, fragment) in expected)
        {
            var code = CodeGenerator.Generate(Get("https://example.com/"), options, target);
            Assert.True(code.Contains(fragment, StringComparison.Ordinal), $"{target}:\n{code}");
        }
    }

    /// <summary>
    /// <c>requests</c> goes through OpenSSL, which wants a PEM on disk. The
    /// snippet says how to make one and then points at it, rather than at a file
    /// that will fail.
    /// </summary>
    [Fact]
    public void PythonSaysHowToConvertAPkcs12Bundle()
    {
        var options = WithTls(new TlsSettings
        {
            ClientIdentity = new ClientIdentitySettings { Path = "/home/ada/client.p12", Password = "hunter2" },
        });

        var code = CodeGenerator.Generate(Get("https://example.com/"), options, CodeTarget.Python);
        Assert.Contains("openssl pkcs12 -in /home/ada/client.p12", code, StringComparison.Ordinal);
        Assert.Contains("cert=\"/home/ada/client.pem\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("cert=\"/home/ada/client.p12\"", code, StringComparison.Ordinal);

        // The command is there to be copied, so it is on one line.
        var command = code.Split('\n').FirstOrDefault(line => line.Contains("openssl", StringComparison.Ordinal));
        Assert.NotNull(command);
        Assert.True(
            command.Contains("-out", StringComparison.Ordinal) && command.Contains("-nodes", StringComparison.Ordinal),
            command);
    }

    /// <summary>
    /// Two of these clients take a CA file and two do not. Both answers are said
    /// out loud rather than left for a failed handshake to explain.
    /// </summary>
    [Fact]
    public void AnExtraCaIsUsedWhereItCanBeAndNamedWhereItCannot()
    {
        var options = WithTls(new TlsSettings
        {
            ExtraCaFiles = ["/home/ada/corp-ca.pem"],
            UseSystemRoots = false,
        });

        var curl = CodeGenerator.Generate(Get("https://example.com/"), options, CodeTarget.Curl);
        Assert.Contains("--cacert '/home/ada/corp-ca.pem'", curl, StringComparison.Ordinal);

        var python = CodeGenerator.Generate(Get("https://example.com/"), options, CodeTarget.Python);
        Assert.Contains("verify=\"/home/ada/corp-ca.pem\"", python, StringComparison.Ordinal);

        foreach (var target in new[] { CodeTarget.PowerShell, CodeTarget.CSharp })
        {
            var code = CodeGenerator.Generate(Get("https://example.com/"), options, target);
            Assert.True(
                code.Contains("/home/ada/corp-ca.pem", StringComparison.Ordinal)
                && code.Contains("certificate store", StringComparison.Ordinal),
                $"{target} did not say where the CA has to go:\n{code}");
        }
    }

    /// <summary>
    /// The app adds a CA to the system roots; curl and requests replace them.
    /// That difference is the one that turns a working snippet into a failing one
    /// for every other host, so it is stated.
    /// </summary>
    [Fact]
    public void ReplacingTheSystemRootsRatherThanAddingToThemIsCalledOut()
    {
        var merging = WithTls(new TlsSettings
        {
            ExtraCaFiles = ["/home/ada/corp-ca.pem"],
            UseSystemRoots = true,
        });

        foreach (var target in new[] { CodeTarget.Curl, CodeTarget.Python })
        {
            var code = CodeGenerator.Generate(Get("https://example.com/"), merging, target);
            Assert.True(
                code.Contains("rather than adding to it", StringComparison.Ordinal),
                $"{target} did not say the system roots are replaced:\n{code}");
        }

        // Trusting only the named CA is exactly what these two do, so there is
        // nothing to warn about.
        var only = WithTls(new TlsSettings
        {
            ExtraCaFiles = ["/home/ada/corp-ca.pem"],
            UseSystemRoots = false,
        });

        foreach (var target in new[] { CodeTarget.Curl, CodeTarget.Python })
        {
            var code = CodeGenerator.Generate(Get("https://example.com/"), only, target);
            Assert.False(
                code.Contains("rather than adding to it", StringComparison.Ordinal),
                $"{target} warned about something it does correctly:\n{code}");
        }
    }

    /// <summary>
    /// Verification off leaves nothing for a CA file to do, and <c>verify</c> is
    /// one argument that cannot say both.
    /// </summary>
    [Fact]
    public void VerificationOffWinsOverACaFile()
    {
        var options = WithTls(new TlsSettings
        {
            ExtraCaFiles = ["/home/ada/corp-ca.pem"],
            AcceptInvalidCerts = true,
        });

        var code = CodeGenerator.Generate(Get("https://example.com/"), options, CodeTarget.Python);
        Assert.Contains("verify=False", code, StringComparison.Ordinal);
        Assert.DoesNotContain("verify=\"/home/ada", code, StringComparison.Ordinal);
    }

    /// <summary>An empty path is not a certificate. The settings dialog can leave one there.</summary>
    [Fact]
    public void ABlankCertificatePathIsNotACertificate()
    {
        var options = WithTls(new TlsSettings
        {
            ClientIdentity = new ClientIdentitySettings { Path = "   " },
            ExtraCaFiles = ["  "],
        });

        foreach (var target in CodeTargets.All)
        {
            var code = CodeGenerator.Generate(Get("https://example.com/"), options, target);
            foreach (var fragment in new[] { "--cert", "cert=", "CreateFromPemFile", "--cacert", "verify=" })
            {
                Assert.False(
                    code.Contains(fragment, StringComparison.Ordinal),
                    $"{target} wrote `{fragment}` for a blank path:\n{code}");
            }
        }
    }

    /// <summary>
    /// The languages added after the first four, and the shapes each of them has
    /// to get right. One test per language rather than per rule: what matters is
    /// that the request survives the trip into a language, not which call carries it.
    /// </summary>
    [Fact]
    public void EveryLanguageWritesTheWholeRequest()
    {
        var request = new HttpRequest
        {
            Method = RequestMethod.Post,
            Url = "https://api.example.com/v1/things",
            QueryParams = [KeyValueEntry.New("q", "SW1A 1AA")],
            Headers = [KeyValueEntry.New("X-Trace", "abc 123")],
            Auth = new BasicAuth { Username = "ada", Password = "lovelace" },
            Body = new JsonBody { Text = "{\n  name: Ada\n}" },
        };

        foreach (var (target, code) in Everywhere(request))
        {
            Assert.True(
                code.Contains("https://api.example.com/v1/things?q=SW1A%201AA", StringComparison.Ordinal),
                $"{target} did not carry the URL:\n{code}");
            Assert.True(code.Contains("X-Trace", StringComparison.Ordinal), $"{target} did not carry the header:\n{code}");
            Assert.True(
                code.Contains("application/json", StringComparison.Ordinal),
                $"{target} did not carry the content type:\n{code}");
            Assert.True(
                code.Contains("  name: Ada", StringComparison.Ordinal),
                $"{target} did not carry the body as typed:\n{code}");
            Assert.True(
                code.Contains("ada", StringComparison.Ordinal)
                && (code.Contains("lovelace", StringComparison.Ordinal)
                    || code.Contains("YWRhOmxvdmVsYWNl", StringComparison.Ordinal)),
                $"{target} did not carry the credentials:\n{code}");
        }
    }

    /// <summary>
    /// A body's own indentation is data. The languages whose code sits inside a
    /// function are the ones that could have indented it along with everything else.
    /// </summary>
    [Fact]
    public void ABodyKeepsItsOwnIndentation()
    {
        var request = Get("https://example.com/");
        request.Method = RequestMethod.Post;
        request.Body = new JsonBody { Text = "{\n      deeply: indented\n}" };

        /*
         * The six spaces are the test. A language that laid the snippet out by
         * indenting whole blocks would have made them ten, and sent a body nobody
         * wrote. Zig opens each line with `\\`, which is why the newline before
         * them is not part of what is looked for.
         */
        foreach (var (target, code) in Everywhere(request))
        {
            Assert.True(
                code.Contains("      deeply: indented", StringComparison.Ordinal),
                $"{target} re-indented the body:\n{code}");
            Assert.False(
                code.Contains("          deeply: indented", StringComparison.Ordinal),
                $"{target} added indentation of its own:\n{code}");
        }
    }

    /// <summary>Every language names the library it needs, where it needs one that is not in the box.</summary>
    [Fact]
    public void ALanguageIsGroupedWithTheLibrariesThatWriteIt()
    {
        var rust = CodeTargets.All.Where(target => target.Language() == "Rust").ToList();

        Assert.Equal(2, rust.Count);
        Assert.Equal("blocking", rust[0].Library());
        Assert.Equal("Rust (blocking)", rust[0].Label());

        // A language with one way of doing this has no library to choose.
        Assert.Null(CodeTarget.Go.Library());
        Assert.Equal("Go", CodeTarget.Go.Label());

        // Nothing is listed twice.
        var labels = CodeTargets.All.Select(target => target.Label()).ToList();
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Where a client cannot answer a challenge, it says so rather than looking
    /// like it sent credentials it never had.
    /// </summary>
    [Fact]
    public void ALanguageThatCannotAnswerAChallengeSaysSo()
    {
        var request = Get("https://example.com/");
        request.Auth = new DigestAuth { Username = "ada", Password = "lovelace" };

        // These answer it themselves; the rest have to explain.
        CodeTarget[] answers =
        [
            CodeTarget.Curl,
            CodeTarget.PowerShell,
            CodeTarget.Python,
            CodeTarget.CSharp,
            CodeTarget.PhpCurl,
            CodeTarget.PhpGuzzle,
        ];

        foreach (var (target, code) in Everywhere(request))
        {
            if (answers.Contains(target))
            {
                continue;
            }
            Assert.True(
                code.ToLowerInvariant().Contains("digest", StringComparison.Ordinal),
                $"{target} said nothing about the challenge:\n{code}");
        }
    }
}
