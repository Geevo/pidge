import { StrictMode, useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { type HistoryEntry, type SavedRequest, Sidebar, type SidebarTab } from "@api-client/ui";
import "@api-client/ui/styles.css";
import "./theme.css";

import type { SidebarMessage } from "../extension/protocol";
import { followEditorTheme } from "./theme";
import { getVsCodeApi } from "./vscodeApi";

/** What the extension host sends whenever either list changes. */
interface Lists {
  readonly history: HistoryEntry[];
  readonly savedRequests: SavedRequest[];
}

const api = getVsCodeApi();
const post = (message: SidebarMessage): void => api.postMessage(message);

function SidebarView() {
  const [lists, setLists] = useState<Lists | null>(null);
  const [tab, setTab] = useState<SidebarTab>(
    () => (api.getState() as { tab?: SidebarTab } | undefined)?.tab ?? "history",
  );

  useEffect(() => {
    const onMessage = (event: MessageEvent<unknown>): void => {
      const data = event.data as { kind?: string; event?: string; payload?: unknown } | null;
      if (data?.kind === "event" && data.event === "lists") setLists(data.payload as Lists);
    };
    window.addEventListener("message", onMessage);
    post({ type: "ready" });
    return () => window.removeEventListener("message", onMessage);
  }, []);

  if (!lists) return null;

  return (
    <Sidebar
      history={lists.history}
      savedRequests={lists.savedRequests}
      tab={tab}
      onTabChange={(next) => {
        setTab(next);
        api.setState({ tab: next });
      }}
      onNewRequest={() => post({ type: "newRequest" })}
      onOpenHistory={(entry) => post({ type: "openHistory", id: entry.id })}
      onOpenSaved={(saved) => post({ type: "openSaved", id: saved.id })}
      // VS Code builds the right-click menu from this, and hands it to the command.
      rowAttributes={(section, id) => ({
        "data-vscode-context": JSON.stringify({
          webviewSection: section,
          id,
          preventDefaultContextMenuItems: true,
        }),
      })}
    />
  );
}

const container = document.getElementById("root");
if (!container) throw new Error("missing #root");

followEditorTheme();

createRoot(container).render(
  <StrictMode>
    <SidebarView />
  </StrictMode>,
);
