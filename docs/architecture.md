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

## The window

The desktop window is undecorated. GTK on Wayland always draws its own header —
it does not implement the protocol KDE and other compositors use for server-side
decorations — and that header is far taller than the platform's own. Under
XWayland the same app got a normal, thin titlebar, but XWayland costs a copy and
composite per frame and made scrolling visibly laggy.

So the app draws its own: the tab strip doubles as the title bar, with the
leftover space as a drag region and minimise/maximise/close at its end. That is
one row of chrome rather than two.

`tao` only calls `set_decorated(false)` on Linux and adds nothing back, so an
undecorated window has no resize edges at all. `ResizeEdges` supplies eight
invisible strips that call `startResizeDragging`.

All of this hangs off `PlatformBridge.window`, which is optional and absent in
VS Code, where the editor owns the frame. Every call through it is caught:
`getCurrentWindow()` throws outright when the Tauri internals are missing, and
an effect that throws unmounts the entire application — window chrome must not
be able to do that.

## Panes

`SplitPane` arranges the request and response either as rows or as columns, with
a divider that drags, takes arrow keys, and resets to even on a double click.
The layout and the split are persisted in `Settings`, so they survive a restart.

Two details worth knowing:

- The size is held in a ref as well as in state. A `pointermove` can arrive
  before React has re-rendered the `pointerdown`, and reading the state would
  drop that movement — a fast drag could do nothing at all.
- The new size is handed up only on release. Committing on every frame would be
  a hundred state updates and a hundred debounced writes to disk for one drag.

## Response bodies

A JSON body is rendered in the same CodeMirror the request body uses, in
read-only mode, which brings syntax highlighting and fold arrows for free rather
than needing a bespoke tree view. `Collapse all` and `Expand all` run
CodeMirror's own commands against the view.

Folding is enabled only for JSON that actually parses. Invalid JSON is shown
verbatim with no language attached, so a broken payload does not render as a
wall of red.

### Giving the editor a height

CodeMirror only virtualizes when it has a bounded height. Dropped into an
ordinary `overflow: auto` panel it grows to the full document instead — a
172 KB response measured 327,699 px tall — and lays out every line, which made
scrolling crawl.

So a panel whose child is an editor gets `.ac-scroll--flush`: it becomes a flex
column with hidden overflow and hands scrolling to `.cm-scroller`. Both the
request body and the response body do this. After the fix the response editor
scrolls fractionally cheaper than a plain `<div>` of comparable length.

The scrolling regions also set `contain: paint`. A dialog floats above a
translucent full-screen backdrop, and without containment a scroll inside it can
repaint everything underneath rather than just the scrolled region.

Above 2 MB the body falls back to a plain `<pre>` with a note. Highlighting and
folding a document that size costs more than it is worth, and the fallback still
shows everything.

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

## TLS

reqwest is built on rustls here, and reqwest's `rustls` feature pulls in
`rustls-platform-verifier`. That means the client already trusts whatever the
operating system trusts — the Windows certificate store, the macOS keychain, the
system CA bundle on Linux — with no configuration at all.

`crates/http-engine/src/tls.rs` covers the two things the OS store cannot:

- **An extra CA.** `tls_certs_merge()` maps to `Verifier::new_with_extra_roots`,
  so an internal root is _added_ to the system store rather than replacing it. A
  corporate CA should not cost you the ability to reach the rest of the internet.
  Turning `useSystemRoots` off switches to `tls_certs_only()`, for talking to one
  internal host and nothing else. Asking for neither is refused rather than
  quietly trusting nothing.
- **A client certificate.** rustls accepts PEM only, but Windows exports
  `.p12`/`.pfx`, so a PKCS#12 bundle is unpacked in process with `p12-keystore`
  and re-encoded as PEM. In process deliberately: shelling out to `openssl`
  would add a tool Windows does not ship, and would put the password in the
  process list where any other user could read it.

The format is detected from the file contents, not the extension, so a `.crt`
holding PEM works and a `.pem` holding DER does too.

reqwest defers parsing a DER certificate until the client is built, so a bad
certificate surfaces at `build()` rather than where it was loaded. When any TLS
setting is non-default, a build failure is reported as a TLS error naming the
certificate settings, because that is what it will be.

### Rebuilding the engine

TLS settings shape the reqwest client, which is built once. `Session` therefore
holds its engine behind a `Mutex` and rebuilds it when `replace_state` sees an
`EngineConfig` that differs from the live one, so adding a CA takes effect on the
next send rather than the next launch.

State is saved _before_ the rebuild is attempted. If a certificate path is wrong
the error still reaches the UI, but the setting persists — otherwise the dialog
reporting the error would have nothing left to correct.

## Room left deliberately

Proxies and client-certificate selection per host are not wired up. The reqwest
client builder in `HttpEngine::new` is the one place either would go.
