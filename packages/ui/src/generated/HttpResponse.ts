// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.
import type { KeyValueEntry } from "./KeyValueEntry";
import type { TlsDetails } from "./TlsDetails";

/**
 * The result of one successful round trip. "Successful" means we got a
 * response, not that the status code was 2xx.
 */
export type HttpResponse = { status: number, statusText: string, headers: Array<KeyValueEntry>, 
/**
 * Raw bytes, base64 encoded on the wire.
 */
body: string, mimeType: string | null, durationMs: number, sizeBytes: number, 
/**
 * True when the body hit the configured size limit and was cut short.
 */
truncated: boolean, 
/**
 * The URL actually reached, after any redirects.
 */
finalUrl: string, 
/**
 * Non-fatal notes for the user, e.g. an auth helper that was overridden.
 */
warnings: Array<string>, 
/**
 * The connection's TLS, for the padlock. `null` for plain HTTP.
 *
 * Boxed because it is several hundred bytes of certificate that most
 * responses do not carry, and `HttpResponse` travels inside the sidecar's
 * message enum, which is as large as its largest variant.
 */
tls: TlsDetails | null, };
