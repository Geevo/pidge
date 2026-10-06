# Architecture

## The shape of it

```
                       ┌──────────────────────────┐
  Desktop              │   packages/ui  (React)   │            VS Code
                       │   PlatformBridge         │
                       └───────────┬──────────────┘
              ┌────────────────────┴────────────────────┐
              │                                         │
     ┌────────▼─────────┐                    ┌──────────▼───────────┐
     │ PhotinoX window  │                    │ extension host (TS)  │
     │ src/Pidge.Desktop│                    │ apps/vscode          │
     └────────┬─────────┘                    └──────────┬───────────┘
              │                                         │ stdin/stdout
              │                              ┌──────────▼───────────┐
              │                              │ src/Pidge.Sidecar    │
              │                              └──────────┬───────────┘
              └────────────────┬─────────────────────────┘
                     ┌─────────▼──────────┐
                     │ Pidge.Session      │  the application service
                     └─────────┬──────────┘
          ┌────────────────────┼────────────────────┐
  ┌───────▼────────┐  ┌────────▼────────┐  ┌────────▼────────┐
  │ HttpEngine     │  │ Storage         │  │ Variables       │
  └───┬────────┬───┘  └─────────────────┘  └─────────────────┘
      │        │
      │  ┌─────▼────────┐
      │  │ Codegen      │  the same request, written out
      │  └──────────────┘
   ┌──▼───────────┐
   │ Core         │  models + normalized errors
   └──────────────┘
```

## The rule that keeps it honest

`Pidge.Session` is the only thing either frontend is allowed to drive. The
bridge commands in `src/Pidge.Desktop/Host.cs` and the message handlers in
`src/Pidge.Sidecar/SidecarServer.cs` are both thin: they deserialize, call one
`AppSession` method, and serialize the result.

This is deliberate. If behaviour lived in the adapters, the two platforms would
drift the moment one of them grew a feature. As it is, "does the desktop app do
X?" and "does the VS Code extension do X?" have the same answer by construction.

`Pidge.HttpEngine` goes further and knows nothing about persistence either. It
takes an `HttpRequest`, sends it, and returns an `HttpResponse` or throws a
`RequestErrorException` carrying a `RequestError`. That is why the engine tests
can drive it directly, and why a CLI could be added without touching it.

`Pidge.Codegen` sits on the engine rather than beside it, for the same reason.
Writing a request out as curl means knowing the URL that will be requested, the
headers that will go with it and the content type the body implies — and
`RequestPlanning.Effective` is what works those out for the send itself. A
snippet that computed them a second time would be right until one of them
changed.

What it cannot take from the engine is the settings around a send: the timeout,
whether redirects are followed, and the trust and identity settings. Generated
code has no app to inherit those from, so `ClientOptions` carries them in and
each generator writes them out. Where a client has no equivalent — `.NET` reads
trust from the machine store, and `requests` cannot open a PKCS#12 bundle — the
snippet says so in a comment instead of looking complete and failing.

## Projects

| Project            | Owns                                                                                    |
| ------------------ | --------------------------------------------------------------------------------------- |
| `Pidge.Core`       | `HttpRequest`, `HttpResponse`, `RequestError`, secret-header redaction, URL parsing     |
| `Pidge.Variables`  | `{{name}}` substitution and environments                                                |
| `Pidge.HttpEngine` | the `SocketsHttpHandler` transport, request building, cancellation, timing, size limits |
| `Pidge.Codegen`    | the request written out in ten languages, and the libraries for them                    |
| `Pidge.Storage`    | `AppState`, atomic writes, schema version and migrations, history cap, secrets          |
| `Pidge.Session`    | engine + store + in-memory state; every operation a frontend can perform                |
| `Pidge.Protocol`   | the newline-delimited JSON messages between the extension and the sidecar               |
| `Pidge.Sidecar`    | the `api-client-sidecar` executable: a line reader around `AppSession`                  |
| `Pidge.Desktop`    | the `pidge` executable: one window, the embedded UI, and where the window was last      |
| `Pidge.TestServer` | a local HTTP/1.1 server for the tests                                                   |

`Pidge.Core` parses URLs itself, to the WHATWG URL Standard (`WebUrl`), with its
own percent-encoding and UTS 46 host names, so a URL means the same thing here
as it does in the browser the UI runs in.

Both executables are published with NativeAOT.

## Types cross the boundary once

The .NET types are the single source of truth. The TypeScript declarations in
`packages/ui/src/generated/` mirror them, one file per type, and are committed so
a checkout typechecks without building the .NET side. `TypeScriptMirrorTests` in
`tests/Pidge.Protocol.Tests` holds each declaration to the JSON contract of its
type — the same fields, the same nullability, the same tags on a tagged union,
the same strings for an enum — so a field added, renamed or dropped on one side
fails the tests rather than a request.

