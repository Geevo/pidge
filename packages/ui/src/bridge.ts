import type { AppState, HistoryEntry, HttpRequest, HttpResponse, RequestError } from "./types";

/**
 * Everything the UI needs from its host.
 *
 * Components never ask which platform they are on; they call the bridge. The
 * desktop implementation forwards to Tauri commands, the VS Code one posts
 * messages to the extension host, and the tests use a fake.
 */
export interface PlatformBridge {
  /**
   * Sends a request. A 4xx, a 5xx, a DNS failure and a timeout are all normal
   * outcomes, so this rejects only when the host itself is broken.
   */
  sendRequest(request: HttpRequest): Promise<SendOutcome>;
  /** Aborts an in-flight request by its id. Safe to call when nothing is running. */
  cancelRequest(requestId: string): Promise<void>;
  loadState(): Promise<LoadedState>;
  saveState(state: AppState): Promise<AppState>;
  saveRequest(input: SaveRequestInput): Promise<AppState>;
  deleteSavedRequest(savedRequestId: string): Promise<AppState>;
  clearHistory(): Promise<AppState>;
  /**
   * Optional: commands the host initiates, such as a Command Palette entry.
   * Returns an unsubscribe function. Hosts with no such commands omit it.
   */
  subscribe?(listener: (command: HostCommand) => void): () => void;
  /** Present only when this host has no native window frame of its own. */
  readonly window?: WindowControls;
  /** Name shown in diagnostics, e.g. "desktop" or "vscode". */
  readonly platform: string;
}

/** Commands a host can push into the UI. */
export type HostCommand = "newRequest";

/** Which edge or corner a resize drag started from. */
export type ResizeEdge =
  "North" | "NorthEast" | "East" | "SouthEast" | "South" | "SouthWest" | "West" | "NorthWest";

/**
 * Present only when the host expects the app to draw its own title bar.
 *
 * The desktop window is undecorated because GTK's own header is far taller than
 * the platform's; VS Code supplies none of this, so the whole object is absent
 * there and no window chrome is rendered.
 */
export interface WindowControls {
  minimize(): Promise<void>;
  toggleMaximize(): Promise<void>;
  close(): Promise<void>;
  isMaximized(): Promise<boolean>;
  startDragging(): Promise<void>;
  /** Undecorated windows get no resize edges for free; these supply them. */
  startResizing(edge: ResizeEdge): Promise<void>;
  /**
   * Tells the host which way the palette leans, or `null` to follow the system.
   *
   * The menus the webview itself puts up — the right-click menu — are the
   * platform's and take no notice of CSS. This is the only handle on them. It
   * is a hint rather than a guarantee: on Linux it sets the GTK dark
   * preference, which a theme shipping its dark variant separately ignores.
   */
  setTheme(theme: "light" | "dark" | null): Promise<void>;
}

/**
 * The result of one send, mirroring `api_client_session::SendOutcome`.
 *
 * `historyEntry` is the row the engine recorded, so the history panel stays
 * live without reloading the whole state. It is absent for a cancelled send.
 */
export interface SendOutcome {
  readonly response: HttpResponse | null;
  readonly error: RequestError | null;
  readonly historyEntry: HistoryEntry | null;
}

export interface LoadedState {
  readonly state: AppState;
  /** Set when the state file could not be read and defaults were used. */
  readonly recovery: string | null;
  readonly storagePath: string;
}

export interface SaveRequestInput {
  readonly savedRequestId: string | null;
  readonly name: string;
  readonly request: HttpRequest;
}

/** A request failure that carries the engine's normalized error. */
export class BridgeRequestError extends Error {
  constructor(readonly requestError: RequestError) {
    super(requestError.message);
    this.name = "BridgeRequestError";
  }
}

export function isRequestError(value: unknown): value is RequestError {
  return (
    typeof value === "object" &&
    value !== null &&
    "kind" in value &&
    "message" in value &&
    typeof (value as RequestError).message === "string"
  );
}

/** Normalizes anything thrown by a bridge into a `RequestError`. */
export function toRequestError(value: unknown): RequestError {
  if (value instanceof BridgeRequestError) return value.requestError;
  if (isRequestError(value)) return value;
  return {
    kind: "other",
    message: value instanceof Error ? value.message : String(value),
    detail: null,
  };
}
