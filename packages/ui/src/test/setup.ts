import "@testing-library/jest-dom/vitest";

// jsdom has no layout, and CodeMirror asks for measurements on mount.
if (!globalThis.ResizeObserver) {
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
}

if (!Range.prototype.getClientRects) {
  Range.prototype.getClientRects = () => Object.assign([], { item: () => null });
  Range.prototype.getBoundingClientRect = () => new DOMRect();
}
