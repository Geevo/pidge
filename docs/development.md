# Development

## Setup

```bash
pnpm install
```

Rust dependencies are fetched on first build.

For desktop builds you also need the
[Tauri prerequisites](https://tauri.app/start/prerequisites/). Without them,
`apps/desktop/src-tauri` cannot compile; the rest of the workspace is unaffected:

```bash
cargo check --workspace --exclude api-client-desktop
```

## Running

```bash
pnpm dev:desktop    # Vite + Tauri, hot reload
```

For the VS Code extension:

```bash
pnpm build:sidecar --debug          # cargo build + stage the binary
pnpm --filter pidge build
```

Then <kbd>F5</kbd> in VS Code, and **pidge: Open** in the Extension
Development Host. When running from source without a staged binary, the
extension falls back to `target/debug/api-client-sidecar`, so a plain
`cargo build -p api-client-sidecar` is enough.

`pnpm dev:vscode` watches the extension host. The webview is a separate bundle;
rebuild it with `pnpm --filter pidge build:webview` and reload the
window.

## Checks

```bash
cargo fmt --all --check
cargo clippy --workspace --all-targets --all-features -- -D warnings
cargo test --workspace

pnpm lint
pnpm typecheck
pnpm test
```

## Tests

**Rust.** `cargo test --workspace`. The engine tests run against
`crates/testserver`, a small local HTTP/1.1 server. Nothing in the suite touches
the public internet. It has routes for JSON, echo, headers, arbitrary statuses,
delays, binary bodies, deliberately invalid JSON, redirect chains and loops,
cookies, multipart echo, large bodies, a body that dribbles out slowly, and one
that never responds at all.

`crates/sidecar/tests/stdio.rs` drives the real binary over a pipe, the same way
the extension host does: handshake, version mismatch, malformed input,
concurrent requests, cancellation, state round trips, and a clean shutdown.
It starts the sidecar with `PIDGE_KEYRING=off`, so the suite never adds
items to the keyring of the machine running it, and behaves the same whether or
not that machine has one. The storage tests
use an in-memory keyring instead of the system one.

**TypeScript.** `pnpm test` runs Vitest with React Testing Library. The UI tests
use a fake bridge rather than a process, and cover sending, response rendering
(JSON, invalid JSON, binary), errors in the response pane, cancellation, tabs,
keyboard shortcuts, URL/param sync, history, and saving.

## Generated types

TypeScript declarations for the wire types are generated from the Rust types by
`ts-rs` and committed under `packages/ui/src/generated/`:

```bash
pnpm gen:types
```

`cargo test` regenerates them as a side effect, so a stale binding shows up as a
diff rather than as a runtime surprise. Never edit them by hand.

## Conventions

- Boring, obvious code over clever abstractions.
- Comments explain _why_, not _what_.
- Behaviour lives in `crates/session` and below. Adapters stay thin.
- New features start by asking whether the app needs them at all. If a product
  decision is ambiguous, favour less product.

## Adding a capability end to end

1. Extend the models in `crates/core`.
2. Implement it in `http-engine`, `storage`, or `variables`.
3. Expose it on `Session`.
4. Add the Tauri command and the protocol message — both one-liners.
5. Add it to `PlatformBridge` and implement it in both bridges.
6. `pnpm gen:types`, then build the UI against the generated types.
7. Cover it in `cargo test` and in `pnpm test`.
