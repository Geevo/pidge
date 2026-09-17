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
  readonly window?: WindowControls;
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

## Themes

Every colour is a token on `:root`, so a theme is one block redefining them.
`useTheme` writes the resolved palette to `data-theme` on the document element.

"System" is resolved in `useTheme` with `matchMedia`, not left to the
`prefers-color-scheme` block alone, and it listens so that a desktop changing
its mind reaches a window that is already open. The media query still matters:
it paints the first frame, before React has mounted. It is keyed on
`:root:not([data-theme])` rather than "not light", or a named light palette
would have had the system's dark values layered over it in that first frame.

The base tokens on `:root` are the light palette, so a theme only restates what
differs from it. That is why the warm light block is a third the size of the
warm dark one rather than a copy.

`color-scheme` is set per theme as well as the colours, so the browser's own
furniture — scrollbars, the caret, the right-click menu — follows.

### The controls the platform draws

A native `<select>` can be styled shut but not open. The list that drops down is
the platform's: in a WebKit webview on Linux it is a GTK menu in the desktop's
own theme, which ignores `option { background-color }` entirely and left a light
palette with a black list on a dark desktop.

`gtk-application-prefer-dark-theme` does not rescue it either. Breeze — KDE's
theme — ships its dark variant as a separate theme rather than as a variant, so
the preference changes nothing. That was measured with a WebKitGTK window rather
than assumed.

So `Select` draws both halves: a `<button role="combobox">` and a
`<ul role="listbox">`, with arrow keys, Home/End, Escape, and a click outside to
close. The list is rendered into `document.body` and positioned against the
trigger, because the dialog and the tab strip both clip. It closes on scroll and
on resize rather than following the trigger around.

`window.prompt` and `window.confirm` went the same way, and for the same reason.
The prompt's entry is sized by the platform and was too narrow in a WebKit
webview to show the URL it was suggesting, and both look like a different
application on each operating system. `PromptDialog` asks for a name — the
suggestion selected backwards, so a long URL shows from its start rather than
its query string — and `ConfirmDialog` asks the three yes-or-no questions:
discarding a tab's changes, clearing history, deleting a saved request.

Whether to ask before closing a tab is `needsCloseConfirmation`, and it moved
out of `useApiClient` when the dialog did. A hook that puts a dialog up cannot
be driven by anything without a screen, and the sidecar has none.

That leaves the menu the webview puts up on a right click, which is still the
platform's and stays in the desktop's colours. Passing the palette down to the
window was tried and removed: on Linux it sets
`gtk-application-prefer-dark-theme`, which changes neither that menu under
Breeze nor what `prefers-color-scheme` reports, so it was machinery that did
nothing.

A palette cannot be judged from its name, so Settings applies one as soon as it
is picked and `App` holds that choice separately from the saved settings.
Cancelling drops it and the saved theme comes back. The preview lives in `App`
rather than in the dialog because `App` is what writes `data-theme`; two writers
of the same attribute would make the result depend on the order of effects.

Method colours are deliberately not themed. They are Swagger's palette because
they carry meaning, and a POST should be the same green wherever it is read.

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

Drawing the buttons means drawing the right ones. A single set looks foreign
everywhere except where it came from, so `window_buttons` reports which desktop
the app is running on — Windows, KDE or anything else, read from
`XDG_CURRENT_DESKTOP` — and `WindowChrome` keeps all three sets. Breeze draws
chevrons and, once the window is maximised, a diamond in place of the maximise
chevron; Adwaita a low bar and two rings; Windows a line and a square. The
button behind the glyph differs as well: Windows fills a tall rectangle, Breeze
lights a circle under the pointer, Adwaita keeps a faint circle there all the
time. The sizes and the 24px spacing were measured off real title bars on a
Plasma desktop rather than guessed.

The answer arrives before the first render, because swapping the buttons
afterwards would be visible. It costs one `invoke` behind the boot screen.

Which of the two middle glyphs is drawn follows the window itself, through
`onResized`, rather than the click that asked for it. A compositor maximises
when it is ready, so `isMaximized()` straight after `toggleMaximize()` still
reports the old state and the glyph ends up a step behind; the event also
covers the ways a window is maximised without the button, such as a double
click on the title bar or a keyboard shortcut.

All of this hangs off `PlatformBridge.window`, which is optional and absent in
VS Code, where the editor owns the frame. Every call through it is caught:
`getCurrentWindow()` throws outright when the Tauri internals are missing, and
an effect that throws unmounts the entire application — window chrome must not
be able to do that.

## Before the app has loaded

A webview paints white until something tells it otherwise, and the bundle that
would tell it is the thing being waited for. `apps/desktop/index.html` therefore
carries its own `<style>`: the page background, following
`prefers-color-scheme`, and a spinner inside `#root` that React's first render
replaces. Both repeat values from `styles.css` on purpose — they have to work
when that file has not arrived.

