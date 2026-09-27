import { describe, expect, it } from "vitest";

import { landingIndex, type Slot } from "./tabDrag";

/** Tabs of these widths laid end to end from x = 4, with a 2px gap. */
function strip(...widths: number[]): Slot[] {
  let left = 4;
  return widths.map((width) => {
    const slot = { left, width };
    left += width + 2;
    return slot;
  });
}

describe("landingIndex", () => {
  it("stays put until halfway over a neighbour, in either direction", () => {
    const slots = strip(100, 100, 100);
    // Dragging the first tab right: its neighbour is 102px on (width + gap).
    expect(landingIndex(slots, 0, 4 + 50)).toBe(0);
    expect(landingIndex(slots, 0, 4 + 52)).toBe(1);
    // Back left over the same ground: it returns at the same point, not the
    // moment the two touch.
    expect(landingIndex(slots, 0, 4 + 100)).toBe(1);
    expect(landingIndex(slots, 0, 4 + 52)).toBe(1);
    expect(landingIndex(slots, 0, 4 + 50)).toBe(0);
  });

  it("is symmetric with tabs of different widths", () => {
    // The strip the bug turned up in: two long names, three blank tabs.
    const slots = strip(240, 240, 157.3, 157.3, 157.3);
    const [first, second] = slots as [Slot, Slot];
    // The dragged tab is the first; the second is 242px on. Halfway is 121.
    expect(landingIndex(slots, 0, first.left + 120)).toBe(0);
    expect(landingIndex(slots, 0, first.left + 122)).toBe(1);
    // Where it used to snap back: just touching the neighbour, which by then
    // sits in the first place, ending at 244.
    expect(landingIndex(slots, 0, second.left - 5)).toBe(1);
  });

  it("lands at either end when pushed as far as it goes", () => {
    const slots = strip(240, 240, 157.3, 157.3, 157.3);
    const last = slots[4]!;
    const end = last.left + last.width;
    expect(landingIndex(slots, 0, end - 240)).toBe(4);
    expect(landingIndex(slots, 4, slots[0]!.left)).toBe(0);
    // A narrow tab dragged from the end to the start, past wide ones.
    expect(landingIndex(slots, 4, 4)).toBe(0);
  });

  it("moves past several tabs at once", () => {
    const slots = strip(100, 100, 100, 100);
    expect(landingIndex(slots, 0, slots[3]!.left)).toBe(3);
    expect(landingIndex(slots, 3, slots[0]!.left)).toBe(0);
    expect(landingIndex(slots, 3, slots[1]!.left + 10)).toBe(1);
  });
});
