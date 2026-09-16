import type {
  AppState,
  HostCommand,
  HttpRequest,
  LoadedState,
  PlatformBridge,
  SaveRequestInput,
  SendOutcome,
} from "@api-client/ui";

import { getVsCodeApi } from "./vscodeApi";

/**
 * The VS Code bridge.
 *
 * The webview never opens a socket. Everything is an RPC to the extension host,
 * which forwards it to the Rust sidecar, so a request from here and a request
 * from the desktop app take exactly the same path.
 */
class VsCodeBridge implements PlatformBridge {
  readonly platform = "vscode";

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

  loadState(): Promise<LoadedState> {
    return this.call<LoadedState>("loadState", {});
  }

  saveState(state: AppState): Promise<AppState> {
    return this.call<AppState>("saveState", { state });
  }

  saveRequest(input: SaveRequestInput): Promise<AppState> {
    return this.call<AppState>("saveRequest", input);
  }

  deleteSavedRequest(savedRequestId: string): Promise<AppState> {
    return this.call<AppState>("deleteSavedRequest", { savedRequestId });
  }

  clearHistory(): Promise<AppState> {
    return this.call<AppState>("clearHistory", {});
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
      const event = (data as { event?: string }).event;
      if (event === "newRequest") {
        for (const listener of this.listeners) listener("newRequest");
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
