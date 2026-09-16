/**
 * Keyboard shortcuts. Ctrl on Windows and Linux, Cmd on macOS, decided once
 * here rather than in every handler.
 */
export type ShortcutName = "send" | "focusUrl" | "newTab" | "closeTab" | "save";

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
    default:
      return null;
  }
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
  };
  return `${modifier}${keys[name]}`;
}
