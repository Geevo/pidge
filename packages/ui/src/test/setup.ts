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

// This jsdom has no matchMedia, and the theme asks the desktop what it prefers.
if (!window.matchMedia) {
  window.matchMedia = (query: string) => ({
    media: query,
    matches: false,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    addListener: () => undefined,
    removeListener: () => undefined,
    onchange: null,
    dispatchEvent: () => false,
  });
}
