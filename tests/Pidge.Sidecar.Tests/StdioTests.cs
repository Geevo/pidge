using System.Diagnostics;
using Pidge.Codegen;
using Pidge.Core;
using Pidge.Protocol;
using Pidge.Storage;

namespace Pidge.Sidecar.Tests;

public class StdioTests
{
    [Fact]
    public void RefusesToWorkBeforeAHandshake()
    {
        using var sidecar = SidecarProcess.Start();

        sidecar.Send("req-1", new ClientMessage.LoadState());

        var error = Assert.IsType<ServerMessage.ProtocolError>(sidecar.Recv().Msg);
        Assert.Contains("handshake", error.Message);
    }

    [Fact]
    public void AProtocolMismatchFailsLoudly()
    {
        using var sidecar = SidecarProcess.Start();

        sidecar.SendRaw(
            $$$"""{"v":{{{WireFormat.ProtocolVersion + 1}}},"id":"hs","msg":{"type":"handshake","clientName":"test","clientVersion":"0"}}""");

        var mismatch = Assert.IsType<ServerMessage.HandshakeError>(sidecar.Recv().Msg);
        Assert.Equal(WireFormat.ProtocolVersion, mismatch.ExpectedProtocolVersion);
        Assert.Equal(WireFormat.ProtocolVersion + 1, mismatch.ReceivedProtocolVersion);
    }

    [Fact]
    public void AMalformedLineIsReportedAndTheProcessCarriesOn()
    {
        using var sidecar = SidecarProcess.Start();
        sidecar.Handshake();

        sidecar.SendRaw("{ not json");
        var error = Assert.IsType<ServerMessage.ProtocolError>(sidecar.Recv().Msg);
        Assert.Contains("parse", error.Message);

        // Still usable afterwards.
        sidecar.Send("load", new ClientMessage.LoadState());
        Assert.IsType<ServerMessage.StateLoaded>(sidecar.Recv().Msg);
    }

    [Fact]
    public void ErrorsComeBackAsRequestErrors()
    {
        using var sidecar = SidecarProcess.Start();
        sidecar.Handshake();

        sidecar.Send("req-1", new ClientMessage.SendRequest { Request = HttpRequest.Get("{{missing}}") });

        var reply = Assert.IsType<ServerMessage.RequestError>(sidecar.Recv().Msg);
        Assert.Equal(RequestErrorKind.UnresolvedVariable, reply.Error.Kind);
        Assert.NotNull(reply.HistoryEntry);
    }

    [Fact]
    public void ASendWithoutACorrelationIdIsRefused()
    {
        using var sidecar = SidecarProcess.Start();
        sidecar.Handshake();

        sidecar.Send(null, new ClientMessage.SendRequest { Request = HttpRequest.Get("{{missing}}") });

        var envelope = sidecar.Recv();
        Assert.Null(envelope.Id);
        var error = Assert.IsType<ServerMessage.ProtocolError>(envelope.Msg);
        Assert.Equal("sendRequest needs a correlation id", error.Message);
    }

    [Fact]
    public void CancellingNothingIsAcknowledged()
    {
        using var sidecar = SidecarProcess.Start();
        sidecar.Handshake();

        sidecar.Send("cancel-1", new ClientMessage.CancelRequest { RequestId = "nothing" });

        var envelope = sidecar.Recv();
        Assert.Equal("nothing", envelope.Id);
        Assert.False(Assert.IsType<ServerMessage.RequestCancelled>(envelope.Msg).WasInFlight);
    }

