import { useEffect, useState } from "react";

import { toRequestError } from "../bridge";
import { errorTitle } from "../lib/errors";
import type { CodeTarget, HttpRequest, RequestError } from "../types";
import { CodeEditor, type EditorLanguage } from "./CodeEditor";
import { Select } from "./Select";

/**
 * `language` is what to colour the snippet as, which is not always the target's
 * own name: a curl command is a shell script.
 */
const TARGETS: { value: CodeTarget; label: string; language: EditorLanguage }[] = [
  { value: "curl", label: "curl", language: "shell" },
  { value: "powershell", label: "PowerShell", language: "powershell" },
  { value: "python", label: "Python", language: "python" },
  { value: "csharp", label: "C#", language: "csharp" },
  { value: "node", label: "Node.js", language: "javascript" },
];

/**
 * Long enough that typing a URL does not send a generate per keystroke, short
 * enough that the code is there by the time the eye reaches it.
 */
const SETTLE_MS = 120;

interface Props {
  request: HttpRequest;
  target: CodeTarget;
  onTargetChange: (target: CodeTarget) => void;
  /**
   * The host writes the code, not this component: only the host knows the
   * environment the variables come from and the settings the snippet has to
   * carry. Its identity changes when either does, which is what regenerates.
   */
  generate: (request: HttpRequest, target: CodeTarget) => Promise<string>;
}

/**
 * The request as code for somebody else's client.
 *
 * It is a view of the request rather than another way to edit it: nothing here
 * changes what will be sent, and the snippet is regenerated from the request
 * rather than kept in step by hand.
 */
export function CodeView({ request, target, onTargetChange, generate }: Props) {
  const [code, setCode] = useState<string | null>(null);
  const [error, setError] = useState<RequestError | null>(null);
  const entry = TARGETS.find((candidate) => candidate.value === target) ?? TARGETS[0]!;

  useEffect(() => {
    let live = true;
    const timer = setTimeout(() => {
      generate(request, target).then(
        (generated) => {
          if (!live) return;
          setCode(generated);
          setError(null);
        },
        (failure: unknown) => {
          if (!live) return;
          setCode(null);
          setError(toRequestError(failure));
        },
      );
    }, SETTLE_MS);

    return () => {
      live = false;
      clearTimeout(timer);
    };
  }, [generate, request, target]);

  return (
    <div className="ac-codegen">
      <div className="ac-response-tools">
        <Select
          value={target}
          options={TARGETS}
          onChange={(value) => onTargetChange(value as CodeTarget)}
          label="Language"
          title="Which client to write this request for"
        />
        <span className="ac-spacer" />
        <CopyButton code={code} />
      </div>

      {error ? (
        <div className="ac-error" role="alert">
          <p className="ac-error__title">{errorTitle(error)}</p>
          <p className="ac-error__message">{error.message}</p>
        </div>
      ) : (
        /*
         * Read-only, wrapped, and with no folding: a curl command is one long
         * line by construction, and a snippet that has to be scrolled sideways
         * to be read is one nobody checks before pasting.
         */
        <CodeEditor
          value={code ?? ""}
          language={entry.language}
          readOnly
          wrap
          ariaLabel={`Request as ${entry.label}`}
        />
      )}
    </div>
  );
}

/**
 * Copying, and saying that it happened.
 *
 * `navigator.clipboard` needs a secure context and a user gesture, and in a
 * WebKit webview it can be missing outright; `execCommand` is deprecated and
 * works everywhere, which is the same reason undo and redo are wired up by
 * hand. The button reports what actually happened either way.
 */
function CopyButton({ code }: { code: string | null }) {
  const [state, setState] = useState<"idle" | "copied" | "failed">("idle");

  useEffect(() => {
    if (state === "idle") return;
    const timer = setTimeout(() => setState("idle"), 1200);
    return () => clearTimeout(timer);
  }, [state]);

  const copy = async () => {
    if (code === null) return;
    setState((await writeToClipboard(code)) ? "copied" : "failed");
  };

  return (
    <button
      type="button"
      className="ac-button ac-button--quiet"
      disabled={code === null}
      onClick={() => void copy()}
    >
      {state === "copied" ? "Copied" : state === "failed" ? "Press Ctrl+C" : "Copy"}
    </button>
  );
}

async function writeToClipboard(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return copyBySelection(text);
  }
}

/** The fallback: a hidden field, selected, and the browser's own copy command. */
function copyBySelection(text: string): boolean {
  const field = document.createElement("textarea");
  field.value = text;
  field.setAttribute("readonly", "");
  // Off screen rather than hidden: an element with no box cannot be selected.
  field.style.position = "fixed";
  field.style.top = "-1000px";
  document.body.appendChild(field);

  try {
    field.select();
    return document.execCommand("copy");
  } catch {
    return false;
  } finally {
    field.remove();
  }
}
