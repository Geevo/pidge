/**
 * Undo and redo for a plain text field.
 *
 * The browser's own history is not usable here. WebKitGTK never binds the keys
 * and leaves that to the application, and where the keys are bound the history
 * is one lump: a whole run of typing collapses into a single step, so one Ctrl+Z
 * empties the field rather than taking back the last thing you wrote.
 *
 * So the field keeps its own. Snapshots coalesce while someone is typing and
 * break where a person would expect to stop: after a pause, and at the
 * punctuation that separates one part of a URL from the next.
 */

export interface Snapshot {
  readonly value: string;
  readonly caret: number;
}

export interface History {
  readonly entries: readonly Snapshot[];
  readonly index: number;
  /** When the entry at `index` was last written, for coalescing. */
  readonly editedAt: number;
}

/** Typing that stops for this long starts a new undo step. */
const PAUSE_MS = 600;

/** Typing one of these ends the current step, whatever the timing. */
const BOUNDARY = /[\s/?&=#:]/;

/** Long enough to cover any real editing session, short enough to stay small. */
const LIMIT = 200;

export function createHistory(initial: Snapshot): History {
  return { entries: [initial], index: 0, editedAt: 0 };
}

/**
 * Adds a snapshot, folding it into the current one where it continues the same
 * piece of typing. `now` is passed in rather than read, so the rule can be
 * tested without waiting.
 */
export function record(history: History, next: Snapshot, now: number): History {
  const current = history.entries[history.index];
  if (!current || current.value === next.value) return history;

  if (shouldCoalesce(current, next, history.editedAt, now)) {
    const entries = [...history.entries.slice(0, history.index), next];
    return { entries, index: entries.length - 1, editedAt: now };
  }

  // A new step drops anything that had been undone: that future is gone.
  const kept = [...history.entries.slice(0, history.index + 1), next];
  const entries = kept.slice(-LIMIT);
  return { entries, index: entries.length - 1, editedAt: now };
}

export function undo(history: History): { history: History; snapshot: Snapshot } | null {
  if (history.index === 0) return null;
  const index = history.index - 1;
  return {
    history: { ...history, index, editedAt: 0 },
    snapshot: history.entries[index]!,
  };
}

export function redo(history: History): { history: History; snapshot: Snapshot } | null {
  if (history.index >= history.entries.length - 1) return null;
  const index = history.index + 1;
  return {
    history: { ...history, index, editedAt: 0 },
    snapshot: history.entries[index]!,
  };
}

function shouldCoalesce(current: Snapshot, next: Snapshot, editedAt: number, now: number): boolean {
  // A step that has just been undone is never extended; the next edit is new.
  if (editedAt === 0) return false;
  if (now - editedAt >= PAUSE_MS) return false;

  const grew = next.value.length > current.value.length && next.value.startsWith(current.value);
  if (grew) return !BOUNDARY.test(next.value.slice(current.value.length));

  // A run of backspaces is one step too.
  return next.value.length < current.value.length && current.value.startsWith(next.value);
}
