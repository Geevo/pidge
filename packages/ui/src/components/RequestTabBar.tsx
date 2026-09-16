import type { ScratchTab } from "../types";
import { requestLabel } from "../lib/format";
import { MethodBadge } from "./MethodBadge";

interface Props {
  tabs: readonly ScratchTab[];
  activeTabId: string | null;
  onSelect: (tabId: string) => void;
  onClose: (tabId: string) => void;
  onNew: () => void;
}

export function RequestTabBar({ tabs, activeTabId, onSelect, onClose, onNew }: Props) {
  return (
    <div className="ac-tabbar" role="tablist" aria-label="Open requests">
      {tabs.map((tab) => {
        const label = tab.name ?? requestLabel(tab.request.url, tab.request.method);
        return (
          <div key={tab.id} className="ac-tab" role="presentation">
            <button
              type="button"
              role="tab"
              className="ac-tab"
              style={{ border: 0, padding: 0, background: "transparent" }}
              aria-selected={activeTabId === tab.id}
              onClick={() => onSelect(tab.id)}
              onAuxClick={(event) => {
                // Middle click closes, as in every editor.
                if (event.button === 1) onClose(tab.id);
              }}
            >
              <MethodBadge method={tab.request.method} small />
              <span className="ac-tab__label">{label}</span>
              {tab.savedRequestId && tab.dirty ? (
                <span className="ac-tab__dot" aria-label="Unsaved changes" />
              ) : null}
            </button>
            <button
              type="button"
              className="ac-icon-button"
              aria-label={`Close ${label}`}
              onClick={() => onClose(tab.id)}
            >
              ×
            </button>
          </div>
        );
      })}
      <button type="button" className="ac-icon-button" aria-label="New request" onClick={onNew}>
        +
      </button>
    </div>
  );
}
