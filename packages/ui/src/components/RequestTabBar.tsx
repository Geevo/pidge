import { useCallback, useLayoutEffect, useRef } from "react";

import type { ScratchTab } from "../types";
import { requestLabel } from "../lib/format";
import { MethodBadge } from "./MethodBadge";
import type { WindowControls } from "../bridge";
import { WindowButtons } from "./WindowChrome";
import { CloseIcon, PlusIcon } from "./icons";

interface Props {
  tabs: readonly ScratchTab[];
  activeTabId: string | null;
  onSelect: (tabId: string) => void;
  onClose: (tabId: string) => void;
  /** Moves a tab to a new position in the strip, as it is dragged there. */
  onMove: (tabId: string, toIndex: number) => void;
  onNew: () => void;
  /** Supplied only when the app draws its own title bar. */
  windowControls?: WindowControls;
}

/** How far a press has to travel before it is a drag rather than a click. */
const DRAG_THRESHOLD = 4;

/** Where one tab sits in the strip, measured when the drag begins. */
interface Slot {
  left: number;
  width: number;
}

interface Drag {
  tabId: string;
  pointerId: number;
  startX: number;
  /** Where the tab was grabbed, measured from its left edge. */
  grab: number;
  /** The pointer's latest position. */
  x: number;
  /** Set once the press has travelled far enough to be a drag. */
  slots: Slot[] | null;
  from: number;
  /** Where the tab would land if it were let go now. */
  to: number;
}

/**
 * The tab strip.
 *
 * Each tab is a row that owns the selected background, with the select button
 * and the close button as siblings inside it — nesting one button in another
 * is invalid, and putting the highlight on the button alone leaves the close
 * control sitting outside its own tab.
 *
 * A tab is dragged along the strip with pointer events rather than HTML drag
 * and drop: a desktop webview can claim drag and drop for dropping files on
 * the window, and the native drag image cannot be kept to the strip anyway.
 *
 * Nothing is reordered until the tab is let go. While it is held, the dragged
 * tab follows the pointer and the tabs it has passed step aside by its width,
 * all by offsetting `left`, with the stylesheet easing them there. Reordering
 * the elements mid-drag instead had React re-insert them on every swap, and
 * each re-insert both flickered and could lose the pointer.
 *
 * `left` rather than a transform: a transform animation lifts each tab onto a
 * compositor layer as it starts and drops it back as it stops, and at a
 * fractional desktop scale a tab's text is not drawn quite the same on both.
 */
