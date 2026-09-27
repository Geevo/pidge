# Storage

Everything is local. One human-readable JSON file per installation.

## Where

| Platform | Location                                                                            |
| -------- | ----------------------------------------------------------------------------------- |
| Linux    | `$XDG_DATA_HOME/api-client/state.json`, else `~/.local/share/api-client/state.json` |
| macOS    | `~/Library/Application Support/api-client/state.json`                               |
| Windows  | `%APPDATA%\api-client\state.json`                                                   |
| VS Code  | the extension's **global** storage directory                                        |

VS Code uses global storage rather than workspace storage on purpose: the client
works with no folder open, and your history should not depend on which project
you happen to have in the window.

## What

```jsonc
{
  "version": 2,
  "settings": { "theme": "system", "timeoutMs": 30000, "maxHistory": 500, ... },
  "savedRequests": [ { "id": "…", "name": "List users", "request": { … } } ],
  "history": [ { "id": "…", "timestamp": 1700000000000, "request": { … }, "status": 200 } ],
  "tabs": [ { "id": "…", "request": { … }, "savedRequestId": null, "dirty": false } ],
  "activeTabId": "…",
  "environments": [ { "id": "…", "name": "Local", "variables": [ … ] } ],
  "activeEnvironmentId": null,
  "secrets": "keyring"
}
```

There is no workspace, collection, project, or folder. Saved requests are a flat
list.

**History does not keep response bodies.** It keeps the request, the status, the
duration, and the size. History exists so you can get back to a request, not so
the client can accumulate payloads on your disk. It is capped at
`settings.maxHistory` (500 by default), newest first.

## Secrets

Passwords and tokens are encrypted inside the file; everything else stays
readable. A copy of `state.json` on its own — a backup, a synced dotfile, an
attachment to a bug report — gives none of them away.

What counts as a secret:

| Where               | What                                                                                                           |
| ------------------- | -------------------------------------------------------------------------------------------------------------- |
| A request's auth    | bearer token; Basic, Digest, NTLM and OAuth 2 passwords; OAuth 1 and 2 secrets; OAuth 2 refresh token; API key |
| A request's headers | the value of any header `core::redact` treats as secret: `Authorization`, `Cookie`, `X-API-Key` and friends    |
| Environments        | every variable's value, since a variable is where a token goes to be kept out of the requests that use it      |
| Settings            | the client certificate's password                                                                              |

Usernames, client ids, OAuth 1 tokens and ordinary headers are identifiers, not
secrets, and stay as typed. So do request bodies and URLs: a password typed into
a JSON body, or as `user:pass@` in a URL, is saved as it is.

Each secret is written as `enc:v1:` followed by base64 of a 24-byte nonce and
the XChaCha20-Poly1305 ciphertext. Every save encrypts afresh with a random
nonce; the 192-bit nonce makes that safe however long the key lives.

### The key

One random 256-bit key per state file, in `state.key` beside it, protected by
whatever the platform has that **never asks the user anything**. A password
dialog at startup is worse than any of the alternatives, so anything that could
show one is treated as unavailable.

| Platform | Protection                                                                                                                   |
| -------- | ---------------------------------------------------------------------------------------------------------------------------- |
| Windows  | DPAPI (`CryptProtectData`, UI forbidden), with an application-specific entropy                                               |
| Linux    | an already unlocked Secret Service keyring (GNOME Keyring, KDE Wallet), **and** `systemd-creds --user` where systemd is 256+ |

On Linux the key is stored both ways when both are there. The keyring answers
in about 150 ms, so it is asked first; it is never unlocked, because unlocking
is what shows a dialog. `systemd-creds` ties the key to the machine and the
user the way DPAPI does and needs no unlocking, but its decryption service takes
two seconds per call, so it is only the fallback for a launch that finds the
keyring locked or not running. Sealing with it costs about a second, once, on
the first launch.

Where systemd is older than 256 — Ubuntu 24.04 LTS, Debian 12 — the keyring
holds the key on its own. A launch that finds it locked there cannot read the
key; see below.

The desktop app and the VS Code extension keep separate state directories, so
each has its own key. A key file lists what it was protected with, and is only
ever read that way.

This protects the file, not the session. Any program running as you can ask an
unlocked keyring, DPAPI or `systemd-creds` for the key; that is a limit of every
one of these, not something an application can add.

### Without protection

When nothing can protect the key, secrets are saved as plain text and the app
says so once, with the reason. That is:

- macOS, which is not supported yet;
- a Linux session with neither an unlocked keyring nor systemd 256 — a bare
  window manager on an older distribution, SSH, a container.

The file records that it has said so (`"secrets": "plainText"`), and says it
again only if protection that worked stops working.

The state file, the key file and the backups are written `0600` on Linux and
macOS whatever else happens, and a state file left readable by an earlier
version is fixed on load. `%APPDATA%` is already private on Windows.

### When the key cannot open them

- **The key cannot be got at right now** (a locked keyring with no
  `systemd-creds` to fall back on). The secrets are blank for the session, the
  app says why, and every save writes the ciphertext back unchanged, so they
  return on the next launch that can read the key. Anything typed in the
  meantime is saved as plain text and encrypted then.
- **The key is gone or different** (`state.key` deleted, a new machine, a reset
  keyring). What it encrypted cannot be read. The file is copied to
  `state.undecryptable-<timestamp>.json`, the secrets are cleared, and the app
  says so. Everything else loads as normal.

`API_CLIENT_KEYRING=off` skips the key entirely and saves secrets as plain text
without the notice. The sidecar tests use it.

## Atomic writes

`Store::save` writes `state.json.tmp`, `fsync`s it, and renames it over
`state.json`. A crash mid-write cannot leave a half-written file, because the
rename is atomic and the old file stays intact until it completes.

## Migrations

`state.json` carries a `version`. `crates/storage/src/migrate.rs` walks a chain
of steps from that version up to `SCHEMA_VERSION`, one step per version, so
adding a v3 means adding one function.

Version 2 is the one that may hold encrypted secrets. A version 1 file is a
version 2 file with every secret in plain text, so the step only renumbers it;
the load encrypts them and writes the file back straight away. A version 1
build refuses a version 2 file rather than sending ciphertext as a password.

A file written by a **newer** build is refused rather than mangled — the app
says so and starts from defaults rather than silently dropping fields it does
not understand.

Files written before versioning existed report version 0 and are read on a
best-effort basis; `#[serde(default)]` on `AppState` and `Settings` fills in
anything missing, which is also what makes adding a field a non-event.

## Corruption

If the file cannot be read or parsed:

1. It is renamed to `state.corrupt-<timestamp>.json`, so nothing is destroyed.
2. The app starts from defaults.
3. A dismissible notice explains what happened and where the old file went.

Refusing to open would be worse than losing a scratch tab. The tests cover the
whole path, including that the app is immediately usable afterwards.
