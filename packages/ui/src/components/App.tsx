import { useCallback, useEffect, useRef, useState } from "react";

import type { PlatformBridge } from "../bridge";
import { matchShortcut, shortcutHint } from "../lib/shortcuts";
import { urlChanged } from "../lib/url";
import { activeTab, runtimeFor } from "../state/reducer";
import { useApiClient } from "../state/useApiClient";
import type { HttpMethod, HttpRequest } from "../types";
import { EnvironmentSelector } from "./EnvironmentSelector";
import { EnvironmentsDialog } from "./EnvironmentsDialog";
import { HistoryPanel } from "./HistoryPanel";
import { RequestEditor } from "./RequestEditor";
import { RequestTabBar } from "./RequestTabBar";
import { ResponseViewer } from "./ResponseViewer";
import { SavedRequestsPanel } from "./SavedRequestsPanel";
import { UrlBar } from "./UrlBar";

interface Props {
  bridge: PlatformBridge;
}

/**
 * The whole screen. It opens directly into a blank request: no welcome, no
 * dashboard, nothing to dismiss.
 */
export function App({ bridge }: Props) {
  const client = useApiClient(bridge);
  const { state } = client;
  const tab = activeTab(state);
  const runtime = runtimeFor(state, tab.id);
  const sending = runtime.status.state === "sending";

  const urlRef = useRef<HTMLInputElement>(null);
  const [environmentsOpen, setEnvironmentsOpen] = useState(false);

  useTheme(state.app.settings.theme);

  // The host can ask for things too, e.g. the VS Code Command Palette.
  useEffect(
    () =>
      bridge.subscribe?.((command) => {
        if (command === "newRequest") client.newTab();
      }),
    [bridge, client],
  );

  const setRequest = useCallback(
    (request: HttpRequest) => client.setRequest(tab.id, request),
    [client, tab.id],
  );

  const send = useCallback(() => void client.send(tab.id), [client, tab.id]);

  const save = useCallback(() => {
    const suggested = tab.name ?? tab.request.url.trim();
    const name = window.prompt("Save request as", suggested || "Untitled request");
    if (name === null || name.trim() === "") return;
    void client.saveActiveRequest(name.trim());
  }, [client, tab.name, tab.request.url]);

  // Shortcuts are global: the URL field is the default focus, but Send has to
  // work from the body editor and the headers table too.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      const shortcut = matchShortcut(event);
      if (!shortcut) return;
      event.preventDefault();

      switch (shortcut) {
        case "send":
          if (!sending) send();
          break;
        case "focusUrl":
          urlRef.current?.focus();
          urlRef.current?.select();
          break;
        case "newTab":
          client.newTab();
          break;
        case "closeTab":
          client.closeTab(tab.id);
          break;
        case "save":
          save();
          break;
      }
    };

    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [client, save, send, sending, tab.id]);

  return (
    <div className="ac-app">
      {state.notice ? (
        <div className="ac-notice" role="status">
          <span>{state.notice}</span>
          <span className="ac-spacer" />
          <button
            type="button"
            className="ac-icon-button"
            aria-label="Dismiss"
            onClick={() => client.dispatch({ type: "dismissNotice" })}
          >
            ×
          </button>
        </div>
      ) : null}

      <RequestTabBar
        tabs={state.app.tabs}
        activeTabId={state.app.activeTabId}
        onSelect={(tabId) => client.dispatch({ type: "selectTab", tabId })}
        onClose={client.closeTab}
        onNew={() => client.newTab()}
      />

      <div className="ac-topbar">
        <UrlBar
          ref={urlRef}
          method={tab.request.method}
          url={tab.request.url}
          sending={sending}
          onMethodChange={(method: HttpMethod) => setRequest({ ...tab.request, method })}
          onUrlChange={(url) => {
            const synced = urlChanged(url, tab.request.queryParams);
            setRequest({ ...tab.request, url: synced.url, queryParams: [...synced.queryParams] });
          }}
          onSend={send}
          onCancel={() => void client.cancel(tab.id)}
        />
        <EnvironmentSelector
          environments={state.app.environments}
          activeEnvironmentId={state.app.activeEnvironmentId}
          onChange={client.setActiveEnvironment}
          onManage={() => setEnvironmentsOpen(true)}
        />
        <button
          type="button"
          className="ac-button ac-button--quiet"
          title={`Save request (${shortcutHint("save")})`}
          onClick={save}
        >
          Save
        </button>
      </div>

      <div className="ac-body">
        <nav className="ac-sidebar-rail" aria-label="Panels">
          <button
            type="button"
            className="ac-rail-button"
            title="History"
            aria-label="History"
            aria-pressed={state.drawer === "history"}
            onClick={() => client.setDrawer(state.drawer === "history" ? null : "history")}
          >
            ↻
          </button>
          <button
            type="button"
            className="ac-rail-button"
            title="Saved requests"
            aria-label="Saved requests"
            aria-pressed={state.drawer === "saved"}
            onClick={() => client.setDrawer(state.drawer === "saved" ? null : "saved")}
          >
            ★
          </button>
        </nav>

        {state.drawer ? (
          <aside className="ac-drawer">
            {state.drawer === "history" ? (
              <HistoryPanel
                history={state.app.history}
                onOpen={(entry) => client.newTab(entry.request)}
                onClear={() => void client.clearHistory()}
              />
            ) : (
              <SavedRequestsPanel
                savedRequests={state.app.savedRequests}
                onOpen={(saved) => client.newTab(saved.request, saved.name, saved.id)}
                onDelete={(id) => void client.deleteSavedRequest(id)}
              />
            )}
          </aside>
        ) : null}

        <main className="ac-main">
          <div className="ac-split">
            <RequestEditor
              request={tab.request}
              pane={runtime.requestPane}
              onPaneChange={(pane) =>
                client.dispatch({ type: "setRequestPane", tabId: tab.id, pane })
              }
              onChange={setRequest}
              onSubmit={send}
            />
            <ResponseViewer
              status={runtime.status}
              pane={runtime.responsePane}
              wrapLines={state.app.settings.wrapResponseLines}
              onPaneChange={(pane) =>
                client.dispatch({ type: "setResponsePane", tabId: tab.id, pane })
              }
            />
          </div>
        </main>
      </div>

      {environmentsOpen ? (
        <EnvironmentsDialog
          environments={state.app.environments}
          activeEnvironmentId={state.app.activeEnvironmentId}
          onSave={client.setEnvironments}
          onClose={() => setEnvironmentsOpen(false)}
        />
      ) : null}
    </div>
  );
}

/**
 * The desktop app sets the theme explicitly; the VS Code build leaves it on
 * "system" so it follows the editor's own colours.
 */
function useTheme(theme: "system" | "light" | "dark") {
  useEffect(() => {
    const root = document.documentElement;
    if (theme === "system") root.removeAttribute("data-theme");
    else root.setAttribute("data-theme", theme);
  }, [theme]);
}
