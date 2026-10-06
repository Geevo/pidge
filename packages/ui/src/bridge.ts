import type {
  AppState,
  CodeTarget,
  Environment,
  ExportFormat,
  ImportOutcome,
  HistoryEntry,
  HttpRequest,
  HttpResponse,
  RequestError,
  SavedRequest,
  Settings,
} from "./types";

/**
 * Everything the UI needs from its host.
 *
 * Components never ask which platform they are on; they call the bridge. The
 * desktop implementation messages its native host, the VS Code one posts
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
  /**
   * Writes the request out as code for another client, without sending it.
   *
   * Nothing is generated here: the host resolves the environment variables and
   * reads the settings the snippet has to carry, so the code and the Send
   * button describe the same request. It rejects with a `RequestError` for a
   * URL that will not parse or a variable with no value.
   */
  generateCode(request: HttpRequest, target: CodeTarget): Promise<string>;
  loadState(): Promise<LoadedState>;
  saveState(state: AppState): Promise<AppState>;
  saveRequest(input: SaveRequestInput): Promise<AppState>;
  deleteSavedRequest(savedRequestId: string): Promise<AppState>;
  clearHistory(): Promise<AppState>;
  /**
   * Optional: opens the platform's own file chooser and resolves with the path,
   * or `null` if it was dismissed. Absent on a host that cannot show one, and
   * the Browse buttons are absent with it — typing the path still works.
   */
  pickFile?(request: FilePickRequest): Promise<string | null>;
  /**
   * Optional: asks where to save, then writes the chosen saved requests there.
   * Resolves with the path written, or `null` if the dialog was dismissed.
   *
   * The host writes the contents, so both platforms produce the same file, and
   * the host opens the dialog, so the path is always one the user just chose.
   * Absent on a host that cannot save files, and the Export button with it.
   */
  exportSavedRequests?(input: ExportInput): Promise<string | null>;
  /**
   * Optional: asks for a file, then adds the saved requests in it — a JSON
   * export or a `.http` file. Resolves with what was added, or `null` if the
   * dialog was dismissed; rejects with the reason a file was refused, in
   * which case nothing changed. The host picks and reads the file itself.
   */
  importSavedRequests?(): Promise<ImportOutcome | null>;
  /**
   * Optional: commands the host initiates, such as a change made in another
   * of its tabs. Returns an unsubscribe function. Hosts with no such commands
   * omit it.
   */
  subscribe?(listener: (command: HostCommand) => void): () => void;
  /**
   * Present when the app is one tab of an editor that has tabs, a side bar,
   * colours and file dialogs of its own: VS Code. The app then shows a single
   * request and leaves the rest to the editor. There is no tab strip, no
   * history or saved-request drawer and no palette, and Save asks for the
   * name through here, in the editor's own prompt.
   */
  readonly editor?: EditorHost;
  /** Present only when this host has no native window frame of its own. */
  readonly window?: WindowControls;
  /** Name shown in diagnostics, e.g. "desktop" or "vscode". */
  readonly platform: string;
}

/** What to put in the title bar of the chooser, and what to show in it. */
export interface FilePickRequest {
  readonly title: string;
  readonly filters?: readonly FilePickFilter[];
}

export interface FilePickFilter {
  readonly name: string;
  /** Without the dot, as both hosts expect. */
  readonly extensions: readonly string[];
}

export interface ExportInput {
  readonly savedRequestIds: readonly string[];
  readonly format: ExportFormat;
  /** Unless set, secrets are written as `{{variables}}` named for them. */
  readonly includeSecrets: boolean;
  /** A suggestion for the dialog, with no directory. */
  readonly fileName: string;
}

/**
 * Commands a host can push into the UI.
 *
 * `shared` carries what every tab has in common, after another tab changed
 * it: each editor tab is an app of its own, and they share one state file.
 */
export type HostCommand = {
  readonly type: "shared";
  readonly settings: Settings;
  readonly environments: Environment[];
  readonly activeEnvironmentId: string | null;
};

export interface EditorHost {
  /**
   * Asks for a name, then saves the request as the saved request
   * `savedRequestId`, or as a new one. Resolves with what was saved, or
   * `null` if the user backed out.
   */
  saveRequest(
    request: HttpRequest,
    name: string | null,
    savedRequestId: string | null,
  ): Promise<SavedRequest | null>;
}

/**
 * Which desktop's title-bar buttons to draw.
 *
 * The three disagree about everything: Breeze draws chevrons and a diamond,
 * Adwaita a bar and two rings, Windows a line and a square, and the shape of
 * the button behind the glyph differs as well. The host knows which desktop it
 * is on; the UI only draws what it is told.
 */
export type WindowButtonStyle = "windows" | "kde" | "gnome";

/**
 * Present only when the host expects the app to draw its own title bar.
 *
 * The desktop window is undecorated because GTK's own header is far taller than
 * the platform's; VS Code supplies none of this, so the whole object is absent
 * there and no window chrome is rendered.
 */
export interface WindowControls {
  /** Which desktop's buttons the title bar should draw. */
  readonly buttons: WindowButtonStyle;
  minimize(): Promise<void>;
  toggleMaximize(): Promise<void>;
  close(): Promise<void>;
  isMaximized(): Promise<boolean>;
  /**
   * Calls back whenever the window is resized, which includes being maximised
   * and restored. Resolves with an unsubscribe.
   *
   * Asking after a toggle is not enough: a compositor applies the new state
   * when it is ready, so the answer that comes back is the old one, and the
   * maximise glyph ends up a step behind. It also says nothing about the ways
   * a window is maximised without the button — a double click on the title
   * bar, a keyboard shortcut, a tiling drag.
   */
  onResized(listener: () => void): Promise<() => void>;
}

/**
 * The result of one send, mirroring the session's `SendOutcome`.
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
  /** The host's version, shown in Settings. */
  readonly version: string;
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