That contract is System.Text.Json with source-generated contexts —
`CoreJsonContext`, `StorageJsonContext`, `SessionJsonContext`,
`ProtocolJsonContext`, `DesktopJsonContext` — each built from
`PidgeJson.CreateOptions`, so both hosts write the same JSON and native
compilation has no reflection to lose.

Response bodies are `byte[]` in C# and base64 `string` in TypeScript, because
JSON has no byte array and an array of numbers would be ruinous for a 50 MB
response. `packages/ui/src/lib/base64.ts` decodes it.

## The platform bridge

```ts
interface PlatformBridge {
  sendRequest(request: HttpRequest): Promise<SendOutcome>;
  cancelRequest(requestId: string): Promise<void>;
  generateCode(request: HttpRequest, target: CodeTarget): Promise<string>;
  loadState(): Promise<LoadedState>;
  saveState(state: AppState): Promise<AppState>;
  saveRequest(input: SaveRequestInput): Promise<AppState>;
  deleteSavedRequest(savedRequestId: string): Promise<AppState>;
  clearHistory(): Promise<AppState>;
  pickFile?(request: FilePickRequest): Promise<string | null>;
  exportSavedRequests?(input: ExportInput): Promise<string | null>;
  importSavedRequests?(): Promise<ImportOutcome | null>;
  subscribe?(listener: (command: HostCommand) => void): () => void;
  readonly editor?: EditorHost;
  readonly window?: WindowControls;
  readonly platform: string;
}
```

Components never branch on the platform. `App` takes a bridge and that is the
whole seam; the tests pass a fake in place of a process.

On the desktop the bridge (`apps/desktop/src/bridge.ts`) posts each call to the
host as `{ id, command, args }` through `window.external.sendMessage`, and
`Host` answers with `{ id, ok, value | error }`. The host runs the work off the
UI thread and posts the answer back through the window's dispatcher. In VS Code
the extension host turns the same calls into sidecar messages; see
[protocol.md](protocol.md).

`editor` is what makes VS Code more than a second window. Each request there is
an editor tab with an app of its own, so with `editor` present the app shows a
single request: no tab strip, no drawers, no palette, and the editor's theme
rather than one of its own. Save goes through `editor.saveRequest`, which asks
for the name in VS Code's own input box. History and saved requests are a
separate page in the side bar, `Sidebar` from `packages/ui`, whose rows carry
`data-vscode-context` so Open, Export and Delete are VS Code's own right-click
menu. Every tab and the side bar share one state file through the extension
host, which tells the other tabs (`HostCommand`) when settings or environments
change.

## Who owns history

History is owned by `AppSession`, not by the UI, and `AppSession.ReplaceState`
deliberately ignores the `history` field of whatever the UI sends. Otherwise a
UI that took a snapshot, sent a request, and then wrote its snapshot back would
erase the row that send had just created.

So that the panel still updates live, `AppSession.SendWithOverridesAsync`
returns the `HistoryEntry` it recorded alongside the response, and the reducer
prepends it. `ClearHistory` and `DeleteHistoryEntry` are the only ways a
frontend can remove anything from it.

## Themes

Every colour is a token on `:root`, so a theme is one block redefining them.
`useTheme` writes the resolved palette to `data-theme` on the document element.

"System" is resolved in `useTheme` with `matchMedia`, not left to the
`prefers-color-scheme` block alone, and it listens so that a desktop changing
its mind reaches a window that is already open. The media query still matters:
it paints the first frame, before React has mounted. It is keyed on
`:root:not([data-theme])` rather than "not light", or a named light palette
would have had the system's dark values layered over it in that first frame.

The base tokens on `:root` are the light palette, so a theme only restates what
differs from it. That is why the warm light block is a third the size of the
warm dark one rather than a copy.

Each block is matched by attribute as well as on the root — `[data-theme="dark"]`
beside `:root[data-theme="dark"]` — because Settings draws a sample of every
theme at once, and a sample is not the document. Inheritance is what makes this
need spelling out: a warm light sample inside a dark window would otherwise take
the window's accent, since the warm blocks restate only the greys. So the two
light themes are listed on the base block and the two dark ones on the dark
block, and each warm block overrides from there. In VS Code the mapping onto the
editor's colours is keyed on `:root[data-theme]` too, and comes later, so the
editor's colours win over whichever palette the editor's light or dark picked.

`color-scheme` is set per theme as well as the colours, so the browser's own
furniture — scrollbars, the caret, the right-click menu — follows.

### The right-click menu

