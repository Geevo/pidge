import { useCallback, useEffect, useMemo, useReducer, useRef } from "react";

import type { ExportInput, PlatformBridge } from "../bridge";
import type { ImportOutcome } from "../types";
import { toRequestError } from "../bridge";
import type { AppState, Environment, HttpRequest, Settings } from "../types";
import { blankTab } from "./factories";
import type { Action, DrawerPanel, UiState } from "./reducer";
import { activeTab, reducer, runtimeFor } from "./reducer";

const SAVE_DEBOUNCE_MS = 400;

function initialState(): UiState {
  const tab = blankTab();
  return {
    app: {
      version: 1,
      settings: {
        theme: "system",
        syntaxTheme: "app",
        timeoutMs: 30000,
        followRedirects: true,
        maxHistory: 500,
        maxResponseBytes: 50 * 1024 * 1024,
        restoreTabs: true,
        wrapResponseLines: false,
        fontScale: 100,
        paneLayout: "rows",
        splitPercent: 42,
        tls: {
          useSystemRoots: true,
          extraCaFiles: [],
          clientIdentity: null,
          acceptInvalidCerts: false,
        },
      },
      savedRequests: [],
      history: [],
      tabs: [tab],
      activeTabId: tab.id,
      environments: [],
      activeEnvironmentId: null,
    },
    runtime: {},
    drawer: null,
    codeTarget: "curl",
    storagePath: "",
    version: "",
    notice: null,
    loaded: false,
  };
}

export interface ApiClient {
  readonly state: UiState;
  readonly dispatch: React.Dispatch<Action>;
  readonly send: (tabId: string) => Promise<void>;
  readonly cancel: (tabId: string) => Promise<void>;
  readonly setUrl: (tabId: string, url: string) => void;
  readonly setRequest: (tabId: string, request: HttpRequest) => void;
  readonly newTab: (request?: HttpRequest, name?: string, savedRequestId?: string) => void;
  readonly closeTab: (tabId: string) => void;
  readonly saveActiveRequest: (name: string) => Promise<void>;
  /** Saves through the editor host instead; absent unless there is one. */
  readonly saveActiveRequestToEditor?: () => Promise<void>;
  readonly deleteSavedRequest: (savedRequestId: string) => Promise<void>;
  readonly clearHistory: () => Promise<void>;
  /** Absent when the host cannot save files. Says how it went in the notice. */
  readonly exportSavedRequests?: (input: ExportInput) => Promise<void>;
  /** Absent when the host cannot open files. Says how it went in the notice. */
  readonly importSavedRequests?: () => Promise<void>;
  readonly setDrawer: (drawer: DrawerPanel | null) => void;
  readonly setSettings: (settings: Settings) => void;
  readonly setEnvironments: (
    environments: Environment[],
    activeEnvironmentId: string | null,
  ) => void;
  readonly setActiveEnvironment: (environmentId: string | null) => void;
}

/**
 * Owns the conversation with the host: hydrate once, persist on a debounce,
 * and turn sends into state transitions. Components stay free of async code.
 */
