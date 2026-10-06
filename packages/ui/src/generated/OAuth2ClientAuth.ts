// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.

/**
 * How the client identifies itself to the token endpoint. The specification
 * prefers the header and allows the body; servers differ on which they accept.
 */
export type OAuth2ClientAuth = "basicHeader" | "requestBody";
