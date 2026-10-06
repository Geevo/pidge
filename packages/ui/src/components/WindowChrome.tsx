import { useEffect, useState, type ReactElement } from "react";

import type { WindowButtonStyle, WindowControls } from "../bridge";

/** The four glyphs one desktop draws, `restore` replacing `maximize` when maximised. */
interface Glyphs {
  minimize: ReactElement;
  maximize: ReactElement;
  restore: ReactElement;
  close: ReactElement;
}

/*
 * Each set was copied from the desktop's own artwork rather than approximated:
 * Breeze's chevrons, and the diamond it shows in place of maximise once the
 * window is maximised; Adwaita's low bar and two rings, from its symbolic
 * icons; and the line, square and pair of squares Windows draws.
 */
const GLYPHS: Record<WindowButtonStyle, Glyphs> = {
  kde: {
    minimize: (
      <svg width="13" height="13" viewBox="0 0 13 13" fill="none" aria-hidden="true">
        <path d="M1 3.8L6.5 9.2L12 3.8" stroke="currentColor" strokeWidth="1.3" />
      </svg>
    ),
    maximize: (
      <svg width="13" height="13" viewBox="0 0 13 13" fill="none" aria-hidden="true">
        <path d="M1 9.2L6.5 3.8L12 9.2" stroke="currentColor" strokeWidth="1.3" />
      </svg>
    ),
    restore: (
      <svg width="13" height="13" viewBox="0 0 13 13" fill="none" aria-hidden="true">
        <path d="M6.5 1L12 6.5L6.5 12L1 6.5Z" stroke="currentColor" strokeWidth="1.3" />
      </svg>
    ),
    close: (
      <svg width="13" height="13" viewBox="0 0 13 13" fill="none" aria-hidden="true">
        <path d="M1.75 1.75l9.5 9.5M11.25 1.75l-9.5 9.5" stroke="currentColor" strokeWidth="1.3" />
      </svg>
    ),
  },

  gnome: {
    minimize: (
      <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">
        <path d="M4 10h8v2H4z" fill="currentColor" />
      </svg>
    ),
    maximize: (
      <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">
        <path d="M4 4v8h8V4zm2 2h4v4H6z" fill="currentColor" fillRule="evenodd" />
      </svg>
    ),
    restore: (
      <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">
        <path d="M5 5v6h6V5zm2 2h2v2H7z" fill="currentColor" fillRule="evenodd" />
      </svg>
    ),
    close: (
      <svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true">
        <path d="M4.5 4.5l7 7M11.5 4.5l-7 7" stroke="currentColor" strokeWidth="1.9" />
      </svg>
    ),
  },

  windows: {
    minimize: (
      <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden="true">
        <path d="M1 5h8" stroke="currentColor" strokeWidth="1.2" />
      </svg>
    ),
    maximize: (
      <svg width="10" height="10" viewBox="0 0 10 10" fill="none" aria-hidden="true">
        <rect x="1" y="1" width="8" height="8" stroke="currentColor" strokeWidth="1.1" />
      </svg>
    ),
    restore: (
      <svg width="10" height="10" viewBox="0 0 10 10" fill="none" aria-hidden="true">
        <rect x="1" y="3" width="6" height="6" stroke="currentColor" strokeWidth="1.1" />
        <path d="M3 3V1h6v6H7" stroke="currentColor" strokeWidth="1.1" />
      </svg>
    ),
    close: (
      <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden="true">
        <path d="M1 1l8 8M9 1l-8 8" stroke="currentColor" strokeWidth="1.2" />
      </svg>
    ),
  },
};

/**
 * Minimise, maximise and close, for a window that draws its own title bar.
 *
 * These live at the end of the tab strip rather than in a bar of their own, so
 * the window costs one row of chrome instead of two.
 *
 * A window that draws its own buttons looks foreign the moment they are the
 * wrong ones, so the host says which desktop it is on and all three sets are
 * kept here: the glyphs differ, and so does the button behind them — Windows
 * fills a tall rectangle, Breeze lights a circle under the pointer, Adwaita
 * keeps a circle there the whole time.
 */
export function WindowButtons({ controls }: { controls: WindowControls }) {
  const [maximized, setMaximized] = useState(false);
  const glyphs = GLYPHS[controls.buttons];

  /*
   * The glyph follows the window, not the click. Every call is caught as well:
   * these buttons are chrome, and if the host cannot answer, the icon is merely
   * wrong, which is a great deal better than an unhandled rejection inside an
   * effect taking the whole application down.
   */
  useEffect(() => {
    let cancelled = false;
    let unsubscribe: (() => void) | undefined;

    const sync = () => {
      controls
        .isMaximized()
        .then((value) => {
          if (!cancelled) setMaximized(value);
        })
        .catch(() => undefined);
    };

    sync();
    controls
      .onResized(sync)
      .then((stop) => {
        if (cancelled) stop();
        else unsubscribe = stop;
      })
      .catch(() => undefined);

    return () => {
      cancelled = true;
      unsubscribe?.();
    };
  }, [controls]);

  const run = (action: () => Promise<unknown>) => {
    action().catch(() => undefined);
  };

  return (
    <div className={`ac-window-buttons ac-window-buttons--${controls.buttons}`}>
      <button
        type="button"
        className="ac-window-button"
        aria-label="Minimise"
        title="Minimise"
        onClick={() => run(() => controls.minimize())}
      >
        {glyphs.minimize}
      </button>

      <button
        type="button"
        className="ac-window-button"
        aria-label={maximized ? "Restore" : "Maximise"}
        title={maximized ? "Restore" : "Maximise"}
        onClick={() => run(() => controls.toggleMaximize())}
      >
        {maximized ? glyphs.restore : glyphs.maximize}
      </button>

      <button
        type="button"
        className="ac-window-button ac-window-button--close"
        aria-label="Close window"
        title="Close"
        onClick={() => run(() => controls.close())}
      >
        {glyphs.close}
      </button>
    </div>
  );
}
