import { useState } from "react";

import type { HistoryEntry } from "../types";
import { formatDuration, formatTime, requestLabel, statusClass } from "../lib/format";
import { matchesSearch } from "../lib/search";
import { ConfirmDialog } from "./ConfirmDialog";
import { DrawerSearch } from "./DrawerSearch";
import { MethodBadge } from "./MethodBadge";

interface Props {
  history: readonly HistoryEntry[];
  onOpen: (entry: HistoryEntry) => void;
  onClear: () => void;
}

/**
 * Selecting an entry opens a new scratch tab. The record itself is never
 * edited, so history stays an accurate log of what was sent.
 */
export function HistoryPanel({ history, onOpen, onClear }: Props) {
  const [confirming, setConfirming] = useState(false);
  const [query, setQuery] = useState("");

  const shown = history.filter((entry) =>
    matchesSearch(query, [
      entry.request.method,
      entry.request.url,
      entry.status !== null ? String(entry.status) : "failed",
    ]),
  );

  return (
    <>
      <div className="ac-drawer__header">
        <span>History</span>
        <button
          type="button"
          className="ac-button ac-button--quiet ac-button--danger"
          disabled={history.length === 0}
          onClick={() => setConfirming(true)}
        >
          Clear
        </button>
      </div>

      {confirming ? (
        <ConfirmDialog
          title="Clear history"
          message={`Clear all ${history.length} history entries? This cannot be undone.`}
          confirmLabel="Clear"
          danger
          onConfirm={onClear}
          onClose={() => setConfirming(false)}
        />
      ) : null}

      {history.length === 0 ? (
        <p className="ac-hint">Nothing sent yet.</p>
      ) : (
        <DrawerSearch label="Search history" value={query} onChange={setQuery} />
      )}

      {history.length > 0 && shown.length === 0 ? <p className="ac-hint">No matches.</p> : null}

      {shown.length > 0 ? (
        <ul className="ac-list">
          {shown.map((entry) => (
            <li key={entry.id} className="ac-list__item">
              <button type="button" className="ac-list__button" onClick={() => onOpen(entry)}>
                <span className="ac-list__title">
                  <MethodBadge method={entry.request.method} small />{" "}
                  {requestLabel(entry.request.url, entry.request.method)}
                </span>
                <span className="ac-list__meta">
                  {entry.status !== null ? (
                    <span className="ac-status__code" data-band={statusClass(entry.status)}>
                      {entry.status}
                    </span>
                  ) : (
                    <span className="ac-status__code" data-band="server">
                      failed
                    </span>
                  )}
                  {entry.durationMs !== null ? (
                    <span>{formatDuration(entry.durationMs)}</span>
                  ) : null}
                  <span>{formatTime(entry.timestamp)}</span>
                </span>
              </button>
            </li>
          ))}
        </ul>
      ) : null}
    </>
  );
}
