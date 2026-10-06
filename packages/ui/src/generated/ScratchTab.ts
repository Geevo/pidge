// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.
import type { HttpRequest } from "./HttpRequest";

/**
 * An open editor tab. Tabs exist whether or not they were ever saved.
 */
export type ScratchTab = { id: string, 
/**
 * `null` means the tab is titled from its URL.
 */
name: string | null, request: HttpRequest, 
/**
 * Set when the tab came from, or was written to, a saved request.
 */
savedRequestId: string | null, 
/**
 * True when the tab differs from the saved request it is linked to.
 */
dirty: boolean, 
/**
 * This tab's own split position, as the request pane's percentage share.
 * `null` uses `Settings.splitPercent`, which is also what a new tab
 * starts from. Defaulted so a state file written before this existed
 * still loads.
 */
splitPercent: number | null, };
