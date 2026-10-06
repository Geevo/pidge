// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.
import type { HttpRequest } from "./HttpRequest";

/**
 * One past send. Response bodies are deliberately not kept: history is for
 * getting back to a request, not for archiving payloads.
 */
export type HistoryEntry = { id: string, timestamp: number, request: HttpRequest, status: number | null, statusText: string | null, durationMs: number | null, sizeBytes: number | null, 
/**
 * Set instead of the status fields when the request never completed.
 */
error: string | null, };
