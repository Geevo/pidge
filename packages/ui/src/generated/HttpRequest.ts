// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.
import type { AuthConfig } from "./AuthConfig";
import type { HttpMethod } from "./HttpMethod";
import type { KeyValueEntry } from "./KeyValueEntry";
import type { RequestBody } from "./RequestBody";

/**
 * Everything needed to send one request. This is the unit the UI edits,
 * the engine executes, and storage persists.
 */
export type HttpRequest = { id: string, method: HttpMethod, url: string, queryParams: Array<KeyValueEntry>, headers: Array<KeyValueEntry>, auth: AuthConfig, body: RequestBody, 
/**
 * Overrides the default timeout for this request only.
 */
timeoutMs: number | null, 
/**
 * Whether query parameters are percent-encoded on their way into the URL.
 *
 * On is right for almost everything. Off is for a value that is already
 * encoded, or that holds a `/` or `:` a server wants to see unescaped —
 * at which point the text is the user's responsibility, not ours.
 *
 * Defaulted, so a request saved before this existed still loads.
 */
encodeQuery: boolean, };
