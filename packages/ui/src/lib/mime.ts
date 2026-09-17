/** The content type without its parameters, lowercased. */
export function baseMimeType(mimeType: string | null): string {
  return (mimeType ?? "").split(";")[0]!.trim().toLowerCase();
}

export function isJsonMime(mimeType: string | null): boolean {
  const base = baseMimeType(mimeType);
  return base === "application/json" || base.endsWith("+json");
}

export function isTextMime(mimeType: string | null): boolean {
  const base = baseMimeType(mimeType);
  if (base === "") return false;
  return (
    base.startsWith("text/") ||
    isJsonMime(mimeType) ||
    base.endsWith("+xml") ||
    base.endsWith("+yaml") ||
    [
      "application/xml",
      "application/javascript",
      "application/ecmascript",
      "application/x-javascript",
      "application/yaml",
      "application/x-yaml",
      "application/x-ndjson",
      "application/x-www-form-urlencoded",
    ].includes(base)
  );
}

/** What the editor can highlight. `text` is everything else. */
export type SyntaxLanguage = "json" | "html" | "xml" | "css" | "javascript" | "yaml" | "text";

/**
 * The language to highlight a body in, from its content type.
 *
 * The server's own word is the only evidence worth trusting here: sniffing the
 * bytes gets XML and HTML wrong in both directions, and highlighting a document
 * as the wrong language is worse than not highlighting it at all.
 */
export function syntaxForMime(mimeType: string | null): SyntaxLanguage {
  const base = baseMimeType(mimeType);
  if (isJsonMime(mimeType)) return "json";
  // Before the +xml test below: XHTML is both, and it reads as HTML.
  if (base === "text/html" || base === "application/xhtml+xml") return "html";
  if (base === "application/xml" || base === "text/xml" || base.endsWith("+xml")) return "xml";
  if (base === "text/css") return "css";
  if (
    base === "application/javascript" ||
    base === "text/javascript" ||
    base === "application/x-javascript" ||
    base === "application/ecmascript"
  ) {
    return "javascript";
  }
  if (
    base === "application/yaml" ||
    base === "text/yaml" ||
    base === "application/x-yaml" ||
    base === "text/x-yaml" ||
    base.endsWith("+yaml")
  ) {
    return "yaml";
  }
  return "text";
}

/**
 * Pretty-prints JSON, or returns null when the body is not valid JSON.
 * Invalid JSON is shown verbatim rather than swallowed.
 */
export function prettyJson(text: string): string | null {
  const trimmed = text.trim();
  if (trimmed === "") return null;
  if (!'{["-0123456789tfn'.includes(trimmed[0]!)) return null;

  try {
    return JSON.stringify(JSON.parse(trimmed), null, 2);
  } catch {
    return null;
  }
}
