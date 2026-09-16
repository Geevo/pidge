# Architecture

## The shape of it

```
                       ┌──────────────────────────┐
  Desktop              │   packages/ui  (React)   │            VS Code
                       │   PlatformBridge         │
                       └───────────┬──────────────┘
              ┌────────────────────┴────────────────────┐
              │                                         │
     ┌────────▼─────────┐                    ┌──────────▼───────────┐
     │ Tauri commands   │                    │ extension host (TS)  │
     │ apps/desktop     │                    │ apps/vscode          │
     └────────┬─────────┘                    └──────────┬───────────┘
              │                                         │ stdin/stdout
              │                              ┌──────────▼───────────┐
              │                              │ crates/sidecar       │
              │                              └──────────┬───────────┘
              └────────────────┬─────────────────────────┘
                     ┌─────────▼──────────┐
                     │ crates/session     │  the application service
                     └─────────┬──────────┘
          ┌────────────────────┼────────────────────┐
  ┌───────▼────────┐  ┌────────▼────────┐  ┌────────▼────────┐
  │ http-engine    │  │ storage         │  │ variables       │
  └───────┬────────┘  └─────────────────┘  └─────────────────┘
          │
   ┌──────▼───────┐
   │ core         │  models + normalized errors
   └──────────────┘
```

## The rule that keeps it honest

`crates/session` is the only thing either frontend is allowed to drive. The
Tauri commands in `apps/desktop/src-tauri/src/commands.rs` and the message
handlers in `crates/sidecar/src/session.rs` are both thin: they deserialize,
call one `Session` method, and serialize the result.

This is deliberate. If behaviour lived in the adapters, the two platforms would
drift the moment one of them grew a feature. As it is, "does the desktop app do
X?" and "does the VS Code extension do X?" have the same answer by construction.

`crates/http-engine` goes further and knows nothing about persistence either.
It takes an `HttpRequest`, sends it, and returns an `HttpResponse` or a
`RequestError`. That is why the engine tests can drive it directly, and why a
CLI could be added without touching it.

## Crates

| Crate         | Owns                                                                      |
| ------------- | ------------------------------------------------------------------------- |
| `core`        | `HttpRequest`, `HttpResponse`, `RequestError`, secret-header redaction    |
| `variables`   | `{{name}}` substitution and `Environment`                                 |
| `http-engine` | reqwest client, request building, cancellation, timing, response limits   |
| `storage`     | `AppState`, atomic writes, schema version and migrations, history cap     |
| `session`     | engine + store + in-memory state; every operation a frontend can perform  |
| `protocol`    | the newline-delimited JSON messages between the extension and the sidecar |
| `sidecar`     | the binary: a line reader around `Session`                                |
| `testserver`  | a local HTTP/1.1 server for the tests                                     |

## Types cross the boundary once

The Rust types are the single source of truth. `ts-rs` derives TypeScript
declarations from them and writes them to `packages/ui/src/generated/`, which is
committed so a checkout typechecks without running `cargo`.

```bash
pnpm gen:types   # cargo test --workspace export_bindings
```

`.cargo/config.toml` sets `TS_RS_EXPORT_DIR`, so `cargo test` keeps the bindings
current as a side effect of running the suite. Nothing in `generated/` should be
edited by hand: change the Rust struct and regenerate.

Response bodies are `Vec<u8>` in Rust and base64 `string` in TypeScript, because
JSON has no byte array and an array of numbers would be ruinous for a 50 MB
response. `packages/ui/src/lib/base64.ts` decodes it.

## The platform bridge

```ts
interface PlatformBridge {
  sendRequest(request: HttpRequest): Promise<SendOutcome>;
  cancelRequest(requestId: string): Promise<void>;
  loadState(): Promise<LoadedState>;
  saveState(state: AppState): Promise<AppState>;
  saveRequest(input: SaveRequestInput): Promise<AppState>;
  deleteSavedRequest(savedRequestId: string): Promise<AppState>;
  clearHistory(): Promise<AppState>;
  subscribe?(listener: (command: HostCommand) => void): () => void;
  readonly platform: string;
}
```

Components never branch on the platform. `App` takes a bridge and that is the
whole seam; the tests pass a fake in place of a process.

## Who owns history

History is owned by `Session`, not by the UI, and `Session::replace_state`
deliberately ignores the `history` field of whatever the UI sends. Otherwise a
UI that took a snapshot, sent a request, and then wrote its snapshot back would
erase the row that send had just created.

So that the panel still updates live, `Session::send` returns the `HistoryEntry`
it recorded alongside the response, and the reducer prepends it. `clear_history`
is the only way a frontend can empty it.

## Errors

`RequestError` has a `kind` the UI can switch on, a `message` written for a
person, and an optional `detail` holding the flattened source chain. The detail
goes behind a disclosure triangle; the message goes in the response pane. Errors
are never only a toast — they appear where the response would have been, next to
the request that caused them.

## Request building

`crates/http-engine/src/build.rs` turns a resolved request into a reqwest
builder. Two decisions worth knowing:

- **An explicit `Authorization` header wins.** If one is set and enabled, the
  Auth tab is skipped and a warning is attached to the response. Silently
  overwriting what someone typed would be worse than either alternative.
- **A `Content-Type` you set is never replaced.** The body type supplies one
  only when you have not.

## Cancellation

`CancellationHandle` wraps a `CancellationToken`. `HttpEngine::execute` selects
on it both before sending and between body chunks, so cancelling mid-download
works, not just cancelling before the connection opens. `CancellationRegistry`
maps request ids to handles so `cancel_http_request(id)` needs no bookkeeping in
the adapters.

A cancelled request is not recorded in history: it never really happened.

## Response size

Bodies are streamed and cut off at `max_response_bytes` (50 MB by default), with
`truncated: true` on the response and a note in the status line. Truncating
beats erroring: you still get to look at the first 50 MB.

## Room left deliberately

`EngineConfig` already carries `accept_invalid_certs`, and the reqwest client
builder is the one place that would need to change for custom CAs, proxies, or
client certificates. None of it is wired to UI, because none of it is needed to
type a URL and press Send.
