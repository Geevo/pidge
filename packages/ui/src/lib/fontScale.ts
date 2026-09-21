/**
 * How large the interface is drawn, as a percentage of its designed size.
 *
 * Held as a whole number rather than a multiplier because it is a number the
 * user reads: "125%" is a size, `1.25` is an implementation detail. The CSS
 * side is one custom property, `--ac-font-scale`, which every size token is
 * multiplied by.
 */

/** The design, and what a fresh install starts at. */
export const DEFAULT_FONT_SCALE = 100;

/**
 * The sizes offered, smallest first.
 *
 * A short ladder rather than a free number: the steps are coarse enough that
 * each one is visibly different from the last, and picking a size is then a
 * matter of clicking until it reads, with nothing to type.
 *
 * It stops at 150 rather than going on. The chrome around the text — paddings,
 * gaps, icons — is fixed, and past half again as large the window stops
 * looking like a larger app and starts looking like a magnified one. The
 * bottom is 90, which is as tight as those same paddings allow before the text
 * begins to touch them.
 */
export const FONT_SCALES = [90, 100, 110, 125, 150] as const;

const SMALLEST = Math.min(...FONT_SCALES);
const LARGEST = Math.max(...FONT_SCALES);

/**
 * A stored scale made safe to apply. A state file edited by hand — or written
 * by a future version — cannot leave the app unreadable or off the screen.
 */
export function clampFontScale(percent: number): number {
  if (!Number.isFinite(percent)) return DEFAULT_FONT_SCALE;
  return Math.min(LARGEST, Math.max(SMALLEST, Math.round(percent)));
}

/**
 * The next rung up or down, for the keyboard.
 *
 * A scale between two rungs — from an older version, or a hand-edited file —
 * moves to the neighbouring rung rather than snapping to the nearest first, so
 * one keystroke is always one visible change.
 */
export function stepFontScale(percent: number, direction: 1 | -1): number {
  const current = clampFontScale(percent);
  const next =
    direction === 1
      ? FONT_SCALES.find((scale) => scale > current)
      : [...FONT_SCALES].reverse().find((scale) => scale < current);
  return next ?? current;
}
