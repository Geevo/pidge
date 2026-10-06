import type {
  AppState,
  CodeTarget,
  ExportInput,
  FilePickRequest,
  ImportOutcome,
  HttpRequest,
  LoadedState,
  PlatformBridge,
  SaveRequestInput,
  SendOutcome,
  WindowButtonStyle,
  WindowControls,
} from "@api-client/ui";

/**
 * What Photino puts on `window.external`: one string each way. Everything the
 * page and the host say to each other is a JSON object in one of those strings.
 */
interface HostChannel {
  sendMessage(message: string): void;
  receiveMessage(listener: (message: string) => void): void;
}

/** A reply to a call, matched to it by `id`. */
interface Reply {
  readonly id: number;
  readonly ok: boolean;
  readonly value?: unknown;
  readonly error?: unknown;
}

/** Something the host says unprompted, such as the window having been resized. */
interface HostEvent {
  readonly event: string;
}

function channel(): HostChannel | null {
  const external = (window as unknown as { external?: Partial<HostChannel> }).external;
  return typeof external?.sendMessage === "function" &&
    typeof external.receiveMessage === "function"
    ? (external as HostChannel)
    : null;
}

let nextId = 1;
const pending = new Map<
  number,
  { resolve: (value: unknown) => void; reject: (error: unknown) => void }
>();
const listeners = new Map<string, Set<() => void>>();
let listening = false;

function listen(host: HostChannel) {
  if (listening) return;
  listening = true;
  host.receiveMessage((raw) => {
    let message: Reply | HostEvent;
    try {
      message = JSON.parse(raw) as Reply | HostEvent;
    } catch {
      return;
    }
    if ("event" in message) {
      listeners.get(message.event)?.forEach((listener) => listener());
      return;
    }
    const call = pending.get(message.id);
    if (!call) return;
    pending.delete(message.id);
    if (message.ok) call.resolve(message.value);
    // Rejected with what the host sent, as it was: a RequestError where the
    // command has one, a sentence otherwise. The UI knows both.
    else call.reject(message.error);
  });
}

/**
 * Calls a host command and resolves with its answer.
 *
 * Outside the desktop window — `vite dev` in a plain browser — there is no
 * host, and every call rejects rather than throwing, so nothing that awaits
 * one is taken down by it.
 */
function invoke<T>(command: string, args: Record<string, unknown> = {}): Promise<T> {
  const host = channel();
  if (!host) return Promise.reject(new Error("The desktop host is not available."));
  listen(host);
  const id = nextId++;
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve: resolve as (value: unknown) => void, reject });
    try {
      host.sendMessage(JSON.stringify({ id, command, args }));
    } catch (error) {
      pending.delete(id);
      reject(error instanceof Error ? error : new Error(String(error)));
    }
  });
}

/** Tells the host something that needs no answer. */
function notify(command: string, args: Record<string, unknown> = {}) {
  channel()?.sendMessage(JSON.stringify({ command, args }));
}

/**
 * The title bar is the leftover strip of the tab bar, marked with
 * `data-drag-region`. A press there moves the window and a double press
 * maximises it, as a native title bar would.
 *
 * Linux is the exception: GTK only starts a move from the native press itself,
 * so there the host is told where the strips are and lets the window manager
 * do the rest.
 */
function installDragRegions() {
  const attribute = "data-drag-region";

  document.addEventListener("mousedown", (event) => {
    const target = event.target;
    if (event.button !== 0 || !(target instanceof HTMLElement)) return;
    const value = target.getAttribute(attribute);
    if (value === null || value === "false") return;
    event.preventDefault();
    if (event.detail === 2) notify("window_toggle_maximize");
    else notify("window_start_drag");
  });

  let queued = false;
  let last = "";
  const report = () => {
    queued = false;
    const regions = [...document.querySelectorAll<HTMLElement>(`[${attribute}]`)]
      .filter((element) => element.getAttribute(attribute) !== "false")
      .map((element) => element.getBoundingClientRect())
      .filter((rect) => rect.width > 0 && rect.height > 0)
      .map((rect) => ({
        x: Math.round(rect.left),
        y: Math.round(rect.top),
        width: Math.round(rect.width),
        height: Math.round(rect.height),
      }));
    const encoded = JSON.stringify(regions);
    if (encoded === last) return;
    last = encoded;
    notify("window_drag_regions", { regions });
  };
  const schedule = () => {
    if (queued) return;
    queued = true;
    requestAnimationFrame(report);
  };

  const resized = new ResizeObserver(schedule);
  const observe = () => {
    document
      .querySelectorAll<HTMLElement>(`[${attribute}]`)
      .forEach((element) => resized.observe(element));
    schedule();
  };
  new MutationObserver(observe).observe(document.body, {
    subtree: true,
    childList: true,
    attributes: true,
    attributeFilter: [attribute],
  });
  window.addEventListener("resize", schedule);
  observe();
}

