import type { HttpRequest, MultipartEntry, RequestBody } from "../types";
import { emptyMultipartRow } from "../state/factories";
import { prettyJson } from "../lib/mime";
import { CodeEditor } from "./CodeEditor";
import { KeyValueTable } from "./KeyValueTable";

interface Props {
  request: HttpRequest;
  onChange: (request: HttpRequest) => void;
  onSubmit: () => void;
}

type BodyKind = RequestBody["type"];

const BODY_KINDS: readonly { kind: BodyKind; label: string }[] = [
  { kind: "none", label: "None" },
  { kind: "json", label: "JSON" },
  { kind: "text", label: "Text" },
  { kind: "urlEncoded", label: "URL Encoded" },
  { kind: "multipart", label: "Multipart" },
];

export function BodyEditor({ request, onChange, onSubmit }: Props) {
  const setBody = (body: RequestBody) => onChange({ ...request, body });

  const changeKind = (kind: BodyKind) => {
    if (kind === request.body.type) return;
    // Carry the text across when switching between the two text-ish bodies,
    // so picking the wrong one first is not destructive.
    const carried =
      request.body.type === "json" || request.body.type === "text" ? request.body.text : "";

    switch (kind) {
      case "none":
        return setBody({ type: "none" });
      case "json":
        return setBody({ type: "json", text: carried });
      case "text":
        return setBody({ type: "text", text: carried, contentType: null });
      case "urlEncoded":
        return setBody({ type: "urlEncoded", entries: [] });
      case "multipart":
        return setBody({ type: "multipart", entries: [] });
    }
  };

  const body = request.body;

  return (
    <div>
      <div className="ac-field">
        <label htmlFor="ac-body-kind">Body</label>
        <select
          id="ac-body-kind"
          value={body.type}
          onChange={(event) => changeKind(event.target.value as BodyKind)}
        >
          {BODY_KINDS.map(({ kind, label }) => (
            <option key={kind} value={kind}>
              {label}
            </option>
          ))}
        </select>

        {body.type === "json" ? (
          <button
            type="button"
            className="ac-button ac-button--quiet"
            disabled={prettyJson(body.text) === null}
            onClick={() => {
              const formatted = prettyJson(body.text);
              if (formatted !== null) setBody({ type: "json", text: formatted });
            }}
          >
            Format
          </button>
        ) : null}

        {body.type === "text" ? (
          <input
            type="text"
            aria-label="Content type"
            placeholder="text/plain"
            spellCheck={false}
            value={body.contentType ?? ""}
            onChange={(event) =>
              setBody({
                type: "text",
                text: body.text,
                contentType: event.target.value === "" ? null : event.target.value,
              })
            }
          />
        ) : null}
      </div>

      {body.type === "none" ? <p className="ac-hint">No request body.</p> : null}

      {body.type === "json" || body.type === "text" ? (
        <CodeEditor
          value={body.text}
          language={body.type === "json" ? "json" : "text"}
          ariaLabel="Request body"
          onSubmit={onSubmit}
          onChange={(text) =>
            setBody(
              body.type === "json"
                ? { type: "json", text }
                : { type: "text", text, contentType: body.contentType },
            )
          }
        />
      ) : null}

      {body.type === "urlEncoded" ? (
        <KeyValueTable
          label="Form fields"
          rows={body.entries}
          namePlaceholder="field"
          onChange={(entries) => setBody({ type: "urlEncoded", entries })}
        />
      ) : null}

      {body.type === "multipart" ? (
        <MultipartEditor
          entries={body.entries}
          onChange={(entries) => setBody({ type: "multipart", entries })}
        />
      ) : null}
    </div>
  );
}

interface MultipartProps {
  entries: readonly MultipartEntry[];
  onChange: (entries: MultipartEntry[]) => void;
}

/**
 * Multipart parts are either text or a file path. File contents are read by the
 * engine at send time, so a large upload never passes through the webview.
 */
function MultipartEditor({ entries, onChange }: MultipartProps) {
  const displayed =
    entries.length > 0 && isBlankPart(entries[entries.length - 1]!)
      ? entries
      : [...entries, emptyMultipartRow()];

  const update = (index: number, patch: Partial<MultipartEntry>) => {
    const next = displayed.map((entry, position) =>
      position === index ? { ...entry, ...patch } : entry,
    );
    while (next.length > 0 && isBlankPart(next[next.length - 1]!)) next.pop();
    onChange(next);
  };

  return (
    <table className="ac-kv">
      <caption className="ac-visually-hidden">Multipart fields</caption>
      <thead>
        <tr>
          <th className="ac-kv__check">
            <span className="ac-visually-hidden">Enabled</span>
          </th>
          <th>Name</th>
          <th>Kind</th>
          <th>Value</th>
          <th className="ac-kv__remove">
            <span className="ac-visually-hidden">Remove</span>
          </th>
        </tr>
      </thead>
      <tbody>
        {displayed.map((entry, index) => (
          <tr key={entry.id}>
            <td className="ac-kv__check">
              <input
                type="checkbox"
                aria-label={`Enable part ${index + 1}`}
                checked={entry.enabled}
                onChange={(event) => update(index, { enabled: event.target.checked })}
              />
            </td>
            <td>
              <input
                type="text"
                aria-label={`Part name ${index + 1}`}
                placeholder="field"
                spellCheck={false}
                value={entry.name}
                onChange={(event) => update(index, { name: event.target.value })}
              />
            </td>
            <td>
              <select
                aria-label={`Part kind ${index + 1}`}
                value={entry.value.kind}
                onChange={(event) =>
                  update(index, {
                    value:
                      event.target.value === "file"
                        ? { kind: "file", path: "", fileName: null, contentType: null }
                        : { kind: "text", value: "" },
                  })
                }
              >
                <option value="text">Text</option>
                <option value="file">File</option>
              </select>
            </td>
            <td>
              <input
                type="text"
                aria-label={`Part value ${index + 1}`}
                placeholder={entry.value.kind === "file" ? "/path/to/file" : "value"}
                spellCheck={false}
                value={entry.value.kind === "file" ? entry.value.path : entry.value.value}
                onChange={(event) =>
                  update(index, {
                    value:
                      entry.value.kind === "file"
                        ? { ...entry.value, path: event.target.value }
                        : { kind: "text", value: event.target.value },
                  })
                }
              />
            </td>
            <td className="ac-kv__remove">
              {isBlankPart(entry) ? null : (
                <button
                  type="button"
                  className="ac-icon-button"
                  aria-label={`Remove part ${index + 1}`}
                  onClick={() => onChange(displayed.filter((_, position) => position !== index))}
                >
                  ×
                </button>
              )}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function isBlankPart(entry: MultipartEntry): boolean {
  const value = entry.value.kind === "file" ? entry.value.path : entry.value.value;
  return entry.name === "" && value === "";
}
