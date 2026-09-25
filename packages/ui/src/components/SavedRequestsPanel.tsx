import { useState } from "react";

import type { SavedRequest } from "../types";
import { requestLabel } from "../lib/format";
import { matchesSearch } from "../lib/search";
import { ConfirmDialog } from "./ConfirmDialog";
import { DrawerSearch } from "./DrawerSearch";
import { MethodBadge } from "./MethodBadge";
import { TrashIcon } from "./icons";

interface Props {
  savedRequests: readonly SavedRequest[];
  onOpen: (saved: SavedRequest) => void;
  onDelete: (savedRequestId: string) => void;
}

/** A flat list. No folders, no collections, no projects. */
export function SavedRequestsPanel({ savedRequests, onOpen, onDelete }: Props) {
  const [deleting, setDeleting] = useState<SavedRequest | null>(null);
  const [query, setQuery] = useState("");

  const shown = savedRequests.filter((saved) =>
    matchesSearch(query, [saved.name, saved.request.method, saved.request.url]),
  );

  return (
    <>
      <div className="ac-drawer__header">
        <span>Saved</span>
      </div>

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
