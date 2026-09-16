/** The handle VS Code injects into a webview exactly once. */
export interface VsCodeApi {
  postMessage(message: unknown): void;
  getState(): unknown;
  setState(state: unknown): void;
}

declare global {
  function acquireVsCodeApi(): VsCodeApi;
}

let cached: VsCodeApi | null = null;

/** Calling `acquireVsCodeApi` twice throws, so it is acquired once and shared. */
export function getVsCodeApi(): VsCodeApi {
  cached ??= acquireVsCodeApi();
  return cached;
}
