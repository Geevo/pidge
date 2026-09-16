import { invoke } from "@tauri-apps/api/core";
import type {
  AppState,
  HttpRequest,
  LoadedState,
  PlatformBridge,
  SaveRequestInput,
  SendOutcome,
} from "@api-client/ui";

/**
 * The desktop bridge.
 *
 * Every method is one `invoke` and nothing else. All the behaviour lives in
 * `api-client-session`, which the VS Code sidecar drives through the same API.
 */
export const tauriBridge: PlatformBridge = {
  platform: "desktop",

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
