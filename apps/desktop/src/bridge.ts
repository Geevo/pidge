import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { open } from "@tauri-apps/plugin-dialog";
import type {
  AppState,
  FilePickRequest,
  HttpRequest,
  LoadedState,
  PlatformBridge,
  ResizeEdge,
  SaveRequestInput,
  SendOutcome,
  WindowButtonStyle,
  WindowControls,
} from "@api-client/ui";

/**
 * Turns a synchronous throw into a rejected promise.
 *
 * `getCurrentWindow()` throws outright when the Tauri internals are missing —
 * running the frontend in a plain browser, for instance. Called from an effect
 * that would unmount the whole app, so window chrome is kept unable to take the
 * application down with it.
 */
function attempt<T>(action: () => Promise<T>): Promise<T> {
  try {
    return action();
  } catch (error) {
    return Promise.reject(error instanceof Error ? error : new Error(String(error)));
  }
}

/**
 * The window is undecorated, because GTK draws a header far taller than the
 * platform's own, so the app supplies the title bar and resize edges itself.
 *
 * Which buttons those are is the host's to say: only the Rust side can see the
 * operating system and the desktop session behind it.
 */
function windowControls(buttons: WindowButtonStyle): WindowControls {
  return {
    buttons,
    minimize: () => attempt(() => getCurrentWindow().minimize()),
    toggleMaximize: () => attempt(() => getCurrentWindow().toggleMaximize()),
    close: () => attempt(() => getCurrentWindow().close()),
    isMaximized: () => attempt(() => getCurrentWindow().isMaximized()),
    onResized: (listener) => attempt(() => getCurrentWindow().onResized(() => listener())),
    startResizing: (edge: ResizeEdge) => attempt(() => getCurrentWindow().startResizeDragging(edge)),
  };
}

/**
 * Asks which title-bar buttons to draw, before anything is drawn.
 *
 * Swapping them after the first paint would be visible, and this is one
 * `invoke` behind the boot screen. Outside Tauri — `vite dev` in a browser —
 * there is no host to ask and no window to control either, so the answer only
 * has to be harmless.
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
 * Every method is one `invoke` and nothing else. All the behaviour lives in
 * `api-client-session`, which the VS Code sidecar drives through the same API.
 */
const tauriBridge = {
  platform: "desktop",

  async pickFile(request: FilePickRequest): Promise<string | null> {
    const chosen = await open({
      title: request.title,
      multiple: false,
      directory: false,
      filters: request.filters?.map((filter) => ({
        name: filter.name,
        extensions: [...filter.extensions],
      })),
    });
    // The plugin resolves with the path, or null when it is dismissed.
    return typeof chosen === "string" ? chosen : null;
  },

  sendRequest(request: HttpRequest): Promise<SendOutcome> {
    return invoke<SendOutcome>("send_http_request", { request });
  },

  async cancelRequest(requestId: string): Promise<void> {
    await invoke<boolean>("cancel_http_request", { requestId });
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
  return { ...tauriBridge, window: windowControls(await buttonStyle()) };
}
