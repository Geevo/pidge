import type { ScratchTab } from "../types";
import { requestLabel } from "../lib/format";
import { MethodBadge } from "./MethodBadge";
import { CloseIcon, PlusIcon } from "./icons";

interface Props {
  tabs: readonly ScratchTab[];
  activeTabId: string | null;
  onSelect: (tabId: string) => void;
  onClose: (tabId: string) => void;
  onNew: () => void;
}

/**
 * The tab strip.
 *
 * Each tab is a row that owns the selected background, with the select button
 * and the close button as siblings inside it — nesting one button in another
 * is invalid, and putting the highlight on the button alone leaves the close
 * control sitting outside its own tab.
 */
export function RequestTabBar({ tabs, activeTabId, onSelect, onClose, onNew }: Props) {
  return (
    <div className="ac-tabbar">
      <div className="ac-tabbar__list" role="tablist" aria-label="Open requests">
        {tabs.map((tab) => {
          const label = tab.name ?? requestLabel(tab.request.url, tab.request.method);
          const active = activeTabId === tab.id;

          return (
            <div
              key={tab.id}
              className={`ac-tab${active ? " ac-tab--active" : ""}`}
              role="presentation"
            >
              <button
                type="button"
                role="tab"
                className="ac-tab__button"
                aria-selected={active}
                title={label}
                onClick={() => onSelect(tab.id)}
                onAuxClick={(event) => {
                  // Middle click closes, as in every editor.
                  if (event.button === 1) onClose(tab.id);
                }}
              >
                <MethodBadge method={tab.request.method} small />
                <span className="ac-tab__label">{label}</span>
              </button>

              {tab.savedRequestId && tab.dirty ? (
                <span className="ac-tab__dot" title="Unsaved changes" />
              ) : null}

              <button
                type="button"
                className="ac-tab__close"
                aria-label={`Close ${label}`}
                onClick={() => onClose(tab.id)}
              >
                <CloseIcon size={12} />
              </button>
            </div>
          );
        })}

        <button
          type="button"
          className="ac-tabbar__new"
          aria-label="New request"
          title="New request"
          onClick={onNew}
        >
          <PlusIcon size={15} />
        </button>
      </div>
    </div>
  );
}
