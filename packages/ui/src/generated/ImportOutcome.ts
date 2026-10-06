// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.
import type { AppState } from "./AppState";

/**
 * What an import brought in, for the notice that reports it.
 */
export type ImportOutcome = { state: AppState, imported: number, 
/**
 * `{{names}}` the new requests use that no environment defines, which
 * is what an export without its secrets leaves to be filled in.
 */
undefinedVariables: Array<string>, 
/**
 * A sentence for each request the file held that could not come across.
 */
skipped: Array<string>, 
/**
 * The file holds a password or token as it is: worth deleting now.
 */
plainSecrets: boolean, };