export function RequestTabBar({
  tabs,
  activeTabId,
  onSelect,
  onClose,
  onMove,
  onNew,
  windowControls,
}: Props) {
  const listRef = useRef<HTMLDivElement>(null);
  const drag = useRef<Drag | null>(null);
  /* The dropped tab's place on screen, for easing it into its new slot. */
  const dropped = useRef<{ tabId: string; left: number } | null>(null);

  const tabElements = useCallback(
    () => [...(listRef.current?.querySelectorAll<HTMLElement>(".ac-tab") ?? [])],
    [],
  );

  /*
   * The tabs that stepped aside are already where the new order puts them, so
   * their offsets are dropped with no easing; the dropped tab is started
   * from where it was let go and eased the rest of the way into its slot.
   */
  const settle = useCallback(() => {
    const drop = dropped.current;
    dropped.current = null;
    if (!drop) return;

    const elements = tabElements();
    for (const element of elements) {
      element.style.transition = "none";
      element.style.left = "";
    }

    const element = elements.find((tab) => tab.dataset.tabId === drop.tabId);
    const offset = element ? drop.left - element.getBoundingClientRect().left : 0;
    const still = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;
    if (element && offset !== 0 && !still) {
      element.style.left = `${offset}px`;
      // Read a size, so the browser takes the start position before easing.
      void element.offsetWidth;
      element.style.transition = "";
      element.style.left = "";
    }
    for (const other of elements) other.style.transition = "";
  }, [tabElements]);

  const endDrag = useCallback(
    (commit: boolean) => {
      const current = drag.current;
      drag.current = null;
      if (!current?.slots) return;

      const element = tabElements()[current.from];
      if (element) {
        dropped.current = { tabId: current.tabId, left: element.getBoundingClientRect().left };
        element.classList.remove("ac-tab--dragging");
      }
      listRef.current?.classList.remove("ac-tabbar__list--dragging");

      /*
       * Selected on the drop rather than on the press. Selecting redraws the
       * whole request below, and doing that as the drag began froze the first
       * frames of it. A press that never became a drag selects through the
       * tab's click instead.
       */
      if (commit) onSelect(current.tabId);
      if (commit && current.to !== current.from) {
        // Settled once the new order is in the page.
        onMove(current.tabId, current.to);
      } else {
        settle();
      }
    },
    [onMove, onSelect, settle, tabElements],
  );

  useLayoutEffect(settle, [tabs, settle]);

  /*
   * The moves and the release are handled on the strip, which also takes the
   * pointer capture, so the drag carries on wherever the pointer goes.
   */
  const onPointerMove = (event: React.PointerEvent<HTMLDivElement>) => {
    const current = drag.current;
    const list = listRef.current;
    if (!current || !list || current.pointerId !== event.pointerId) return;
    // Released somewhere the strip never heard about, before any drag began.
    if ((event.buttons & 1) === 0) {
      endDrag(false);
      return;
    }
    current.x = event.clientX;

    if (!current.slots) {
      if (Math.abs(event.clientX - current.startX) < DRAG_THRESHOLD) return;
      current.slots = tabElements().map((tab) => {
        const rect = tab.getBoundingClientRect();
        return { left: rect.left, width: rect.width };
      });
      try {
        event.currentTarget.setPointerCapture(event.pointerId);
      } catch {
        // Not supported here; the drag still tracks inside the window.
      }
      /*
       * The drag's classes are set here rather than through a render: a render
       * as the drag began cost a frame, and until it landed the lifted tab
       * still eased its offset and trailed behind the pointer.
       */
      tabElements()[current.from]?.classList.add("ac-tab--dragging");
      listRef.current?.classList.add("ac-tabbar__list--dragging");
    }

    const { slots, from } = current;
    const own = slots[from]!;
    const bounds = list.getBoundingClientRect();
    const left = Math.max(
      bounds.left,
      Math.min(current.x - current.grab, bounds.right - own.width),
    );
    const middle = left + own.width / 2;

    // Lands after every other tab whose middle its own middle has passed.
    current.to = slots.filter(
      (slot, index) => index !== from && slot.left + slot.width / 2 < middle,
    ).length;

    // The tabs it has passed move one place over, by its width and the gap.
    const step = own.width + (parseFloat(getComputedStyle(list).columnGap) || 0);
    const elements = tabElements();
    elements.forEach((element, index) => {
      if (index === from) {
        element.style.left = `${left - own.left}px`;
      } else if (from < index && index <= current.to) {
        element.style.left = `${-step}px`;
      } else if (current.to <= index && index < from) {
        element.style.left = `${step}px`;
      } else {
        element.style.left = "";
      }
    });
  };

  return (
    <div className="ac-tabbar">
      <div
        ref={listRef}
        className="ac-tabbar__list"
        role="tablist"
        aria-label="Open requests"
        onPointerMove={onPointerMove}
        onPointerUp={() => endDrag(true)}
        onPointerCancel={() => endDrag(false)}
        onLostPointerCapture={() => endDrag(false)}
      >
        {tabs.map((tab, index) => {
          const label = tab.name ?? requestLabel(tab.request.url, tab.request.method);
          const active = activeTabId === tab.id;

          return (
            <div
              key={tab.id}
              className={`ac-tab${active ? " ac-tab--active" : ""}`}
              role="presentation"
              data-tab-id={tab.id}
              onPointerDown={(event) => {
                // The main button only, and not on the close button.
                if (event.button !== 0) return;
                if ((event.target as Element).closest(".ac-tab__close")) return;
                drag.current = {
                  tabId: tab.id,
                  pointerId: event.pointerId,
                  startX: event.clientX,
                  grab: event.clientX - event.currentTarget.getBoundingClientRect().left,
                  x: event.clientX,
                  slots: null,
                  from: index,
                  to: index,
                };
              }}
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

        {/*
          The leftover strip is the title bar. The host's own drag-region
          handler covers both dragging and double-click to maximise, so there
          is deliberately no handler here: adding one toggled the window twice.
        */}
        <div
          className="ac-tabbar__drag"
          {...(windowControls ? { "data-tauri-drag-region": true } : {})}
        />
      </div>

      {windowControls ? <WindowButtons controls={windowControls} /> : null}
    </div>
  );
}