In a production build of the UI the webview's own menu opens only where Copy and Paste mean
something — in a text field, or over selected text — and nowhere else
(`trimContextMenu` in `apps/desktop/src/bridge.ts`). Elsewhere it offers Back,
Reload, Save As and Print, none of which mean anything in an app. Keeping the
native menu rather than drawing one in the page keeps Paste working without a
clipboard permission prompt. The UI served by the dev server keeps the full
menu, Inspect Element included. In VS Code the webview menu is VS Code's, which already offers only
Cut, Copy and Paste, and on a side bar row the row's own Open and Delete (and
Export, for a saved request) instead.

### The controls the platform draws

A native `<select>` can be styled shut but not open. The list that drops down is
the platform's: in a WebKit webview on Linux it is a GTK menu in the desktop's
own theme, which ignores `option { background-color }` entirely and left a light
palette with a black list on a dark desktop.

`gtk-application-prefer-dark-theme` does not rescue it either. Breeze — KDE's
theme — ships its dark variant as a separate theme rather than as a variant, so
the preference changes nothing. That was measured with a WebKitGTK window rather
than assumed.

So `Select` draws both halves: a `<button role="combobox">` and a
`<ul role="listbox">`, with arrow keys, Home/End, Escape, and a click outside to
close. The list is rendered into `document.body` and positioned against the
trigger, because the dialog and the tab strip both clip. It closes on scroll and
on resize rather than following the trigger around.

`window.prompt` and `window.confirm` went the same way, and for the same reason.
The prompt's entry is sized by the platform and was too narrow in a WebKit
webview to show the URL it was suggesting, and both look like a different
application on each operating system. `PromptDialog` asks for a name — the
suggestion selected backwards, so a long URL shows from its start rather than
its query string — and `ConfirmDialog` asks the three yes-or-no questions:
discarding a tab's changes, clearing history, deleting a saved request.

Whether to ask before closing a tab is `needsCloseConfirmation`, and it moved
out of `useApiClient` when the dialog did. A hook that puts a dialog up cannot
be driven by anything without a screen, and the sidecar has none.

That leaves the menu the webview puts up on a right click, which is still the
platform's and stays in the desktop's colours. Nothing passes the palette down
to the window: on Linux that would set `gtk-application-prefer-dark-theme`,
which changes neither that menu under Breeze nor what `prefers-color-scheme`
reports.

A palette cannot be judged from its name, so the theme is picked from samples
rather than from a list: the app in miniature — window, tab, URL bar, Send, two
lines of text — drawn five times over in five palettes. "System" is both
palettes at once, split corner to corner, since drawing whichever the desktop
prefers today would make it a copy of Light or Dark with nothing to say it
follows anything.

Picking one still applies it immediately, and `App` holds that choice separately
from the saved settings. Cancelling drops it and the saved theme comes back. The
preview lives in `App` rather than in the dialog because `App` is what writes
`data-theme`; two writers of the same attribute would make the result depend on
the order of effects.

Method colours are deliberately not themed. They are Swagger's palette because
they carry meaning, and a POST should be the same green wherever it is read.

### Syntax colours

The colours a body is highlighted in are a second choice, kept apart from the
theme: `data-syntax` on the document element, beside `data-theme`. Every
combination of the two is legal, so they cannot be one setting.

Each scheme is a block of `--ac-syntax-*` tokens, and CodeMirror's highlight
style names those tokens rather than any colour. The style is therefore built
once and never rebuilt: picking a scheme repaints the editors instead of
reconfiguring them, and nothing loses its cursor or its folds.

The default scheme, `app`, points the tokens back at the palette above — the
accent for names, the status colours for values — which is what the editors
were wearing before there was a choice. The two borrowed schemes, VS Code's
defaults and One, are literal colours, and each has a light and a dark form
selected by the theme: the background under the code is the app's, and a scheme
drawn for the other one is unreadable on it. Only the tokens change; the editor
keeps the app's background, gutter and caret whichever scheme is chosen.

`[data-syntax]` is matched as a plain attribute for the same reason the theme
blocks are: Settings draws a sample of each scheme at once, and every sample has
to carry its own colours while the document keeps the chosen ones. Both pickers
are built the same way — the sample is the control, with a real radio inside it,
hidden, so the group keeps the arrow keys and the checked state the platform
already implements.

## The window

The desktop window is a PhotinoX window — WebView2 on Windows, WebKitGTK on
Linux — and it is chromeless (`SetChromeless(true)` in
`src/Pidge.Desktop/Program.cs`). GTK on Wayland always draws its own header — it
does not implement the protocol KDE and other compositors use for server-side
decorations — and that header is far taller than the platform's own. Under
XWayland the same app got a normal, thin titlebar, but XWayland costs a copy and
composite per frame and made scrolling visibly laggy.

