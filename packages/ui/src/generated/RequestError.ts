// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.
import type { RequestErrorKind } from "./RequestErrorKind";

/**
 * A request failure in a shape both frontends can render directly.
 */
export type RequestError = { kind: RequestErrorKind, 
/**
 * Plain-language explanation, safe to show in the response pane.
 */
message: string, 
/**
 * Technical chain for the diagnostics disclosure. May contain library detail.
 */
detail: string | null, };
