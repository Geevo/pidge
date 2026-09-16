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
    ["application/xml", "application/javascript", "application/x-www-form-urlencoded"].includes(
      base,
    )
  );
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