Measured in the dev build, where the module graph is slowest: the HTML is parsed
at 121 ms and React's first frame lands at 1077 ms. That was a second of blank
window; it is now a second of the app's own background.

The spinner fades in at 400 ms, so a start quicker than that shows nothing at
all rather than a flash of loading, and it holds still under
`prefers-reduced-motion`.

No second window: a splash window would have to be created, positioned,
themed and then closed, and it cannot show anything the first window cannot
show earlier.

## Panes

`SplitPane` arranges the request and response either as rows or as columns, with
a divider that drags, takes arrow keys, and resets to even on a double click.

The layout is a preference and lives in `Settings`. The split position is
per-tab: `ScratchTab::split_percent` holds it, so comparing a long response in
one tab does not squash the request editor in another. `None` falls back to
`Settings::split_percent`, which each drag also updates — that way existing tabs
stay where they were put, and a new tab opens where you were last working rather
than always at the original default. Both persist with the tab.

Two details worth knowing:

- The size is held in a ref as well as in state. A `pointermove` can arrive
  before React has re-rendered the `pointerdown`, and reading the state would
  drop that movement — a fast drag could do nothing at all.
- The new size is handed up only on release. Committing on every frame would be
  a hundred state updates and a hundred debounced writes to disk for one drag.

## Response bodies

A body is rendered in the same CodeMirror the request body uses, in read-only
mode, which brings syntax highlighting and fold arrows for free rather than
needing a bespoke tree view. `Collapse all` and `Expand all` run CodeMirror's
own commands against the view.

`syntaxForMime` picks the language from the response's content type: JSON, HTML,
XML, YAML, CSS or JavaScript, and plain text for anything else. The content type
is the only evidence used. Sniffing the bytes gets HTML and XML wrong in both
directions, and a document highlighted as the wrong language is harder to read
than one with no highlighting at all. XHTML is the one special case — it matches
both rules and is treated as HTML, which is how it reads.

Folding is offered for every language, since each knows its own blocks, and
withheld from plain text, which has none. Invalid JSON is the exception in the
other direction: it is shown verbatim with no language attached, so a broken
payload does not render as a wall of red.

Only JSON is reformatted. Whitespace carries meaning in HTML and YAML, so
re-indenting them would change the document you asked to look at.

The six languages are bundled rather than fetched on demand: they cost 181 KB
of the frontend bundle, which is read from disk, and a body should be
highlighted the moment it arrives.

The highlight style is one set of rules for all of them, built from the same
palette as the rest of the app — names in the accent colour, text green, numbers
amber, keywords orange, comments faint, and anything the parser rejected in the
danger colour.

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

## Settings

Three sections — General, Certs, About — behind one draft. The draft is a single
`Settings` object shared by all three rather than one per section, so an edit
made under Certs is still there when Save is pressed from General.

The dialog has a fixed height rather than one that fits its contents. The
sections are not the same length — General needs about 220px of body and Certs
about 430px — and a dialog that jumps a quarter of its height when a tab is
pressed is disorienting. It is sized to the tallest, still capped at 80% of the
window, and anything past that scrolls.

Certs groups the three decisions it holds — what to trust, what to present, and
what to skip checking — because as a flat stack of fields they ran together.
Turning verification off is boxed and tinted rather than sitting in line with
the rest, since it is not a preference among preferences.

Each path field has a Browse button when `PlatformBridge.pickFile` is present:
the desktop supplies it through Tauri's dialog plugin, VS Code through
`showOpenDialog` on the extension host. It is optional like `window`, and the
buttons are absent on a host without one — typing the path still works, which is
what the field did before.

About shows the host's own version, which travels with the state: `LoadedState`
already carried the storage path, and the version goes the same way rather than
being baked into the frontend at build time, where it could drift from the
binary actually running.

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
- **A parameter in both the URL and the table is sent once.** They are one
  thing shown twice — editing the table rewrites the URL's query, and typing a
  query in the URL fills the table — so appending the table on top of the URL
  sent every parameter twice. The match is on decoded pairs, against the URL as
  it arrived, so two identical rows still send two copies.
- **Encoding can be turned off per request.** `encodeQuery` is on by default
  and is what almost everyone wants. Off is for a value that is already encoded,
  or that holds a `/` or `:` a server wants to see unescaped; the text then goes
  in as typed and is the user's to get right. The switch lives beside the params
  table and rewrites the URL as it is flipped, so what will be sent is on screen
  rather than a send away. The UI is where the app encodes — `paramsChanged`
  writes the query into the URL — and the engine applies the same rule to
  anything it appends itself.
