import { forwardRef } from "react";

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
 */
export const UrlBar = forwardRef<HTMLInputElement, Props>(function UrlBar(
  { method, url, sending, onMethodChange, onUrlChange, onSend, onCancel },
  ref,
) {
  return (
    <div className="ac-urlbar">
      <MethodSelector value={method} onChange={onMethodChange} />
      <input
        ref={ref}
        className="ac-url-input"
        type="text"
        aria-label="URL"
        placeholder="localhost:3000/api/test"
        spellCheck={false}
        autoComplete="off"
        autoCorrect="off"
        autoCapitalize="off"
        value={url}
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
