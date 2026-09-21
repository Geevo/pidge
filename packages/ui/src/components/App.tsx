import { useCallback, useEffect, useRef, useState } from "react";

import type { PlatformBridge } from "../bridge";
import { installFieldHistory } from "../lib/fieldHistory";
import { DEFAULT_FONT_SCALE, clampFontScale, stepFontScale } from "../lib/fontScale";
import { selectContents } from "../lib/selection";
import { isSelectAll, matchShortcut, shortcutHint } from "../lib/shortcuts";
import { urlChanged } from "../lib/url";
import { activeTab, needsCloseConfirmation, runtimeFor } from "../state/reducer";
import { useApiClient } from "../state/useApiClient";
import type { CodeTarget, HttpMethod, HttpRequest, ScratchTab, SyntaxTheme, Theme } from "../types";
import { EnvironmentSelector } from "./EnvironmentSelector";
import { EnvironmentsDialog } from "./EnvironmentsDialog";
import { HistoryPanel } from "./HistoryPanel";
import { RequestEditor } from "./RequestEditor";
import { RequestTabBar } from "./RequestTabBar";
import { ResponseViewer } from "./ResponseViewer";
import { SavedRequestsPanel } from "./SavedRequestsPanel";
import { ConfirmDialog } from "./ConfirmDialog";
import { PromptDialog } from "./PromptDialog";
import { SettingsDialog } from "./SettingsDialog";
import { SplitPane, clampPercent } from "./SplitPane";
import { StatusBar } from "./StatusBar";
import { ResizeEdges } from "./WindowChrome";
import { UrlBar } from "./UrlBar";
import { BookmarkIcon, CloseIcon, ColumnsIcon, HistoryIcon, RowsIcon, SettingsIcon } from "./icons";

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
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [saveOpen, setSaveOpen] = useState(false);
  const [closing, setClosing] = useState<ScratchTab | null>(null);

  /*
   * The theme picked in Settings but not yet saved. A palette is not something
   * you can judge from its name, so it applies as soon as it is chosen; closing
   * the dialog without saving drops this and the saved theme comes back.
   */
  const [previewTheme, setPreviewTheme] = useState<Theme | null>(null);
  /** The same, for the syntax colours: they are picked by looking at them. */
  const [previewSyntax, setPreviewSyntax] = useState<SyntaxTheme | null>(null);
  useTheme(previewTheme ?? state.app.settings.theme);
  useSyntaxTheme(previewSyntax ?? state.app.settings.syntaxTheme);
  useFontScale(state.app.settings.fontScale);

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

  /*
   * Rebuilt when the environment changes as well as when the bridge does: the
   * host resolves `{{name}}` against whichever environment is active, so the
   * same request is different code under a different one, and the Code pane
   * regenerates on this function's identity.
   */
  const generateCode = useCallback(
    (request: HttpRequest, target: CodeTarget) => bridge.generateCode(request, target),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [bridge, state.app.activeEnvironmentId, state.app.environments],
  );

  // An untouched tab closes silently; one with work in it asks first.
  const askCloseTab = useCallback(
    (tabId: string) => {
      const candidate = state.app.tabs.find((each) => each.id === tabId);
      if (!candidate) return;
      if (needsCloseConfirmation(candidate)) setClosing(candidate);
      else client.closeTab(tabId);
    },
    [client, state.app.tabs],
  );

  const save = useCallback(() => setSaveOpen(true), []);

  /*
   * The text size, kept as it changes rather than on leaving a dialog, unlike
   * the palettes: the keyboard reaches it from anywhere, and somebody holding
   * Ctrl and + until the screen reads has no dialog to leave.
   */
  const settings = state.app.settings;
  const setFontScale = useCallback(
    (percent: number) => {
      const fontScale = clampFontScale(percent);
      if (fontScale !== settings.fontScale) client.setSettings({ ...settings, fontScale });
    },
    [client, settings],
  );

  /*
   * Undo and redo for every text field, wired once here rather than in each of
   * them. `fieldHistory` explains why the app has to do this itself.
   */
  useEffect(() => installFieldHistory(), []);

  // Shortcuts are global: the URL field is the default focus, but Send has to
  // work from the body editor and the headers table too.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      /*
       * Select all belongs to whatever holds focus, and a field, an editor or
       * the response body has already answered by the time this runs. Focus on
       * a button or nowhere at all is the case worth catching: the browser
       * would take the whole window, so the key never reaches it.
       *
       * An open dialog is the whole of what is on screen, and is what the key
       * means there — the version and the path under About are why anyone
       * reaches for it. The last one is the one on top.
       */
      if (isSelectAll(event)) {
        if (event.defaultPrevented || isTextEntry(event.target)) return;
        event.preventDefault();

        const dialogs = document.querySelectorAll(".ac-dialog");
        const dialog = dialogs[dialogs.length - 1];
        if (dialog) selectContents(dialog);
        return;
      }

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
          askCloseTab(tab.id);
          break;
        case "save":
          save();
          break;
        case "textBigger":
          setFontScale(stepFontScale(settings.fontScale, 1));
          break;
        case "textSmaller":
          setFontScale(stepFontScale(settings.fontScale, -1));
          break;
        case "textReset":
          setFontScale(DEFAULT_FONT_SCALE);
          break;
      }
    };

    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [askCloseTab, client, save, send, sending, setFontScale, settings.fontScale, tab.id]);

  return (
    <div className="ac-app">
      {bridge.window ? <ResizeEdges controls={bridge.window} /> : null}
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
            <CloseIcon size={12} />
          </button>
        </div>
      ) : null}

      <RequestTabBar
        tabs={state.app.tabs}
        activeTabId={state.app.activeTabId}
        onSelect={(tabId) => client.dispatch({ type: "selectTab", tabId })}
        onClose={askCloseTab}
        onNew={() => client.newTab()}
        windowControls={bridge.window}
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
          className="ac-icon-button"
          title={
            state.app.settings.paneLayout === "rows"
              ? "Put the response beside the request"
              : "Put the response below the request"
          }
          aria-label="Toggle pane layout"
          onClick={() =>
            client.setSettings({
              ...state.app.settings,
              paneLayout: state.app.settings.paneLayout === "rows" ? "columns" : "rows",
            })
          }
        >
          {state.app.settings.paneLayout === "rows" ? (
            <ColumnsIcon size={15} />
          ) : (
            <RowsIcon size={15} />
          )}
        </button>
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
            <HistoryIcon size={16} />
          </button>
          <button
            type="button"
            className="ac-rail-button"
            title="Saved requests"
            aria-label="Saved requests"
            aria-pressed={state.drawer === "saved"}
            onClick={() => client.setDrawer(state.drawer === "saved" ? null : "saved")}
          >
            <BookmarkIcon size={16} />
          </button>
          <span className="ac-spacer" />
          <button
            type="button"
            className="ac-rail-button"
            title="Settings"
            aria-label="Settings"
            onClick={() => setSettingsOpen(true)}
          >
            <SettingsIcon size={16} />
          </button>
        </nav>

        <div className="ac-workspace">
          <div className="ac-workspace__panes">
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
              <SplitPane
                layout={state.app.settings.paneLayout}
                percent={tab.splitPercent ?? state.app.settings.splitPercent}
                firstLabel="request"
                secondLabel="response"
                onCommit={(percent) =>
                  client.dispatch({
                    type: "setSplitPercent",
                    tabId: tab.id,
                    percent: clampPercent(percent),
                  })
                }
                first={
                  <RequestEditor
                    request={tab.request}
                    pane={runtime.requestPane}
                    codeTarget={state.codeTarget}
                    onPaneChange={(pane) =>
                      client.dispatch({ type: "setRequestPane", tabId: tab.id, pane })
                    }
                    onCodeTargetChange={(target) =>
                      client.dispatch({ type: "setCodeTarget", target })
                    }
                    onChange={setRequest}
                    onSubmit={send}
                    generateCode={generateCode}
                  />
                }
                second={
                  <ResponseViewer
                    status={runtime.status}
                    pane={runtime.responsePane}
                    wrapLines={state.app.settings.wrapResponseLines}
                    onPaneChange={(pane) =>
                      client.dispatch({ type: "setResponsePane", tabId: tab.id, pane })
                    }
                  />
                }
              />
            </main>
          </div>

          <StatusBar status={runtime.status} />
        </div>
      </div>

      {closing ? (
        <ConfirmDialog
          title="Discard changes"
          message={`Discard unsaved changes to ${closing.name ?? (closing.request.url.trim() || "this request")}?`}
          confirmLabel="Discard"
          danger
          onConfirm={() => client.closeTab(closing.id)}
          onClose={() => setClosing(null)}
        />
      ) : null}

      {saveOpen ? (
        <PromptDialog
          title="Save request"
          label="Name"
          initialValue={tab.name ?? (tab.request.url.trim() || "Untitled request")}
          onSubmit={(name) => void client.saveActiveRequest(name)}
          onClose={() => setSaveOpen(false)}
        />
      ) : null}

      {settingsOpen ? (
        <SettingsDialog
          settings={state.app.settings}
          storagePath={state.storagePath}
          version={state.version}
          onBrowse={bridge.pickFile?.bind(bridge)}
          onPreviewTheme={setPreviewTheme}
          onPreviewSyntax={setPreviewSyntax}
          onFontScale={setFontScale}
          onSave={client.setSettings}
          onClose={() => {
            setPreviewTheme(null);
            setPreviewSyntax(null);
            setSettingsOpen(false);
          }}
        />
      ) : null}

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

