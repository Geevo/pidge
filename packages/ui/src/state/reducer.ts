import type {
  AppState,
  CodeTarget,
  Environment,
  HistoryEntry,
  HttpRequest,
  RequestPane,
  ResponsePane,
  SavedRequest,
  ScratchTab,
  Settings,
  TabStatus,
} from "../types";
import type { RequestError, HttpResponse } from "../types";
import { blankTab, cloneRequest, isUntouched } from "./factories";

/**
 * Per-tab state that is never written to disk: what the tab is doing right
 * now, and which sub-tabs are showing.
 */
export interface TabRuntime {
  readonly status: TabStatus;
  readonly requestPane: RequestPane;
  readonly responsePane: ResponsePane;
}

export type DrawerPanel = "history" | "saved";

export interface UiState {
  /** The persisted shape, exactly as the host sees it. */
  readonly app: AppState;
  readonly runtime: Readonly<Record<string, TabRuntime>>;
  /** `null` means the drawer is closed; the editor is the point of the app. */
  readonly drawer: DrawerPanel | null;
  /**
   * Which language the Code pane is showing. One choice for the window rather
   * than one per tab: it is a preference about the reader, not about a request.
   */
  readonly codeTarget: CodeTarget;
  readonly storagePath: string;
  /** The host's version, for Settings. Empty until the state has loaded. */
  readonly version: string;
  /** A one-off message, e.g. a recovered state file. */
  readonly notice: string | null;
  readonly loaded: boolean;
}

const defaultRuntime: TabRuntime = {
  status: { state: "idle" },
  requestPane: "params",
  responsePane: "body",
};

export type Action =
  | {
      type: "hydrate";
      app: AppState;
      storagePath: string;
      version: string;
      notice: string | null;
    }
  | { type: "showNotice"; notice: string }
  | { type: "dismissNotice" }
  | { type: "setRequest"; tabId: string; request: HttpRequest }
  | { type: "newTab"; request?: HttpRequest; name?: string; savedRequestId?: string }
  | { type: "closeTab"; tabId: string }
  | { type: "selectTab"; tabId: string }
  | { type: "moveTab"; tabId: string; toIndex: number }
  | { type: "sendStarted"; tabId: string }
  | {
      type: "sendSucceeded";
      tabId: string;
      response: HttpResponse;
      historyEntry: HistoryEntry | null;
    }
  | { type: "sendFailed"; tabId: string; error: RequestError; historyEntry: HistoryEntry | null }
  | { type: "sendCancelled"; tabId: string }
  | { type: "setRequestPane"; tabId: string; pane: RequestPane }
  | { type: "setResponsePane"; tabId: string; pane: ResponsePane }
  | { type: "setDrawer"; drawer: DrawerPanel | null }
  | { type: "setCodeTarget"; target: CodeTarget }
  | { type: "setSettings"; settings: Settings }
  | { type: "setEnvironments"; environments: Environment[]; activeEnvironmentId: string | null }
  | { type: "setActiveEnvironment"; environmentId: string | null }
  | { type: "setSavedRequests"; savedRequests: SavedRequest[] }
  | { type: "markTabSaved"; tabId: string; saved: SavedRequest }
  | { type: "setHistory"; history: HistoryEntry[] }
  | { type: "setSplitPercent"; tabId: string; percent: number };

