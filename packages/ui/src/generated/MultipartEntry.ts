// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.
import type { MultipartValue } from "./MultipartValue";

/**
 * A single part of a multipart/form-data body.
 */
export type MultipartEntry = { id: string, enabled: boolean, name: string, value: MultipartValue, };
