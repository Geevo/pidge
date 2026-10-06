using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Pidge.Core;
using Pidge.Protocol;
using Pidge.Session;
using Pidge.Storage;

namespace Pidge.Sidecar;

internal static class SidecarServer
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Reads protocol lines until stdin closes or a shutdown arrives.
    ///
    /// Every handler below is a direct call into <see cref="AppSession"/>; the
    /// interesting logic lives there and is shared with the desktop app.
    /// </summary>
    /// <exception cref="RequestErrorException">The session could not start; the reason was sent as a protocol error first.</exception>
    public static async Task RunAsync(Store store, Stream input, Stream output)
    {
        var outbound = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

        // One writer, so concurrent requests cannot interleave their lines.
        var writer = Task.Run(async () =>
        {
            await foreach (var line in outbound.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    await output.WriteAsync(Utf8.GetBytes(line)).ConfigureAwait(false);
                    await output.FlushAsync().ConfigureAwait(false);
                }
                catch (IOException)
                {
                    break;
                }
            }
        });

        AppSession session;
        try
        {
            session = AppSession.Start(store);
        }
        catch (RequestErrorException e)
        {
            Send(outbound.Writer, new ServerEnvelope(null, new ServerMessage.ProtocolError { Message = e.Error.Message }));
            outbound.Writer.Complete();
            await writer.ConfigureAwait(false);
            throw;
        }

        var sends = new InFlightSends();
        var handshaken = false;
        using var reader = new StreamReader(input, Utf8);

        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            ClientEnvelope envelope;
            try
            {
                envelope = WireFormat.DecodeClientLine(line);
            }
            catch (JsonException e)
            {
                Send(outbound.Writer, new ServerEnvelope(null, new ServerMessage.ProtocolError
                {
                    Message = $"could not parse message: {e.Message}",
                }));
                continue;
            }

            if (WireFormat.VersionMismatch(envelope.V) is { } mismatch)
            {
                Send(outbound.Writer, new ServerEnvelope(envelope.Id, mismatch));
                break;
            }

            var id = envelope.Id;

            if (!handshaken && envelope.Msg is not ClientMessage.Handshake)
            {
                Send(outbound.Writer, new ServerEnvelope(id, new ServerMessage.ProtocolError
                {
                    Message = "handshake required before any other message",
                }));
                continue;
            }

            if (envelope.Msg is ClientMessage.Shutdown)
            {
                break;
            }

            if (envelope.Msg is ClientMessage.Handshake handshake)
            {
                handshaken = true;
                Trace.TraceInformation($"handshake client={handshake.ClientName} version={handshake.ClientVersion}");
            }

            ServerEnvelope? reply;
            try
            {
                reply = Handle(session, outbound.Writer, sends, id, envelope.Msg);
            }
            catch (Exception e)
            {
                // A bug in one handler must not take the window's sidecar down
                // with it; the extension shows this the way it shows any other.
                Trace.TraceError($"could not handle {envelope.Msg.GetType().Name}: {e}");
                reply = new ServerEnvelope(id, new ServerMessage.ProtocolError { Message = e.Message });
            }
            if (reply is not null)
            {
                Send(outbound.Writer, reply);
            }
        }

        // Sends still running answer before the pipe closes, as they would
        // have had stdin stayed open.
        await sends.WhenAll().ConfigureAwait(false);
        outbound.Writer.Complete();
        await writer.ConfigureAwait(false);
        session.Dispose();
    }

    /// <summary>The reply to one message, or null when it answers later (a send) or not at all.</summary>
    private static ServerEnvelope? Handle(
        AppSession session,
        ChannelWriter<string> outbound,
        InFlightSends sends,
        string? id,
        ClientMessage message)
    {
        switch (message)
        {
            case ClientMessage.Handshake:
                return new ServerEnvelope(id, new ServerMessage.HandshakeOk
                {
                    ServerName = "api-client-sidecar",
                    ServerVersion = AppVersion.Current,
                    ProtocolVersion = WireFormat.ProtocolVersion,
                });

            case ClientMessage.SendRequest send:
                if (id is null)
                {
                    return new ServerEnvelope(null, new ServerMessage.ProtocolError
                    {
                        Message = "sendRequest needs a correlation id",
                    });
                }

                // Requests run concurrently; the writer serializes replies.
                sends.Track(Task.Run(async () =>
                {
                    var outcome = await session.SendWithOverridesAsync(send.Request, send.Variables).ConfigureAwait(false);
                    ServerMessage reply = (outcome.Response, outcome.Error) switch
                    {
                        ({ } response, _) => new ServerMessage.RequestComplete
                        {
                            Response = response,
                            HistoryEntry = outcome.HistoryEntry,
                        },
                        (null, { } error) => new ServerMessage.RequestError
                        {
                            Error = error,
                            HistoryEntry = outcome.HistoryEntry,
                        },
                        _ => new ServerMessage.ProtocolError { Message = "the request produced no result" },
                    };
                    Send(outbound, new ServerEnvelope(id, reply));
                }));
                return null;

            case ClientMessage.GenerateCode generate:
                // Nothing here touches the network, so it answers in line
                // rather than on its own task.
                try
                {
                    var code = session.GenerateCode(generate.Request, generate.Variables, generate.Target);
                    return new ServerEnvelope(id, new ServerMessage.CodeGenerated { Code = code });
                }
                catch (RequestErrorException e)
                {
                    return new ServerEnvelope(id, new ServerMessage.CodeGenerated { Error = e.Error });
                }

            case ClientMessage.CancelRequest cancel:
                return new ServerEnvelope(cancel.RequestId, new ServerMessage.RequestCancelled
                {
                    WasInFlight = session.Cancel(cancel.RequestId),
                });

            case ClientMessage.ExportSavedRequests export:
                return new ServerEnvelope(id, new ServerMessage.SavedRequestsExported
                {
                    Contents = session.ExportSavedRequests(export.SavedRequestIds, export.Format, export.IncludeSecrets),
                });

            case ClientMessage.ImportSavedRequests import:
                try
                {
                    var outcome = session.ImportSavedRequests(import.Contents);
                    return new ServerEnvelope(id, new ServerMessage.SavedRequestsImported
                    {
                        State = outcome.State,
                        Imported = (uint)outcome.Imported,
                        UndefinedVariables = outcome.UndefinedVariables,
                        Skipped = outcome.Skipped,
                        PlainSecrets = outcome.PlainSecrets,
                    });
                }
                catch (SessionException e) when (e.Kind == SessionErrorKind.Storage)
                {
                    return new ServerEnvelope(id, new ServerMessage.StorageError { Message = e.Message });
                }
                catch (SessionException e)
                {
                    return new ServerEnvelope(id, new ServerMessage.ImportRejected { Message = e.Message });
                }

            case ClientMessage.LoadState:
                return new ServerEnvelope(id, new ServerMessage.StateLoaded
                {
                    State = session.Snapshot(),
                    Recovery = session.Recovery?.Message,
                    StoragePath = session.StoragePath,
                    Version = AppVersion.Current,
                });

            case ClientMessage.SaveState save:
                try
                {
                    session.ReplaceState(save.State);
                    return StateSaved(session, id);
                }
                catch (SessionException e)
                {
                    return new ServerEnvelope(id, new ServerMessage.StorageError { Message = e.Message });
                }

            case ClientMessage.SaveRequest save:
                return Saving(session, id, () => session.SaveRequest(save.SavedRequestId, save.Name, save.Request));

            case ClientMessage.DeleteSavedRequest delete:
                return Saving(session, id, () => session.DeleteSavedRequest(delete.SavedRequestId));

            case ClientMessage.ClearHistory:
                return Saving(session, id, session.ClearHistory);

            case ClientMessage.DeleteHistoryEntry delete:
                return Saving(session, id, () => session.DeleteHistoryEntry(delete.HistoryEntryId));

            default:
                throw new ArgumentOutOfRangeException(nameof(message), message.GetType().Name);
        }
    }

    private static ServerEnvelope Saving(AppSession session, string? id, Action change)
    {
        try
        {
            change();
            return StateSaved(session, id);
        }
        catch (StorageException e)
        {
            return new ServerEnvelope(id, new ServerMessage.StorageError { Message = e.Message });
        }
    }

    private static ServerEnvelope StateSaved(AppSession session, string? id) =>
        new(id, new ServerMessage.StateSaved { State = session.Snapshot() });

    private static void Send(ChannelWriter<string> outbound, ServerEnvelope envelope)
    {
        string line;
        try
        {
            line = WireFormat.EncodeLine(envelope);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            Trace.TraceError($"could not encode an outgoing message: {e.Message}");
            return;
        }
        outbound.TryWrite(line);
    }

    /// <summary>The sends still running, so the last of their replies is not lost at exit.</summary>
    private sealed class InFlightSends
    {
        private readonly HashSet<Task> _tasks = [];
        private readonly Lock _lock = new();

        public void Track(Task task)
        {
            lock (_lock)
            {
                _tasks.Add(task);
            }
            task.ContinueWith(
                finished =>
                {
                    if (finished.Exception is { } error)
                    {
                        Trace.TraceError($"a send failed: {error.InnerException?.Message ?? error.Message}");
                    }
                    lock (_lock)
                    {
                        _tasks.Remove(finished);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public Task WhenAll()
        {
            lock (_lock)
            {
                return Task.WhenAll(_tasks.ToArray()).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }
}
