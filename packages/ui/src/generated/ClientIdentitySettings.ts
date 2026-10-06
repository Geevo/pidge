// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.

/**
 * A client certificate and its private key.
 */
export type ClientIdentitySettings = { 
/**
 * A PEM file holding the certificate chain and key, or a PKCS#12
 * (`.p12`/`.pfx`) bundle. The format is detected from the contents.
 */
path: string, 
/**
 * Required for PKCS#12, which is always encrypted. An empty string is a
 * legitimate PKCS#12 password and is not the same as `null`.
 */
password: string | null, };
