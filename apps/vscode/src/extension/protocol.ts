/**
 * The two message vocabularies the extension host sits between.
 *
 * Down to the sidecar: the generated protocol types, so the Rust definitions
 * stay the single source of truth. Up to the webview: a tiny request/response
 * envelope, because a webview cannot be trusted to correlate anything itself.
 */
import type { ClientEnvelope, ServerEnvelope } from "@api-client/ui";

export type { ClientEnvelope, ClientMessage, ServerEnvelope, ServerMessage } from "@api-client/ui";

/** Must match `api_client_protocol::PROTOCOL_VERSION`. */
export const PROTOCOL_VERSION = 1;

export function encodeLine(envelope: ClientEnvelope): string {
  return `${JSON.stringify(envelope)}\n`;
}

export function decodeLine(line: string): ServerEnvelope {
  return JSON.parse(line) as ServerEnvelope;
}

/** webview -> extension host */
export interface WebviewRequest {
  readonly kind: "request";
  readonly id: string;
  readonly method: string;
  readonly params: unknown;
}

/** extension host -> webview */
export type WebviewResponse =
  | { readonly kind: "response"; readonly id: string; readonly ok: true; readonly result: unknown }
  | { readonly kind: "response"; readonly id: string; readonly ok: false; readonly error: string };

/** extension host -> webview, unprompted */
export interface WebviewEvent {
  readonly kind: "event";
  readonly event: string;
  readonly payload: unknown;
}

export function isWebviewRequest(value: unknown): value is WebviewRequest {
  return (
    typeof value === "object" &&
    value !== null &&
    (value as WebviewRequest).kind === "request" &&
    typeof (value as WebviewRequest).id === "string" &&
    typeof (value as WebviewRequest).method === "string"
  );
}
