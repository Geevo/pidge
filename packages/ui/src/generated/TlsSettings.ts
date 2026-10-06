// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.
import type { ClientIdentitySettings } from "./ClientIdentitySettings";

/**
 * How the client establishes trust and identifies itself.
 *
 * The defaults are what a developer expects without configuring anything: the
 * operating system's own trust store, no client certificate, and certificate
 * checking on.
 */
export type TlsSettings = { 
/**
 * Trust the OS store: Windows CryptoAPI, the macOS keychain, or the
 * system CA bundle on Linux. Turning this off trusts only `extra_ca_files`.
 */
useSystemRoots: boolean, 
/**
 * Extra CAs to trust, as paths to PEM bundles or single DER certificates.
 * These are added to the system roots rather than replacing them.
 */
extraCaFiles: Array<string>, 
/**
 * A certificate to present when a server asks for one (mutual TLS).
 */
clientIdentity: ClientIdentitySettings | null, 
/**
 * Skips certificate verification entirely. For a development server with
 * a self-signed certificate; adding its CA above is the better answer.
 */
acceptInvalidCerts: boolean, };