    [Fact]
    public void StateCanBeLoadedSavedAndCleared()
    {
        using var sidecar = SidecarProcess.Start();
        sidecar.Handshake();

        sidecar.Send("load", new ClientMessage.LoadState());
        var loaded = Assert.IsType<ServerMessage.StateLoaded>(sidecar.Recv().Msg);
        Assert.Null(loaded.Recovery);
        Assert.EndsWith("state.json", loaded.StoragePath);
        // Whatever this build is, the UI has something to show in About.
        Assert.NotEmpty(loaded.Version);
        Assert.Single(loaded.State.Tabs);
        var state = loaded.State;

        state.Tabs.Add(ScratchTab.Blank());
        sidecar.Send("save", new ClientMessage.SaveState { State = state });
        Assert.Equal(2, Assert.IsType<ServerMessage.StateSaved>(sidecar.Recv().Msg).State.Tabs.Count);

        sidecar.Send("save-req", new ClientMessage.SaveRequest
        {
            Name = "Users",
            Request = HttpRequest.Get("https://example.com/users"),
        });
        var saved = Assert.IsType<ServerMessage.StateSaved>(sidecar.Recv().Msg).State.SavedRequests;
        Assert.Single(saved);

        sidecar.Send("delete", new ClientMessage.DeleteSavedRequest { SavedRequestId = saved[0].Id });
        Assert.Empty(Assert.IsType<ServerMessage.StateSaved>(sidecar.Recv().Msg).State.SavedRequests);

        sidecar.Send("clear", new ClientMessage.ClearHistory());
        Assert.Empty(Assert.IsType<ServerMessage.StateSaved>(sidecar.Recv().Msg).State.History);
    }

    [Fact]
    public void ShutdownEndsTheProcessCleanly()
    {
        using var sidecar = SidecarProcess.Start();
        sidecar.Handshake();
        sidecar.Send(null, new ClientMessage.Shutdown());

        Assert.True(sidecar.Child.WaitForExit(30_000), "should exit");
        Assert.Equal(0, sidecar.Child.ExitCode);
    }

    [Fact]
    public void ClosingStdinEndsTheProcessCleanly()
    {
        using var sidecar = SidecarProcess.Start();
        sidecar.Handshake();
        sidecar.Child.StandardInput.Close();

        Assert.True(sidecar.Child.WaitForExit(30_000), "should exit");
        Assert.Equal(0, sidecar.Child.ExitCode);
    }

    /// <summary>Stdout is the protocol: UTF-8 without a byte order mark, one line per message, ending in \n.</summary>
    [Fact]
    public void StdoutCarriesNothingButProtocolLines()
    {
        using var sidecar = SidecarProcess.Start();
        sidecar.SendRaw("""{"v":4,"id":"hs","msg":{"type":"handshake","clientName":"tést","clientVersion":"0"}}""");
        sidecar.SendRaw("""{"v":4,"id":"code","msg":{"type":"generateCode","request":{"id":"r","method":"GET","url":"https://example.com/é","queryParams":[],"headers":[],"auth":{"type":"none"},"body":{"type":"none"}},"target":"curl"}}""");
        sidecar.SendRaw("""{"v":4,"msg":{"type":"shutdown"}}""");

        using var bytes = new MemoryStream();
        sidecar.Child.StandardOutput.BaseStream.CopyTo(bytes);
        var raw = bytes.ToArray();

        Assert.Equal((byte)'{', raw[0]);
        Assert.DoesNotContain((byte)'\r', raw);
        Assert.Equal((byte)'\n', raw[^1]);
        var lines = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(raw).Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("", lines[2]);
        Assert.IsType<ServerMessage.HandshakeOk>(WireFormat.DecodeServerLine(lines[0]).Msg);
        Assert.IsType<ServerMessage.CodeGenerated>(WireFormat.DecodeServerLine(lines[1]).Msg);
    }

    [Fact]
    public void ReportsItsProtocolVersionOnTheCommandLine()
    {
        using var process = Process.Start(SidecarProcess.StartInfo("--protocol-version"))!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(WireFormat.ProtocolVersion + "\n", output);
    }

    [Fact]
    public void ReportsItsVersionOnTheCommandLine()
    {
        using var process = Process.Start(SidecarProcess.StartInfo("--version"))!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(AppVersion.Current + "\n", output);
    }

