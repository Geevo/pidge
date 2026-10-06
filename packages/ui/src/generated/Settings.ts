// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.
import type { PaneLayout } from "./PaneLayout";
import type { SyntaxTheme } from "./SyntaxTheme";
import type { Theme } from "./Theme";
import type { TlsSettings } from "./TlsSettings";

export type Settings = { theme: Theme, syntaxTheme: SyntaxTheme, timeoutMs: number, followRedirects: boolean, maxHistory: number, maxResponseBytes: number, 
/**
 * Reopen the scratch tabs that were open last time.
 */
restoreTabs: boolean, wrapResponseLines: boolean, 
/**
 * Every size in the interface, as a percentage of its designed size.
 * 100 is the design; the UI clamps what it applies, so a hand-edited
 * state file cannot leave the text unreadably small or off the screen.
 */
fontScale: number, paneLayout: PaneLayout, 
/**
 * The request pane's share of the split, as a percentage. Clamped when
 * applied, so a hand-edited state file cannot collapse a pane entirely.
 */
splitPercent: number, 
/**
 * Trust and client-certificate settings. Changing any of these rebuilds
 * the HTTP client, so they take effect on the next send.
 */
tls: TlsSettings, };
