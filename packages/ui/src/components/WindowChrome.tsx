import { useEffect, useState } from "react";

import type { ResizeEdge, WindowControls } from "../bridge";

/** The eight edges and corners, in the order they are stacked. */
const EDGES: readonly ResizeEdge[] = [
  "North",
  "South",
  "East",
  "West",
  "NorthWest",
  "NorthEast",
  "SouthWest",
  "SouthEast",
];

/**
 * Minimise, maximise and close, for a window that draws its own title bar.
 *
 * These live at the end of the tab strip rather than in a bar of their own, so
 * the window costs one row of chrome instead of two.
 */
export function WindowButtons({ controls }: { controls: WindowControls }) {
  const [maximized, setMaximized] = useState(false);

  /*
   * Every call is caught. These buttons are chrome: if the host cannot answer,
   * the icon is merely wrong, which is a great deal better than an unhandled
   * rejection inside an effect taking the whole application down.
   */
  useEffect(() => {
    let cancelled = false;
    controls
      .isMaximized()
      .then((value) => {
        if (!cancelled) setMaximized(value);
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [controls]);

  const run = (action: () => Promise<unknown>) => {
    action()
      .then(() => controls.isMaximized())
      .then(setMaximized)
      .catch(() => undefined);
  };

  return (
    <div className="ac-window-buttons">
      <button
        type="button"
        className="ac-window-button"
        aria-label="Minimise"
        title="Minimise"
        onClick={() => run(() => controls.minimize())}
      >
        <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden="true">
          <path d="M1 5h8" stroke="currentColor" strokeWidth="1.2" />
        </svg>
      </button>

      <button
        type="button"
        className="ac-window-button"
        aria-label={maximized ? "Restore" : "Maximise"}
        title={maximized ? "Restore" : "Maximise"}
        onClick={() => run(() => controls.toggleMaximize())}
      >
        <svg width="10" height="10" viewBox="0 0 10 10" fill="none" aria-hidden="true">
          {maximized ? (
            <>
              <rect x="1" y="3" width="6" height="6" stroke="currentColor" strokeWidth="1.1" />
              <path d="M3 3V1h6v6H7" stroke="currentColor" strokeWidth="1.1" />
            </>
          ) : (
            <rect x="1" y="1" width="8" height="8" stroke="currentColor" strokeWidth="1.1" />
          )}
        </svg>
      </button>

      <button
        type="button"
        className="ac-window-button ac-window-button--close"
        aria-label="Close window"
        title="Close"
        onClick={() => run(() => controls.close())}
      >
        <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden="true">
          <path d="M1 1l8 8M9 1l-8 8" stroke="currentColor" strokeWidth="1.2" />
        </svg>
      </button>
    </div>
  );
}

/**
 * Invisible strips around the window that start a resize.
 *
 * An undecorated window gets no resize edges from the platform — `tao` only
 * calls `set_decorated(false)` on Linux and adds nothing back — so without
 * these the window could not be resized at all.
 */
export function ResizeEdges({ controls }: { controls: WindowControls }) {
  return (
    <div className="ac-resize-edges" aria-hidden="true">
      {EDGES.map((edge) => (
        <div
          key={edge}
          className={`ac-resize-edge ac-resize-edge--${edge.toLowerCase()}`}
          onPointerDown={(event) => {
            // Only a primary-button press should resize.
            if (event.button !== 0) return;
            event.preventDefault();
            controls.startResizing(edge).catch(() => undefined);
          }}
        />
      ))}
    </div>
  );
}
