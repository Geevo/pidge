# Packaging

## Desktop

```bash
pnpm build:desktop                    # this machine
pnpm build:desktop --rid linux-x64    # a specific runtime identifier
```

`scripts/desktop.mjs publish` builds the frontend with Vite, then publishes
`src/Pidge.Desktop` as a native (NativeAOT) executable into
`artifacts/desktop/<rid>/`. The built `apps/desktop/dist/` is embedded into the
executable as resources, and so are the native window host library PhotinoX
loads and, on Windows, the WebView2 loader. The executable is the whole app,
with nothing beside it.

Native compilation can't link those two libraries in, so they are carried as
resources and written out on first start, to a folder named for their contents
under the machine-local data folder (`%LOCALAPPDATA%\pidge\native` on Windows,
`~/.local/share/pidge/native` on Linux). Each start compares the files there
with the ones it carries and rewrites any that differ, and a new version clears
out the folders of older ones. `src/Pidge.Desktop/NativeLibraries.cs` does the
writing, and the `EmbedNativeLibraries` target in `Pidge.Desktop.csproj` the
embedding; a build without a runtime identifier (`dotnet run`, the tests)
embeds nothing and loads the libraries from the package as usual.

Native compilation only targets the operating system it runs on, so Windows is
built on Windows and Linux on Linux. `--rid` picks the architecture, and needs
the matching native toolchain installed.

The browser engine is the system's: WebView2 on Windows (present on Windows 11
and most of Windows 10) and WebKitGTK 4.1 on Linux. Neither is bundled.

`-warnaserror` is on for every publish. Native compilation reports what it
cannot see through (reflection, dynamic code) as warnings, and each one is
something that would break only in the published build.

### Releases

Releases are built by `.github/workflows/release.yml`, not on anyone's machine.
Pushing a tag builds the Linux packages on Ubuntu 22.04, so they run on older
glibc, and the Windows zip on a Windows runner:

```bash
git tag v0.1.0 && git push origin v0.1.0
```

The tag has to match `<Version>` in `Directory.Build.props`, optionally with a
suffix such as `-pre.12`; one with a suffix becomes a pre-release. The result is
a **draft** with every file and a `SHA256SUMS`, so the notes are written and the
release published by hand. Running the workflow from the Actions tab builds the
same files as workflow artifacts without releasing anything.

The Windows zip holds the executable, `LICENSE`, `THIRD-PARTY-LICENSES.md`
and the font licences. For Linux, `scripts/package-linux.sh` wraps the same
executable three ways, all installing the same files: `/usr/bin/pidge`, a
desktop entry, the 512 px icon, and the licences in `/usr/share/doc/pidge/`.

- `pidge_<version>_amd64.deb`, built with `dpkg-deb` (xz, which older dpkg can
  read). It depends on `libgtk-3-0`, `libwebkit2gtk-4.1-0`, `libnotify4` and
  `libssl3`.
- `pidge-<version>-1.x86_64.rpm`, built with `rpmbuild`. It requires `gtk3`,
  `webkit2gtk4.1`, `libnotify` and `openssl-libs`, listed by hand because the
  window host is inside the executable, where rpm's own scan can't see it.
- `pidge-<version>-x86_64.AppImage`: the static AppImage runtime, pinned by
  checksum, with a squashfs image of the app appended. Like the packages, it
  uses the system's GTK and WebKitGTK rather than carrying its own, so it is a
  few megabytes rather than the hundred or so a bundled WebKit would cost.

Nothing is code-signed. What there is instead:

- `SHA256SUMS`, which anyone can check a download against:
  `sha256sum --check --ignore-missing SHA256SUMS`.
- While the repository is public, a build attestation for every file: GitHub's
  signed record that it was built by this workflow from that tag.
  `gh attestation verify <file> -R Geevo/pidge` checks it. GitHub does not
  offer attestations for private repositories on this plan, so the step is
  skipped there.

Windows SmartScreen still warns about an unsigned executable; only a
code-signing certificate stops that.

## The icon

