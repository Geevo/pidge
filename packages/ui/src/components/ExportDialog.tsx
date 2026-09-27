import { useEffect, useRef, useState } from "react";

import type { ExportInput } from "../bridge";
import type { ExportFormat, SavedRequest } from "../types";

interface Props {
  savedRequests: readonly SavedRequest[];
  /** Ticked to begin with: whatever the drawer's search is showing. */
  initiallySelected: readonly string[];
  onExport: (input: ExportInput) => void;
  onClose: () => void;
}

const FORMATS: { value: ExportFormat; label: string; detail: string }[] = [
  {
    value: "json",
    label: "JSON",
    detail: "Everything, exactly as saved. Best for keeping or moving between machines.",
  },
  {
    value: "http",
    label: ".http",
    detail:
      "Readable, and runs in VS Code REST Client and JetBrains. Digest, NTLM and OAuth are left out.",
  },
];

/** Which saved requests, in which form, and whether their secrets go too. */
export function ExportDialog({ savedRequests, initiallySelected, onExport, onClose }: Props) {
  const [selected, setSelected] = useState<ReadonlySet<string>>(() => new Set(initiallySelected));
  const [format, setFormat] = useState<ExportFormat>("json");
  const [includeSecrets, setIncludeSecrets] = useState(false);
  const exportRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    exportRef.current?.focus();
  }, []);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.preventDefault();
        onClose();
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onClose]);

  const allSelected = savedRequests.every((saved) => selected.has(saved.id));
  const toggle = (id: string, on: boolean) => {
    const next = new Set(selected);
    if (on) next.add(id);
    else next.delete(id);
    setSelected(next);
  };

  // In the drawer's order, which is the order the file lists them in.
  const chosen = savedRequests.filter((saved) => selected.has(saved.id));

  const submit = () => {
    if (chosen.length === 0) return;
    onExport({
      savedRequestIds: chosen.map((saved) => saved.id),
      format,
      includeSecrets,
      fileName: `${chosen.length === 1 ? slug(chosen[0]!.name) : "saved-requests"}.${format}`,
    });
    onClose();
  };

  return (
    <div className="ac-dialog-backdrop" role="presentation" onClick={onClose}>
      <div
        className="ac-dialog ac-dialog--prompt"
        role="dialog"
        aria-modal="true"
        aria-label="Export saved requests"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="ac-dialog__header">
          <h2 className="ac-dialog__title">Export saved requests</h2>
        </div>

        <div className="ac-dialog__body">
          <div className="ac-group" role="group" aria-labelledby="ac-export-requests">
            <h3 id="ac-export-requests" className="ac-group__title">
              Requests
            </h3>
            <label className="ac-field ac-field--toggle">
              <input
                type="checkbox"
                checked={allSelected}
                onChange={(event) =>
                  setSelected(
                    event.target.checked
                      ? new Set(savedRequests.map((saved) => saved.id))
                      : new Set(),
                  )
                }
              />
              <span>All ({savedRequests.length})</span>
            </label>
            <div className="ac-export__list">
              {savedRequests.map((saved) => (
                <label key={saved.id} className="ac-field ac-field--toggle">
                  <input
                    type="checkbox"
                    checked={selected.has(saved.id)}
                    onChange={(event) => toggle(saved.id, event.target.checked)}
                  />
                  <span>{saved.name}</span>
                </label>
              ))}
            </div>
          </div>

          <div className="ac-group" role="radiogroup" aria-labelledby="ac-export-format">
            <h3 id="ac-export-format" className="ac-group__title">
              Format
            </h3>
            {FORMATS.map((option) => (
              <label key={option.value} className="ac-field ac-field--toggle">
                <input
                  type="radio"
                  name="ac-export-format"
                  value={option.value}
                  checked={format === option.value}
                  onChange={() => setFormat(option.value)}
                />
                <span>
                  {option.label}
                  <small>{option.detail}</small>
                </span>
              </label>
            ))}
          </div>

          <div className="ac-group">
            <h3 className="ac-group__title">Passwords and tokens</h3>
            <label
              className={`ac-field ac-field--toggle${includeSecrets ? " ac-field--danger" : ""}`}
            >
              <input
                type="checkbox"
                checked={includeSecrets}
                onChange={(event) => setIncludeSecrets(event.target.checked)}
              />
              <span>
                Include passwords and tokens
                <small>
                  {includeSecrets
                    ? "Written as plain text. Treat the file like the passwords in it."
                    : "Written as {{placeholders}}, so the file is safe to share. Check URLs and bodies yourself."}
                </small>
              </span>
            </label>
          </div>
        </div>

        <div className="ac-dialog__footer">
          <button type="button" className="ac-button" onClick={onClose}>
            Cancel
          </button>
          <button
            type="button"
            ref={exportRef}
            className="ac-button ac-button--primary"
            disabled={chosen.length === 0}
            onClick={submit}
          >
            {chosen.length === 1 ? "Export 1 request" : `Export ${chosen.length} requests`}
          </button>
        </div>
      </div>
    </div>
  );
}

/** A file name from a request's name: `List users` becomes `list-users`. */
function slug(name: string): string {
  const slugged = name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
  return slugged || "saved-request";
}
