import type { AppState, CodeTarget, HttpRequest, SavedRequest, ScratchTab } from "@api-client/ui";
import * as vscode from "vscode";

import { webviewHtml, webviewOptions } from "./html";
import { type WebviewEvent, type WebviewResponse, isWebviewRequest } from "./protocol";
import type { Sidecar } from "./sidecar";
import { type Shared, type Store, describe, newTab } from "./store";

export interface Services {
  readonly context: vscode.ExtensionContext;
  readonly sidecar: Sidecar;
  readonly store: Store;
}

/** What VS Code keeps for a tab across a restart: which tab it was, and nothing more. */
interface PanelState {
  readonly tabId?: string;
}

/**
 * One request, in one editor tab.
 *
 * The tab strip, closing and reordering are VS Code's, and the tab itself is
 * kept in the state file, where secrets are encrypted, rather than in what
 * VS Code stores for a webview. The webview has no network access of its own:
 * every request is an RPC to here, which forwards it to the sidecar.
 */
export class RequestPanel {
  static readonly viewType = "pidge.request";

  private static readonly open = new Set<RequestPanel>();
  private static lastActive: RequestPanel | undefined;

  private readonly disposables: vscode.Disposable[] = [];
  /** What this webview last had of the shared state, to tell its own changes apart. */
  private seen: { settings: string; environments: string } | null = null;
  /** The saved request this tab is, so opening that one again comes back here. */
  private savedRequestId: string | null;

  /** Opens a new tab for the request, or a blank one. */
  static create(services: Services, request?: HttpRequest, saved?: SavedRequest): RequestPanel {
    const tab = newTab(request, saved);
    const panel = vscode.window.createWebviewPanel(
      RequestPanel.viewType,
      title(tab),
      vscode.ViewColumn.Active,
      RequestPanel.options(services.context),
    );
    return new RequestPanel(panel, services, tab.id, tab);
  }

  /** A tab VS Code kept from last time. */
  static revive(services: Services, panel: vscode.WebviewPanel, state: unknown): RequestPanel {
    panel.webview.options = RequestPanel.options(services.context);
    const tabId = (state as PanelState | undefined)?.tabId;
    if (tabId) return new RequestPanel(panel, services, tabId, null);
    const tab = newTab();
    return new RequestPanel(panel, services, tab.id, tab);
  }

  /** Brings the tab that has this saved request open to the front, if there is one. */
  static revealSaved(savedRequestId: string): boolean {
    for (const each of RequestPanel.open) {
      if (each.savedRequestId === savedRequestId) {
        each.panel.reveal();
        return true;
      }
    }
    return false;
  }

  /** The tab used last, for "pidge: Open". */
  static revealLast(): boolean {
    const last = RequestPanel.lastActive ?? [...RequestPanel.open].at(-1);
    last?.panel.reveal();
    return last !== undefined;
  }

  private static options(
    context: vscode.ExtensionContext,
  ): vscode.WebviewPanelOptions & vscode.WebviewOptions {
    return { ...webviewOptions(context.extensionUri), retainContextWhenHidden: true };
  }

  /**
   * `initial` is the tab when it is new. Revived tabs are looked up in the
   * state file once it has loaded, and start blank if it no longer has them.
   */
  private constructor(
    private readonly panel: vscode.WebviewPanel,
    private readonly services: Services,
    private readonly tabId: string,
    private initial: ScratchTab | null,
  ) {
    this.savedRequestId = initial?.savedRequestId ?? null;
    RequestPanel.open.add(this);
    RequestPanel.lastActive = this;

    panel.iconPath = vscode.Uri.joinPath(services.context.extensionUri, "media", "pidge.png");
    panel.webview.html = webviewHtml(panel.webview, services.context.extensionUri, "webview.js");

    this.disposables.push(
      panel.webview.onDidReceiveMessage((message: unknown) => {
        void this.onMessage(message);
      }),
      panel.onDidChangeViewState(({ webviewPanel }) => {
        if (webviewPanel.active) RequestPanel.lastActive = this;
      }),
      services.store.onDidChangeShared(({ shared, from }) => {
        if (from !== this) this.share(shared);
      }),
      panel.onDidDispose(() => this.dispose()),
    );
  }

  private post(event: WebviewEvent): void {
    void this.panel.webview.postMessage(event);
  }

  /** Tells the webview what another tab changed. */
  private share(shared: Shared): void {
    if (!this.seen) return;
    this.seen = {
      settings: JSON.stringify(shared.settings),
      environments: JSON.stringify([shared.environments, shared.activeEnvironmentId]),
    };
    this.post({ kind: "event", event: "shared", payload: shared });
  }

  private async onMessage(message: unknown): Promise<void> {
    if (!isWebviewRequest(message)) return;

    try {
      const result = await this.dispatch(message.method, message.params);
      this.reply({ kind: "response", id: message.id, ok: true, result });
    } catch (error) {
      this.reply({
        kind: "response",
        id: message.id,
        ok: false,
        error: error instanceof Error ? error.message : String(error),
      });
    }
  }

