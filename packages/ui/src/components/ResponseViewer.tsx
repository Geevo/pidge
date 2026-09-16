import { useMemo } from "react";

import type { HttpResponse, RequestError, ResponsePane, TabStatus } from "../types";
import { decodeBase64, decodeText, looksBinary } from "../lib/base64";
import { formatBytes } from "../lib/format";
import { isJsonMime, isTextMime, prettyJson } from "../lib/mime";
import { ResponseHeaders } from "./ResponseHeaders";
import { StatusSummary } from "./StatusSummary";

interface Props {
  status: TabStatus;
  pane: ResponsePane;
  wrapLines: boolean;
  onPaneChange: (pane: ResponsePane) => void;
}

export function ResponseViewer({ status, pane, wrapLines, onPaneChange }: Props) {
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

      <div className="ac-scroll" role="tabpanel">
        {status.state === "idle" ? (
          <p className="ac-empty">
            <span>Type a URL and press Send.</span>
            <span>Ctrl/Cmd+Enter works from anywhere.</span>
          </p>
        ) : null}

        {status.state === "sending" ? <p className="ac-empty">Sending…</p> : null}

        {status.state === "failed" ? <ErrorView error={status.error} /> : null}

        {status.state === "done" && pane === "body" ? (
          <ResponseBody response={status.response} wrapLines={wrapLines} />
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

function ResponseBody({ response, wrapLines }: { response: HttpResponse; wrapLines: boolean }) {
  const rendered = useMemo(() => renderBody(response), [response]);

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

  return (
    <pre className={`ac-response-body${wrapLines ? " ac-response-body--wrap" : ""}`}>
      {rendered.text}
    </pre>
  );
}

type RenderedBody = { kind: "text"; text: string } | { kind: "binary" } | { kind: "empty" };

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
    return { kind: "text", text: formatted ?? text };
  }
  return { kind: "text", text };
}

function titleFor(error: RequestError): string {
  const titles: Record<RequestError["kind"], string> = {
    invalidUrl: "Invalid URL",
    unsupportedScheme: "Unsupported scheme",
    unresolvedVariable: "Unresolved variable",
    invalidHeader: "Invalid header",
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
