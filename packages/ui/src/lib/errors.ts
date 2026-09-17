import type { RequestError } from "../types";

/** The short name for a failure, used wherever one has to be announced. */
export function errorTitle(error: RequestError): string {
  const titles: Record<RequestError["kind"], string> = {
    invalidUrl: "Invalid URL",
    unsupportedScheme: "Unsupported scheme",
    unresolvedVariable: "Unresolved variable",
    invalidHeader: "Invalid header",
    auth: "Authentication failed",
    dns: "DNS lookup failed",
    connectionRefused: "Connection refused",
    connectionFailed: "Connection failed",
    tls: "TLS error",
    timeout: "Timed out",
    cancelled: "Cancelled",
    tooManyRedirects: "Too many redirects",
    redirect: "Redirect error",
    bodySerialization: "Could not build request body",
    bodyRead: "Could not read response body",
    responseTooLarge: "Response too large",
    io: "I/O error",
    other: "Request failed",
  };
  return titles[error.kind];
}