- **Query values are percent-encoded, and the typed query is left alone.** The
  `url` crate's `query_pairs_mut` serialises as a form does — a space becomes
  `+` — and rewrites the query already in the URL while it is there. `+` means
  space in a form body, not in a URL: a server is entitled to read `SW1A+1AA`
  as a postcode with a plus in it, and one did, answering 400 to a request that
  curl and Bruno could make. The query string is assembled by hand instead:
  what the user typed is carried across byte for byte, and appended pairs are
  encoded to RFC 3986's unreserved set, which is what `encodeURIComponent` and
  curl produce.

## Auth that needs a round trip

Bearer, basic and an API key are headers, so they are applied in
`build.rs` where the request is assembled. Digest is not: RFC 7616 is a
conversation, and the response can only be computed once the server has sent a
nonce. `HttpEngine::send` therefore sends, and on a 401 carrying a
`WWW-Authenticate: Digest` challenge, answers it and sends again.

Both attempts live inside the one timeout. Two round trips the user did not ask
for should not buy twice the wait.

A challenge that cannot be answered — a different scheme, a malformed header,
`auth-int` with no body — is not an error. The 401 is the honest answer to what
was asked, so it is returned with a warning saying why there was no second
attempt.

OAuth 2 needs a round trip of a different shape: a token before anything can be
sent at all. `crates/http-engine/src/oauth2.rs` posts the form, reads the access
token and attaches it as a bearer token, for the three grants that are just a
request — client credentials, password, refresh token. Authorization code and
the other interactive flows are deliberately absent: they need a browser and a
redirect listener, which is a different kind of program from this one.

Tokens are cached until shortly before they expire, keyed by everything that
decides which token comes back — endpoint, grant, client, scope, user, refresh
token — so changing any of it asks for a new one instead of reusing a token
minted for something else. A burst of requests to one API costs one token
request.

A token that cannot be got is an error rather than a response, with its own
`RequestErrorKind::Auth`: the request never left, and reporting it as a failed
send would be a lie. The endpoint's own `error` field is quoted, since
`invalid_client` says more than 401 does.

OAuth 1 is neither: it is arithmetic. Every request carries a signature over its
own method, URL and parameters, so `crates/http-engine/src/oauth1.rs` signs at
build time and there is nothing to fetch or cache. The awkward part is the
signature base string — percent-encoding applied twice, parameters sorted after
encoding, a form-encoded body signed along with the query — and getting it wrong
produces a rejection with no explanation. So the base string is tested against
the worked example in RFC 5849 §3.4.1.1, character for character.

The integration tests check the header's shape rather than recomputing the
signature in the test server. A second implementation written by the same hand
would agree with the first one's mistakes; the RFC's own vector will not.

The test server issues a challenge and then recomputes the digest itself, so the
test fails if the client's answer is merely well-formed rather than correct.

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

### The padlock

`ClientBuilder::tls_info(true)` puts reqwest's `TlsInfo` on the response, which
carries the negotiated version and the peer's leaf certificate as DER.
`crates/http-engine/src/peer_cert.rs` parses that with `x509-parser` into
`TlsDetails`, and the response carries it: `None` for plain HTTP, so the padlock
appears exactly when the connection was encrypted.

It is the leaf only. reqwest hands back the peer certificate and not the chain
above it, so the dialog shows one certificate honestly rather than implying a
chain that was never captured.

A certificate that will not parse gives `certificate: None` rather than failing
the response. The TLS layer has already accepted the connection by then; a gap
in what we can display is not a reason to throw the response away.

`TlsDetails` is boxed on `HttpResponse`. It is a few hundred bytes that most
responses do not carry, and `HttpResponse` travels inside the sidecar's message
enum, which is as large as its largest variant.

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

### NTLM

NTLM authenticates a _connection_ rather than a request: a 401 offering the
scheme, a negotiate message, the server's challenge, and an authenticate message
computed from it — the last two on the same socket, or the server has no
challenge to check the response against.

Nothing in reqwest pins a connection to a sequence of requests. What makes this
work is narrower: `HttpEngine::ntlm_client` builds a client whose pool holds one
connection and which nothing else uses, then sends the two messages back to
back. The idle connection the first leg returns is the only one the second can
take. Redirects are off for that client, since following one mid-handshake would
open a new connection and lose the challenge.

That is a property of the pool rather than a guarantee from an API, so the test
server enforces it: `/ntlm` keeps its challenge in per-connection state and
refuses an authenticate message that arrives anywhere else. The test asserts
both messages landed on one connection, and the server recomputes the NTLMv2
proof rather than pattern-matching it.

`crates/http-engine/src/ntlm.rs` builds the messages: NTLMv2 only, no signing or
sealing, no session key. The key derivation is pinned to the worked example in
MS-NLMP §4.2.4.1.1, because a server that dislikes the response says only 401,
which tells you nothing about which step was wrong.

The first request of every NTLM exchange is unauthenticated, on the shared
client, because the scheme is not known until the server names it. That is the
protocol's cost, not an implementation choice.
