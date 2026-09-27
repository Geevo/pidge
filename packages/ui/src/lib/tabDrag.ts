/**
 * Where one tab sits in the strip, measured when a drag begins. Measured
 * along the strip's whole scrolled length rather than on screen, so the slots
 * still hold while the strip scrolls under a dragged tab.
 */
export interface Slot {
  left: number;
  width: number;
}

/**
 * Where a dragged tab would land if let go with its left edge at `left`: the
 * place whose position it is nearest to.
 *
 * Each place is where the tab would sit in the order that place makes. Before
 * its own, it starts where the tab it displaces starts; after, it ends where
 * that tab ends. So a neighbour steps aside once the dragged tab is halfway
 * over it, and steps back once it is halfway off again, whatever either
 * tab's width. Deciding by whether the dragged tab's middle had passed each
 * neighbour's middle, as measured before the drag, was not symmetric: going
 * out it took covering a neighbour completely, and coming back the neighbour
 * returned the moment the two touched, since it had moved and its measured
 * middle had not.
 *
 * The ends need no special case: pushed as far as it goes, the tab is exactly
 * at the first or the last place.
 */
export function landingIndex(slots: readonly Slot[], from: number, left: number): number {
  const own = slots[from];
  if (!own) return from;

  let nearest = from;
  let distance = Infinity;
  slots.forEach((slot, index) => {
    const start = index <= from ? slot.left : slot.left + slot.width - own.width;
    const away = Math.abs(left - start);
    if (away < distance) {
      nearest = index;
      distance = away;
    }
  });
  return nearest;
}