export function useApiClient(bridge: PlatformBridge): ApiClient {
  const [state, dispatch] = useReducer(reducer, undefined, initialState);

  // Effects need the current state without re-subscribing on every keystroke.
  const stateRef = useRef(state);
  useEffect(() => {
    stateRef.current = state;
  }, [state]);

  const saveTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const pendingSave = useRef<AppState | null>(null);

  useEffect(() => {
    let cancelled = false;
    void bridge
      .loadState()
      .then((loaded) => {
        if (cancelled) return;
        dispatch({
          type: "hydrate",
          app: loaded.state,
          storagePath: loaded.storagePath,
          version: loaded.version,
          notice: loaded.recovery,
        });
      })
      .catch((error: unknown) => {
        if (cancelled) return;
        dispatch({
          type: "hydrate",
          app: stateRef.current.app,
          storagePath: "",
          version: "",
          notice: `Could not load your saved data: ${toRequestError(error).message}`,
        });
      });
    return () => {
      cancelled = true;
    };
  }, [bridge]);

  const flushSave = useCallback(() => {
    const next = pendingSave.current;
    pendingSave.current = null;
    if (!next) return;
    void bridge.saveState(next).catch(() => {
      // A failed write should not interrupt what the user is doing; the next
      // save will try again, and the response pane is where errors belong.
    });
  }, [bridge]);

  // Persist whenever the durable part of the state changes. Runtime state
  // (in-flight status, which pane is open) deliberately does not trigger this.
  useEffect(() => {
    if (!state.loaded) return;
    pendingSave.current = state.app;
    if (saveTimer.current) clearTimeout(saveTimer.current);
    saveTimer.current = setTimeout(flushSave, SAVE_DEBOUNCE_MS);
    return () => {
      if (saveTimer.current) clearTimeout(saveTimer.current);
    };
  }, [state.app, state.loaded, flushSave]);

  // A close or a reload should not drop the last few hundred milliseconds.
  useEffect(() => {
    const onHide = () => flushSave();
    window.addEventListener("pagehide", onHide);
    window.addEventListener("beforeunload", onHide);
    return () => {
      window.removeEventListener("pagehide", onHide);
      window.removeEventListener("beforeunload", onHide);
    };
  }, [flushSave]);

  const send = useCallback(
    async (tabId: string) => {
      const tab = stateRef.current.app.tabs.find((candidate) => candidate.id === tabId);
      if (!tab) return;
      if (runtimeFor(stateRef.current, tabId).status.state === "sending") return;

      dispatch({ type: "sendStarted", tabId });
      try {
        const outcome = await bridge.sendRequest(tab.request);
        if (outcome.response) {
          dispatch({
            type: "sendSucceeded",
            tabId,
            response: outcome.response,
            historyEntry: outcome.historyEntry,
          });
        } else if (outcome.error?.kind === "cancelled") {
          dispatch({ type: "sendCancelled", tabId });
        } else {
          dispatch({
            type: "sendFailed",
            tabId,
            error: outcome.error ?? toRequestError("the request produced no result"),
            historyEntry: outcome.historyEntry,
          });
        }
      } catch (error) {
        dispatch({ type: "sendFailed", tabId, error: toRequestError(error), historyEntry: null });
      }
    },
    [bridge],
  );

  const cancel = useCallback(
    async (tabId: string) => {
      const tab = stateRef.current.app.tabs.find((candidate) => candidate.id === tabId);
      if (!tab) return;
      await bridge.cancelRequest(tab.request.id).catch(() => undefined);
      dispatch({ type: "sendCancelled", tabId });
    },
    [bridge],
  );

  const setRequest = useCallback((tabId: string, request: HttpRequest) => {
    dispatch({ type: "setRequest", tabId, request });
  }, []);

  const setUrl = useCallback((tabId: string, url: string) => {
    const tab = stateRef.current.app.tabs.find((candidate) => candidate.id === tabId);
    if (!tab) return;
    dispatch({ type: "setRequest", tabId, request: { ...tab.request, url } });
  }, []);

  const newTab = useCallback((request?: HttpRequest, name?: string, savedRequestId?: string) => {
    dispatch({ type: "newTab", request, name, savedRequestId });
  }, []);

  /*
   * Closes without asking. Whether to ask is `needsCloseConfirmation`, and it is
   * the view's business: a hook that puts a dialog up cannot be driven by
   * anything that does not have a screen.
   */
  const closeTab = useCallback((tabId: string) => {
    dispatch({ type: "closeTab", tabId });
  }, []);

  const saveActiveRequest = useCallback(
    async (name: string) => {
      const tab = activeTab(stateRef.current);
      const next = await bridge.saveRequest({
        savedRequestId: tab.savedRequestId,
        name,
        request: tab.request,
      });
      dispatch({ type: "setSavedRequests", savedRequests: next.savedRequests });
      const saved =
        next.savedRequests.find((candidate) => candidate.id === tab.savedRequestId) ??
        next.savedRequests[next.savedRequests.length - 1];
      if (saved) dispatch({ type: "markTabSaved", tabId: tab.id, saved });
    },
    [bridge],
  );

  const saveActiveRequestToEditor = useCallback(async () => {
    if (!bridge.editor) return;
    const tab = activeTab(stateRef.current);
    try {
      const saved = await bridge.editor.saveRequest(tab.request, tab.name, tab.savedRequestId);
      if (saved) dispatch({ type: "markTabSaved", tabId: tab.id, saved });
    } catch (error) {
      dispatch({
        type: "showNotice",
        notice: `Could not save: ${toRequestError(error).message}`,
      });
    }
  }, [bridge]);

  const deleteSavedRequest = useCallback(
    async (savedRequestId: string) => {
      const next = await bridge.deleteSavedRequest(savedRequestId);
      dispatch({ type: "setSavedRequests", savedRequests: next.savedRequests });
    },
    [bridge],
  );

  const exportSavedRequests = useCallback(
    async (input: ExportInput) => {
      if (!bridge.exportSavedRequests) return;
      try {
        const path = await bridge.exportSavedRequests(input);
        if (path === null) return;
        const count = input.savedRequestIds.length;
        const what = count === 1 ? "1 request" : `${count} requests`;
        dispatch({
          type: "showNotice",
          notice: input.includeSecrets
            ? `Exported ${what} to ${path}, with passwords and tokens in plain text.`
            : `Exported ${what} to ${path}.`,
        });
      } catch (error) {
        dispatch({
          type: "showNotice",
          notice: `Could not export: ${toRequestError(error).message}`,
        });
      }
    },
    [bridge],
  );

  const importSavedRequests = useCallback(async () => {
    if (!bridge.importSavedRequests) return;
    try {
      const outcome = await bridge.importSavedRequests();
      if (outcome === null) return;
      dispatch({ type: "setSavedRequests", savedRequests: outcome.state.savedRequests });
      dispatch({ type: "showNotice", notice: importNotice(outcome) });
    } catch (error) {
      dispatch({
        type: "showNotice",
        notice: `Could not import: ${toRequestError(error).message}`,
      });
    }
  }, [bridge]);

  const clearHistory = useCallback(async () => {
    const next = await bridge.clearHistory();
    dispatch({ type: "setHistory", history: next.history });
  }, [bridge]);

  const setDrawer = useCallback((drawer: DrawerPanel | null) => {
    dispatch({ type: "setDrawer", drawer });
  }, []);

  const setSettings = useCallback((settings: Settings) => {
    dispatch({ type: "setSettings", settings });
  }, []);

  const setEnvironments = useCallback(
    (environments: Environment[], activeEnvironmentId: string | null) => {
      dispatch({ type: "setEnvironments", environments, activeEnvironmentId });
    },
    [],
  );

  const setActiveEnvironment = useCallback((environmentId: string | null) => {
    dispatch({ type: "setActiveEnvironment", environmentId });
  }, []);

  const canSaveToEditor = bridge.editor !== undefined;
  const canExport = bridge.exportSavedRequests !== undefined;
  const canImport = bridge.importSavedRequests !== undefined;

  return useMemo(
    () => ({
      state,
      dispatch,
      send,
      cancel,
      setUrl,
      setRequest,
      newTab,
      closeTab,
      saveActiveRequest,
      saveActiveRequestToEditor: canSaveToEditor ? saveActiveRequestToEditor : undefined,
      deleteSavedRequest,
      clearHistory,
      exportSavedRequests: canExport ? exportSavedRequests : undefined,
      importSavedRequests: canImport ? importSavedRequests : undefined,
      setDrawer,
      setSettings,
      setEnvironments,
      setActiveEnvironment,
    }),
    [
      state,
      send,
      cancel,
      setUrl,
      setRequest,
      newTab,
      closeTab,
      saveActiveRequest,
      canSaveToEditor,
      saveActiveRequestToEditor,
      deleteSavedRequest,
      clearHistory,
      canExport,
      exportSavedRequests,
      canImport,
      importSavedRequests,
      setDrawer,
      setSettings,
      setEnvironments,
      setActiveEnvironment,
    ],
  );
}

/**
 * One notice for an import: how many came in, what they still need, what was
 * left behind, and whether the file itself is now a liability.
 */
export function importNotice(outcome: ImportOutcome): string {
  const parts = [
    outcome.imported === 1 ? "Imported 1 request." : `Imported ${outcome.imported} requests.`,
  ];
  const names = outcome.undefinedVariables.map((name) => `{{${name}}}`);
  if (names.length > 0) {
    const list =
      names.length === 1 ? names[0] : `${names.slice(0, -1).join(", ")} and ${names.at(-1)}`;
    parts.push(
      `${outcome.imported === 1 ? "It uses" : "They use"} ${list}, which no environment defines yet.`,
    );
  }
  parts.push(...outcome.skipped);
  if (outcome.plainSecrets) {
    parts.push("The file holds passwords or tokens in plain text; it is worth deleting.");
  }
  return parts.join(" ");
}