So the app draws its own: the tab strip doubles as the title bar, with the
leftover space as a drag region and minimise/maximise/close at its end. That is
one row of chrome rather than two.

On Windows the window is chromeless only to begin with. `src/Pidge.Desktop/Windows/`
gives it back the usual window styles, so it animates, snaps and minimises from
the taskbar, and drops only the title bar. The sides and bottom keep the
system's sizing border, invisible but for a line, so they resize from just
outside the window, as any other window's do. The top edge, where the page
reaches the top of the window, is a window of its own: all but transparent,
owned by the main one so it stays just above it, and answering as the top of
the sizing border, so a drag there resizes and a double click stretches the
window to the height of the screen. That code goes by the window handle alone,
and knows nothing of PhotinoX. The cost is the one every framed window pays:
what shows is smaller than the window's size by the side and bottom borders.

On Linux, PhotinoX gives the undecorated window resize edges of its own: a
strip just inside each edge that starts the window manager's resize, and that
steps aside while the window is maximised. The page draws none. The drag region
works the same way on both: it is marked
`data-drag-region`, a press there asks the host to begin a move and a double
press toggles maximise. GTK only starts a move from the native press itself, so
on Linux the page reports where those strips are and the window manager does
the rest.

Drawing the buttons means drawing the right ones. A single set looks foreign
everywhere except where it came from, so `window_buttons` reports which desktop
the app is running on — Windows, KDE or anything else, read from
`XDG_CURRENT_DESKTOP` — and `WindowChrome` keeps all three sets. Breeze draws
chevrons and, once the window is maximised, a diamond in place of the maximise
chevron; Adwaita a low bar and two rings; Windows a line and a square. The
button behind the glyph differs as well: Windows fills a tall rectangle, Breeze
lights a circle under the pointer, Adwaita keeps a faint circle there all the
time. The sizes and the 24px spacing were measured off real title bars on a
Plasma desktop rather than guessed.

The answer arrives before the first render, because swapping the buttons
afterwards would be visible. It costs one round trip to the host behind the
boot screen.

Which of the two middle glyphs is drawn follows the window itself, through
`onResized`, rather than the click that asked for it. The host posts a `resized`
event whenever the window's size or state changes. A compositor maximises when
it is ready, so `isMaximized()` straight after `toggleMaximize()` still reports
the old state and the glyph ends up a step behind; the event also covers the
ways a window is maximised without the button, such as a double click on the
title bar or a keyboard shortcut.

All of this hangs off `PlatformBridge.window`, which is optional and absent in
VS Code, where the editor owns the frame. Every call through it is caught: with
no host to answer, a call rejects, and an effect that throws unmounts the entire
application — window chrome must not be able to do that.

Where the window was is remembered by the host as it moves, through
`AppSession.SetWindowPlacement`, and restored before the window is first shown;
[storage.md](storage.md) has the rules.

## Before the app has loaded

A webview paints white until something tells it otherwise, and the bundle that
would tell it is the thing being waited for. `apps/desktop/index.html` therefore
carries its own `<style>`: the page background, following
`prefers-color-scheme`, and a spinner inside `#root` that React's first render
replaces. Both repeat values from `styles.css` on purpose — they have to work
when that file has not arrived.

Measured in the dev build, where the module graph is slowest: the HTML is parsed
at 121 ms and React's first frame lands at 1077 ms. That was a second of blank
window; it is now a second of the app's own background.

The spinner fades in at 400 ms, so a start quicker than that shows nothing at
all rather than a flash of loading, and it holds still under
`prefers-reduced-motion`.

No second window: a splash window would have to be created, positioned,
themed and then closed, and it cannot show anything the first window cannot
show earlier.

## Panes

`SplitPane` arranges the request and response either as rows or as columns, with
a divider that drags, takes arrow keys, and resets to even on a double click.

The layout is a preference and lives in `Settings`. The split position is
per-tab: `ScratchTab.SplitPercent` holds it, so comparing a long response in
one tab does not squash the request editor in another. `null` falls back to
`Settings.SplitPercent`, which each drag also updates — that way existing tabs
stay where they were put, and a new tab opens where you were last working rather
than always at the original default. Both persist with the tab.

Two details worth knowing:

- The size is held in a ref as well as in state. A `pointermove` can arrive
  before React has re-rendered the `pointerdown`, and reading the state would
  drop that movement — a fast drag could do nothing at all.
- The new size is handed up only on release. Committing on every frame would be
  a hundred state updates and a hundred debounced writes to disk for one drag.

### Why the URL field is not a controlled input

