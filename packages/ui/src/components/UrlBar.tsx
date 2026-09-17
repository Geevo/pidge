import { forwardRef, useEffect, useRef } from "react";

import type { HttpMethod } from "../types";
import { MethodSelector } from "./MethodSelector";

interface Props {
  method: HttpMethod;
  url: string;
  sending: boolean;
  onMethodChange: (method: HttpMethod) => void;
  onUrlChange: (url: string) => void;
  onSend: () => void;
  onCancel: () => void;
}

/**
 * The main control of the app. Everything else is secondary to typing here and
 * pressing Send.
 *
 * The field holds its own text rather than being driven by the `url` prop on
 * every keystroke. A controlled input has its `defaultValue` written by React
 * on each commit, which sets the value attribute, and setting that attribute
 * throws away the browser's undo history.
 *
 * Undo and redo are handled for every field at once, in `lib/fieldHistory`.
 *
 * The effect below carries changes that did not come from typing: switching
 * tabs, the params table rewriting the query, a session restored at startup.
 */
export const UrlBar = forwardRef<HTMLInputElement, Props>(function UrlBar(
  { method, url, sending, onMethodChange, onUrlChange, onSend, onCancel },
  ref,
) {
  const input = useRef<HTMLInputElement | null>(null);
  // Only ever read on the first render; after that the effect keeps it in step.
  const initialUrl = useRef(url);

  useEffect(() => {
    const node = input.current;
    if (node && node.value !== url) node.value = url;
  }, [url]);

  return (
    <div className="ac-urlbar">
      <MethodSelector value={method} onChange={onMethodChange} />
      <input
        ref={(node) => {
          input.current = node;
          if (typeof ref === "function") ref(node);
          else if (ref) ref.current = node;
        }}
        className="ac-url-input"
        type="text"
        aria-label="URL"
        placeholder="localhost:3000/api/test"
        spellCheck={false}
        autoComplete="off"
        autoCorrect="off"
        autoCapitalize="off"
        defaultValue={initialUrl.current}
        onChange={(event) => onUrlChange(event.target.value)}
        onKeyDown={(event) => {
          if (event.key === "Enter" && !event.shiftKey) {
            event.preventDefault();
            if (!sending) onSend();
          }
        }}
      />
      {sending ? (
        <button type="button" className="ac-button" onClick={onCancel}>
          Cancel
        </button>
      ) : (
        <button type="button" className="ac-button ac-button--primary" onClick={onSend}>
          Send
        </button>
      )}
    </div>
  );
});