/** Somewhere text is typed, which selects its own contents and needs no help. */
function isTextEntry(target: EventTarget | null): boolean {
  return (
    target instanceof HTMLInputElement ||
    target instanceof HTMLTextAreaElement ||
    (target instanceof HTMLElement && target.isContentEditable)
  );
}

/**
 * Applies a palette to the document.
 *
 * "System" is resolved here rather than by a `prefers-color-scheme` block alone,
 * so that what the app believes and what it draws cannot disagree: the media
 * query is evaluated by the engine, and a webview that answers it differently
 * would leave the app light on a dark desktop with nothing to point at. The
 * query is still what paints the first frame, before this runs.
 */
function useTheme(theme: Theme) {
  useEffect(() => {
    const root = document.documentElement;

    if (theme !== "system") {
      root.setAttribute("data-theme", theme);
      return;
    }

    const query = window.matchMedia("(prefers-color-scheme: dark)");
    const apply = () => root.setAttribute("data-theme", query.matches ? "dark" : "light");

    apply();
    query.addEventListener("change", apply);
    return () => query.removeEventListener("change", apply);
  }, [theme]);
}

/**
 * Applies a set of syntax colours to the document, the same way.
 *
 * A separate attribute rather than more values in `data-theme`: the two are
 * chosen separately, and every combination of them is legal.
 */
function useSyntaxTheme(syntaxTheme: SyntaxTheme) {
  useEffect(() => {
    document.documentElement.setAttribute("data-syntax", syntaxTheme);
  }, [syntaxTheme]);
}

/**
 * Applies the text size to the document.
 *
 * A property rather than an attribute, because unlike a palette this is a
 * number: one multiplier on the root that every size token in `styles.css` is
 * written in terms of, so the whole interface grows at once and no component
 * has to know its own size.
 */
function useFontScale(fontScale: number) {
  useEffect(() => {
    const scale = clampFontScale(fontScale) / 100;
    document.documentElement.style.setProperty("--ac-font-scale", String(scale));
  }, [fontScale]);
}
