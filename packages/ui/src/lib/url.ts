import type { KeyValueEntry } from "../types";
import { newId } from "./ids";

/**
 * Keeping the URL and the params table in sync, without either one destroying
 * what the user typed in the other.
 *
 * The rules are deliberately conservative: editing the URL rewrites the table,
 * editing the table rewrites only the query string, and a URL that will not
 * parse is left completely alone.
 */

export interface UrlSyncResult {
  readonly url: string;
  readonly queryParams: readonly KeyValueEntry[];
}

/** Splits `path?a=1&b=2` into its base and its pairs. */
export function parseQueryParams(url: string): KeyValueEntry[] {
  const index = url.indexOf("?");
  if (index === -1) return [];

  const query = url.slice(index + 1).split("#")[0]!;
  if (query === "") return [];

  return query.split("&").map((pair) => {
    const separator = pair.indexOf("=");
    const [rawName, rawValue] =
      separator === -1 ? [pair, ""] : [pair.slice(0, separator), pair.slice(separator + 1)];
    return {
      id: newId(),
      enabled: true,
      name: safeDecode(rawName),
      value: safeDecode(rawValue),
    };
  });
}

/**
 * Called when the user edits the URL. Disabled rows are preserved, because
 * they are not in the URL and the user did not ask to lose them.
 */
export function urlChanged(url: string, existing: readonly KeyValueEntry[]): UrlSyncResult {
  const fromUrl = parseQueryParams(url);
  const disabled = existing.filter((entry) => !entry.enabled);

  // Reuse the original row ids where the name still matches, so React does not
  // remount the inputs and steal focus while the user is typing.
  const reusable = existing.filter((entry) => entry.enabled);
  const merged = fromUrl.map((entry, index) => {
    const previous = reusable[index];
    return previous && previous.name === entry.name ? { ...entry, id: previous.id } : entry;
  });

  return { url, queryParams: [...merged, ...disabled] };
}

/**
 * Called when the user edits the params table. Rewrites only the query string,
 * leaving scheme, host, path, and fragment exactly as typed.
 */
export function paramsChanged(url: string, params: readonly KeyValueEntry[]): UrlSyncResult {
  const hashIndex = url.indexOf("#");
  const fragment = hashIndex === -1 ? "" : url.slice(hashIndex);
  const withoutFragment = hashIndex === -1 ? url : url.slice(0, hashIndex);
  const base = withoutFragment.split("?")[0]!;

  const query = params
    .filter((entry) => entry.enabled && entry.name.trim() !== "")
    .map((entry) => `${encodeURIComponent(entry.name.trim())}=${encodeURIComponent(entry.value)}`)
    .join("&");

  return {
    url: query === "" ? `${base}${fragment}` : `${base}?${query}${fragment}`,
    queryParams: params,
  };
}

/** A malformed percent-escape should show as typed, not throw. */
function safeDecode(value: string): string {
  try {
    return decodeURIComponent(value.replace(/\+/g, " "));
  } catch {
    return value;
  }
}
