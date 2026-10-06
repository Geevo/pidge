using System.Text.Json;
using System.Text.Json.Nodes;
using Pidge.Codegen;
using Pidge.Core;
using Pidge.Storage;

namespace Pidge.Protocol.Tests;

/// <summary>The IPC contract. These tests pin the literal JSON, because the TypeScript side reads exactly these strings.</summary>
public class WireTests
{
    private static readonly ProtocolJsonContext Json = ProtocolJsonContext.Wire;

    private static JsonNode Parse(string line) => JsonNode.Parse(line)!;

    [Fact]
    public void EveryMessageCarriesAVersionATypeAndAnId()
    {
        var envelope = new ClientEnvelope(
            "req-1",
            new ClientMessage.SendRequest { Request = HttpRequest.Get("https://example.com") });

        var value = Parse(WireFormat.EncodeLine(envelope));

        Assert.Equal(WireFormat.ProtocolVersion, value["v"]!.GetValue<uint>());
        Assert.Equal("req-1", value["id"]!.GetValue<string>());
        Assert.Equal("sendRequest", value["msg"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void EncodedMessagesAreExactlyOneLine()
    {
        var line = WireFormat.EncodeLine(new ClientEnvelope(null, new ClientMessage.Shutdown()));

        Assert.EndsWith("\n", line);
        Assert.Single(line, '\n');
        Assert.Equal("{\"v\":4,\"id\":null,\"msg\":{\"type\":\"shutdown\"}}\n", line);
    }

    [Fact]
    public void ClientMessagesRoundTrip()
    {
        ClientMessage[] messages =
        [
            new ClientMessage.Handshake { ClientName = "vscode", ClientVersion = "0.1.0" },
            new ClientMessage.SendRequest
            {
                Request = HttpRequest.Get("https://example.com"),
                Variables = new() { ["token"] = "abc" },
            },
            new ClientMessage.CancelRequest { RequestId = "req-1" },
            new ClientMessage.LoadState(),
            new ClientMessage.ClearHistory(),
            new ClientMessage.Shutdown(),
        ];

        foreach (var message in messages)
        {
            var envelope = new ClientEnvelope("id", message);
            var line = WireFormat.EncodeLine(envelope);
            var decoded = WireFormat.DecodeClientLine(line);
            Assert.True(PidgeJson.Same(envelope, decoded, Json.ClientEnvelope), line);
        }
    }

    [Fact]
    public void ClientMessagesHaveTheShapeTheExtensionSends()
    {
        Assert.Equal(
            """{"v":4,"id":"h","msg":{"type":"handshake","clientName":"vscode","clientVersion":"0.1.0"}}""",
            WireFormat.EncodeLine(new ClientEnvelope("h", new ClientMessage.Handshake { ClientName = "vscode", ClientVersion = "0.1.0" })).TrimEnd('\n'));
        Assert.Equal(
            """{"v":4,"id":"c","msg":{"type":"cancelRequest","requestId":"req-1"}}""",
            WireFormat.EncodeLine(new ClientEnvelope("c", new ClientMessage.CancelRequest { RequestId = "req-1" })).TrimEnd('\n'));
        Assert.Equal(
            """{"v":4,"id":"x","msg":{"type":"exportSavedRequests","savedRequestIds":["a"],"format":"http","includeSecrets":false}}""",
            WireFormat.EncodeLine(new ClientEnvelope("x", new ClientMessage.ExportSavedRequests { SavedRequestIds = ["a"], Format = ExportFormat.Http })).TrimEnd('\n'));
        Assert.Equal(
            """{"v":4,"id":"s","msg":{"type":"saveRequest","savedRequestId":null,"name":"n","request":{"id":"r","method":"GET","url":"","queryParams":[],"headers":[],"auth":{"type":"none"},"body":{"type":"none"},"timeoutMs":null,"encodeQuery":true}}}""",
            WireFormat.EncodeLine(new ClientEnvelope("s", new ClientMessage.SaveRequest { Name = "n", Request = new HttpRequest { Id = "r" } })).TrimEnd('\n'));
    }

    [Fact]
    public void VariablesMayBeLeftOut()
    {
        var decoded = WireFormat.DecodeClientLine(
            """{"v":4,"id":"1","msg":{"type":"sendRequest","request":{"id":"r","method":"GET","url":"https://example.com","queryParams":[],"headers":[],"auth":{"type":"none"},"body":{"type":"none"},"timeoutMs":null}}}""");

        var send = Assert.IsType<ClientMessage.SendRequest>(decoded.Msg);
        Assert.Empty(send.Variables);
        Assert.Equal("https://example.com", send.Request.Url);
    }

    [Fact]
    public void TheTypeNeedNotComeFirst()
    {
        var decoded = WireFormat.DecodeClientLine("""{"msg":{"requestId":"r","type":"cancelRequest"},"id":null,"v":4}""");
        Assert.Equal("r", Assert.IsType<ClientMessage.CancelRequest>(decoded.Msg).RequestId);
        Assert.Null(decoded.Id);
    }

    [Fact]
    public void ResponseBodiesTravelAsBase64()
    {
        var response = new HttpResponse
        {
            Status = 200,
            StatusText = "OK",
            Body = "hello"u8.ToArray(),
            MimeType = "text/plain",
            DurationMs = 3,
            SizeBytes = 5,
            FinalUrl = "https://example.com",
        };

        var envelope = new ServerEnvelope("req-1", new ServerMessage.RequestComplete { Response = response });
        var line = WireFormat.EncodeLine(envelope);

        Assert.Equal("aGVsbG8=", Parse(line)["msg"]!["response"]!["body"]!.GetValue<string>());

        var decoded = WireFormat.DecodeServerLine(line);
        var complete = Assert.IsType<ServerMessage.RequestComplete>(decoded.Msg);
        Assert.True(PidgeJson.Same(response, complete.Response, Json.HttpResponse));
    }

    [Fact]
    public void BinaryBodiesSurviveTheRoundTrip()
    {
        var body = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var response = new HttpResponse
        {
            Status = 200,
            StatusText = "OK",
            Body = body,
            DurationMs = 1,
            SizeBytes = (ulong)body.Length,
            FinalUrl = "https://example.com",
        };

        var line = WireFormat.EncodeLine(response, Json.HttpResponse);
        var decoded = WireFormat.DecodeLine(line, Json.HttpResponse);
        Assert.Equal(body, decoded.Body);
    }

    [Fact]
    public void ErrorsKeepTheirKindAcrossTheWire()
    {
        var error = new RequestError(RequestErrorKind.ConnectionRefused, "nope").WithDetail("tcp connect error");

        var envelope = new ServerEnvelope("req-1", new ServerMessage.RequestError { Error = error });
        var line = WireFormat.EncodeLine(envelope);

        var value = Parse(line);
        Assert.Equal("requestError", value["msg"]!["type"]!.GetValue<string>());
        Assert.Equal("connectionRefused", value["msg"]!["error"]!["kind"]!.GetValue<string>());

        var decoded = WireFormat.DecodeServerLine(line);
        var failed = Assert.IsType<ServerMessage.RequestError>(decoded.Msg);
        Assert.True(PidgeJson.Same(error, failed.Error, Json.RequestError));
    }

    [Fact]
    public void AMatchingVersionIsAccepted()
    {
        Assert.Null(WireFormat.VersionMismatch(WireFormat.ProtocolVersion));
    }

    [Fact]
    public void AMismatchedVersionProducesAnActionableMessage()
    {
        var mismatch = WireFormat.VersionMismatch(WireFormat.ProtocolVersion + 1);

        Assert.NotNull(mismatch);
        Assert.Equal(WireFormat.ProtocolVersion, mismatch.ExpectedProtocolVersion);
        Assert.Equal(WireFormat.ProtocolVersion + 1, mismatch.ReceivedProtocolVersion);
        Assert.Contains("Reinstall", mismatch.Message);
        Assert.Equal(
            "Protocol mismatch: the extension speaks version 5, this sidecar speaks version 4. Reinstall the extension so the two match.",
            mismatch.Message);
    }

    [Fact]
    public void AMessageWithAnUnknownTypeFailsToDecode()
    {
        const string line = """{"v":1,"id":null,"msg":{"type":"somethingElse"}}""";
        Assert.ThrowsAny<JsonException>(() => WireFormat.DecodeClientLine(line));
    }

    [Fact]
    public void AMessageWithNoTypeFailsToDecode()
    {
        Assert.ThrowsAny<JsonException>(() => WireFormat.DecodeClientLine("""{"v":4,"id":null,"msg":{}}"""));
        Assert.ThrowsAny<JsonException>(() => WireFormat.DecodeClientLine("""{"v":4,"id":null}"""));
        Assert.ThrowsAny<JsonException>(() => WireFormat.DecodeClientLine("null"));
        Assert.ThrowsAny<JsonException>(() => WireFormat.DecodeClientLine("not json"));
    }

    [Fact]
    public void SurroundingWhitespaceIsTolerated()
    {
        var line = "  " + WireFormat.EncodeLine(new ClientEnvelope(null, new ClientMessage.LoadState())).TrimEnd('\n') + "  ";
        var decoded = WireFormat.DecodeClientLine(line);
        Assert.IsType<ClientMessage.LoadState>(decoded.Msg);
    }

    /// <summary>
    /// Every target is a plain lowercase name, because the TypeScript side sends
    /// exactly these strings — and the first four are the ones they have always
    /// been, so adding a language does not change what an older pair understood.
    /// </summary>
    [Fact]
    public void ACodeTargetIsAPlainLowercaseName()
    {
        var names = CodeTargets.All
            .Select(target => JsonSerializer.Serialize(target, Json.CodeTarget).Trim('"'))
            .ToList();

        Assert.Equal(["curl", "powershell", "python", "csharp"], names.Take(4));
        Assert.Contains("rust-blocking", names);
        Assert.Contains("java-okhttp", names);

        foreach (var name in names)
        {
            Assert.True(name.All(c => char.IsAsciiLetterLower(c) || c == '-'), $"`{name}` is not a plain name");
        }

        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void AGenerateNamesItsTargetAndCarriesItsVariables()
    {
        var envelope = new ClientEnvelope(
            "code-1",
            new ClientMessage.GenerateCode
            {
                Request = HttpRequest.Get("https://example.com"),
                Target = CodeTarget.PowerShell,
                Variables = new() { ["host"] = "example.com" },
            });

        var value = Parse(WireFormat.EncodeLine(envelope));

        Assert.Equal("generateCode", value["msg"]!["type"]!.GetValue<string>());
        Assert.Equal("powershell", value["msg"]!["target"]!.GetValue<string>());
        Assert.Equal("example.com", value["msg"]!["variables"]!["host"]!.GetValue<string>());
    }

    /// <summary>One message either way: the code, or the reason there is not any.</summary>
    [Fact]
    public void AGenerateAnswersWithTheCodeOrWithTheReason()
    {
        var generated = new ServerEnvelope(
            "code-1",
            new ServerMessage.CodeGenerated { Code = "curl --url 'https://example.com/'" });
        var value = Parse(WireFormat.EncodeLine(generated));
        Assert.Equal("codeGenerated", value["msg"]!["type"]!.GetValue<string>());
        Assert.True(((JsonObject)value["msg"]!).ContainsKey("error"));
        Assert.Null(value["msg"]!["error"]);

        var failed = new ServerEnvelope(
            "code-2",
            new ServerMessage.CodeGenerated { Error = new RequestError(RequestErrorKind.InvalidUrl, "Enter a URL.") });
        var line = WireFormat.EncodeLine(failed);
        var decoded = WireFormat.DecodeServerLine(line);
        Assert.True(PidgeJson.Same(failed, decoded, Json.ServerEnvelope));
    }

    [Fact]
    public void ServerMessagesHaveTheShapeTheExtensionReads()
    {
        Assert.Equal(
            """{"v":4,"id":"h","msg":{"type":"handshakeOk","serverName":"api-client-sidecar","serverVersion":"1.0.0","protocolVersion":4}}""",
            WireFormat.EncodeLine(new ServerEnvelope("h", new ServerMessage.HandshakeOk { ServerName = "api-client-sidecar", ServerVersion = "1.0.0", ProtocolVersion = 4 })).TrimEnd('\n'));
        Assert.Equal(
            """{"v":4,"id":"c","msg":{"type":"requestCancelled","wasInFlight":true}}""",
            WireFormat.EncodeLine(new ServerEnvelope("c", new ServerMessage.RequestCancelled { WasInFlight = true })).TrimEnd('\n'));
        Assert.Equal(
            """{"v":4,"id":null,"msg":{"type":"protocolError","message":"bad"}}""",
            WireFormat.EncodeLine(new ServerEnvelope(null, new ServerMessage.ProtocolError { Message = "bad" })).TrimEnd('\n'));
        Assert.Equal(
            """{"v":4,"id":"i","msg":{"type":"importRejected","message":"no"}}""",
            WireFormat.EncodeLine(new ServerEnvelope("i", new ServerMessage.ImportRejected { Message = "no" })).TrimEnd('\n'));
        Assert.Equal(
            """{"v":4,"id":"s","msg":{"type":"storageError","message":"disk"}}""",
            WireFormat.EncodeLine(new ServerEnvelope("s", new ServerMessage.StorageError { Message = "disk" })).TrimEnd('\n'));
        Assert.Equal(
            """{"v":4,"id":"e","msg":{"type":"savedRequestsExported","contents":"x\ny"}}""",
            WireFormat.EncodeLine(new ServerEnvelope("e", new ServerMessage.SavedRequestsExported { Contents = "x\ny" })).TrimEnd('\n'));
    }

    [Fact]
    public void ImportResultsCarryTheStateAndWhatToTellTheUser()
    {
        var state = new AppState();
        var line = WireFormat.EncodeLine(new ServerEnvelope("i", new ServerMessage.SavedRequestsImported
        {
            State = state,
            Imported = 2,
            UndefinedVariables = ["host"],
            Skipped = ["Request 3: no URL"],
            PlainSecrets = true,
        }));

        var msg = Parse(line)["msg"]!.AsObject();
        Assert.Equal(
            ["type", "state", "imported", "undefinedVariables", "skipped", "plainSecrets"],
            msg.Select(pair => pair.Key));
        Assert.Equal(2, msg["imported"]!.GetValue<int>());
    }

    [Fact]
    public void StateLoadedNamesWhereTheStateIs()
    {
        var line = WireFormat.EncodeLine(new ServerEnvelope("l", new ServerMessage.StateLoaded
        {
            State = new AppState(),
            StoragePath = "/tmp/state.json",
            Version = "1.2.3",
        }));

        var msg = Parse(line)["msg"]!.AsObject();
        Assert.Equal(["type", "state", "recovery", "storagePath", "version"], msg.Select(pair => pair.Key));
        Assert.Null(msg["recovery"]);
        Assert.False(msg["state"]!.AsObject().ContainsKey("window"));
    }
}