`packages/ui/src/assets/app-icon.png` is the source: 1024 px, with the corners
already transparent. Settings shows the same file in About, so the artwork
exists once. The desktop app carries two copies derived from it in
`src/Pidge.Desktop/icons/`:

- `icon.ico` (16 to 256 px) is compiled into the Windows executable, which is
  what Explorer and the taskbar show.
- `icon.png` (512 px) is embedded as a resource and set as the window icon at
  startup, which is what X11 and the Windows title bar show.

To regenerate them, with ImageMagick:

```bash
magick packages/ui/src/assets/app-icon.png -resize 512x512 src/Pidge.Desktop/icons/icon.png
magick packages/ui/src/assets/app-icon.png -define icon:auto-resize=256,64,48,32,24,16 src/Pidge.Desktop/icons/icon.ico
```

On Wayland a window has no icon of its own: the compositor matches the window's
app id to a desktop entry and shows that entry's icon. The app id is the binary
name, `pidge`, so a desktop entry with `Icon=pidge` and `StartupWMClass=pidge`
is what gives the window its icon there. The .deb and .rpm install that
entry. Run as an AppImage, or straight from `artifacts/`, there is no entry to
match unless something integrates the AppImage, and Wayland shows a generic
icon. That is a property of the launch, not of the build.

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

`scripts/build-sidecar.mjs` publishes one natively and stages it there:

```bash
pnpm build:sidecar                    # this machine
pnpm build:sidecar --rid osx-arm64    # a specific runtime identifier
pnpm build:sidecar --debug            # a debug build, quicker to make
```

The runtime identifier is mapped to the platform and architecture names VS Code
uses (`osx-arm64` is staged as `darwin-arm64`). As with the desktop app, native
compilation only targets the operating system it runs on, so each platform is
built on its own runner and the binaries are collected before packaging.

Then:

```bash
pnpm --filter pidge build     # extension host + webview
pnpm --filter pidge package   # vsce package --no-dependencies
```

`Sidecar.resolveBinary` looks, in order, at:

1. the `pidge.sidecarPath` setting,
2. `bin/<platform>-<arch>/`,
3. `artifacts/bin/Pidge.Sidecar/debug` then `release`, so the repo runs from
   source after a plain `dotnet build`.

If none exist it says so, naming the platform it looked for, rather than failing
at the first request.

## Fonts and their licences

IBM Plex Sans and IBM Plex Mono are bundled from `packages/ui/src/fonts/` and
emitted into the build output as hashed `.woff2` files. They are never fetched
at runtime.

Both are SIL Open Font License 1.1, which requires the licence to travel with
the font, so `scripts/viteFontLicenses.ts` emits it as a build asset in the same
pass that emits the font. There is nothing to remember at packaging time:

| Artifact  | Fonts                                             | Licences                                                       |
| --------- | ------------------------------------------------- | -------------------------------------------------------------- |
| Desktop   | `dist/assets/*.woff2`, embedded in the executable | `dist/licenses/`, embedded too, and beside it in every package |
| Extension | `media/assets/*.woff2`                            | `media/licenses/`                                              |

Verify what a VSIX would contain with:

```bash
pnpm --filter pidge exec vsce ls --no-dependencies
```

`--no-dependencies` is required: `vsce` otherwise shells out to `npm ls`, which
cannot read a pnpm workspace.

## A note on the extension's package name

`apps/vscode/package.json` is named `pidge`, not `@api-client/vscode` like the
other workspace packages. The name doubles as the VS Code extension id and
`vsce` rejects a scoped one. `@types/vscode` is pinned to the same minor as
`engines.vscode` for the same reason — `vsce` refuses to package a mismatch.

## Webview bundle

The webview is built by Vite to `apps/vscode/media/webview.js` and
`webview.css`, with fixed names because the panel HTML references them directly.
It runs under a strict CSP: scripts only from that bundle and only with a
per-render nonce, no remote origins at all. Inline styles are allowed because
CodeMirror injects its own stylesheet at runtime; inline scripts are not.
