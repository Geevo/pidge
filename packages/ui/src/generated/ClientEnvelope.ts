// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.
import type { ClientMessage } from "./ClientMessage";

/**
 * Extension host to sidecar.
 */
export type ClientEnvelope = { 
/**
 * Protocol version, on every message.
 */
v: number, 
/**
 * Correlation id, present for anything that expects a reply.
 */
id: string | null, msg: ClientMessage, };
