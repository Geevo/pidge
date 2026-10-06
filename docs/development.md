# Development

## Setup

You need the .NET 10 SDK, Node 20+ and pnpm. On Linux the desktop window also
needs the GTK 3 and WebKitGTK 4.1 development packages (see the README).

```bash
pnpm install
dotnet build Pidge.slnx
```

NuGet packages are restored on the first build. The desktop project embeds the
built frontend, so it refuses to build until `apps/desktop/dist` exists:

```bash
pnpm --filter @api-client/desktop build
```

## Running

```bash
pnpm dev:desktop    # Vite with hot reload, and the window pointed at it
```

That starts the Vite dev server and `dotnet run`s `src/Pidge.Desktop` with
`PIDGE_DEV_SERVER=http://localhost:5173`, which makes the window load the UI
from the dev server instead of from the embedded files. Changes to the UI
reload in place; changes to C# need the command restarted.

For the VS Code extension:

```bash
pnpm build:sidecar --debug          # dotnet publish + stage the binary
pnpm --filter pidge build
```

Then <kbd>F5</kbd> in VS Code, and the pidge icon in the Extension
Development Host's activity bar: **New Request** opens a request as an editor
tab. When running from source without a staged binary, the
extension falls back to `artifacts/bin/Pidge.Sidecar/debug/`, so a plain
`dotnet build src/Pidge.Sidecar` is enough.

`pnpm dev:vscode` watches the extension host. The webviews, a request tab and
the side bar, are a separate build; rebuild them with
`pnpm --filter pidge build:webview` and reload the window.

`PIDGE_LOG=debug` makes either host log more (to stderr for the sidecar).

## Checks

```bash
dotnet format Pidge.slnx --verify-no-changes
dotnet build Pidge.slnx -warnaserror
dotnet test Pidge.slnx

pnpm lint
pnpm typecheck
pnpm test
pnpm format:check
```

Every library is marked AOT-compatible, so the build reports trimming and
native-compilation hazards as warnings, which `-warnaserror` turns into
failures. The published apps are built the same way.

## Tests

**.NET.** `dotnet test Pidge.slnx`. The engine tests run against
`tests/Pidge.TestServer`, a small local HTTP/1.1 server. Nothing in the suite
touches the public internet. It has routes for JSON, echo, headers, arbitrary
statuses, delays, binary bodies, deliberately invalid JSON, redirect chains and
loops, cookies, multipart echo, large bodies, a body that dribbles out slowly,
and one that never responds at all.

`tests/Pidge.Sidecar.Tests` drives the real sidecar over a pipe, the same way
the extension host does: handshake, version mismatch, malformed input,
concurrent requests, cancellation, state round trips, and a clean shutdown. It
starts the sidecar with `PIDGE_KEYRING=off`, so the suite never adds items to
the keyring of the machine running it, and behaves the same whether or not that
machine has one. The storage tests use an in-memory keyring instead of the
system one.

**TypeScript.** `pnpm test` runs Vitest with React Testing Library. The UI tests
use a fake bridge rather than a process, and cover sending, response rendering
(JSON, invalid JSON, binary), errors in the response pane, cancellation, tabs,
keyboard shortcuts, URL/param sync, history, and saving.

## Wire types

The TypeScript declarations for the wire types live under
`packages/ui/src/generated/`, one file per .NET type. They are written by hand,
and `TypeScriptMirrorTests` in `tests/Pidge.Protocol.Tests` compares each one
with the JSON contract of its .NET type, so a field added, renamed or dropped on
one side fails the build rather than a request.

## Unicode tables

Host names go through UTS 46 (international domain names) in
`src/Pidge.Core/Idna`, which needs the Unicode mapping and property tables in
`IdnaTables.g.cs`. To move to a newer Unicode version:

```bash
node scripts/gen-idna-tables.mjs --unicode 17.0.0
```

## Conventions

- Boring, obvious code over clever abstractions.
- Comments explain _why_, not _what_.
- Behaviour lives in `src/Pidge.Session` and below. Hosts stay thin.
- No reflection-based serialization: JSON goes through source-generated
  `JsonSerializerContext`s, so it survives native compilation.
- New features start by asking whether the app needs them at all. If a product
  decision is ambiguous, favour less product.

## Adding a capability end to end

1. Extend the models in `src/Pidge.Core`.
2. Implement it in `Pidge.HttpEngine`, `Pidge.Storage` or `Pidge.Variables`.
3. Expose it on `Session`.
4. Add the desktop bridge command and the protocol message.
5. Add it to `PlatformBridge` and implement it in both bridges.
6. Mirror any new wire type in `packages/ui/src/generated/`.
7. Cover it in `dotnet test` and in `pnpm test`.
