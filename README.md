# API Client

A small, local-first HTTP client with two frontends: a desktop app and a VS Code
extension. Both drive the same Rust request engine.

```
OPEN APP -> TYPE URL -> SEND REQUEST
```

No login. No account. No cloud. No workspace, project, collection, or team.
No onboarding, no dashboard, no telemetry. The app opens directly into a blank
request, and a request never has to be named or saved before you send it.

## What it does

- `GET`, `POST`, `PUT`, `PATCH`, `DELETE`, `HEAD`, `OPTIONS`
- Query params, headers, and bodies: JSON, text, `x-www-form-urlencoded`, multipart
- Auth: bearer, basic, digest, NTLM, API key, OAuth 1 and OAuth 2 — and an
  explicit `Authorization` header always wins
- Cancellation, timeouts, redirects, and cookies
- Status, duration, and size on one line: `200 OK · 143 ms · 2.4 KB`
- Response body and headers, with JSON highlighted and foldable, and binary
  shown as a size
- Request and response side by side or stacked, with a draggable divider
- Multiple scratch tabs, keyboard-driven
- Local history and a flat list of saved requests
- `{{variable}}` substitution from flat environments
- TLS: the OS certificate store by default, plus extra CAs and client
  certificates (PEM or `.p12`/`.pfx`)

## Try it

```bash
pnpm install
pnpm dev:desktop
```

Type `localhost:3000/api/test`, press <kbd>Ctrl</kbd>+<kbd>Enter</kbd>.

For the VS Code extension:

```bash
pnpm build:sidecar
pnpm --filter api-client build
```

Then open the repo in VS Code and press <kbd>F5</kbd>, or run **API Client: Open**
from the Command Palette in the Extension Development Host.

## Keyboard

| Shortcut                             | Action           |
| ------------------------------------ | ---------------- |
| <kbd>Ctrl/Cmd</kbd>+<kbd>Enter</kbd> | Send             |
| <kbd>Ctrl/Cmd</kbd>+<kbd>L</kbd>     | Focus the URL    |
| <kbd>Ctrl/Cmd</kbd>+<kbd>N</kbd>     | New request      |
| <kbd>Ctrl/Cmd</kbd>+<kbd>W</kbd>     | Close the tab    |
| <kbd>Ctrl/Cmd</kbd>+<kbd>S</kbd>     | Save the request |

<kbd>Enter</kbd> in the URL field sends too.

## Layout

```
crates/
  core/         request/response models and normalized errors
  http-engine/  HTTP execution, cancellation, timing, size limits
  variables/    {{variable}} substitution
  storage/      versioned JSON persistence, migrations, history
  session/      the application service both frontends drive
  protocol/     newline-delimited JSON protocol for the sidecar
  sidecar/      the long-running engine process VS Code talks to
  testserver/   a local HTTP server used by the engine tests
apps/
  desktop/      Tauri 2 + React + Vite
  vscode/       extension host + React webview
packages/
  ui/           the shared React UI and platform bridge
docs/
```

Rust is the engine. React is the control panel.

- **Desktop**: React → Tauri commands → shared Rust crates
- **VS Code**: React webview → extension host → Rust sidecar → shared Rust crates

There is no second HTTP implementation. The engine does not know which frontend
called it, and the webview has no network access of its own.

See [docs/architecture.md](docs/architecture.md).

## Requirements

- Rust (stable) with `rustfmt` and `clippy`
- Node 20+ and pnpm 10+
- Desktop builds additionally need the [Tauri prerequisites](https://tauri.app/start/prerequisites/)
  for your platform. On Fedora:

  ```bash
  sudo dnf install webkit2gtk4.1-devel gtk3-devel libsoup3-devel librsvg2-devel \
    openssl-devel curl wget file libappindicator-gtk3-devel patchelf
  ```

  Without them `cargo check --workspace` cannot build `api-client-desktop`; the
  rest of the workspace builds fine with `--exclude api-client-desktop`.

## Commands

```bash
pnpm dev:desktop      # desktop app with hot reload
pnpm dev:vscode       # watch the extension host
pnpm build            # build every package
pnpm build:sidecar    # build the engine and stage it for the extension
pnpm gen:types        # regenerate the TypeScript types from the Rust types
pnpm lint
pnpm typecheck
pnpm test
```

```bash
cargo fmt --all --check
cargo check --workspace
cargo test --workspace
cargo clippy --workspace --all-targets --all-features -- -D warnings
```

## Where your data lives

Everything is local, in one human-readable JSON file.

- **Desktop**: the platform app-data directory
  (`~/.local/share/api-client`, `~/Library/Application Support/api-client`,
  `%APPDATA%\api-client`)
- **VS Code**: the extension's global storage, so it is the same whichever
  folder you have open — and it works with no folder open at all

If that file is ever unreadable it is preserved alongside the new one and the
app starts from defaults rather than refusing to open.

See [docs/storage.md](docs/storage.md).

## Certificates

The client trusts whatever your operating system trusts — the Windows
certificate store, the macOS keychain, the system CA bundle on Linux — without
being told to.

Settings adds the rest:

- **Additional trusted CAs.** PEM or DER files, _merged_ with the system store
  rather than replacing it.
- **A client certificate** for mutual TLS. A PEM holding the certificate and
  key, or a PKCS#12 `.p12`/`.pfx` bundle, which is unpacked for you — no
  `openssl` conversion step.
- **Accept invalid certificates**, for a dev server whose CA you would rather
  not install. Adding the CA is the better answer and the UI says so.

See [docs/architecture.md](docs/architecture.md#tls).

## Privacy

The only network request is the one you ask the client to send. There is no
analytics, no telemetry, no remote config, no update ping, and no external
fonts. Secret header values (`Authorization`, `Cookie`, `X-API-Key`, and
friends) are never written to a log.

## Docs

- [Architecture](docs/architecture.md)
- [Sidecar protocol](docs/protocol.md)
- [Storage and migrations](docs/storage.md)
- [Packaging](docs/packaging.md)
- [Development](docs/development.md)

## HTTPS

An encrypted response gets a padlock in the status line, with the protocol
beside it. It opens the certificate the server presented: subject, issuer, the
names it is valid for, its dates, serial and SHA-256 fingerprint. An expired or
self-signed certificate says so.

## Auth

None, bearer, basic, digest, NTLM, an API key in a header or the query string,
OAuth 1 request signing, and the OAuth 2 grants that are a request to a token
endpoint — client credentials, password and refresh token.

Digest and NTLM cost round trips, because the response can only be computed once
the server has sent a challenge — and NTLM's handshake is held on one connection,
as the protocol requires. OAuth 2 fetches a token first and keeps it until it
expires. The OAuth 2 browser flows are not here; `docs/architecture.md` says why.

Anything else is a header you type yourself, and a header you typed always wins
over the Auth tab.

## Themes

System, light and dark, plus a warm variant of each: the same palette with the
grey ramp warmed up, so the app reads as ink on paper rather than slate. Pick
one in Settings.

Method colours stay Swagger's in every theme: those carry meaning rather than
decoration.

## Typography

The app bundles IBM Plex Sans and IBM Plex Mono (75 KB together, latin subset)
rather than using whatever each platform provides, so a request looks the same on
Windows and Linux. Nothing is fetched at runtime; both CSPs forbid it.

Both fonts are SIL Open Font License 1.1. See
[THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md).

## Licence

MIT. See [LICENSE](LICENSE). Bundled fonts are covered by
[THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md).