Ctrl+Z in the URL bar did nothing, and the reason is not in our code. React
writes `defaultValue` on every commit of a controlled input, which sets the
value attribute, and setting that attribute makes the browser throw away the
undo history it had been building. Typing twenty-five characters wiped the stack
twenty-five times.

So the field keeps its own text. It renders with a `defaultValue` captured on
the first render, and an effect writes into the node only when the `url` in
state differs from what the field is showing — switching tabs, the params table
rewriting the query, a session restored at startup. Those replace what is in the
field anyway, so losing undo history with them is the right outcome; typing no
longer touches it at all.

A test pins this by counting writes to `value` and `defaultValue` while typing.
It fails, loudly, if anyone makes the field controlled again.

That was half the problem. The other half is that **WebKitGTK does not bind
Ctrl+Z at all**: the keystroke arrives at the page unprevented, nothing happens,
and `document.execCommand("undo")` performs the edit perfectly well when called.
It leaves the binding to the embedding application. Chromium binds it, which is
why Windows had undo and Linux had neither — and why Windows lost only redo to
the attribute writes above, since Chromium keeps the history behind the cursor
and discards what is ahead of it.

Running `document.execCommand("undo")` on the key fixes that, but it buys a
history that is no use: a whole run of typing is one step, so a single press
empties the field instead of taking back the last thing written.

So the field keeps its own history, in `lib/textHistory.ts`. Snapshots coalesce
while someone types and a step ends where a person would expect to stop — after
a pause of 600 ms, and at the punctuation that separates the parts of a URL
(`/ ? & = # :` and whitespace). Typing `example.com/users?a=1` and pressing undo
walks back through `example.com/users?a`, `example.com/users`, `example.com`.
Undoing does not extend the step it lands on, a new edit discards the redo
future, and the history starts again whenever something other than typing
replaces the field — switching tabs, or the params table rewriting the query —
so undo cannot walk into another request's URL.

Each step calls the same handler as typing, so the params table and the rest of
the state follow the field back and forth.

### Every field, wired once

The keys and the history are the same in every text field, so they are installed
once at the document rather than wired into forty-odd inputs — `lib/fieldHistory`
keeps a history per element, records on `input`, and steps it on the key. It
covers the URL bar, the params and headers tables, the auth fields and the
dialogs, and it covers the next field somebody adds without them having to know
any of this.

Two things it stays out of. Anything inside a `.cm-editor` belongs to CodeMirror,
which binds the keys itself and keeps a better history than ours. And `number`
inputs are left alone, because asking one for a caret position throws.

When a field's text was replaced by something other than typing — a different
tab's request, the params table rewriting the query — the recorded history no
longer matches what is on screen, and the field starts again from there rather
than stepping back into text the person never typed.

## Response bodies

A body is rendered in the same CodeMirror the request body uses, in read-only
mode, which brings syntax highlighting and fold arrows for free rather than
needing a bespoke tree view. `Collapse all` and `Expand all` run CodeMirror's
own commands against the view.

`syntaxForMime` picks the language from the response's content type: JSON, HTML,
XML, YAML, CSS or JavaScript, and plain text for anything else. The content type
is the only evidence used. Sniffing the bytes gets HTML and XML wrong in both
directions, and a document highlighted as the wrong language is harder to read
than one with no highlighting at all. XHTML is the one special case — it matches
both rules and is treated as HTML, which is how it reads.

Folding is offered for every language, since each knows its own blocks, and
withheld from plain text, which has none. Invalid JSON is the exception in the
other direction: it is shown verbatim with no language attached, so a broken
payload does not render as a wall of red.

Only JSON is reformatted. Whitespace carries meaning in HTML and YAML, so
re-indenting them would change the document you asked to look at.

A body the server called JSON is laid out on arrival, as it always has been.

`Pretty print` is for the other case, and appears only there: a body whose
content type is not JSON but whose text parses as JSON anyway. Plenty of APIs
answer `text/plain` with one long line of it. Unticked — which is how it starts
— the body is exactly what arrived; ticked, it is laid out, and since ticked
implies JSON the highlighting and the fold arrows follow.

Asking is what makes that safe. The content type did not claim JSON, so the app
does not decide that it is; a successful parse only means the offer can be made,
and the offer is the user's to take.

Nothing else grows a checkbox. Text that is only text has nothing to lay out,
and neither has a body whose content type says JSON but which does not parse —
SWAPI's `?format=wookiee` answers `application/json` with unquoted barewords, and
it is shown exactly as it came.

The six languages are bundled rather than fetched on demand: they cost 181 KB
of the frontend bundle, which is read from disk, and a body should be
highlighted the moment it arrives.

