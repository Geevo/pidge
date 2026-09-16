# Packaging

## Desktop

```bash
pnpm --filter @api-client/desktop build:app
```

Tauri builds the frontend first (`beforeBuildCommand`) and produces installers
for the host platform under `target/release/bundle/`. Windows, macOS, and Linux
are all supported; each has to be built on its own platform, as usual for Tauri.

The app declares only `core:default` in
`apps/desktop/src-tauri/capabilities/default.json`. It needs no filesystem,
shell, or network permissions from Tauri: HTTP goes through the Rust engine, and
storage goes through `crates/storage`.

## VS Code

The extension ships the sidecar binaries it was packaged with. It never
downloads anything at runtime.

Expected layout:

```
apps/vscode/bin/
  linux-x64/api-client-sidecar
  darwin-x64/api-client-sidecar
  darwin-arm64/api-client-sidecar
  win32-x64/api-client-sidecar.exe
```

`scripts/build-sidecar.mjs` builds and stages one:

```bash
pnpm build:sidecar                                       # this host
pnpm build:sidecar --target aarch64-apple-darwin         # a specific target
pnpm build:sidecar --target x86_64-pc-windows-msvc
```

Cross-compilation needs the Rust target installed (`rustup target add …`) and a
working linker for it; in practice each platform is built on its own runner and
the binaries are collected before packaging.

Then:

```bash
pnpm --filter @api-client/vscode build     # extension host + webview
pnpm --filter @api-client/vscode package   # vsce package --no-dependencies
```

`Sidecar.resolveBinary` looks, in order, at:

1. the `apiClient.sidecarPath` setting,
2. `bin/<platform>-<arch>/`,
3. `target/debug` then `target/release`, so the repo runs from source.

If none exist it says so, naming the platform it looked for, rather than failing
at the first request.

## Fonts

Inter and JetBrains Mono are bundled from `packages/ui/src/fonts/` and emitted
into the build output as hashed `.woff2` files. They are never fetched at
runtime.

Both are SIL Open Font License 1.1, which requires the licence to travel with
the font. `Inter-LICENSE.txt` and `JetBrainsMono-LICENSE.txt` sit next to the
`.woff2` files in the source tree; include them, or `THIRD-PARTY-LICENSES.md`,
in anything you distribute.

## Webview bundle

The webview is built by Vite to `apps/vscode/media/webview.js` and
`webview.css`, with fixed names because the panel HTML references them directly.
It runs under a strict CSP: scripts only from that bundle and only with a
per-render nonce, no remote origins at all. Inline styles are allowed because
CodeMirror injects its own stylesheet at runtime; inline scripts are not.
