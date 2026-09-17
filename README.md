# API Client

A small HTTP client for your own machine.

```
OPEN APP -> TYPE URL -> SEND REQUEST
```

No login, no account, no cloud. No workspace, project, collection or team to
make before you can send a `GET`. It opens on an empty request with the cursor
in the URL field, and a request never has to be named or saved to be sent.

Everything it keeps is one JSON file in your own user directory, and the only
request it ever makes is the one you asked for.

## Get it

There are builds on the [releases page](../../releases) — nothing to install
either way.

| Platform        | File                                   | Notes                                                                               |
| --------------- | -------------------------------------- | ----------------------------------------------------------------------------------- |
| Windows 10/11   | `api-client-<version>-windows-x64.zip` | Unzip and run the `.exe`. Needs the WebView2 runtime, which Windows 11 already has. |
| Linux           | `api-client-<version>-x86_64.AppImage` | `chmod +x` it and run it. Carries its own browser engine.                           |
| Linux, packaged | `.deb` / `.rpm`                        | A twentieth of the size: these use the WebKitGTK your system already has.           |

No macOS build. It ought to build there; nobody has tried.

## What you can do with it

- Every method you would expect: `GET`, `POST`, `PUT`, `PATCH`, `DELETE`,
  `HEAD`, `OPTIONS`.
- Bodies as JSON, plain text, form-encoded or multipart, with JSON highlighted
  and foldable on the way back.
- Query parameters and headers as tables, each row switched on and off.
- Auth without hand-rolling a header: bearer, basic, digest, NTLM, API key,
  OAuth 1 and OAuth 2. A header you typed yourself always wins over the tab.
- `{{variables}}` from flat environments, so one request runs against staging
  and production.
- HTTPS you can look at: a padlock on the response opens the certificate the
  server actually presented, and says so when it is expired or self-signed.
- Your system's certificate store by default, plus extra CAs and client
  certificates for a network that needs them.
- Tabs, local history, saved requests, light and dark themes, and a keyboard
  that does what you expect.

What it will not do: accounts, sync, sharing, dashboards, telemetry, update
pings, or anything that asks you to sign in.

## Keyboard

| Shortcut                             | Action           |
| ------------------------------------ | ---------------- |
| <kbd>Ctrl/Cmd</kbd>+<kbd>Enter</kbd> | Send             |
| <kbd>Ctrl/Cmd</kbd>+<kbd>L</kbd>     | Focus the URL    |
| <kbd>Ctrl/Cmd</kbd>+<kbd>N</kbd>     | New request      |
| <kbd>Ctrl/Cmd</kbd>+<kbd>W</kbd>     | Close the tab    |
| <kbd>Ctrl/Cmd</kbd>+<kbd>S</kbd>     | Save the request |

<kbd>Enter</kbd> in the URL field sends too.

## Where your things are kept

One human-readable JSON file, where your platform keeps application data:

| Platform | Path                                       |
| -------- | ------------------------------------------ |
| Windows  | `%APPDATA%\api-client`                     |
| macOS    | `~/Library/Application Support/api-client` |
| Linux    | `~/.local/share/api-client`                |

Settings shows you the exact path. Delete the file and the app starts fresh; if
it is ever unreadable it is kept beside the new one rather than thrown away.

## Build it yourself

You need [Rust](https://rustup.rs) (stable), [Node](https://nodejs.org) 20+ and
[pnpm](https://pnpm.io) 10+, plus the
[Tauri prerequisites](https://tauri.app/start/prerequisites/) for your platform.
On Fedora:

```bash
sudo dnf install webkit2gtk4.1-devel gtk3-devel libsoup3-devel librsvg2-devel \
  openssl-devel curl wget file libappindicator-gtk3-devel patchelf
```

Then:

```bash
pnpm install
pnpm dev:desktop     # the app, with hot reload
```

To produce the same files as the releases:

```bash
pnpm --filter @api-client/desktop build:app
```

On Linux prefix that with `NO_STRIP=1`, or the AppImage step fails.
[docs/packaging.md](docs/packaging.md) explains why, and how the Windows binary
is cross-compiled from Linux.

## In VS Code

The same app runs in a VS Code panel, driving the same engine through a sidecar
process. `pnpm dev:vscode` watches it; the extension is not published anywhere
yet.

## Working on it

```bash
pnpm lint && pnpm typecheck && pnpm test && pnpm format:check
cargo fmt --all --check
cargo clippy --workspace --all-targets --all-features -- -D warnings
cargo test --workspace
```

Rust is the engine, React is the control panel, and there is no second HTTP
implementation: the UI has no network access of its own.

```
crates/    core, http-engine, variables, storage, session, protocol, sidecar, testserver
apps/      desktop (Tauri 2), vscode (extension host + webview)
packages/  ui — the React app both frontends mount
```

- [Architecture](docs/architecture.md) — how the pieces fit, and why
- [Development](docs/development.md) — the day-to-day workflow
- [Packaging](docs/packaging.md) — builds, installers, icons
- [Storage and migrations](docs/storage.md)
- [Sidecar protocol](docs/protocol.md)

## Privacy

No analytics, no telemetry, no remote configuration, no update check, no web
fonts. Secret header values — `Authorization`, `Cookie`, `X-API-Key` and
friends — are never written to a log.

## Licence

MIT; see [LICENSE](LICENSE). The bundled IBM Plex fonts are SIL Open Font
License 1.1; see [THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md).
