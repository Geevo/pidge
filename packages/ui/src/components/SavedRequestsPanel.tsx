import type { SavedRequest } from "../types";
import { requestLabel } from "../lib/format";
import { MethodBadge } from "./MethodBadge";
import { TrashIcon } from "./icons";

interface Props {
  savedRequests: readonly SavedRequest[];
  onOpen: (saved: SavedRequest) => void;
  onDelete: (savedRequestId: string) => void;
}

/** A flat list. No folders, no collections, no projects. */
export function SavedRequestsPanel({ savedRequests, onOpen, onDelete }: Props) {
  return (
    <>
      <div className="ac-drawer__header">
        <span>Saved</span>
      </div>

      {savedRequests.length === 0 ? (
        <p className="ac-hint">Nothing saved. Ctrl/Cmd+S keeps the current request.</p>
      ) : (
        <ul className="ac-list">
          {savedRequests.map((saved) => (
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
                onClick={() => {
                  if (window.confirm(`Delete "${saved.name}"?`)) onDelete(saved.id);
                }}
              >
                <TrashIcon size={13} />
              </button>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}
