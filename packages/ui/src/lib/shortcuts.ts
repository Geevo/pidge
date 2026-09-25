/**
 * Keyboard shortcuts. Ctrl on Windows and Linux, Cmd on macOS, decided once
 * here rather than in every handler.
 */
export type ShortcutName =
  "send" | "focusUrl" | "newTab" | "closeTab" | "save" | "textBigger" | "textSmaller" | "textReset";

const isMac = (): boolean =>
  typeof navigator !== "undefined" &&
  /mac|iphone|ipad/i.test(navigator.platform || navigator.userAgent);

export function matchShortcut(event: KeyboardEvent): ShortcutName | null {
  const primary = isMac() ? event.metaKey : event.ctrlKey;
  if (!primary || event.altKey) return null;

  switch (event.key.toLowerCase()) {
    case "enter":
      return "send";
    case "l":
      return event.shiftKey ? null : "focusUrl";
    case "n":
      return event.shiftKey ? null : "newTab";
    case "w":
      return event.shiftKey ? null : "closeTab";
    case "s":
      return event.shiftKey ? null : "save";
    /*
     * The keys a browser binds to zoom, bound here to the text size instead.
     *
     * Both spellings of each: the key on a US layout is `=` unshifted and `+`
     * shifted, and a numeric keypad sends `+` and `-` whatever the layout. A
     * user who needs the text larger should not have to find the one the app
     * happens to listen for.
     */
    case "=":
    case "+":
      return "textBigger";
    case "-":
    case "_":
      return "textSmaller";
    case "0":
      return event.shiftKey ? null : "textReset";
    default:
      return null;
  }
}

/**
 * Undo and redo inside a plain text field.
 *
 * WebKitGTK does not bind these itself: the keystroke reaches the page and
 * nothing happens, though `document.execCommand` performs the edit perfectly
 * well. It expects the embedding application to wire the keys up, so we do.
 * Chromium binds them natively, but running the same command there too keeps
 * one code path rather than two behaviours to reason about.
 */
export function matchEditingCommand(event: {
  key: string;
  ctrlKey: boolean;
  metaKey: boolean;
  altKey: boolean;
  shiftKey: boolean;
}): "undo" | "redo" | null {
  const primary = isMac() ? event.metaKey : event.ctrlKey;
  if (!primary || event.altKey) return null;

  const key = event.key.toLowerCase();
  if (key === "z") return event.shiftKey ? "redo" : "undo";
  // Ctrl+Y is redo on Windows and Linux; on a Mac it is not.
  if (key === "y" && !isMac() && !event.shiftKey) return "redo";
  return null;
}

/**
 * Select all, which a read-only pane has to answer for itself.
 *
 * An element that cannot take focus never sees the keystroke, and the browser's
 * own select all then takes the whole window — every button and label with it.
 */
export function isSelectAll(event: {
  key: string;
  ctrlKey: boolean;
  metaKey: boolean;
  altKey: boolean;
  shiftKey: boolean;
}): boolean {
  const primary = isMac() ? event.metaKey : event.ctrlKey;
  return primary && !event.altKey && !event.shiftKey && event.key.toLowerCase() === "a";
}

/** Shown in tooltips so the shortcut is discoverable without a menu. */
export function shortcutHint(name: ShortcutName): string {
  const modifier = isMac() ? "⌘" : "Ctrl+";
  const keys: Record<ShortcutName, string> = {
    send: "Enter",
    focusUrl: "L",
    newTab: "N",
    closeTab: "W",
    save: "S",
    textBigger: "+",
    textSmaller: "-",
    textReset: "0",
  };
  return `${modifier}${keys[name]}`;
}

/**
 * Find. An editor with focus answers this itself; anywhere else in the window
 * it means the response body, which is what anyone reaching for it is reading.
 */
export function isFind(event: {
  key: string;
  ctrlKey: boolean;
  metaKey: boolean;
  altKey: boolean;
  shiftKey: boolean;
}): boolean {
  const primary = isMac() ? event.metaKey : event.ctrlKey;
  return primary && !event.altKey && !event.shiftKey && event.key.toLowerCase() === "f";
}
