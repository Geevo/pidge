import type { HistoryEntry, SavedRequest } from "@api-client/ui";
import * as vscode from "vscode";

import { RequestPanel, type Services } from "./panel";
import { Sidecar } from "./sidecar";
import { Store } from "./store";
import { exportSavedRequests, importSavedRequests } from "./transfer";
import { SidebarView } from "./views";

/**
 * What a command about one row is called with: the row's id, from the side
 * bar or from VS Code's right-click menu on it.
 */
interface Row {
  readonly id?: string;
}

/**
 * Each request is an editor tab of its own, and saved requests and history
 * are in the side bar. Everything is kept in the extension's global storage,
 * so it works with no folder open.
 */
export function activate(context: vscode.ExtensionContext): void {
  const output = vscode.window.createOutputChannel("pidge");
  const sidecar = new Sidecar(context, output);
  const store = new Store(sidecar);
  const services: Services = { context, sidecar, store };
  context.subscriptions.push(output, sidecar, store);

  const fail = (error: unknown): void => {
    const message = error instanceof Error ? error.message : String(error);
    void vscode.window.showErrorMessage(`pidge: ${message}`, "Show Log").then((choice) => {
      if (choice === "Show Log") output.show(true);
    });
  };

  /** Runs a command, reporting what went wrong rather than failing silently. */
  const command = <T extends unknown[]>(
    id: string,
    run: (...args: T) => Promise<unknown>,
  ): vscode.Disposable =>
    vscode.commands.registerCommand(id, async (...args: T) => {
      try {
        await run(...args);
      } catch (error) {
        fail(error);
      }
    });

  /** Starts the engine first, so a missing one is reported before a tab opens. */
  const open = async (open: () => void): Promise<void> => {
    await sidecar.start();
    open();
  };

  /** The row a command is about, if it still exists. */
  const savedRow = async (row?: Row): Promise<SavedRequest | undefined> => {
    await store.load();
    return store.savedRequests.find((saved) => saved.id === row?.id);
  };
  const historyRow = async (row?: Row): Promise<HistoryEntry | undefined> => {
    await store.load();
    return store.history.find((entry) => entry.id === row?.id);
  };

  const sidebar = new SidebarView(context.extensionUri, store, fail);
  context.subscriptions.push(
    sidebar,
    vscode.window.registerWebviewViewProvider(SidebarView.viewType, sidebar),

    vscode.window.registerWebviewPanelSerializer(RequestPanel.viewType, {
      deserializeWebviewPanel: (panel, state) => {
        RequestPanel.revive(services, panel, state);
        return Promise.resolve();
      },
    }),

    command("pidge.newRequest", () => open(() => RequestPanel.create(services))),

    command("pidge.open", async () => {
      if (RequestPanel.revealLast()) return;
      await open(() => RequestPanel.create(services));
    }),

    command("pidge.openSaved", async (row?: Row) => {
      const request = await savedRow(row);
      if (!request || RequestPanel.revealSaved(request.id)) return;
      await open(() => RequestPanel.create(services, request.request, request));
    }),

    command("pidge.openHistory", async (row?: Row) => {
      const entry = await historyRow(row);
      if (entry) await open(() => RequestPanel.create(services, entry.request));
    }),

    command("pidge.deleteSaved", async (row?: Row) => {
      const request = await savedRow(row);
      if (!request) return;
      const remove = "Delete";
      const choice = await vscode.window.showWarningMessage(
        `Delete "${request.name}"?`,
        { modal: true, detail: "Tabs that have it open keep their copy." },
        remove,
      );
      if (choice === remove) await store.deleteSavedRequest(request.id);
    }),

    command("pidge.importSaved", () => importSavedRequests(store)),

    command("pidge.exportSaved", async (row?: Row) =>
      exportSavedRequests(store, await savedRow(row)),
    ),

    command("pidge.deleteHistoryEntry", async (row?: Row) => {
      const entry = await historyRow(row);
      if (entry) await store.deleteHistoryEntry(entry.id);
    }),

    command("pidge.clearHistory", async () => {
      const clear = "Clear History";
      const choice = await vscode.window.showWarningMessage(
        "Clear every request in history?",
        { modal: true, detail: "Saved requests are not affected." },
        clear,
      );
      if (choice !== clear) return;
      await store.load();
      await store.clearHistory();
    }),

    command("pidge.restartSidecar", async () => {
      await sidecar.restart();
      void vscode.window.showInformationMessage("pidge: request engine restarted.");
    }),
  );
}

export function deactivate(): void {
  // Everything is disposed through context.subscriptions.
}
