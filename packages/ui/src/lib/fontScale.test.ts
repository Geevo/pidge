import { describe, expect, it } from "vitest";

import { DEFAULT_FONT_SCALE, FONT_SCALES, clampFontScale, stepFontScale } from "./fontScale";

describe("clampFontScale", () => {
  it("leaves a scale from the ladder alone", () => {
    for (const scale of FONT_SCALES) expect(clampFontScale(scale)).toBe(scale);
  });

  it("holds a hand-edited state file inside the offered range", () => {
    expect(clampFontScale(5)).toBe(Math.min(...FONT_SCALES));
    expect(clampFontScale(10_000)).toBe(Math.max(...FONT_SCALES));
  });

  it("falls back to the design when the stored value is not a usable number", () => {
    expect(clampFontScale(Number.NaN)).toBe(DEFAULT_FONT_SCALE);
    expect(clampFontScale(Number.POSITIVE_INFINITY)).toBe(DEFAULT_FONT_SCALE);
  });
});

describe("stepFontScale", () => {
  it("walks up and down the ladder one rung at a time", () => {
    expect(stepFontScale(100, 1)).toBe(110);
    expect(stepFontScale(110, 1)).toBe(125);
    expect(stepFontScale(125, -1)).toBe(110);
    expect(stepFontScale(100, -1)).toBe(90);
  });

  it("stops at each end rather than wrapping round", () => {
    const smallest = Math.min(...FONT_SCALES);
    const largest = Math.max(...FONT_SCALES);
    expect(stepFontScale(smallest, -1)).toBe(smallest);
    expect(stepFontScale(largest, 1)).toBe(largest);
  });

  it("moves off a value that is not on the ladder", () => {
    // A file written by hand, or by a version with a different ladder: one
    // keystroke still has to be one visible change.
    expect(stepFontScale(117, 1)).toBe(125);
    expect(stepFontScale(117, -1)).toBe(110);
  });
});
