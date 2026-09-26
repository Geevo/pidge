import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { foldAll, unfoldAll } from "@codemirror/language";
import type { EditorView } from "@codemirror/view";

import type { HttpResponse, RequestError, ResponsePane, TabStatus } from "../types";
import { decodeBase64, decodeText, looksBinary } from "../lib/base64";
import { errorTitle } from "../lib/errors";
import { formatBytes } from "../lib/format";
import {
  isJsonMime,
  isTextMime,
  prettyJson,
  syntaxForMime,
  type SyntaxLanguage,
} from "../lib/mime";
import { isFind } from "../lib/shortcuts";
import { CodeEditor, isFoldable } from "./CodeEditor";
import { openFind } from "./findPanel";
import { ResponseHeaders } from "./ResponseHeaders";

/**
 * Every body is shown in the editor, whatever its size. There used to be a
 * plain-text fallback past 2 MB, on the belief that the editor could not cope;
 * measured in WebKitGTK it was the other way round. The editor only draws the
 * lines on screen, and put 50 MB of JSON up in 1.4 s where the fallback took
 * over a minute.
 *
 * Its one slow case is wrapping a single enormous line, which it has to lay
 * out whole: a 20 MB line took 25 s wrapped and under a second unwrapped. So a
 * line longer than this is left unwrapped, whatever the setting says.
 */
const WRAP_LINE_LIMIT = 256 * 1024;

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
  const editorOwnsScrolling = pane === "body" && rendered?.kind === "text";

  return (
    <section className="ac-pane ac-pane--response" aria-label="Response">
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
      <p className="ac-error__title">{errorTitle(error)}</p>
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

  /*
   * Ctrl/Cmd+F from anywhere outside an editor finds in the body: from the URL
   * bar, a header, or nowhere in particular. An editor with focus has already
   * taken the key for itself by the time it arrives here, and an open dialog
   * is not the body's to search under.
   */
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (!isFind(event) || event.defaultPrevented || !view.current) return;
      if (event.target instanceof Element && event.target.closest(".cm-editor")) return;
      if (document.querySelector(".ac-dialog")) return;
      event.preventDefault();
      openFind(view.current);
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, []);

  /*
   * A body the server called JSON is laid out on arrival, as it always has
   * been, and has nothing to ask about.
   *
   * The checkbox is for the other case: plenty of APIs answer `text/plain` with
   * a single line of JSON, and that line is unreadable until someone asks for
   * it to be broken up. Asking is what makes it safe — the content type did not
   * say JSON, so the app does not decide that it is; a successful parse only
   * means the offer can be made.
   */
  const sentAsJson = rendered.kind === "text" && rendered.language === "json";
  const canLayOut = rendered.kind === "text" && rendered.json !== null;
  const offerToFormat = canLayOut && !sentAsJson;
  const startsFormatted = sentAsJson;
  const [formatted, setFormatted] = useState(startsFormatted);

  /*
   * A new response is a new question, so the last one's choice does not carry.
   * Adjusted during the render that noticed rather than in an effect
   * afterwards, which is the pattern React asks for and avoids painting the
   * previous body's state for a frame.
   */
  const [shown, setShown] = useState(rendered);
  if (shown !== rendered) {
    setShown(rendered);
    setFormatted(startsFormatted);
  }

  const longestLine = useMemo(
    () =>
      rendered.kind === "text"
        ? longestLineOf(formatted && rendered.json !== null ? rendered.json : rendered.text)
        : 0,
    [rendered, formatted],
  );

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

  // Formatted means it is JSON, so it is highlighted and folded as JSON.
  const asJson = formatted ? rendered.json : null;
  const text = asJson ?? rendered.text;
  const language = asJson === null ? rendered.language : "json";
  const foldable = isFoldable(language);
  const tooLongToWrap = wrapLines && longestLine > WRAP_LINE_LIMIT;

  return (
    <div className="ac-response-body__rich">
      {offerToFormat || foldable || tooLongToWrap ? (
        <div className="ac-response-tools">
          {offerToFormat ? (
            <label
              className="ac-tool-check"
              title="This body is not JSON by its content type, but it parses as JSON."
            >
              <input
                type="checkbox"
                checked={formatted}
                onChange={(event) => setFormatted(event.target.checked)}
              />
              <span>Pretty print</span>
            </label>
          ) : null}

          {foldable ? (
            <>
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
            </>
          ) : null}

          {tooLongToWrap ? (
            <span className="ac-response-tools__note">Not wrapped: one line is too long.</span>
          ) : null}
        </div>
      ) : null}

      <CodeEditor
        value={text}
        language={language}
        readOnly
        folding={foldable}
        wrap={wrapLines && !tooLongToWrap}
        ariaLabel="Response body"
        onReady={onReady}
      />
    </div>
  );
}

/** The length of the longest line, without splitting a large body into lines. */
function longestLineOf(text: string): number {
  let longest = 0;
  let start = 0;
  for (let end = text.indexOf("\n"); end !== -1; end = text.indexOf("\n", start)) {
    longest = Math.max(longest, end - start);
    start = end + 1;
  }
  return Math.max(longest, text.length - start);
}

type RenderedBody =
  /** `json` holds the formatted form when the body parses, whatever its type. */
  | { kind: "text"; text: string; language: SyntaxLanguage; json: string | null }
  | { kind: "binary" }
  | { kind: "empty" };

/**
 * Decode the body, name its language, and work out its formatted form if it is
 * JSON. Bytes that clearly are not text are refused.
 *
 * Only JSON is ever reformatted: whitespace carries meaning in HTML and YAML,
 * so re-indenting those would change the document you asked to see. The
 * formatted form is computed whatever the content type says, so a `text/plain`
 * body that happens to be JSON can be formatted on request.
 */
export function renderBody(response: HttpResponse): RenderedBody {
  const bytes = decodeBase64(response.body);
  if (bytes.length === 0) return { kind: "empty" };

  const binaryByType = !isTextMime(response.mimeType) && response.mimeType !== null;
  if (binaryByType || looksBinary(bytes)) return { kind: "binary" };

  const text = decodeText(bytes);
  const json = prettyJson(text);
  if (isJsonMime(response.mimeType)) {
    // Invalid JSON is shown verbatim, and without the JSON language, so a
    // parse error does not turn into a wall of red.
    return { kind: "text", text, language: json === null ? "text" : "json", json };
  }
  return { kind: "text", text, language: syntaxForMime(response.mimeType), json };
}
