import type {
  AppState,
  CodeTarget,
  EditorHost,
  FilePickRequest,
  HostCommand,
  HttpRequest,
  LoadedState,
  PlatformBridge,
  SavedRequest,
  SendOutcome,
} from "@api-client/ui";

import { getVsCodeApi } from "./vscodeApi";

/**
 * The VS Code bridge.
 *
 * The webview never opens a socket. Everything is an RPC to the extension host,
 * which forwards it to the sidecar, so a request from here and a request
 * from the desktop app take exactly the same path.
 *
 * Each editor tab is one of these, holding one request. Saved requests and
 * history are in the side bar and kept by the extension host, so the parts of
 * the bridge that manage them in the app are refused.
 */
class VsCodeBridge implements PlatformBridge {
  readonly platform = "vscode";

  readonly editor: EditorHost = {
    saveRequest: (request, name, savedRequestId) =>
      this.call<SavedRequest | null>("saveRequest", { request, name, savedRequestId }),
  };

  private readonly api = getVsCodeApi();
  private nextId = 0;
  private readonly pending = new Map<
    string,
    { resolve: (value: unknown) => void; reject: (error: Error) => void }
  >();
  private readonly listeners = new Set<(command: HostCommand) => void>();

  constructor() {
    window.addEventListener("message", (event: MessageEvent<unknown>) =>
      this.onMessage(event.data),
    );
  }

  sendRequest(request: HttpRequest): Promise<SendOutcome> {
    return this.call<SendOutcome>("sendRequest", { request });
  }

  async cancelRequest(requestId: string): Promise<void> {
    await this.call<null>("cancelRequest", { requestId });
  }

  generateCode(request: HttpRequest, target: CodeTarget): Promise<string> {
    return this.call<string>("generateCode", { request, target });
  }

  pickFile(request: FilePickRequest): Promise<string | null> {
    return this.call<string | null>("pickFile", request);
  }

  async loadState(): Promise<LoadedState> {
    const loaded = await this.call<LoadedState>("loadState", {});
    // All VS Code keeps of the tab is which one it was; the tab itself is in
    // the state file, where its secrets are encrypted.
    this.api.setState({ tabId: loaded.state.activeTabId });
    return loaded;
  }

  saveState(state: AppState): Promise<AppState> {
    return this.call<AppState>("saveState", { state });
  }

  saveRequest(): Promise<AppState> {
    return Promise.reject(new Error("Requests are saved through the editor in VS Code."));
  }

  deleteSavedRequest(): Promise<AppState> {
    return Promise.reject(new Error("Saved requests are deleted from the side bar in VS Code."));
  }

  clearHistory(): Promise<AppState> {
    return Promise.reject(new Error("History is cleared from the side bar in VS Code."));
  }

  subscribe(listener: (command: HostCommand) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  private call<T>(method: string, params: unknown): Promise<T> {
    const id = `w${++this.nextId}`;
    return new Promise<T>((resolve, reject) => {
      this.pending.set(id, { resolve: resolve as (value: unknown) => void, reject });
      this.api.postMessage({ kind: "request", id, method, params });
    });
  }

  private onMessage(data: unknown): void {
    if (typeof data !== "object" || data === null) return;
    const message = data as { kind?: string };

    if (message.kind === "event") {
      const { event, payload } = data as { event?: string; payload?: unknown };
      if (event === "shared") {
        const command = { type: "shared", ...(payload as object) } as HostCommand;
        for (const listener of this.listeners) listener(command);
      }
      return;
    }

    if (message.kind !== "response") return;
    const response = data as
      | { kind: "response"; id: string; ok: true; result: unknown }
      | { kind: "response"; id: string; ok: false; error: string };

    const waiting = this.pending.get(response.id);
    if (!waiting) return;
    this.pending.delete(response.id);

    if (response.ok) waiting.resolve(response.result);
    else waiting.reject(new Error(response.error));
  }
}

export const vscodeBridge: PlatformBridge = new VsCodeBridge();
