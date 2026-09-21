import { useEffect, useState } from "react";

import { toRequestError } from "../bridge";
import { errorTitle } from "../lib/errors";
import type { CodeTarget, HttpRequest, RequestError } from "../types";
import { CodeEditor, type EditorLanguage } from "./CodeEditor";
import { Select } from "./Select";

interface Target {
  readonly value: CodeTarget;
  /** What the picker lists. */
  readonly language: string;
  /** What the tabs under it offer, or `null` where there is only one way. */
  readonly library: string | null;
  /**
   * What to colour it as, which is not always the language's own name: a curl
   * command is a shell script, and Node.js is JavaScript.
   */
  readonly highlight: EditorLanguage;
}

/**
 * Every way a request can be written, grouped by language.
 *
 * The order is the picker's order, and the first of each language is what
 * choosing that language gives you.
 */
const TARGETS: readonly Target[] = [
  { value: "curl", language: "curl", library: null, highlight: "shell" },
  { value: "powershell", language: "PowerShell", library: null, highlight: "powershell" },
  { value: "python", language: "Python", library: null, highlight: "python" },
  { value: "csharp", language: "C#", library: null, highlight: "csharp" },
  { value: "rust-blocking", language: "Rust", library: "blocking", highlight: "rust" },
  { value: "rust-async", language: "Rust", library: "async", highlight: "rust" },
  { value: "node-fetch", language: "Node.js", library: "fetch", highlight: "javascript" },
  { value: "node-axios", language: "Node.js", library: "axios", highlight: "javascript" },
  { value: "go", language: "Go", library: null, highlight: "go" },
  { value: "java-httpclient", language: "Java", library: "HttpClient", highlight: "java" },
  { value: "java-okhttp", language: "Java", library: "OkHttp", highlight: "java" },
  { value: "php-curl", language: "PHP", library: "cURL", highlight: "php" },
  { value: "php-guzzle", language: "PHP", library: "Guzzle", highlight: "php" },
  { value: "zig", language: "Zig", library: null, highlight: "zig" },
];

const LANGUAGES = [...new Set(TARGETS.map((target) => target.language))].map((language) => ({
  value: language,
  label: language,
}));

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
  // The libraries for the language showing, which is what the tabs are.
  const libraries = TARGETS.filter((candidate) => candidate.language === entry.language);

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
          value={entry.language}
          options={LANGUAGES}
          onChange={(language) => {
            // The first of a language is what choosing it gives you.
            const first = TARGETS.find((candidate) => candidate.language === language);
            if (first) onTargetChange(first.value);
          }}
          label="Language"
          title="Which client to write this request for"
        />
        <span className="ac-spacer" />
        <CopyButton code={code} />
      </div>

      {/* Only where there is a choice: one library needs no tabs to pick it. */}
      {libraries.length > 1 ? (
        <div className="ac-subtabs ac-subtabs--libraries" role="tablist" aria-label="Library">
          {libraries.map((candidate) => (
            <button
              key={candidate.value}
              type="button"
              role="tab"
              className="ac-subtab"
              aria-selected={candidate.value === target}
              onClick={() => onTargetChange(candidate.value)}
            >
              {candidate.library}
            </button>
          ))}
        </div>
      ) : null}

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
          language={entry.highlight}
          readOnly
          wrap
          ariaLabel={`Request as ${entry.language}`}
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