  /** Each case is one thing the webview asks for. The engine owns the behaviour. */
  private async dispatch(method: string, params: unknown): Promise<unknown> {
    const { sidecar, store } = this.services;

    switch (method) {
      case "sendRequest": {
        const { request } = params as { request: HttpRequest };
        const reply = await sidecar.call(
          { type: "sendRequest", request, variables: {} },
          request.id,
        );
        if (reply.type !== "requestComplete" && reply.type !== "requestError") {
          throw new Error(describe(reply));
        }
        if (reply.historyEntry) store.recordHistory(reply.historyEntry);
        return reply.type === "requestComplete"
          ? { response: reply.response, error: null, historyEntry: reply.historyEntry }
          : { response: null, error: reply.error, historyEntry: reply.historyEntry };
      }

      case "generateCode": {
        const { request, target } = params as { request: HttpRequest; target: CodeTarget };
        const reply = await sidecar.call({
          type: "generateCode",
          request,
          target,
          variables: {},
        });
        if (reply.type !== "codeGenerated") throw new Error(describe(reply));
        if (reply.code === null) throw new Error(reply.error?.message ?? "No code was generated.");
        return reply.code;
      }

      case "cancelRequest": {
        const { requestId } = params as { requestId: string };
        sidecar.notify({ type: "cancelRequest", requestId });
        return null;
      }

      case "loadState": {
        const loaded = await store.load();
        const tab = this.initial ?? store.tab(this.tabId) ?? { ...newTab(), id: this.tabId };
        this.initial = null;
        this.savedRequestId = tab.savedRequestId;
        this.panel.title = title(tab);
        const state = store.viewFor(tab);
        this.seen = {
          settings: JSON.stringify(state.settings),
          environments: JSON.stringify([state.environments, state.activeEnvironmentId]),
        };
        return {
          state,
          recovery: store.takeRecovery(),
          storagePath: loaded.storagePath,
          version: loaded.version,
        };
      }

      case "saveState": {
        const { state } = params as { state: AppState };
        const tab = state.tabs.find((each) => each.id === state.activeTabId) ?? state.tabs[0];
        if (!tab) return state;
        this.savedRequestId = tab.savedRequestId;
        this.panel.title = title(tab);

        // Only what this tab changed: the rest may be older than another tab's change.
        const settings = JSON.stringify(state.settings);
        const environments = JSON.stringify([state.environments, state.activeEnvironmentId]);
        const changed: Partial<Shared> = {};
        if (this.seen && settings !== this.seen.settings) {
          Object.assign(changed, { settings: state.settings });
        }
        if (this.seen && environments !== this.seen.environments) {
          Object.assign(changed, {
            environments: state.environments,
            activeEnvironmentId: state.activeEnvironmentId,
          });
        }
        this.seen = { settings, environments };

        await store.update(tab, changed, this);
        return store.viewFor(tab);
      }

      case "saveRequest": {
        const { request, name, savedRequestId } = params as {
          request: HttpRequest;
          name: string | null;
          savedRequestId: string | null;
        };
        return this.saveRequest(request, name, savedRequestId);
      }

      case "pickFile": {
        const request = params as {
          title: string;
          filters?: { name: string; extensions: string[] }[];
        };
        const chosen = await vscode.window.showOpenDialog({
          title: request.title,
          canSelectMany: false,
          openLabel: "Select",
          filters: Object.fromEntries(
            (request.filters ?? []).map((filter) => [filter.name, filter.extensions]),
          ),
        });
        return chosen?.[0]?.fsPath ?? null;
      }

      default:
        throw new Error(`Unknown request: ${method}`);
    }
  }

  /**
   * Saves the tab's request, asking for its name each time as the desktop app
   * does, which is also how a saved request is renamed.
   */
  private async saveRequest(
    request: HttpRequest,
    name: string | null,
    savedRequestId: string | null,
  ): Promise<SavedRequest | null> {
    const chosen = await vscode.window.showInputBox({
      title: "Save request",
      prompt: "Name",
      value: name ?? (request.url.trim() || "Untitled request"),
      validateInput: (value) => (value.trim() === "" ? "A saved request needs a name." : null),
    });
    if (chosen === undefined) return null;
    const saved = await this.services.store.saveRequest(savedRequestId, chosen.trim(), request);
    this.savedRequestId = saved.id;
    return saved;
  }

  private reply(response: WebviewResponse): void {
    void this.panel.webview.postMessage(response);
  }

  private dispose(): void {
    RequestPanel.open.delete(this);
    if (RequestPanel.lastActive === this) RequestPanel.lastActive = undefined;
    for (const disposable of this.disposables) disposable.dispose();
    this.services.store.forget(this.tabId);
  }
}

/** The tab's title: its name, or where it goes, the way the desktop app titles tabs. */
export function title(tab: ScratchTab): string {
  return tab.name ?? requestLabel(tab.request.url, tab.request.method);
}

/** Mirrors `requestLabel` in the UI. */
export function requestLabel(url: string, method: string): string {
  const trimmed = url.trim();
  if (trimmed === "") return "New request";

  try {
    const parsed = new URL(trimmed.includes("://") ? trimmed : `http://${trimmed}`);
    const path = parsed.pathname === "/" ? "" : parsed.pathname;
    return `${parsed.host}${path}` || method;
  } catch {
    return trimmed;
  }
}
