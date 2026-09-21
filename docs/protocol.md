# Sidecar protocol

The VS Code extension host and the Rust sidecar exchange one JSON object per
line over stdin/stdout.

- **stdout carries protocol messages and nothing else.** Logs go to stderr.
- Every message carries the protocol version, a message type, and a correlation
  id where one applies.
- Secret header values are never logged, at either end.

Defined in `crates/protocol/src/lib.rs`; the TypeScript declarations are
generated from it into `packages/ui/src/generated/`.

## Envelope

```json
{ "v": 2, "id": "req-1", "msg": { "type": "sendRequest", "...": "..." } }
```

| Field | Meaning                                                     |
| ----- | ----------------------------------------------------------- |
| `v`   | protocol version; `PROTOCOL_VERSION` in Rust and TypeScript |
| `id`  | correlation id, or `null` for a message with no reply       |
| `msg` | the message, tagged by `type`                               |

## Handshake

The extension sends `handshake` first; nothing else is accepted before it.

```json
{"v":2,"id":"hs","msg":{"type":"handshake","clientName":"vscode","clientVersion":"0.1.0"}}
{"v":2,"id":"hs","msg":{"type":"handshakeOk","serverName":"api-client-sidecar","serverVersion":"0.1.0","protocolVersion":2}}
```

If the versions do not match, the sidecar replies `handshakeError` and stops,
rather than guessing at a message shape it does not understand:

```json
{
  "v": 2,
  "id": "hs",
  "msg": {
    "type": "handshakeError",
    "message": "Protocol mismatch: …",
    "expectedProtocolVersion": 2,
    "receivedProtocolVersion": 3
  }
}
```

`api-client-sidecar --protocol-version` prints the version without starting a
session, which is useful when diagnosing a mismatched install.

## Requests

Extension to sidecar:

| `type`               | Payload                             |
| -------------------- | ----------------------------------- |
| `handshake`          | `clientName`, `clientVersion`       |
| `sendRequest`        | `request`, `variables`              |
| `cancelRequest`      | `requestId`                         |
| `generateCode`       | `request`, `target`, `variables`    |
| `loadState`          | —                                   |
| `saveState`          | `state`                             |
| `saveRequest`        | `savedRequestId`, `name`, `request` |
| `deleteSavedRequest` | `savedRequestId`                    |
| `clearHistory`       | —                                   |
| `shutdown`           | —                                   |

Sidecar to extension:

| `type`             | Payload                                                         |
| ------------------ | --------------------------------------------------------------- |
| `handshakeOk`      | `serverName`, `serverVersion`, `protocolVersion`                |
| `handshakeError`   | `message`, `expectedProtocolVersion`, `receivedProtocolVersion` |
| `requestComplete`  | `response`, `historyEntry`                                      |
| `requestError`     | `error`, `historyEntry`                                         |
| `requestCancelled` | `wasInFlight`                                                   |
| `codeGenerated`    | `code`, `error`                                                 |
| `stateLoaded`      | `state`, `recovery`, `storagePath`                              |
| `stateSaved`       | `state`                                                         |
| `storageError`     | `message`                                                       |
| `protocolError`    | `message`                                                       |

`sendRequest` uses the request's own id as its correlation id, so
`cancelRequest` needs nothing the UI does not already have.

`generateCode` sends nothing: `target` is one of `curl`, `powershell`, `python`
or `csharp`, and the reply carries either the code or the reason there is none —
a URL that will not parse, or a variable with no value. It is answered in line
rather than on its own task, because nothing about it touches the network.

## Binary bodies

`HttpResponse.body` is base64 on the wire. It is the boring choice: JSON has no
byte array, and an array of numbers would be roughly four times the size for a
response that may be tens of megabytes.

## Concurrency

Requests run concurrently inside the sidecar. A single writer task serializes
stdout, so two responses finishing at the same moment cannot interleave their
lines. The extension host reassembles lines from stream chunks and routes each
one by correlation id.

## Failure handling

- An unparseable line gets a `protocolError` and the process keeps running.
- If the sidecar exits, every pending call is rejected with a message pointing
  at **API Client: Restart Request Engine**.
- The extension never downloads a binary. It uses the one packaged for the
  current platform, or the path in `apiClient.sidecarPath`, or a local
  `target/{debug,release}` build when running from source.

## Tracing

Set `apiClient.trace` to `true` to log message types and correlation ids to the
**API Client** output channel. Payloads are not logged.
