// Mirrors the .NET type of the same name. TypeScriptMirrorTests holds the two together.

/**
 * Normalized failure categories. The UI switches on these; `detail` carries
 * the technical chain for diagnostics without dumping it into the main view.
 */
export type RequestErrorKind = "invalidUrl" | "unsupportedScheme" | "unresolvedVariable" | "invalidHeader" | "dns" | "connectionRefused" | "connectionFailed" | "tls" | "timeout" | "cancelled" | "tooManyRedirects" | "redirect" | "auth" | "bodySerialization" | "bodyRead" | "responseTooLarge" | "io" | "other";
