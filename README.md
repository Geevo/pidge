<p align="center">
  <img src="packages/ui/src/assets/app-icon.png" width="128" alt="">
</p>

# pidge

A tiny HTTP client that lives on your machine and minds its own business.

```
OPEN APP -> TYPE URL -> SEND REQUEST
```

That's the whole pitch. No login, no account, no cloud, no "create a workspace
to get started". It opens on a blank request with the cursor already in the URL
bar, and you never have to name or save anything to send it.

It exists because API clients kept turning into platforms. This one's just a
client. Everything it keeps is one JSON file in your own user directory, and the
only request it ever makes is the one you told it to.

![pidge showing a JSON response, with saved requests in the drawer](docs/screenshots/hero.png)

## Get it

Grab a build from the [releases page](../../releases). There's nothing to
install.

| Platform        | File                              | Notes                                                                   |
| --------------- | --------------------------------- | ----------------------------------------------------------------------- |
| Windows 10/11   | `pidge-<version>-windows-x64.zip` | Unzip, run the `.exe`. Needs WebView2, which Windows 11 already has.    |
| Linux           | `pidge-<version>-x86_64.AppImage` | `chmod +x` and go. Brings its own browser engine.                       |
| Linux, packaged | `.deb` / `.rpm`                   | About a twentieth of the size, since they use the WebKitGTK you've got. |

No macOS build yet. It should build there; nobody's tried.

## What it does

- All the methods you'd expect: `GET`, `POST`, `PUT`, `PATCH`, `DELETE`,
  `HEAD`, `OPTIONS`.
