import type { HistoryEntry } from "../types";
import { formatDuration, formatTime, requestLabel, statusClass } from "../lib/format";
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
  return (
    <>
      <div className="ac-drawer__header">
        <span>History</span>
        <button
          type="button"
          className="ac-button ac-button--quiet ac-button--danger"
          disabled={history.length === 0}
          onClick={() => {
            if (window.confirm(`Clear all ${history.length} history entries?`)) onClear();
          }}
        >
          Clear
        </button>
      </div>

      {history.length === 0 ? (
        <p className="ac-hint">Nothing sent yet.</p>
      ) : (
        <ul className="ac-list">
          {history.map((entry) => (
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
      )}
    </>
  );
}
