// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.

/**
 * The server's certificate, in the terms a person checking it would use.
 */
export type PeerCertificate = { subject: string, issuer: string, 
/**
 * The names this certificate is actually valid for, which is what matters
 * rather than the common name.
 */
subjectAltNames: Array<string>, 
/**
 * RFC 3339, so the UI can format them in the local timezone.
 */
notBefore: string, notAfter: string, serial: string, signatureAlgorithm: string, 
/**
 * Uppercase hex, colon separated, as every other tool prints it.
 */
sha256Fingerprint: string, 
/**
 * Against the clock when the response arrived. Only reachable with
 * verification turned off, but then it is the thing worth knowing.
 */
expired: boolean, 
/**
 * Subject equals issuer: nothing above it vouched for it.
 */
selfSigned: boolean, };