- JSON, plain text, form or multipart bodies. Responses come back highlighted
  and foldable (JSON, HTML, XML, YAML, CSS, JavaScript), going by what the
  server says it sent. JSON gets laid out for you, and when something that's
  secretly JSON turns up as `text/plain` (we've all been there), a
  **Pretty print** box offers to sort it out.
- Query params and headers as tables you can switch on and off row by row, plus
  a toggle for URL-encoding. The URL bar shows you the difference as you flip
  it.
- Auth without hand-rolling headers: bearer, basic, digest, NTLM, API key,
  OAuth 1 and OAuth 2. A header you typed yourself always wins.
- `{{variables}}` from simple environments, so the same request can hit staging
  and prod.
- HTTPS you can actually look at: click the padlock to see the certificate the
  server sent, with a heads-up if it's expired or self-signed.
- Your system's certificate store by default, plus extra CAs and client
  certificates if your network needs them.
- Tabs, history, saved requests, light and dark themes (syntax colours can
  borrow VS Code's, Atom One's or GitHub's), and keyboard shortcuts that do
  what you'd expect.
- Text from 90% to 150%, and the whole window grows with it, not just the
  labels.

### Copy it as code

![The Code tab showing a POST request as curl, with a 201 Created response](docs/screenshots/code.png)

Get the same request as curl, PowerShell, Python, C#, Rust, Node.js, Go, Java,
PHP or Zig, and where a language has more than one way to do it, pick the
library. It's built from what would actually be sent (variables filled in, the
query folded into the URL, your timeout, redirect and certificate settings
included), so what you paste elsewhere does the same thing. If a snippet can't
do something, like an OAuth 1 signature, it says so in a comment instead of
quietly leaving it out.

### Secrets stay secret

![OAuth 2 settings with the client secret covered and an eye to show it](docs/screenshots/secrets.png)

Passwords, tokens and client secrets stay covered until you press the eye, so a
screen share doesn't give them away. On disk they're encrypted with a key your
operating system looks after (DPAPI on Windows, your keyring or
`systemd-creds` on Linux), and it never nags you for a password to get at it.
The details are in [docs/storage.md](docs/storage.md#secrets).

### Share your requests

![The export dialog with five saved requests, the .http format picked and passwords left out](docs/screenshots/export.png)

Export saved requests as JSON (everything, for moving to another machine) or as
a `.http` file that VS Code's REST Client and JetBrains can run. Passwords and
tokens get swapped for `{{placeholders}}` unless you ask for them, so the file
is safe to drop in a chat. Import takes either back, or a `.http` file from REST
Client or JetBrains, and tells you which variables you still need to fill in.

What it won't do: accounts, sync, sharing through somebody's cloud, dashboards,
telemetry, update pings, or anything that asks you to sign in.

## Keyboard

| Shortcut                             | Action            |
| ------------------------------------ | ----------------- |
| <kbd>Ctrl/Cmd</kbd>+<kbd>Enter</kbd> | Send              |
| <kbd>Ctrl/Cmd</kbd>+<kbd>L</kbd>     | Focus the URL     |
| <kbd>Ctrl/Cmd</kbd>+<kbd>N</kbd>     | New request       |
| <kbd>Ctrl/Cmd</kbd>+<kbd>W</kbd>     | Close the tab     |
| <kbd>Ctrl/Cmd</kbd>+<kbd>S</kbd>     | Save the request  |
| <kbd>Ctrl/Cmd</kbd>+<kbd>+</kbd>     | Bigger text       |
| <kbd>Ctrl/Cmd</kbd>+<kbd>-</kbd>     | Smaller text      |
| <kbd>Ctrl/Cmd</kbd>+<kbd>0</kbd>     | Text back to 100% |

<kbd>Enter</kbd> in the URL bar sends too.

## Where your stuff lives

One JSON file you can open and read, wherever your platform keeps app data:

| Platform | Path                                  |
| -------- | ------------------------------------- |
| Windows  | `%APPDATA%\pidge`                     |
| macOS    | `~/Library/Application Support/pidge` |
| Linux    | `~/.local/share/pidge`                |

Settings shows you the exact path. Delete the file and pidge starts fresh. If
it's ever unreadable, pidge keeps the broken one next to the new one rather
than binning it. Coming from back when it was called API Client? Your old
folder gets moved over on first launch.

## Build it yourself

You'll need [Rust](https://rustup.rs) (stable), [Node](https://nodejs.org) 20+
and [pnpm](https://pnpm.io) 10+, plus the
[Tauri prerequisites](https://tauri.app/start/prerequisites/) for your
platform. On Fedora that's:

```bash
sudo dnf install webkit2gtk4.1-devel gtk3-devel libsoup3-devel librsvg2-devel \
  openssl-devel curl wget file libappindicator-gtk3-devel patchelf
```

Then:

```bash
pnpm install
pnpm dev:desktop     # the app, with hot reload
```

To build what the releases ship:

```bash
pnpm --filter @api-client/desktop build:app
```

On Linux, stick `NO_STRIP=1` in front of that or the AppImage step falls over.
[docs/packaging.md](docs/packaging.md) explains why, and how the Windows build
is cross-compiled from Linux.

## In VS Code

The same app runs as a VS Code panel, talking to the same engine through a
sidecar process. `pnpm dev:vscode` watches it. It's not on the Marketplace yet.

## Working on it

```bash
pnpm lint && pnpm typecheck && pnpm test && pnpm format:check
cargo fmt --all --check
cargo clippy --workspace --all-targets --all-features -- -D warnings
cargo test --workspace
```

Rust does the HTTP and React is the control panel. There's no second HTTP
implementation: the UI can't touch the network by itself.

```
crates/    core, http-engine, variables, storage, session, protocol, sidecar, testserver
apps/      desktop (Tauri 2), vscode (extension host + webview)
packages/  ui — the React app both frontends mount
```

- [Architecture](docs/architecture.md): how the pieces fit, and why
- [Development](docs/development.md): the day-to-day workflow
- [Packaging](docs/packaging.md): builds, installers, icons
- [Storage and migrations](docs/storage.md)
- [Sidecar protocol](docs/protocol.md)

## Privacy

No analytics, no telemetry, no remote config, no update check, no web fonts.
Secret header values (`Authorization`, `Cookie`, `X-API-Key` and friends) never
end up in a log, and saved secrets are encrypted as described above.

## Licence

MIT; see [LICENSE](LICENSE). The bundled IBM Plex fonts are SIL Open Font
License 1.1; see [THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md).

## Credits

Co-created by [MJQ7](https://github.com/MJQ7).
