import { forwardRef, useEffect, useRef } from "react";

import type { HttpMethod } from "../types";
import { matchEditingCommand } from "../lib/shortcuts";
import { createHistory, record, redo, undo, type History, type Snapshot } from "../lib/textHistory";
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
 * It keeps its own undo history too, and handles the keys itself. WebKitGTK
 * never binds Ctrl+Z, and where the keys are bound the browser's history is one
 * lump — a whole run of typing undoes in a single step, emptying the field
 * instead of taking back the last thing you wrote.
 *
 * The effect below carries changes that did not come from typing: switching
 * tabs, the params table rewriting the query, a session restored at startup.
 * Those replace what is in the field, so the history starts again rather than
 * letting undo walk back into another request's URL.
 */
export const UrlBar = forwardRef<HTMLInputElement, Props>(function UrlBar(
  { method, url, sending, onMethodChange, onUrlChange, onSend, onCancel },
  ref,
) {
  const input = useRef<HTMLInputElement | null>(null);
  // Only ever read on the first render; after that the effect keeps it in step.
  const initialUrl = useRef(url);
  const history = useRef<History>(createHistory({ value: url, caret: url.length }));

  useEffect(() => {
    const node = input.current;
    if (!node || node.value === url) return;
    node.value = url;
    history.current = createHistory({ value: url, caret: url.length });
  }, [url]);

  const apply = (step: { history: History; snapshot: Snapshot } | null) => {
    const node = input.current;
    if (!step || !node) return;

    history.current = step.history;
    node.value = step.snapshot.value;
    node.setSelectionRange(step.snapshot.caret, step.snapshot.caret);
    onUrlChange(step.snapshot.value);
  };

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
        onChange={(event) => {
          history.current = record(
            history.current,
            { value: event.target.value, caret: event.target.selectionStart ?? 0 },
            Date.now(),
          );
          onUrlChange(event.target.value);
        }}
        onKeyDown={(event) => {
          if (event.key === "Enter" && !event.shiftKey) {
            event.preventDefault();
            if (!sending) onSend();
            return;
          }

          const editing = matchEditingCommand(event);
          if (editing) {
            event.preventDefault();
            apply(editing === "undo" ? undo(history.current) : redo(history.current));
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
