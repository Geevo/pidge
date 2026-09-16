import { useCallback, useEffect, useRef, useState } from "react";

import type { PaneLayout } from "../types";

/** Neither pane may be dragged smaller than this share of the split. */
const MIN_PERCENT = 15;
const MAX_PERCENT = 85;
/** Arrow keys nudge; Page keys jump. */
const STEP = 2;
const PAGE_STEP = 10;

interface Props {
  layout: PaneLayout;
  /** The first pane's share of the split, as a percentage. */
  percent: number;
  /** Fired when a drag ends, not on every frame — this gets persisted. */
  onCommit: (percent: number) => void;
  first: React.ReactNode;
  second: React.ReactNode;
  firstLabel: string;
  secondLabel: string;
}

export function clampPercent(percent: number): number {
  if (!Number.isFinite(percent)) return 50;
  return Math.min(MAX_PERCENT, Math.max(MIN_PERCENT, Math.round(percent)));
}

/**
 * Two panes and a draggable divider.
 *
 * The size is held locally while dragging and only handed up on release, so a
 * drag is not a hundred state updates and a hundred writes to disk.
 */
export function SplitPane({
  layout,
  percent,
  onCommit,
  first,
  second,
  firstLabel,
  secondLabel,
}: Props) {
  const container = useRef<HTMLDivElement>(null);
  const [live, setLive] = useState(() => clampPercent(percent));
  const [dragging, setDragging] = useState(false);

  /*
   * Refs shadow both pieces of state because a pointer sequence can run faster
   * than React re-renders: a `pointermove` arriving before the `pointerdown`
   * render would read `dragging` as false and drop the movement, and
   * `pointerup` would commit a stale percentage.
   */
  const draggingRef = useRef(false);
  const liveRef = useRef(live);

  const setPercent = useCallback((next: number) => {
    liveRef.current = next;
    setLive(next);
  }, []);

  /*
   * Selection is suppressed by touching the DOM directly rather than by
   * rendering a class. The browser begins selecting text on the very first
   * mousedown, which is before React has re-rendered anything, so a class
   * driven by state arrives too late to stop it.
   */
  const setBodyDragging = useCallback(
    (active: boolean) => {
      const { classList } = document.body;
      classList.toggle("ac-dragging", active);
      classList.toggle("ac-dragging--columns", active && layout === "columns");
      classList.toggle("ac-dragging--rows", active && layout === "rows");
    },
    [layout],
  );

  // A drag interrupted by an unmount must not leave the whole page unselectable.
  useEffect(() => () => setBodyDragging(false), [setBodyDragging]);

  // Follow the persisted value unless the user is actively dragging.
  useEffect(() => {
    if (!draggingRef.current) setPercent(clampPercent(percent));
  }, [percent, setPercent]);

  const percentFromPointer = useCallback(
    (event: React.PointerEvent): number | null => {
      const box = container.current?.getBoundingClientRect();
      if (!box) return null;
      const value =
        layout === "columns"
          ? ((event.clientX - box.left) / box.width) * 100
          : ((event.clientY - box.top) / box.height) * 100;
      return clampPercent(value);
    },
    [layout],
  );

  const commit = (next: number) => {
    setPercent(next);
    onCommit(next);
  };

  const nudge = (event: React.KeyboardEvent) => {
    const towardsSecond = layout === "columns" ? "ArrowRight" : "ArrowDown";
    const towardsFirst = layout === "columns" ? "ArrowLeft" : "ArrowUp";

    let next: number | null = null;
    if (event.key === towardsFirst) next = live - STEP;
    else if (event.key === towardsSecond) next = live + STEP;
    else if (event.key === "PageUp") next = live - PAGE_STEP;
    else if (event.key === "PageDown") next = live + PAGE_STEP;
    else if (event.key === "Home") next = MIN_PERCENT;
    else if (event.key === "End") next = MAX_PERCENT;
    else if (event.key === "Enter") next = 50;

    if (next === null) return;
    event.preventDefault();
    commit(clampPercent(next));
  };

  return (
    <div
      ref={container}
      className={`ac-split ac-split--${layout}${dragging ? " ac-split--dragging" : ""}`}
    >
      <div className="ac-split__pane" style={{ flexBasis: `${live}%` }}>
        {first}
      </div>

      <div
        className="ac-split__divider"
        role="separator"
        tabIndex={0}
        aria-label={`Resize ${firstLabel} and ${secondLabel}`}
        aria-orientation={layout === "columns" ? "vertical" : "horizontal"}
        aria-valuenow={live}
        aria-valuemin={MIN_PERCENT}
        aria-valuemax={MAX_PERCENT}
        onKeyDown={nudge}
        onDoubleClick={() => commit(50)}
        onPointerDown={(event) => {
          // Stops the browser starting a text selection from this press.
          event.preventDefault();
          // Capture so the drag survives the pointer leaving the divider, and
          // keeps working over the CodeMirror instance next to it. It is an
          // enhancement, not a requirement: without it the drag still tracks,
          // it just stops if the pointer leaves the window.
          try {
            event.currentTarget.setPointerCapture(event.pointerId);
          } catch {
            // Not supported here; carry on.
          }
          draggingRef.current = true;
          setBodyDragging(true);
          setDragging(true);
        }}
        onPointerMove={(event) => {
          if (!draggingRef.current) return;
          const next = percentFromPointer(event);
          if (next !== null) setPercent(next);
        }}
        onPointerUp={(event) => {
          if (!draggingRef.current) return;
          try {
            event.currentTarget.releasePointerCapture(event.pointerId);
          } catch {
            // Never captured; nothing to release.
          }
          draggingRef.current = false;
          setBodyDragging(false);
          setDragging(false);
          onCommit(liveRef.current);
        }}
        onLostPointerCapture={() => {
          // A cancelled gesture must still release the page.
          if (!draggingRef.current) return;
          draggingRef.current = false;
          setBodyDragging(false);
          setDragging(false);
          onCommit(liveRef.current);
        }}
      >
        <span className="ac-split__grip" aria-hidden="true" />
      </div>

      <div className="ac-split__pane ac-split__pane--rest">{second}</div>
    </div>
  );
}