The highlight style is one set of rules for all of them, built from the same
palette as the rest of the app — names in the accent colour, text green, numbers
amber, keywords orange, comments faint, and anything the parser rejected in the
danger colour.

### Giving the editor a height

CodeMirror only virtualizes when it has a bounded height. Dropped into an
ordinary `overflow: auto` panel it grows to the full document instead — a
172 KB response measured 327,699 px tall — and lays out every line, which made
scrolling crawl.

So a panel whose child is an editor gets `.ac-scroll--flush`: it becomes a flex
column with hidden overflow and hands scrolling to `.cm-scroller`. Both the
request body and the response body do this. After the fix the response editor
scrolls fractionally cheaper than a plain `<div>` of comparable length.

The scrolling regions also set `contain: paint`. A dialog floats above a
translucent full-screen backdrop, and without containment a scroll inside it can
repaint everything underneath rather than just the scrolled region.

Above 2 MB the body falls back to a plain `<pre>` with a note. Highlighting and
folding a document that size costs more than it is worth, and the fallback still
shows everything.

### Why the content policy allows inline styles

The desktop's content policy is added by the host: `Assets.Open`
(`src/Pidge.Desktop/Assets.cs`) serves the UI embedded in the executable from
`app://localhost/`, and writes the policy into `index.html` as a `<meta>` on the
way out. It allows the page itself and nothing else, with one exception —
`style-src 'self' 'unsafe-inline'`.

CodeMirror ships its styles as a stylesheet it inserts into the document at
runtime, and the boot screen in `index.html` is an inline `<style>`, so both
need inline styles. Nothing adds a nonce to that directive, which matters: a
directive that carries a nonce ignores `'unsafe-inline'` altogether, and the
editor's stylesheet would be blocked.

What that looks like: the line numbers draw in one column, the body draws a
thousand pixels below them in the wrong font, and nothing is highlighted,
because every rule the editor relies on has been dropped.

Scripts get no such allowance. `default-src 'self'` covers them, so only the
bundle's own files run. With `PIDGE_DEV_SERVER` set the page comes from the dev
server instead, and is not rewritten.

## Settings

Four sections — General, Themes, Certs, About — behind one draft. The draft is a
single `Settings` object shared by all four rather than one per section, so an
edit made under Certs is still there when Save is pressed from General.

The dialog has a fixed height rather than one that fits its contents. The
sections are not the same length — General and Themes need about 290px of body,
Certs 390 and About 410 — and a dialog that jumps a quarter of its height when a
tab is pressed is disorienting. It is sized to the tallest, still capped at 80%
of the window, and anything past that scrolls.

Every section is boxed groups rather than a flat stack, because a stack of
unrelated rows reads as one long list of equals. Certs holds three decisions —
what to trust, what to present, and what to skip checking. General holds three
more: how a request is sent, what is kept between runs, and how a response is
shown. Turning verification off is boxed and tinted rather than sitting in line
with the rest, since it is not a preference among preferences.

Themes is its own section because its two settings are not fields: they are five
palettes and three sets of syntax colours, each drawn as a sample wide enough to
judge, which needs the width of the dialog rather than the half of it a field
leaves. The group's heading names the radio group, so the samples carry no
second label.

Each path field has a Browse button when `PlatformBridge.pickFile` is present:
the desktop supplies it through the host's `pick_file` command, which opens the
platform's own file chooser, and VS Code through `showOpenDialog` on the
extension host. It is optional like `window`, and the buttons are absent on a
host without one — typing the path still works, which is what the field did
before.

About shows the host's own version, which travels with the state: `LoadedState`
already carried the storage path, and the version goes the same way rather than
being baked into the frontend at build time, where it could drift from the
binary actually running.

## Errors

`RequestError` has a `kind` the UI can switch on, a `message` written for a
person, and an optional `detail` holding the flattened chain of underlying
exceptions. The detail goes behind a disclosure triangle; the message goes in
the response pane. Errors are never only a toast — they appear where the
response would have been, next to the request that caused them.

## Request building

`src/Pidge.HttpEngine/Prepare.cs` turns a resolved request into the method, URL,
headers and body bytes that go out, by the rules in `RequestPlanning`
(`Effective.cs`) that code generation shares. Decisions worth knowing:

- **An explicit `Authorization` header wins.** If one is set and enabled, the
  Auth tab is skipped and a warning is attached to the response. Silently
  overwriting what someone typed would be worse than either alternative.
- **A `Content-Type` you set is never replaced.** The body type supplies one
  only when you have not.
- **A parameter in both the URL and the table is sent once.** They are one
  thing shown twice — editing the table rewrites the URL's query, and typing a
  query in the URL fills the table — so appending the table on top of the URL
  would send every parameter twice. The match is on decoded pairs, against the
  URL as it arrived, so two identical rows still send two copies.
