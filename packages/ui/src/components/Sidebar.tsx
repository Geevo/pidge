import { useState } from "react";

import type { HistoryEntry, SavedRequest } from "../types";
import { formatDuration, formatTime, requestLabel, statusClass } from "../lib/format";
import { matchesSearch } from "../lib/search";
import { DrawerSearch } from "./DrawerSearch";
import { MethodBadge } from "./MethodBadge";

export type SidebarTab = "history" | "saved";

export interface SidebarProps {
  readonly history: readonly HistoryEntry[];
  readonly savedRequests: readonly SavedRequest[];
  readonly tab: SidebarTab;
  readonly onTabChange: (tab: SidebarTab) => void;
  readonly onNewRequest: () => void;
  readonly onOpenHistory: (entry: HistoryEntry) => void;
  readonly onOpenSaved: (saved: SavedRequest) => void;
  /**
   * Extra attributes for each row. VS Code reads its right-click menu for a
   * row from one of them, so deleting and exporting are the editor's own menu
   * items rather than buttons drawn here.
   */
  readonly rowAttributes?: (
    kind: SidebarTab,
    id: string,
  ) => Readonly<Record<string, string>> | undefined;
}

/**
 * History and saved requests in one pane, for a host whose requests open
 * somewhere else: the VS Code side bar, where each request is an editor tab.
 * The lists are the desktop drawers' lists; the actions that need asking
 * about (delete, clear, import, export) belong to the host.
 */
export function Sidebar({
  history,
  savedRequests,
  tab,
  onTabChange,
  onNewRequest,
  onOpenHistory,
  onOpenSaved,
  rowAttributes,
}: SidebarProps) {
  const [query, setQuery] = useState("");

  const tabs: { id: SidebarTab; label: string }[] = [
    { id: "history", label: "History" },
    { id: "saved", label: "Saved" },
  ];

  return (
    <div className="ac-sidebar">
      <div className="ac-sidebar__top">
        <button type="button" className="ac-button ac-button--primary" onClick={onNewRequest}>
          New Request
        </button>
      </div>

      <div className="ac-sidebar__tabs" role="tablist">
        {tabs.map(({ id, label }) => (
          <button
            key={id}
            type="button"
            role="tab"
            id={`ac-sidebar-tab-${id}`}
            aria-selected={tab === id}
            aria-controls="ac-sidebar-panel"
            className="ac-sidebar__tab"
            onClick={() => onTabChange(id)}
          >
            {label}
          </button>
        ))}
      </div>

      <div
        className="ac-sidebar__panel"
        id="ac-sidebar-panel"
        role="tabpanel"
        aria-labelledby={`ac-sidebar-tab-${tab}`}
      >
        {tab === "history" ? (
          <HistoryList
            history={history}
            query={query}
            onQuery={setQuery}
            onOpen={onOpenHistory}
            rowAttributes={rowAttributes}
          />
        ) : (
          <SavedList
            savedRequests={savedRequests}
            query={query}
            onQuery={setQuery}
            onOpen={onOpenSaved}
            rowAttributes={rowAttributes}
          />
        )}
      </div>
    </div>
  );
}

interface ListProps<T> {
  readonly query: string;
  readonly onQuery: (query: string) => void;
  readonly onOpen: (item: T) => void;
  readonly rowAttributes: SidebarProps["rowAttributes"];
}

function HistoryList({
  history,
  query,
  onQuery,
  onOpen,
  rowAttributes,
}: ListProps<HistoryEntry> & { readonly history: readonly HistoryEntry[] }) {
  if (history.length === 0) return <p className="ac-hint">Requests you send show up here.</p>;

  const shown = history.filter((entry) =>
    matchesSearch(query, [
      entry.request.method,
      entry.request.url,
      entry.status !== null ? String(entry.status) : "failed",
    ]),
  );

  return (
    <>
      <DrawerSearch label="Filter history" value={query} onChange={onQuery} />
      {shown.length === 0 ? <p className="ac-hint">No matches.</p> : null}
      <ul className="ac-list">
        {shown.map((entry) => (
          <li key={entry.id} className="ac-list__item" {...rowAttributes?.("history", entry.id)}>
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
                {entry.durationMs !== null ? <span>{formatDuration(entry.durationMs)}</span> : null}
                <span>{formatTime(entry.timestamp)}</span>
              </span>
            </button>
          </li>
        ))}
      </ul>
    </>
  );
}

function SavedList({
  savedRequests,
  query,
  onQuery,
  onOpen,
  rowAttributes,
}: ListProps<SavedRequest> & { readonly savedRequests: readonly SavedRequest[] }) {
  if (savedRequests.length === 0) {
    return <p className="ac-hint">Nothing saved. Ctrl/Cmd+S in a request keeps it here.</p>;
  }

  const shown = savedRequests.filter((saved) =>
    matchesSearch(query, [saved.name, saved.request.method, saved.request.url]),
  );

  return (
    <>
      <DrawerSearch label="Filter saved" value={query} onChange={onQuery} />
      {shown.length === 0 ? <p className="ac-hint">No matches.</p> : null}
      <ul className="ac-list">
        {shown.map((saved) => (
          <li key={saved.id} className="ac-list__item" {...rowAttributes?.("saved", saved.id)}>
            <button type="button" className="ac-list__button" onClick={() => onOpen(saved)}>
              <span className="ac-list__title">{saved.name}</span>
              <span className="ac-list__meta">
                <MethodBadge method={saved.request.method} small />
                <span>{requestLabel(saved.request.url, saved.request.method)}</span>
              </span>
            </button>
          </li>
        ))}
      </ul>
    </>
  );
}