    [Fact]
    public void AnUnknownArgumentIsAFailureWithHelp()
    {
        using var process = Process.Start(SidecarProcess.StartInfo("--bogus"))!;
        var output = process.StandardOutput.ReadToEnd();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(1, process.ExitCode);
        Assert.Equal("", output);
        Assert.StartsWith("unknown argument: --bogus\n", errors);
        Assert.Contains("--state-dir <path>", errors);
    }

    /// <summary>
    /// Writing a request out as code is the sidecar's job too, so the extension
    /// and the desktop app cannot answer the question differently.
    /// </summary>
    [Fact]
    public void WritesARequestOutAsCode()
    {
        using var sidecar = SidecarProcess.Start();
        sidecar.Handshake();

        sidecar.Send("code-1", new ClientMessage.GenerateCode
        {
            Request = HttpRequest.Get("https://example.com/things"),
            Target = CodeTarget.Curl,
        });

        var envelope = sidecar.Recv();
        Assert.Equal("code-1", envelope.Id);
        var reply = Assert.IsType<ServerMessage.CodeGenerated>(envelope.Msg);
        Assert.Null(reply.Error);
        Assert.NotNull(reply.Code);
        Assert.Contains("curl", reply.Code);
        Assert.Contains("https://example.com/things", reply.Code);
    }

    /// <summary>
    /// A variable with no value is the same answer it would be on Send: there
    /// is no honest snippet to write, and the reason comes back in the same message.
    /// </summary>
    [Fact]
    public void SaysWhyThereIsNoCode()
    {
        using var sidecar = SidecarProcess.Start();
        sidecar.Handshake();

        sidecar.Send("code-2", new ClientMessage.GenerateCode
        {
            Request = HttpRequest.Get("{{host}}/things"),
            Target = CodeTarget.Curl,
        });

        var reply = Assert.IsType<ServerMessage.CodeGenerated>(sidecar.Recv().Msg);
        Assert.Null(reply.Code);
        Assert.NotNull(reply.Error);
        Assert.Contains("host", reply.Error.Message);
    }

    [Fact]
    public void ExportsAndImportsSavedRequestsOverThePipe()
    {
        using var sidecar = SidecarProcess.Start();
        sidecar.Handshake();

        var request = HttpRequest.Get("https://example.com/users");
        request.Auth = new BearerAuth { Token = "tok-1" };
        sidecar.Send("save", new ClientMessage.SaveRequest { Name = "Users", Request = request });
        var savedId = Assert.IsType<ServerMessage.StateSaved>(sidecar.Recv().Msg).State.SavedRequests[0].Id;

        sidecar.Send("export", new ClientMessage.ExportSavedRequests
        {
            SavedRequestIds = [savedId],
            Format = ExportFormat.Http,
            IncludeSecrets = false,
        });
        var contents = Assert.IsType<ServerMessage.SavedRequestsExported>(sidecar.Recv().Msg).Contents;
        Assert.Contains("Authorization: Bearer {{token}}", contents);
        Assert.DoesNotContain("tok-1", contents);

        sidecar.Send("import", new ClientMessage.ImportSavedRequests { Contents = contents });
        var imported = Assert.IsType<ServerMessage.SavedRequestsImported>(sidecar.Recv().Msg);
        Assert.Equal(1u, imported.Imported);
        Assert.Equal(2, imported.State.SavedRequests.Count);
        Assert.Equal(new[] { "token" }, imported.UndefinedVariables);
        Assert.False(imported.PlainSecrets);

        sidecar.Send("bad", new ClientMessage.ImportSavedRequests { Contents = """{"tabs": []}""" });
        var rejected = Assert.IsType<ServerMessage.ImportRejected>(sidecar.Recv().Msg);
        Assert.Contains("not a file of saved requests", rejected.Message);
    }
}
