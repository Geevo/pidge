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
  "version": 1,
  "settings": { "theme": "system", "timeoutMs": 30000, "maxHistory": 500, ... },
  "savedRequests": [ { "id": "…", "name": "List users", "request": { … } } ],
  "history": [ { "id": "…", "timestamp": 1700000000000, "request": { … }, "status": 200 } ],
  "tabs": [ { "id": "…", "request": { … }, "savedRequestId": null, "dirty": false } ],
  "activeTabId": "…",
  "environments": [ { "id": "…", "name": "Local", "variables": [ … ] } ],
  "activeEnvironmentId": null
}
```

There is no workspace, collection, project, or folder. Saved requests are a flat
list.

**History does not keep response bodies.** It keeps the request, the status, the
duration, and the size. History exists so you can get back to a request, not so
the client can accumulate payloads on your disk. It is capped at
`settings.maxHistory` (500 by default), newest first.

## Atomic writes

`Store::save` writes `state.json.tmp`, `fsync`s it, and renames it over
`state.json`. A crash mid-write cannot leave a half-written file, because the
rename is atomic and the old file stays intact until it completes.

## Migrations

`state.json` carries a `version`. `crates/storage/src/migrate.rs` walks a chain
of steps from that version up to `SCHEMA_VERSION`, one step per version, so
adding a v2 means adding one function.

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
