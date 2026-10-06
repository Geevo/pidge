using Pidge.Core;
using Pidge.Storage;
using Pidge.TestServer;
using Pidge.Variables;

namespace Pidge.Session.Tests;

using TestServer = Pidge.TestServer.TestServer;

public class SessionNetworkTests
{
    private static AppSession Start(TempDir dir) => AppSession.Start(Store.InDir(dir.Path));

    [Fact]
    public async Task ASuccessfulSendLandsInHistory()
    {
        await using var server = await TestServer.StartAsync();
        using var dir = new TempDir();
        using var session = Start(dir);

        var response = await session.SendAsync(HttpRequest.Get(server.Url("/json")));
        Assert.Equal(200, response.Status);

        var state = session.Snapshot();
        Assert.Single(state.History);
        Assert.Equal((ushort)200, state.History[0].Status);
        Assert.Null(state.History[0].Error);
        Assert.NotNull(state.History[0].DurationMs);
    }

    [Fact]
    public async Task ACancelledSendIsNotRecorded()
    {
        await using var server = await TestServer.StartAsync();
        using var dir = new TempDir();
        using var session = Start(dir);

        var request = HttpRequest.Get(server.Url("/never"));
        var id = request.Id;

        var canceller = Task.Run(async () =>
        {
            await Task.Delay(60);
            Assert.True(session.Cancel(id));
        });

        var error = await Assert.ThrowsAsync<RequestErrorException>(() => session.SendAsync(request));
        await canceller;
        Assert.Equal(RequestErrorKind.Cancelled, error.Error.Kind);
        Assert.Empty(session.Snapshot().History);
        Assert.Equal(0, session.InFlight);
    }

    [Fact]
    public async Task HistoryIsPersistedAcrossRestarts()
    {
        await using var server = await TestServer.StartAsync();
        using var dir = new TempDir();

        using (var session = Start(dir))
        {
            await session.SendAsync(HttpRequest.Get(server.Url("/json")));
        }

        using var reopened = Start(dir);
        Assert.Single(reopened.Snapshot().History);
    }

    [Fact]
    public async Task VariablesComeFromTheActiveEnvironment()
    {
        await using var server = await TestServer.StartAsync();
        using var dir = new TempDir();
        using var session = Start(dir);

        var state = session.Snapshot();
        state.Environments =
        [
            new VariableEnvironment { Id = "env-1", Name = "Local", Variables = [new KeyValueEntry("baseUrl", server.BaseUrl)] },
        ];
        state.ActiveEnvironmentId = "env-1";
        session.ReplaceState(state);

        var response = await session.SendAsync(HttpRequest.Get("{{baseUrl}}/json"));
        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task ExplicitOverridesWinOverTheEnvironment()
    {
        await using var server = await TestServer.StartAsync();
        using var dir = new TempDir();
        using var session = Start(dir);

        var state = session.Snapshot();
        state.Environments =
        [
            new VariableEnvironment
            {
                Id = "env-1",
                Name = "Local",
                Variables = [new KeyValueEntry("baseUrl", "http://127.0.0.1:1")],
            },
        ];
        state.ActiveEnvironmentId = "env-1";
        session.ReplaceState(state);

        var outcome = await session.SendWithOverridesAsync(
            HttpRequest.Get("{{baseUrl}}/json"),
            new Dictionary<string, string> { ["baseUrl"] = server.BaseUrl });

        Assert.NotNull(outcome.Response);
        Assert.Equal(200, outcome.Response.Status);
    }

    [Fact]
    public async Task ASendReturnsTheHistoryRowItCreated()
    {
        await using var server = await TestServer.StartAsync();
        using var dir = new TempDir();
        using var session = Start(dir);

        var outcome = await session.SendWithOverridesAsync(HttpRequest.Get(server.Url("/json")));

        Assert.Null(outcome.Error);
        Assert.Equal(200, outcome.Response!.Status);
        Assert.Equal((ushort)200, outcome.HistoryEntry!.Status);
    }

    [Fact]
    public async Task SavingStateCannotEraseHistoryRecordedSinceTheSnapshot()
    {
        await using var server = await TestServer.StartAsync();
        using var dir = new TempDir();
        using var session = Start(dir);

        // A UI takes a snapshot, then a send happens before it writes back.
        var stale = session.Snapshot();
        await session.SendAsync(HttpRequest.Get(server.Url("/json")));
        session.ReplaceState(stale);

        Assert.Single(session.Snapshot().History);
    }

    [Fact]
    public async Task ClearHistoryStillClearsIt()
    {
        await using var server = await TestServer.StartAsync();
        using var dir = new TempDir();
        using var session = Start(dir);

        await session.SendAsync(HttpRequest.Get(server.Url("/json")));
        session.ClearHistory();

        Assert.Empty(session.Snapshot().History);
    }

    [Fact]
    public async Task ChangingTlsSettingsRebuildsTheEngine()
    {
        var ca = new TestCa();
        await using var server = await TestServer.StartTlsAsync(ca, ClientAuth.None);
        using var dir = new TempDir();
        var caPath = dir.Join("ca.pem");
        File.WriteAllText(caPath, ca.Pem);

        using var session = Start(dir);

        // The CA is unknown to the machine, so this fails first.
        var error = await Assert.ThrowsAsync<RequestErrorException>(() => session.SendAsync(HttpRequest.Get(server.Url("/json"))));
        Assert.Equal(RequestErrorKind.Tls, error.Error.Kind);

        var state = session.Snapshot();
        state.Settings.Tls.ExtraCaFiles = [caPath];
        session.ReplaceState(state);

        // No restart: the same session now trusts it.
        var response = await session.SendAsync(HttpRequest.Get(server.Url("/json")));
        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task SettingsThatDoNotTouchTheEngineLeaveItAlone()
    {
        await using var server = await TestServer.StartAsync();
        using var dir = new TempDir();
        using var session = Start(dir);

        var state = session.Snapshot();
        state.Settings.WrapResponseLines = true;
        session.ReplaceState(state);

        var response = await session.SendAsync(HttpRequest.Get(server.Url("/json")));
        Assert.Equal(200, response.Status);
    }
}
