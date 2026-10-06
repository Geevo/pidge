import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";

import type { ScratchTab } from "../types";
import { requestLabel } from "../lib/format";
import { landingIndex, type Slot } from "../lib/tabDrag";
import { MethodBadge } from "./MethodBadge";
import type { WindowControls } from "../bridge";
import { WindowButtons } from "./WindowChrome";
import { ChevronLeftIcon, ChevronRightIcon, CloseIcon, PlusIcon } from "./icons";

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
  /** The frame that scrolls the strip while the tab is held at one end. */
  frame: number | null;
}

/** How close to an end of the strip a dragged tab has to come to scroll it. */
const EDGE = 24;
/** The fastest the strip scrolls under a dragged tab, in pixels a frame. */
const MAX_SCROLL_STEP = 14;

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
      if (current.frame !== null) cancelAnimationFrame(current.frame);

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
   * Tabs that no longer fit scroll, the way Notepad's do: an arrow at each
   * end of the strip, each dimmed once there is nothing further that way.
   * Tabs shrink to their minimum width first, so the arrows only appear once
   * shrinking is no longer enough.
   */
  const [overflow, setOverflow] = useState({ left: false, right: false, any: false });

  const measureOverflow = useCallback(() => {
    const list = listRef.current;
    if (!list) return;
    const hidden = list.scrollWidth - list.clientWidth;
    const next = {
      any: hidden > 1,
      left: list.scrollLeft > 1,
      right: list.scrollLeft < hidden - 1,
    };
    setOverflow((was) =>
      was.any === next.any && was.left === next.left && was.right === next.right ? was : next,
    );
  }, []);

  // The window resizing, a tab's label changing, a tab coming or going.
  useLayoutEffect(() => {
    measureOverflow();
    const list = listRef.current;
    if (!list || typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(measureOverflow);
    observer.observe(list);
    for (const tab of tabElements()) observer.observe(tab);
    return () => observer.disconnect();
  }, [tabs, measureOverflow, tabElements]);

  const smooth = (): ScrollBehavior =>
    window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth";

  // The open tab is always in view: a new one, or one chosen from elsewhere.
  useEffect(() => {
    const list = listRef.current;
    const tab = tabElements().find((element) => element.dataset.tabId === activeTabId);
    if (!list || !tab || drag.current) return;

    const start = tab.offsetLeft;
    const end = start + tab.offsetWidth;
    if (start < list.scrollLeft) {
      list.scrollTo?.({ left: start, behavior: smooth() });
    } else if (end > list.scrollLeft + list.clientWidth) {
      list.scrollTo?.({ left: end - list.clientWidth, behavior: smooth() });
    }
  }, [activeTabId, tabs.length, tabElements]);

  /* Most of a strip's width at a time, so the tab cut off at the edge stays in sight. */
  const scrollTabs = (direction: -1 | 1) => {
    const list = listRef.current;
    if (!list) return;
    list.scrollTo?.({
      left: list.scrollLeft + direction * list.clientWidth * 0.8,
      behavior: smooth(),
    });
  };

  /*
   * An ordinary wheel scrolls the strip sideways. A sideways swipe on a
   * touchpad already does, natively, so only the vertical part is taken here.
   * Nothing moves under a tab that is being dragged.
   */
  const onWheel = (event: React.WheelEvent<HTMLDivElement>) => {
    const list = listRef.current;
    if (!list || drag.current?.slots || Math.abs(event.deltaY) <= Math.abs(event.deltaX)) return;
    const lines = event.deltaMode === 1 ? 16 : 1;
    list.scrollLeft += event.deltaY * lines;
  };

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
      /*
       * A tab still easing into place from the last drop is finished first,
       * so every tab is measured where it rests rather than somewhere on the
       * way. Grabbing again quickly otherwise measured the strip mid-flight.
       */
      const elements = tabElements();
      for (const tab of elements) {
        tab.style.transition = "none";
        tab.style.left = "";
      }
      const origin = list.getBoundingClientRect().left - list.scrollLeft;
      current.slots = elements.map((tab) => {
        const rect = tab.getBoundingClientRect();
        return { left: rect.left - origin, width: rect.width };
      });
      for (const tab of elements) tab.style.transition = "";
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
      current.frame = requestAnimationFrame(scrollWhileHeld);
    }

    place();
  };

  /*
   * Draws the drag as it stands: the held tab under the pointer, and the tabs
   * it has passed stepped aside to make room.
   *
   * The held tab is kept to the visible strip and to the tabs' own extent.
   * Allowed past the last tab it poked out of the strip, which then took itself
   * to overflow: the scroll arrows appeared, and holding at the end scrolled
   * the strip, which carried the tab further out, which scrolled it further.
   */
  const place = () => {
    const current = drag.current;
    const list = listRef.current;
    if (!current?.slots || !list) return;

    const { slots, from } = current;
    const own = slots[from]!;
    const first = slots[0]!;
    const last = slots[slots.length - 1]!;
    const bounds = list.getBoundingClientRect();
    const origin = bounds.left - list.scrollLeft;
    const lowest = Math.max(bounds.left, origin + first.left);
    const highest = Math.min(bounds.right, origin + last.left + last.width) - own.width;
    const wanted = current.x - current.grab;
    const left = Math.max(lowest, Math.min(wanted, highest)) - origin;
    current.to = landingIndex(slots, from, left);

    // The tabs it has passed move one place over, by its width and the gap.
    const step = own.width + (parseFloat(getComputedStyle(list).columnGap) || 0);
    tabElements().forEach((element, index) => {
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

  /*
   * While the held tab is pushed against either end of the strip, the strip
   * scrolls to bring the tabs beyond into reach, faster the further past the
   * end the pointer goes. It runs every frame of the drag, since holding still
   * at the end has to keep scrolling without any pointer moves to drive it.
   */
  const scrollWhileHeld = () => {
    const current = drag.current;
    const list = listRef.current;
    if (!current?.slots || !list) return;

    const own = current.slots[current.from]!;
    const bounds = list.getBoundingClientRect();
    const start = current.x - current.grab;
    const past = Math.max(bounds.left + EDGE - start, start + own.width - (bounds.right - EDGE));
    if (past > 0) {
      const speed = Math.min(MAX_SCROLL_STEP, 2 + past / 4);
      const direction = start + own.width / 2 < bounds.left + bounds.width / 2 ? -1 : 1;
      const before = list.scrollLeft;
      list.scrollLeft = before + direction * speed;
      if (list.scrollLeft !== before) place();
    }
    current.frame = requestAnimationFrame(scrollWhileHeld);
  };

  return (
    <div className="ac-tabbar">
      <div className="ac-tabbar__strip">
        {overflow.any ? (
          <button
            type="button"
            className="ac-tabbar__scroll"
            aria-label="Scroll tabs left"
            tabIndex={-1}
            disabled={!overflow.left}
            onClick={() => scrollTabs(-1)}
          >
            <ChevronLeftIcon size={14} />
          </button>
        ) : null}

        <div
          ref={listRef}
          className="ac-tabbar__list"
          role="tablist"
          aria-label="Open requests"
          onPointerMove={onPointerMove}
          onPointerUp={() => endDrag(true)}
          onPointerCancel={() => endDrag(false)}
          onLostPointerCapture={() => endDrag(false)}
          onScroll={measureOverflow}
          onWheel={onWheel}
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
                    frame: null,
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
        </div>

        {overflow.any ? (
          <button
            type="button"
            className="ac-tabbar__scroll"
            aria-label="Scroll tabs right"
            tabIndex={-1}
            disabled={!overflow.right}
            onClick={() => scrollTabs(1)}
          >
            <ChevronRightIcon size={14} />
          </button>
        ) : null}

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

      {/*
        The leftover strip is the title bar. The host's own drag-region
        handler covers both dragging and double-click to maximise, so there
        is deliberately no handler here: adding one toggled the window twice.
      */}
      <div className="ac-tabbar__drag" {...(windowControls ? { "data-drag-region": true } : {})} />

      {windowControls ? <WindowButtons controls={windowControls} /> : null}
    </div>
  );
}
