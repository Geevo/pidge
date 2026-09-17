import { useCallback, useMemo, useRef } from "react";
import { foldAll, unfoldAll } from "@codemirror/language";
import type { EditorView } from "@codemirror/view";

import type { HttpResponse, RequestError, ResponsePane, TabStatus } from "../types";
import { decodeBase64, decodeText, looksBinary } from "../lib/base64";
import { formatBytes } from "../lib/format";
import { isJsonMime, isTextMime, prettyJson } from "../lib/mime";
import { CodeEditor } from "./CodeEditor";
import { ResponseHeaders } from "./ResponseHeaders";
import { StatusSummary } from "./StatusSummary";

/**
 * Past this, the body is shown as plain text. Highlighting and folding a
 * multi-megabyte document costs more than it is worth, and the fallback still
 * shows everything.
 */
const RICH_VIEW_LIMIT = 2 * 1024 * 1024;

interface Props {
  status: TabStatus;
  pane: ResponsePane;
  wrapLines: boolean;
  onPaneChange: (pane: ResponsePane) => void;
}

export function ResponseViewer({ status, pane, wrapLines, onPaneChange }: Props) {
  const rendered = useMemo(
    () => (status.state === "done" ? renderBody(status.response) : null),
    [status],
  );

  /*
   * CodeMirror only virtualizes when it has a bounded height. Inside a normal
   * `overflow: auto` container it grows to the full document — hundreds of
   * thousands of pixels for a large response — and lays out every line, which
   * is what made scrolling crawl. When it is on screen the panel becomes a flex
   * container with hidden overflow, and CodeMirror does the scrolling itself.
   */
  const editorOwnsScrolling =
    pane === "body" && rendered?.kind === "text" && rendered.text.length <= RICH_VIEW_LIMIT;

  return (
    <section className="ac-pane ac-pane--response" aria-label="Response">
      {status.state === "done" ? <StatusSummary response={status.response} /> : null}

      {status.state === "done" && status.response.warnings.length > 0 ? (
        <div className="ac-warnings" role="note">
          {status.response.warnings.map((warning) => (
            <div key={warning}>{warning}</div>
          ))}
        </div>
      ) : null}

      {status.state === "done" ? (
        <div className="ac-subtabs" role="tablist" aria-label="Response sections">
          <button
            type="button"
            role="tab"
            className="ac-subtab"
            aria-selected={pane === "body"}
            onClick={() => onPaneChange("body")}
          >
            Body
          </button>
          <button
            type="button"
            role="tab"
            className="ac-subtab"
            aria-selected={pane === "headers"}
            onClick={() => onPaneChange("headers")}
          >
            Headers
            <span className="ac-subtab__count">{status.response.headers.length}</span>
          </button>
        </div>
      ) : null}

      <div className={`ac-scroll${editorOwnsScrolling ? " ac-scroll--flush" : ""}`} role="tabpanel">
        {status.state === "idle" ? (
          <p className="ac-empty">
            <span>Type a URL and press Send.</span>
            <span>Ctrl/Cmd+Enter works from anywhere.</span>
          </p>
        ) : null}

        {status.state === "sending" ? <p className="ac-empty">Sending…</p> : null}

        {status.state === "failed" ? <ErrorView error={status.error} /> : null}

        {status.state === "done" && pane === "body" && rendered ? (
          <ResponseBody response={status.response} rendered={rendered} wrapLines={wrapLines} />
        ) : null}

        {status.state === "done" && pane === "headers" ? (
          <ResponseHeaders headers={status.response.headers} />
        ) : null}
      </div>
    </section>
  );
}

/**
 * Errors belong here, next to the request that caused them, rather than in a
 * toast that disappears before it can be read.
 */
function ErrorView({ error }: { error: RequestError }) {
  return (
    <div className="ac-error" role="alert">
      <p className="ac-error__title">{titleFor(error)}</p>
      <p className="ac-error__message">{error.message}</p>
      {error.detail ? (
        <details>
          <summary>Details</summary>
          <pre>{error.detail}</pre>
        </details>
      ) : null}
    </div>
  );
}

function ResponseBody({
  response,
  rendered,
  wrapLines,
}: {
  response: HttpResponse;
  rendered: RenderedBody;
  wrapLines: boolean;
}) {
  const view = useRef<EditorView | null>(null);
  const onReady = useCallback((editor: EditorView) => {
    view.current = editor;
  }, []);

  if (rendered.kind === "binary") {
    return (
      <p className="ac-empty">
        <span>Binary response — {formatBytes(response.sizeBytes)}</span>
        <span>{response.mimeType ?? "unknown content type"}</span>
      </p>
    );
  }

  if (rendered.kind === "empty") {
    return <p className="ac-empty">No response body.</p>;
  }

  // Big bodies skip the editor entirely rather than freezing on mount.
  if (rendered.text.length > RICH_VIEW_LIMIT) {
    return (
      <>
        <p className="ac-hint">
          {formatBytes(response.sizeBytes)} is too large to highlight; showing plain text.
        </p>
        <pre className={`ac-response-body${wrapLines ? " ac-response-body--wrap" : ""}`}>
          {rendered.text}
        </pre>
      </>
    );
  }

  return (
    <div className="ac-response-body__rich">
      {rendered.isJson ? (
        <div className="ac-response-tools">
          <button
            type="button"
            className="ac-button ac-button--quiet"
            onClick={() => view.current && foldAll(view.current)}
          >
            Collapse all
          </button>
          <button
            type="button"
            className="ac-button ac-button--quiet"
            onClick={() => view.current && unfoldAll(view.current)}
          >
            Expand all
          </button>
        </div>
      ) : null}

      <CodeEditor
        value={rendered.text}
        language={rendered.isJson ? "json" : "text"}
        readOnly
        folding={rendered.isJson}
        wrap={wrapLines}
        ariaLabel="Response body"
        onReady={onReady}
      />
    </div>
  );
}

type RenderedBody =
  { kind: "text"; text: string; isJson: boolean } | { kind: "binary" } | { kind: "empty" };

/**
 * Pretty-print valid JSON, show text as-is, and refuse to render bytes that
 * clearly are not text. Invalid JSON is shown verbatim rather than hidden
 * behind a parse error.
 */
export function renderBody(response: HttpResponse): RenderedBody {
  const bytes = decodeBase64(response.body);
  if (bytes.length === 0) return { kind: "empty" };

  const binaryByType = !isTextMime(response.mimeType) && response.mimeType !== null;
  if (binaryByType || looksBinary(bytes)) return { kind: "binary" };

  const text = decodeText(bytes);
  if (isJsonMime(response.mimeType)) {
    const formatted = prettyJson(text);
    // Invalid JSON is shown verbatim, and without the JSON language, so a
    // parse error does not turn into a wall of red.
    return { kind: "text", text: formatted ?? text, isJson: formatted !== null };
  }
  return { kind: "text", text, isJson: false };
}

function titleFor(error: RequestError): string {
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
