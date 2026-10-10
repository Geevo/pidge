import { useState } from "react";

import { importCurl } from "../lib/curlImport";
import type { HttpRequest } from "../types";
import { CodeEditor } from "./CodeEditor";

interface Props {
  request: HttpRequest;
  onChange: (request: HttpRequest) => void;
  onSubmit: () => void;
}

/**
 * A curl command pasted in and read into the other panes: the way back from
 * the Code pane's curl.
 *
 * The command is applied as it changes, so a paste fills in the request at
 * once and a fix to a half-pasted command takes effect without a button. Text
 * that is not a command yet leaves the request alone and says why.
 *
 * The text is not kept once the pane is left. It is a way into the request
 * rather than a second copy of it, and one kept would go stale the moment the
 * Headers pane was edited.
 */
export function CurlImportView({ request, onChange, onSubmit }: Props) {
  const [text, setText] = useState("");
  const [status, setStatus] = useState<
    | { kind: "idle" }
    | { kind: "imported"; notes: readonly string[] }
    | { kind: "failed"; message: string }
  >({ kind: "idle" });

  const changed = (next: string) => {
    setText(next);
    if (next.trim() === "") {
      setStatus({ kind: "idle" });
      return;
    }

    const result = importCurl(next);
    if (!result.ok) {
      setStatus({ kind: "failed", message: result.message });
      return;
    }

    // The tab's request keeps its id: sends and cancels are matched on it.
    onChange({ ...result.request, id: request.id });
    setStatus({ kind: "imported", notes: result.notes });
  };

  return (
    <div className="ac-codegen ac-curl-import">
      <div className="ac-response-tools">
        <span className="ac-curl-import__status" role="status">
          {status.kind === "idle"
            ? "Paste a curl command to fill in the URL, params, body, headers and auth."
            : status.kind === "failed"
              ? status.message
              : "Imported into Params, Body, Headers and Auth."}
        </span>
        <span className="ac-spacer" />
        {text !== "" ? (
          <button type="button" className="ac-button ac-button--quiet" onClick={() => changed("")}>
            Clear
          </button>
        ) : null}
      </div>

      {status.kind === "imported" && status.notes.length > 0 ? (
        <ul className="ac-curl-import__notes">
          {status.notes.map((note) => (
            <li key={note}>{note}</li>
          ))}
        </ul>
      ) : null}

      <CodeEditor
        value={text}
        language="shell"
        wrap
        ariaLabel="curl command"
        onChange={changed}
        onSubmit={onSubmit}
      />
    </div>
  );
}