/**
 * The webview's own right-click menu, kept only where Copy and Paste mean
 * something: in a text field, or over selected text.
 *
 * Elsewhere it offers Back, Reload, Save As, Print and the like, none of which
 * mean anything in an app, so no menu opens at all. The native menu is kept
 * rather than replaced with one drawn in the page, because a native Paste reads
 * the clipboard directly, where a script would have to ask for permission.
 * Development builds keep the whole menu, for Inspect.
 */
function trimContextMenu() {
  if (import.meta.env.DEV) return;
  document.addEventListener("contextmenu", (event) => {
    if (event.defaultPrevented) return;
    const target = event.target;
    const editable =
      target instanceof HTMLElement &&
      (target.isContentEditable || target.closest("input, textarea, [contenteditable]") !== null);
    const selected = (window.getSelection()?.toString() ?? "").length > 0;
    if (!editable && !selected) event.preventDefault();
  });
}

/**
 * The window is undecorated, so the app supplies the title bar itself. Which
 * buttons is the host's to say: only it can see the operating system and the
 * desktop session behind it. Resizing is the window's own on every platform.
 */
function windowControls(buttons: WindowButtonStyle): WindowControls {
  return {
    buttons,
    minimize: () => invoke<void>("window_minimize"),
    toggleMaximize: () => invoke<void>("window_toggle_maximize"),
    close: () => invoke<void>("window_close"),
    isMaximized: () => invoke<boolean>("window_is_maximized"),
    onResized: (listener) => {
      const host = channel();
      if (host) listen(host);
      let set = listeners.get("resized");
      if (!set) listeners.set("resized", (set = new Set()));
      const once = () => listener();
      set.add(once);
      return Promise.resolve(() => {
        set.delete(once);
      });
    },
  };
}

/**
 * Asks which title-bar buttons to draw, before anything is drawn.
 *
 * Swapping them after the first paint would be visible, and this is one round
 * trip behind the boot screen. Outside the desktop window there is no host to
 * ask and no window to control either, so the answer only has to be harmless.
 */
async function buttonStyle(): Promise<WindowButtonStyle> {
  try {
    return await invoke<WindowButtonStyle>("window_buttons");
  } catch {
    return "windows";
  }
}

/**
 * The desktop bridge.
 *
 * Every method is one call to the host and nothing else. All the behaviour
 * lives in Pidge.Session, which the VS Code sidecar drives through the same API.
 */
const desktopBridge = {
  platform: "desktop",

  pickFile(request: FilePickRequest): Promise<string | null> {
    return invoke<string | null>("pick_file", {
      title: request.title,
      filters: request.filters?.map((filter) => ({
        name: filter.name,
        extensions: [...filter.extensions],
      })),
    });
  },

  sendRequest(request: HttpRequest): Promise<SendOutcome> {
    return invoke<SendOutcome>("send_http_request", { request });
  },

  // The file dialog is opened by the host itself, so no path crosses here.
  importSavedRequests(): Promise<ImportOutcome | null> {
    return invoke<ImportOutcome | null>("import_saved_requests");
  },

  // The save dialog is opened by the host itself, so no path crosses here.
  exportSavedRequests(input: ExportInput): Promise<string | null> {
    return invoke<string | null>("export_saved_requests", {
      savedRequestIds: input.savedRequestIds,
      format: input.format,
      includeSecrets: input.includeSecrets,
      fileName: input.fileName,
    });
  },

  async cancelRequest(requestId: string): Promise<void> {
    await invoke<boolean>("cancel_http_request", { requestId });
  },

  generateCode(request: HttpRequest, target: CodeTarget): Promise<string> {
    return invoke<string>("generate_code", { request, target });
  },

  loadState(): Promise<LoadedState> {
    return invoke<LoadedState>("load_state");
  },

  saveState(state: AppState): Promise<AppState> {
    return invoke<AppState>("save_state", { state });
  },

  saveRequest(input: SaveRequestInput): Promise<AppState> {
    return invoke<AppState>("save_request", {
      savedRequestId: input.savedRequestId,
      name: input.name,
      request: input.request,
    });
  },

  deleteSavedRequest(savedRequestId: string): Promise<AppState> {
    return invoke<AppState>("delete_saved_request", { savedRequestId });
  },

  clearHistory(): Promise<AppState> {
    return invoke<AppState>("clear_history");
  },
};

/** The bridge, once the host has said which desktop it is running on. */
export async function createBridge(): Promise<PlatformBridge> {
  if (channel()) {
    installDragRegions();
    trimContextMenu();
  }
  return { ...desktopBridge, window: windowControls(await buttonStyle()) };
}