- **Encoding can be turned off per request.** `encodeQuery` is on by default
  and is what almost everyone wants. Off is for a value that is already encoded,
  or that holds a `/` or `:` a server wants to see unescaped; the text then goes
  in as typed and is the user's to get right. The switch lives beside the params
  table and rewrites the URL as it is flipped, so what will be sent is on screen
  rather than a send away. The UI is where the app encodes — `paramsChanged`
  writes the query into the URL — and the engine applies the same rule to
  anything it appends itself.
- **Query values are percent-encoded, and the typed query is left alone.** A
  form serialiser turns a space into `+`, and `+` means space in a form body,
  not in a URL: a server is entitled to read `SW1A+1AA` as a postcode with a
  plus in it, and one did, answering 400 to a request that curl and Bruno could
  make. So the query string is assembled by hand rather than as a form: what the
  user typed is carried across byte for byte, and appended pairs are encoded to
  RFC 3986's unreserved set, which is what `encodeURIComponent` and curl
  produce.

## Auth that needs a round trip

Bearer, basic and an API key are headers, so they are applied in `Prepare.cs`
where the request is assembled. Digest is not: RFC 7616 is a conversation, and
the response can only be computed once the server has sent a nonce.
`HttpEngine.SendAsync` therefore sends, and on a 401 carrying a
`WWW-Authenticate: Digest` challenge, answers it and sends again.

Both attempts live inside the one timeout. Two round trips the user did not ask
for should not buy twice the wait.

A challenge that cannot be answered — a different scheme, a malformed header,
`auth-int` with no body — is not an error. The 401 is the honest answer to what
was asked, so it is returned with a warning saying why there was no second
attempt.

OAuth 2 needs a round trip of a different shape: a token before anything can be
sent at all. `src/Pidge.HttpEngine/OAuth2.cs` posts the form, reads the access
token and attaches it as a bearer token, for the three grants that are just a
request — client credentials, password, refresh token. Authorization code and
the other interactive flows are deliberately absent: they need a browser and a
redirect listener, which is a different kind of program from this one.

Tokens are cached until shortly before they expire, keyed by everything that
decides which token comes back — endpoint, grant, client, scope, user, refresh
token — so changing any of it asks for a new one instead of reusing a token
minted for something else. A burst of requests to one API costs one token
request.

A token that cannot be got is an error rather than a response, with its own
`RequestErrorKind.Auth`: the request never left, and reporting it as a failed
send would be a lie. The endpoint's own `error` field is quoted, since
`invalid_client` says more than 401 does.

OAuth 1 is neither: it is arithmetic. Every request carries a signature over its
own method, URL and parameters, so `src/Pidge.HttpEngine/OAuth1.cs` signs as the
request is prepared and there is nothing to fetch or cache. The awkward part is
the signature base string — percent-encoding applied twice, parameters sorted
after encoding, a form-encoded body signed along with the query — and getting it
wrong produces a rejection with no explanation. So the base string is tested
against the worked example in RFC 5849 §3.4.1.1, character for character.

The integration tests check the header's shape rather than recomputing the
signature in the test server. A second implementation written by the same hand
would agree with the first one's mistakes; the RFC's own vector will not.

The test server issues a challenge and then recomputes the digest itself, so the
test fails if the client's answer is merely well-formed rather than correct.

## Cancellation

`CancellationHandle` wraps a `CancellationTokenSource`. The engine links its
token with the timeout's and passes the result to the send and to every read of
the body, so cancelling mid-download works, not just cancelling before the
connection opens. `CancellationRegistry` maps request ids to handles so
`AppSession.Cancel(id)` — `cancel_http_request` on the desktop, `cancelRequest`
from the extension — needs no bookkeeping in the adapters.

A cancelled request is not recorded in history: it never really happened.

### The URL on the status line

The address bar says what was asked for. The status line says what was reached,
from `finalUrl` on the response, which the engine has always returned and which
nothing showed until now. Redirects, resolved variables and the params table can
each make the two differ, and when they do this is the only place it is visible.

Long URLs are cut in the middle rather than the end, by `shortenUrl`: the query
is the half worth seeing, since that is where a request usually stops matching
what was typed. The whole URL is on the element's title, and the rest of the row
holds its width so a long one cannot push the status code onto a second line.

## Response size

Bodies are streamed and cut off at `MaxResponseBytes` (50 MB by default), with
`truncated: true` on the response and a note in the status line. Truncating
beats erroring: you still get to look at the first 50 MB.

## TLS

