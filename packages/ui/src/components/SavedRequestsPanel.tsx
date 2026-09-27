import { useState } from "react";

import type { ExportInput } from "../bridge";
import type { SavedRequest } from "../types";
import { requestLabel } from "../lib/format";
import { matchesSearch } from "../lib/search";
import { ConfirmDialog } from "./ConfirmDialog";
import { DrawerSearch } from "./DrawerSearch";
import { ExportDialog } from "./ExportDialog";
import { MethodBadge } from "./MethodBadge";
import { ExportIcon, ImportIcon, TrashIcon } from "./icons";

interface Props {
  savedRequests: readonly SavedRequest[];
  onOpen: (saved: SavedRequest) => void;
  onDelete: (savedRequestId: string) => void;
  /** Absent when the host cannot save files, and the button with it. */
  onExport?: (input: ExportInput) => void;
  /** Absent when the host cannot open files, and the button with it. */
  onImport?: () => void;
}

/** A flat list. No folders, no collections, no projects. */
export function SavedRequestsPanel({ savedRequests, onOpen, onDelete, onExport, onImport }: Props) {
  const [deleting, setDeleting] = useState<SavedRequest | null>(null);
  const [exporting, setExporting] = useState(false);
  const [query, setQuery] = useState("");

  const shown = savedRequests.filter((saved) =>
    matchesSearch(query, [saved.name, saved.request.method, saved.request.url]),
  );

  return (
    <>
      <div className="ac-drawer__header">
        <span>Saved</span>
        <span className="ac-drawer__actions">
          {onImport ? (
            <button
              type="button"
              className="ac-icon-button"
              aria-label="Import saved requests"
              title="Import"
              onClick={onImport}
            >
              <ImportIcon size={13} />
            </button>
          ) : null}
          {onExport && savedRequests.length > 0 ? (
            <button
              type="button"
              className="ac-icon-button"
              aria-label="Export saved requests"
              title="Export"
              onClick={() => setExporting(true)}
            >
              <ExportIcon size={13} />
            </button>
          ) : null}
        </span>
      </div>

      {exporting && onExport ? (
        <ExportDialog
          savedRequests={savedRequests}
          initiallySelected={shown.map((saved) => saved.id)}
          onExport={onExport}
          onClose={() => setExporting(false)}
        />
      ) : null}

      {deleting ? (
        <ConfirmDialog
          title="Delete saved request"
          message={`Delete "${deleting.name}"? This cannot be undone.`}
          confirmLabel="Delete"
          danger
          onConfirm={() => onDelete(deleting.id)}
          onClose={() => setDeleting(null)}
        />
      ) : null}

      {savedRequests.length === 0 ? (
        <p className="ac-hint">Nothing saved. Ctrl/Cmd+S keeps the current request.</p>
      ) : (
        <DrawerSearch label="Search saved" value={query} onChange={setQuery} />
      )}

      {savedRequests.length > 0 && shown.length === 0 ? (
        <p className="ac-hint">No matches.</p>
      ) : null}

      {shown.length > 0 ? (
        <ul className="ac-list">
          {shown.map((saved) => (
            <li key={saved.id} className="ac-list__item">
              <button type="button" className="ac-list__button" onClick={() => onOpen(saved)}>
                <span className="ac-list__title">{saved.name}</span>
                <span className="ac-list__meta">
                  <MethodBadge method={saved.request.method} small />
                  <span>{requestLabel(saved.request.url, saved.request.method)}</span>
                </span>
              </button>
              <button
                type="button"
                className="ac-icon-button"
                aria-label={`Delete ${saved.name}`}
                onClick={() => setDeleting(saved)}
              >
                <TrashIcon size={13} />
              </button>
            </li>
          ))}
        </ul>
      ) : null}
    </>
  );
}
