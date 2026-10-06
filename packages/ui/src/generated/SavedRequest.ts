// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.
import type { HttpRequest } from "./HttpRequest";

/**
 * A request the user chose to keep. Flat list, no folders, no collections.
 */
export type SavedRequest = { id: string, name: string, request: HttpRequest, createdAt: number, updatedAt: number, };
