import {
  useCallback,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent as ReactKeyboardEvent,
} from "react";
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
import { isSelectAll } from "../lib/shortcuts";
import { CodeEditor, isFoldable } from "./CodeEditor";
import { ResponseHeaders } from "./ResponseHeaders";

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
        <pre
          className={`ac-response-body${wrapLines ? " ac-response-body--wrap" : ""}`}
          tabIndex={0}
          aria-label="Response body"
          onKeyDown={selectAllWithin}
        >
          {rendered.text}
        </pre>
      </>
    );
  }

  // Formatted means it is JSON, so it is highlighted and folded as JSON.
  const asJson = formatted ? rendered.json : null;
  const text = asJson ?? rendered.text;
  const language = asJson === null ? rendered.language : "json";
  const foldable = isFoldable(language);

  return (
    <div className="ac-response-body__rich">
      {offerToFormat || foldable ? (
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
        </div>
      ) : null}

      <CodeEditor
        value={text}
        language={language}
        readOnly
        folding={foldable}
        wrap={wrapLines}
        ariaLabel="Response body"
        onReady={onReady}
      />
    </div>
  );
}

/**
 * Ctrl/Cmd+A over the plain-text body.
 *
 * The editor answers this key itself; a `pre` has no such thing, and left to
 * the browser the shortcut selects the entire window rather than the body under
 * the pointer. Answering it here keeps the selection to the text being read.
 */
function selectAllWithin(event: ReactKeyboardEvent<HTMLElement>) {
  if (!isSelectAll(event)) return;
  const selection = window.getSelection();
  if (!selection) return;

  event.preventDefault();
  const range = document.createRange();
  range.selectNodeContents(event.currentTarget);
  selection.removeAllRanges();
  selection.addRange(range);
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
