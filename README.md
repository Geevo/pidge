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
- Bearer and Basic auth helpers — and an explicit `Authorization` header always wins
- Cancellation, timeouts, redirects, and cookies
- Status, duration, and size on one line: `200 OK · 143 ms · 2.4 KB`
- Response body and headers, with JSON pretty-printed and binary shown as a size
- Multiple scratch tabs, keyboard-driven
- Local history and a flat list of saved requests
- `{{variable}}` substitution from flat environments

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

## Typography

The app bundles Inter and JetBrains Mono (88 KB together, latin subset, variable
weight) rather than using whatever each platform provides, so a request looks the
same on Windows and Linux. Nothing is fetched at runtime; both CSPs forbid it.

Both fonts are SIL Open Font License 1.1. See
[THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md).

## Licence

MIT. See [LICENSE](LICENSE). Bundled fonts are covered by
[THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md).