export function reducer(state: UiState, action: Action): UiState {
  switch (action.type) {
    case "hydrate": {
      const app = ensureOneTab(action.app);
      return {
        ...state,
        app,
        runtime: Object.fromEntries(app.tabs.map((tab) => [tab.id, defaultRuntime])),
        storagePath: action.storagePath,
        version: action.version,
        notice: action.notice,
        loaded: true,
      };
    }

    case "showNotice":
      return { ...state, notice: action.notice };

    case "dismissNotice":
      return { ...state, notice: null };

    case "setRequest":
      return withTabs(
        state,
        state.app.tabs.map((tab) =>
          tab.id === action.tabId ? { ...tab, request: action.request, dirty: true } : tab,
        ),
      );

    case "newTab": {
      const tab: ScratchTab = {
        ...blankTab(),
        request: action.request ? cloneRequest(action.request) : blankTab().request,
        name: action.name ?? null,
        savedRequestId: action.savedRequestId ?? null,
      };
      return {
        ...withTabs(state, [...state.app.tabs, tab], tab.id),
        runtime: { ...state.runtime, [tab.id]: defaultRuntime },
      };
    }

    case "closeTab": {
      const remaining = state.app.tabs.filter((tab) => tab.id !== action.tabId);
      const tabs = remaining.length > 0 ? remaining : [blankTab()];
      const activeTabId =
        state.app.activeTabId === action.tabId
          ? nextActiveId(state.app.tabs, action.tabId, tabs)
          : (state.app.activeTabId ?? tabs[0]!.id);

      const runtime = { ...state.runtime };
      delete runtime[action.tabId];
      for (const tab of tabs) runtime[tab.id] ??= defaultRuntime;

      return { ...withTabs(state, tabs, activeTabId), runtime };
    }

    case "selectTab":
      return { ...state, app: { ...state.app, activeTabId: action.tabId } };

    case "moveTab": {
      const from = state.app.tabs.findIndex((tab) => tab.id === action.tabId);
      const to = Math.max(0, Math.min(action.toIndex, state.app.tabs.length - 1));
      if (from === -1 || from === to) return state;

      const tabs = [...state.app.tabs];
      const [moved] = tabs.splice(from, 1);
      tabs.splice(to, 0, moved!);
      return { ...state, app: { ...state.app, tabs } };
    }

    case "sendStarted":
      return withRuntime(state, action.tabId, { status: { state: "sending" } });

    case "sendSucceeded":
      return withHistory(
        withRuntime(state, action.tabId, {
          status: { state: "done", response: action.response },
          responsePane: "body",
        }),
        action.historyEntry,
      );

    case "sendFailed":
      return withHistory(
        withRuntime(state, action.tabId, {
          status: { state: "failed", error: action.error },
          responsePane: "body",
        }),
        action.historyEntry,
      );

    case "sendCancelled":
      return withRuntime(state, action.tabId, { status: { state: "idle" } });

    case "setRequestPane":
      return withRuntime(state, action.tabId, { requestPane: action.pane });

    case "setResponsePane":
      return withRuntime(state, action.tabId, { responsePane: action.pane });

    case "setDrawer":
      return { ...state, drawer: action.drawer };

    case "setCodeTarget":
      return { ...state, codeTarget: action.target };

    case "setSettings":
      return { ...state, app: { ...state.app, settings: action.settings } };

    case "setEnvironments":
      return {
        ...state,
        app: {
          ...state.app,
          environments: action.environments,
          activeEnvironmentId: action.activeEnvironmentId,
        },
      };

    case "setActiveEnvironment":
      return { ...state, app: { ...state.app, activeEnvironmentId: action.environmentId } };

    case "setSavedRequests":
      return { ...state, app: { ...state.app, savedRequests: action.savedRequests } };

    case "markTabSaved":
      return withTabs(
        state,
        state.app.tabs.map((tab) =>
          tab.id === action.tabId
            ? { ...tab, name: action.saved.name, savedRequestId: action.saved.id, dirty: false }
            : tab,
        ),
      );

    case "setHistory":
      return { ...state, app: { ...state.app, history: action.history } };

    /*
     * The tab keeps its own position, and the setting follows as the default
     * for tabs opened later — so existing tabs stay where you put them, and a
     * new one opens where you were last working rather than always at 42%.
     */
    case "setSplitPercent":
      return {
        ...state,
        app: {
          ...state.app,
          settings: { ...state.app.settings, splitPercent: action.percent },
          tabs: state.app.tabs.map((tab) =>
            tab.id === action.tabId ? { ...tab, splitPercent: action.percent } : tab,
          ),
        },
      };
  }
}

/** True when closing this tab should ask first. */
export function needsCloseConfirmation(tab: ScratchTab): boolean {
  return tab.dirty && !isUntouched(tab.request);
}

export function activeTab(state: UiState): ScratchTab {
  return state.app.tabs.find((tab) => tab.id === state.app.activeTabId) ?? state.app.tabs[0]!;
}

export function runtimeFor(state: UiState, tabId: string): TabRuntime {
  return state.runtime[tabId] ?? defaultRuntime;
}

function withTabs(state: UiState, tabs: ScratchTab[], activeTabId?: string): UiState {
  return {
    ...state,
    app: {
      ...state.app,
      tabs,
      activeTabId: activeTabId ?? state.app.activeTabId ?? tabs[0]?.id ?? null,
    },
  };
}

function withRuntime(state: UiState, tabId: string, patch: Partial<TabRuntime>): UiState {
  return {
    ...state,
    runtime: {
      ...state.runtime,
      [tabId]: { ...(state.runtime[tabId] ?? defaultRuntime), ...patch },
    },
  };
}

/**
 * Prepends the row the engine recorded and trims to the same cap the host uses, so
 * the panel matches what is on disk without a reload.
 */
function withHistory(state: UiState, entry: HistoryEntry | null): UiState {
  if (!entry) return state;
  const history = [entry, ...state.app.history].slice(0, state.app.settings.maxHistory);
  return { ...state, app: { ...state.app, history } };
}

/** Closing the active tab selects its neighbour, the way editors do. */
function nextActiveId(
  before: readonly ScratchTab[],
  closedId: string,
  after: readonly ScratchTab[],
): string {
  const index = before.findIndex((tab) => tab.id === closedId);
  return (after[Math.min(index, after.length - 1)] ?? after[0]!).id;
}

function ensureOneTab(app: AppState): AppState {
  const tabs = app.tabs.length > 0 ? app.tabs : [blankTab()];
  const activeTabId = tabs.some((tab) => tab.id === app.activeTabId)
    ? app.activeTabId
    : tabs[0]!.id;
  return { ...app, tabs, activeTabId };
}
