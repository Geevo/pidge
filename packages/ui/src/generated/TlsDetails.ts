// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.
import type { PeerCertificate } from "./PeerCertificate";

/**
 * What the connection turned out to be, reported back with the response.
 *
 * Absent for plain HTTP. The certificate is the one the server presented —
 * the leaf, not the chain above it, which is all the TLS layer hands back.
 */
export type TlsDetails = { 
/**
 * e.g. "TLS 1.3". `null` when the backend will not say.
 */
protocol: string | null, 
/**
 * `null` when the certificate could not be parsed, which is not a reason
 * to fail a response the TLS layer already accepted.
 */
certificate: PeerCertificate | null, };
