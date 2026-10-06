import { randomUUID } from "node:crypto";

import type {
  AppState,
  Environment,
  ExportFormat,
  HistoryEntry,
  HttpRequest,
  ImportOutcome,
  SavedRequest,
  ScratchTab,
  ServerMessage,
  Settings,
} from "@api-client/ui";
import * as vscode from "vscode";

import type { Sidecar } from "./sidecar";

/** What every tab has in common: one tab changing it changes it for all. */
export interface Shared {
  readonly settings: Settings;
  readonly environments: Environment[];
  readonly activeEnvironmentId: string | null;
}

export interface Loaded {
  readonly storagePath: string;
  readonly version: string;
}

/**
 * Closing a tab forgets it after this long rather than at once. When VS Code
 * shuts down it closes every tab on its way out, and those tabs have to come
 * back next time; the store is disposed first, and the forgetting with it.
 */
export const FORGET_DELAY_MS = 2000;

/**
 * The state file, as the editor tabs share it.
 *
 * Each tab is a webview with an app of its own, so the extension host keeps
 * the one copy: the tabs send their changes here, and this writes the whole
 * state through the sidecar and tells the other tabs what changed.
 */
export class Store implements vscode.Disposable {
  private state: AppState | null = null;
  private loading: Promise<Loaded> | null = null;
  private recovery: string | null = null;
  private forgetTimer: ReturnType<typeof setTimeout> | null = null;
  private disposed = false;

  private readonly historyChanged = new vscode.EventEmitter<void>();
  readonly onDidChangeHistory = this.historyChanged.event;

  private readonly savedChanged = new vscode.EventEmitter<void>();
  readonly onDidChangeSaved = this.savedChanged.event;

  private readonly sharedChanged = new vscode.EventEmitter<{ shared: Shared; from: unknown }>();
  /** Fired with whoever made the change, so it is not told about its own. */
  readonly onDidChangeShared = this.sharedChanged.event;

  constructor(private readonly sidecar: Sidecar) {}

  /** Loads the state once; every tab and view waits on the same load. */
  load(): Promise<Loaded> {
    this.loading ??= (async () => {
      const reply = await this.sidecar.call({ type: "loadState" });
      if (reply.type !== "stateLoaded") throw new Error(describe(reply));
      this.state = reply.state;
      this.recovery = reply.recovery;
      return { storagePath: reply.storagePath, version: reply.version };
    })();
    this.loading.catch(() => {
      this.loading = null;
    });
    return this.loading;
  }

  /** A notice about a recovered state file, for the first tab that asks. */
  takeRecovery(): string | null {
    const recovery = this.recovery;
    this.recovery = null;
    return recovery;
  }

  get history(): readonly HistoryEntry[] {
    return this.state?.history ?? [];
  }

  get savedRequests(): readonly SavedRequest[] {
    return this.state?.savedRequests ?? [];
  }

  get shared(): Shared {
    const state = this.current();
    return {
      settings: state.settings,
      environments: state.environments,
      activeEnvironmentId: state.activeEnvironmentId,
    };
  }

  tab(id: string): ScratchTab | undefined {
    return this.state?.tabs.find((tab) => tab.id === id);
  }

  /**
   * The state as one tab sees it: itself, and what all of them share. Saved
   * requests are in the side bar rather than the tab, so they are left out.
   */
  viewFor(tab: ScratchTab): AppState {
    return {
      ...this.current(),
      tabs: [tab],
      activeTabId: tab.id,
      savedRequests: [],
    };
  }

  /** Keeps the tab, and what it changed of the shared state. */
  async update(tab: ScratchTab, shared: Partial<Shared>, from: unknown): Promise<void> {
    const state = this.current();
    const tabs = state.tabs.some((each) => each.id === tab.id)
      ? state.tabs.map((each) => (each.id === tab.id ? tab : each))
      : [...state.tabs, tab];
    this.state = { ...state, ...shared, tabs, activeTabId: tab.id };

    if (Object.keys(shared).length > 0) this.sharedChanged.fire({ shared: this.shared, from });
    await this.persist();
  }

  /** The tab was closed. Written out a moment later; see `FORGET_DELAY_MS`. */
  forget(tabId: string): void {
    if (!this.state) return;
    this.state = { ...this.state, tabs: this.state.tabs.filter((tab) => tab.id !== tabId) };
    if (this.forgetTimer) clearTimeout(this.forgetTimer);
    this.forgetTimer = setTimeout(() => {
      this.forgetTimer = null;
      void this.persist().catch(() => undefined);
    }, FORGET_DELAY_MS);
  }

  /** The sidecar has already written the entry; this keeps the view in step. */
  recordHistory(entry: HistoryEntry): void {
    const state = this.current();
    const history = [entry, ...state.history].slice(0, state.settings.maxHistory);
    this.state = { ...state, history };
    this.historyChanged.fire();
  }