The transport is .NET's `SocketsHttpHandler`, and with nothing configured it
verifies certificates the way the platform does. That means the client already
trusts whatever the operating system trusts — the Windows certificate store, the
macOS keychain, the system CA bundle on Linux — with no configuration at all.

`src/Pidge.HttpEngine/TlsConfig.cs` covers the two things the OS store cannot:

- **An extra CA.** A certificate validation callback accepts what the platform
  accepted, and otherwise builds the chain again against the named CAs alone
  (`X509ChainTrustMode.CustomRootTrust`), so an internal root is _added_ to the
  system store rather than replacing it. A corporate CA should not cost you the
  ability to reach the rest of the internet. Turning `useSystemRoots` off skips
  the platform's answer and trusts only the named CAs, for talking to one
  internal host and nothing else. Asking for neither is refused rather than
  quietly trusting nothing.
- **A client certificate.** Windows exports `.p12`/`.pfx`, so a PKCS#12 bundle
  is opened in process with `X509CertificateLoader`. In process deliberately:
  shelling out to `openssl` would add a tool Windows does not ship, and would
  put the password in the process list where any other user could read it. A
  PEM certificate and key work too; the key is passed through PKCS#12 once on
  loading, because Windows cannot present a key that only lives in memory.

The format is detected from the file contents, not the extension, so a `.crt`
holding PEM works and a `.pem` holding DER does too.

A CA file that will not parse is reported only once the rest of the settings
have loaded, so a missing client certificate is still the error you see first.
When any TLS setting is non-default, a failure to build the handler is reported
as a TLS error naming the certificate settings, because that is what it will
be.

### The padlock

The handler pools connections and says nothing about which one a response came
back on, so `PlaintextStreamFilter` wraps each connection's stream in a
`TrackedStream` (`TrackedStream.cs`). Writing a request marks the connection on
a `ConnectionCapture` that travels with that request's async flow, and the
response reads the negotiated protocol and the peer's leaf certificate from that
connection's `SslStream`. Requests are sent as HTTP/1.1 so that one request and
one connection can be matched up this way.

`src/Pidge.HttpEngine/PeerCert.cs` parses the certificate with
`X509CertificateLoader` into `TlsDetails`, and the response carries it: `null`
for plain HTTP, so the padlock appears exactly when the connection was
encrypted.

It is the leaf only. The stream hands back the peer certificate and not the
chain above it, so the dialog shows one certificate honestly rather than
implying a chain that was never captured.

A certificate that will not parse gives `certificate: null` rather than failing
the response. The TLS layer has already accepted the connection by then; a gap
in what we can display is not a reason to throw the response away.

### Rebuilding the engine

TLS settings shape the handler, which is built once. `AppSession` therefore
holds its engine behind a lock and replaces it when `ReplaceState` sees an
`EngineConfig` that differs from the live one — a record, so the comparison is
by value — and adding a CA takes effect on the next send rather than the next
launch. The engine it replaces is not disposed, since a send that started on it
may still be using it.

State is saved _before_ the rebuild is attempted. If a certificate path is wrong
the error still reaches the UI, but the setting persists — otherwise the dialog
reporting the error would have nothing left to correct.

## Room left deliberately

Proxy settings and client-certificate selection per host are not wired up; the
handler uses .NET's default proxy, which follows the system's.
`HttpEngine.CreateHandler`, where the `SocketsHttpHandler` is configured, is the
one place either would go.

### NTLM

NTLM authenticates a _connection_ rather than a request: a 401 offering the
scheme, a negotiate message, the server's challenge, and an authenticate message
computed from it — the last two on the same socket, or the server has no
challenge to check the response against.

Nothing in the handler's pool pins a connection to a sequence of requests. What
makes this work is narrower: for NTLM, `CreateHandler(ntlm: true)` builds a
handler whose pool holds one connection (`MaxConnectionsPerServer = 1`) and
which nothing else uses, then sends the two messages back to back. The idle
connection the first leg returns is the only one the second can take. Redirects
are not followed during the handshake, since following one would open a new
connection and lose the challenge.

That is a property of the pool rather than a guarantee from an API, so the test
server enforces it: `/ntlm` keeps its challenge in per-connection state and
refuses an authenticate message that arrives anywhere else. The test asserts
both messages landed on one connection, and the server recomputes the NTLMv2
proof rather than pattern-matching it.

`src/Pidge.HttpEngine/Ntlm.cs` builds the messages: NTLMv2 only, no signing or
sealing, no session key. The key derivation is pinned to the worked example in
MS-NLMP §4.2.4.1.1, because a server that dislikes the response says only 401,
which tells you nothing about which step was wrong.

The first request of every NTLM exchange is unauthenticated, on the shared
handler, because the scheme is not known until the server names it. That is the
protocol's cost, not an implementation choice.
