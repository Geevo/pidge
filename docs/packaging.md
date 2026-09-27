# Packaging

## Desktop

```bash
pnpm --filter @api-client/desktop build:app
```

Tauri builds the frontend first (`beforeBuildCommand`) and produces installers
for the host platform under `target/release/bundle/`. Windows, macOS, and Linux
are all supported; each has to be built on its own platform, as usual for Tauri.

### Windows from Linux

The MSVC target cross-compiles with `cargo-xwin`, which fetches the Windows SDK
and CRT itself:

```bash
rustup target add x86_64-pc-windows-msvc
cargo install cargo-xwin
cd apps/desktop
pnpm exec tauri build --runner cargo-xwin --target x86_64-pc-windows-msvc --no-bundle
```

Four host tools have to be on `PATH`: `clang-cl` compiles the C, `lld-link`
links, `llvm-lib` is the librarian `aws-lc-sys` looks for, and `llvm-rc`
compiles the resource script that carries the icon and the manifest. On Fedora
that is `clang`, `lld` and `llvm`; `lld-link` can also come from the Rust
toolchain's own `rust-lld`, which is the same linker under another name.

The result is `target/x86_64-pc-windows-msvc/release/pidge.exe`,
about 10 MB, with the icon, the manifest and the version resource compiled in.
It needs the WebView2 runtime on the machine that runs it — present on Windows
11 and most of Windows 10, and the NSIS installer can fetch it when it is not.

`--no-bundle` because the MSI is the one thing that cannot be built here: WiX is
a Windows tool. NSIS cross-builds fine.

### The AppImage needs `NO_STRIP=1`

```bash
NO_STRIP=1 pnpm --filter @api-client/desktop build:app
```

Without it the AppImage bundle fails with `failed to run linuxdeploy`, and the
cause is two layers down: linuxdeploy strips every library it bundles using a
`strip` from its own AppImage, which is old enough not to know `SHT_RELR`
(`.relr.dyn`). Any library built by a current toolchain — `libyuv` here — is
rejected as an unrecognised format, linuxdeploy treats that as fatal, and Tauri
reports only that linuxdeploy failed. `NO_STRIP` skips the stripping, which
costs nothing: the libraries come from the system and are already stripped.

The deb and the rpm are unaffected, since neither bundles a library.

### Sizes

An AppImage carries the whole browser engine, because it cannot assume the host
has one: 104 MB, of which the app is 12 MB. `libwebkit2gtk` is 89 MB of it,
`libjavascriptcoregtk` 32 MB and `libicudata` 30 MB, before squashfs compresses
the lot. The deb and the rpm are 5.3 MB each and link against the system's
WebKitGTK.

The app declares only `core:default` in
`apps/desktop/src-tauri/capabilities/default.json`. It needs no filesystem,
shell, or network permissions from Tauri: HTTP goes through the Rust engine, and
storage goes through `crates/storage`.

## The icon

`packages/ui/src/assets/app-icon.png` is the source: 1024 px, with the corners
already transparent. Everything in
`apps/desktop/src-tauri/icons/` is generated from it, and Settings shows the
same file in About, so the artwork exists once:

```bash
cd apps/desktop
pnpm exec tauri icon ../../packages/ui/src/assets/app-icon.png
```

That writes Android, iOS and Windows Store sizes too, which this project has no
targets for; only the five files `bundle.icon` lists are kept.

Where the icon comes from depends on how the app was started. An installed
package puts the PNGs in `hicolor` and writes a desktop entry with
`Icon=pidge` and `StartupWMClass=pidge`, and GTK
derives the same app id from the binary name, so a Wayland taskbar matches the
window to that entry. Run straight from `target/`, there is no desktop entry to
match: X11 falls back to the window icon compiled into the binary, and Wayland
shows a generic one. That is a property of the launch, not of the build.

`app.enableGtkAppId` would set the app id to the bundle identifier instead,
which is exactly what the desktop entry does not say — leaving it off is what
makes the two agree.

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
pnpm --filter pidge build     # extension host + webview
pnpm --filter pidge package   # vsce package --no-dependencies
```

`Sidecar.resolveBinary` looks, in order, at:

1. the `apiClient.sidecarPath` setting,
2. `bin/<platform>-<arch>/`,
3. `target/debug` then `target/release`, so the repo runs from source.

If none exist it says so, naming the platform it looked for, rather than failing
at the first request.

## Fonts and their licences

IBM Plex Sans and IBM Plex Mono are bundled from `packages/ui/src/fonts/` and emitted
into the build output as hashed `.woff2` files. They are never fetched at
runtime.

Both are SIL Open Font License 1.1, which requires the licence to travel with
the font, so `scripts/viteFontLicenses.ts` emits it as a build asset in the same
pass that emits the font. There is nothing to remember at packaging time:

| Artifact  | Fonts                                               | Licences                                                                    |
| --------- | --------------------------------------------------- | --------------------------------------------------------------------------- |
| Desktop   | `dist/assets/*.woff2` (embedded via `frontendDist`) | `dist/licenses/` and, via `bundle.resources`, `src-tauri/licenses/` on disk |
| Extension | `media/assets/*.woff2`                              | `media/licenses/`                                                           |

`src-tauri/licenses/` is written by the same plugin during the frontend build,
which `tauri build` runs first through `beforeBuildCommand`, so the files exist
by the time the bundler reads `bundle.resources`.

Verify what a VSIX would contain with:

```bash
pnpm --filter pidge exec vsce ls --no-dependencies
```

`--no-dependencies` is required: `vsce` otherwise shells out to `npm ls`, which
cannot read a pnpm workspace.

## A note on the extension's package name

`apps/vscode/package.json` is named `api-client`, not `@api-client/vscode` like
the other workspace packages. The name doubles as the VS Code extension id and
`vsce` rejects a scoped one. `@types/vscode` is pinned to the same minor as
`engines.vscode` for the same reason — `vsce` refuses to package a mismatch.

## Webview bundle

The webview is built by Vite to `apps/vscode/media/webview.js` and
`webview.css`, with fixed names because the panel HTML references them directly.
It runs under a strict CSP: scripts only from that bundle and only with a
per-render nonce, no remote origins at all. Inline styles are allowed because
CodeMirror injects its own stylesheet at runtime; inline scripts are not.