  async clearHistory(): Promise<void> {
    const reply = await this.sidecar.call({ type: "clearHistory" });
    if (reply.type !== "stateSaved") throw new Error(describe(reply));
    this.state = { ...this.current(), history: reply.state.history };
    this.historyChanged.fire();
  }

  async deleteHistoryEntry(historyEntryId: string): Promise<void> {
    const reply = await this.sidecar.call({ type: "deleteHistoryEntry", historyEntryId });
    if (reply.type !== "stateSaved") throw new Error(describe(reply));
    this.state = { ...this.current(), history: reply.state.history };
    this.historyChanged.fire();
  }

  /**
   * Creates the saved request, or updates the one with this id, the way the
   * engine's own `UpsertSavedRequest` does.
   *
   * Done here and written with the rest of the state rather than sent as a
   * `saveRequest`: the state file is written whole, so a write from another
   * tab that was already on its way would put the old list back.
   */
  async saveRequest(id: string | null, name: string, request: HttpRequest): Promise<SavedRequest> {
    const state = this.current();
    const now = Date.now();
    const existing = id === null ? undefined : state.savedRequests.find((each) => each.id === id);
    const saved: SavedRequest = {
      id: id ?? randomUUID(),
      name,
      request: { ...request },
      createdAt: existing?.createdAt ?? now,
      updatedAt: now,
    };
    const savedRequests = existing
      ? state.savedRequests.map((each) => (each.id === saved.id ? saved : each))
      : [...state.savedRequests, saved];
    this.state = { ...state, savedRequests };
    this.savedChanged.fire();
    await this.persist();
    return saved;
  }

  async deleteSavedRequest(id: string): Promise<void> {
    const state = this.current();
    this.state = {
      ...state,
      savedRequests: state.savedRequests.filter((each) => each.id !== id),
    };
    this.savedChanged.fire();
    await this.persist();
  }

  /** Adds the saved requests in a JSON export or a `.http` file. */
  async importSavedRequests(contents: string): Promise<ImportOutcome> {
    const reply = await this.sidecar.call({ type: "importSavedRequests", contents });
    if (reply.type === "importRejected") throw new Error(reply.message);
    if (reply.type !== "savedRequestsImported") throw new Error(describe(reply));
    this.state = { ...this.current(), savedRequests: reply.state.savedRequests };
    this.savedChanged.fire();
    // Written again from here, for the reason `saveRequest` gives.
    await this.persist();
    return {
      state: reply.state,
      imported: reply.imported,
      undefinedVariables: reply.undefinedVariables,
      skipped: reply.skipped,
      plainSecrets: reply.plainSecrets,
    };
  }

  async exportSavedRequests(
    ids: readonly string[],
    format: ExportFormat,
    includeSecrets: boolean,
  ): Promise<string> {
    const reply = await this.sidecar.call({
      type: "exportSavedRequests",
      savedRequestIds: [...ids],
      format,
      includeSecrets,
    });
    if (reply.type !== "savedRequestsExported") throw new Error(describe(reply));
    return reply.contents;
  }

  dispose(): void {
    this.disposed = true;
    if (this.forgetTimer) clearTimeout(this.forgetTimer);
    this.historyChanged.dispose();
    this.savedChanged.dispose();
    this.sharedChanged.dispose();
  }

  /**
   * History is the sidecar's own and is not taken from here, so a write that
   * crosses a send cannot lose the row the send added.
   */
  private async persist(): Promise<void> {
    if (this.disposed || !this.state) return;
    const reply = await this.sidecar.call({ type: "saveState", state: this.state });
    if (reply.type !== "stateSaved") throw new Error(describe(reply));
  }

  private current(): AppState {
    if (!this.state) throw new Error("The state has not loaded yet.");
    return this.state;
  }
}

/** A tab for a request, which gets ids of its own so two tabs never share one. */
export function newTab(request?: HttpRequest, saved?: SavedRequest): ScratchTab {
  return {
    id: randomUUID(),
    name: saved?.name ?? null,
    request: request ? { ...request, id: randomUUID() } : blankRequest(),
    savedRequestId: saved?.id ?? null,
    dirty: false,
    splitPercent: null,
  };
}

/** Mirrors `blankRequest` in the UI. */
function blankRequest(): HttpRequest {
  return {
    id: randomUUID(),
    method: "GET",
    url: "",
    queryParams: [],
    headers: [],
    auth: { type: "none" },
    body: { type: "none" },
    timeoutMs: null,
    encodeQuery: true,
  };
}

export function describe(reply: ServerMessage): string {
  if ("message" in reply && typeof reply.message === "string") return reply.message;
  return `Unexpected reply from the request engine: ${reply.type}`;
}
