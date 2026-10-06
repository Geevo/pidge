// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.
import type { ServerMessage } from "./ServerMessage";

/**
 * Sidecar to extension host.
 */
export type ServerEnvelope = { v: number, id: string | null, msg: ServerMessage, };
