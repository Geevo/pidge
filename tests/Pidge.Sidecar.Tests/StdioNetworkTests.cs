using System.Text.Json.Nodes;
using Pidge.Core;
using Pidge.Protocol;

namespace Pidge.Sidecar.Tests;

using TestServer = Pidge.TestServer.TestServer;

public class StdioNetworkTests
{
    [Fact]
    public async Task HandshakesThenSendsARequest()
    {
        await using var server = await TestServer.StartAsync();

        using var sidecar = SidecarProcess.Start();
        sidecar.Handshake();

        sidecar.Send("req-1", new ClientMessage.SendRequest { Request = HttpRequest.Get(server.Url("/json")) });

        var envelope = sidecar.Recv();
        Assert.Equal("req-1", envelope.Id);
        var complete = Assert.IsType<ServerMessage.RequestComplete>(envelope.Msg);
        Assert.Equal(200, complete.Response.Status);
        Assert.NotNull(complete.HistoryEntry);
        Assert.Equal((ushort)200, complete.HistoryEntry.Status);
        Assert.True((bool)JsonNode.Parse(complete.Response.Body)!["ok"]!);
    }

    [Fact]
    public async Task AMalformedLineDoesNotKillTheProcess()
    {
        await using var server = await TestServer.StartAsync();

        using var sidecar = SidecarProcess.Start();
        sidecar.Handshake();

        sidecar.SendRaw("{ not json");
        var error = Assert.IsType<ServerMessage.ProtocolError>(sidecar.Recv().Msg);
        Assert.Contains("parse", error.Message);

        // Still usable afterwards.
        sidecar.Send("req-1", new ClientMessage.SendRequest { Request = HttpRequest.Get(server.Url("/json")) });
        Assert.IsType<ServerMessage.RequestComplete>(sidecar.Recv().Msg);
    }

    [Fact]
    public async Task ARequestCanBeCancelledById()
    {
        await using var server = await TestServer.StartAsync();

        using var sidecar = SidecarProcess.Start();
        sidecar.Handshake();

        var request = HttpRequest.Get(server.Url("/never"));
        var requestId = request.Id;

        sidecar.Send("req-1", new ClientMessage.SendRequest { Request = request });

        await Task.Delay(150);
        sidecar.Send("cancel-1", new ClientMessage.CancelRequest { RequestId = requestId });

        // The ack and the error can arrive in either order.
        var sawAck = false;
        var sawCancelledError = false;
        for (var i = 0; i < 2; i++)
        {
            switch (sidecar.Recv().Msg)
            {
                case ServerMessage.RequestCancelled cancelled:
                    Assert.True(cancelled.WasInFlight);
                    sawAck = true;
                    break;
                case ServerMessage.RequestError failed:
                    Assert.Equal(RequestErrorKind.Cancelled, failed.Error.Kind);
                    Assert.Null(failed.HistoryEntry);
                    sawCancelledError = true;
                    break;
                case var other:
                    Assert.Fail($"unexpected: {other.GetType().Name}");
                    break;
            }
        }

        Assert.True(sawAck && sawCancelledError);
    }

    [Fact]
    public async Task ConcurrentRequestsDoNotInterleaveTheirLines()
    {
        await using var server = await TestServer.StartAsync();

        using var sidecar = SidecarProcess.Start();
        sidecar.Handshake();

        for (var index = 0; index < 5; index++)
        {
            sidecar.Send($"req-{index}", new ClientMessage.SendRequest
            {
                Request = HttpRequest.Get(server.Url($"/delay/{50 - index * 10}")),
            });
        }

        var seen = new HashSet<string>();
        for (var i = 0; i < 5; i++)
        {
            var envelope = sidecar.Recv();
            Assert.IsType<ServerMessage.RequestComplete>(envelope.Msg);
            Assert.NotNull(envelope.Id);
            seen.Add(envelope.Id);
        }

        Assert.Equal(5, seen.Count);
    }
}
