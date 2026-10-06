import type { HttpRequest, KeyValueEntry, MultipartEntry, ScratchTab } from "../types";
import { newId } from "../lib/ids";

/** The state the app opens into. Mirrors `HttpRequest.Blank` in the host. */
export function blankRequest(): HttpRequest {
  return {
    id: newId(),
    method: "GET",
    url: "",
    queryParams: [],
    headers: [],
    auth: { type: "none" },
    body: { type: "none" },
    timeoutMs: null,
    encodeQuery: true,
  };
}

export function blankTab(): ScratchTab {
  return {
    id: newId(),
    name: null,
    request: blankRequest(),
    savedRequestId: null,
    dirty: false,
    // Unset means "start from the settings default".
    splitPercent: null,
  };
}

export function emptyRow(): KeyValueEntry {
  return { id: newId(), enabled: true, name: "", value: "" };
}

export function emptyMultipartRow(): MultipartEntry {
  return {
    id: newId(),
    enabled: true,
    name: "",
    value: { kind: "text", value: "" },
  };
}

/**
 * A copy of a request for a new tab. Reusing an id would make two tabs cancel
 * each other, so everything that identifies a row is regenerated.
 */
export function cloneRequest(request: HttpRequest): HttpRequest {
  const cloneRows = (rows: readonly KeyValueEntry[]): KeyValueEntry[] =>
    rows.map((row) => ({ ...row, id: newId() }));

  return {
    ...request,
    id: newId(),
    queryParams: cloneRows(request.queryParams),
    headers: cloneRows(request.headers),
    body:
      request.body.type === "urlEncoded"
        ? { type: "urlEncoded", entries: cloneRows(request.body.entries) }
        : request.body.type === "multipart"
          ? {
              type: "multipart",
              entries: request.body.entries.map((entry) => ({ ...entry, id: newId() })),
            }
          : request.body,
  };
}

/** True when a tab has nothing the user would miss. Mirrors `HttpRequest::is_untouched`. */
export function isUntouched(request: HttpRequest): boolean {
  const activeRows = (rows: readonly { enabled: boolean; name: string }[]) =>
    rows.some((row) => row.enabled && row.name.trim() !== "");

  const bodyIsEmpty =
    request.body.type === "none" ||
    ((request.body.type === "json" || request.body.type === "text") &&
      request.body.text.trim() === "") ||
    ((request.body.type === "urlEncoded" || request.body.type === "multipart") &&
      !activeRows(request.body.entries));

  return (
    request.method === "GET" &&
    request.url.trim() === "" &&
    !activeRows(request.queryParams) &&
    !activeRows(request.headers) &&
    request.auth.type === "none" &&
    bodyIsEmpty
  );
}
