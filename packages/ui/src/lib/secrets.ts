/**
 * Which values the UI masks until asked.
 *
 * Masking is about the screen — a demo, a screen share, somebody behind you.
 * It is not what keeps a secret safe on disk: the host encrypts those whatever
 * they look like here.
 */

/** Mirrors `SecretHeaders` in `src/Pidge.Core/Redact.cs`; a test holds them together. */
export const SECRET_HEADERS: readonly string[] = [
  "authorization",
  "proxy-authorization",
  "cookie",
  "set-cookie",
  "x-api-key",
  "api-key",
  "x-auth-token",
  "x-amz-security-token",
  "x-csrf-token",
];

export function isSecretHeader(name: string): boolean {
  return SECRET_HEADERS.includes(name.trim().toLowerCase());
}

/**
 * A variable whose name says it holds a secret: `token`, `apiKey`,
 * `DB_PASSWORD`. A guess, but only about what to cover up; a base URL stays
 * readable so the environment is still easy to check at a glance.
 */
export function looksSecret(name: string): boolean {
  return /pass|pwd|secret|token|key|auth|credential|cookie|session|private|signature/i.test(name);
}
