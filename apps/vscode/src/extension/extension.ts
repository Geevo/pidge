import * as vscode from "vscode";

import { ApiClientPanel } from "./panel";
import { Sidecar } from "./sidecar";

/**
 * The extension works with no folder open: everything it stores lives in the
 * extension's global storage, not in a workspace.
 */
export function activate(context: vscode.ExtensionContext): void {
  const output = vscode.window.createOutputChannel("API Client");
  const sidecar = new Sidecar(context, output);
  context.subscriptions.push(output, sidecar);

  const open = async (): Promise<void> => {
    try {
      await sidecar.start();
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      void vscode.window.showErrorMessage(`API Client: ${message}`, "Show Log").then((choice) => {
        if (choice === "Show Log") output.show(true);
      });
      return;
    }
    ApiClientPanel.show(context, sidecar);
  };

  context.subscriptions.push(
    vscode.commands.registerCommand("apiClient.open", open),

    vscode.commands.registerCommand("apiClient.newRequest", async () => {
      await open();
      ApiClientPanel.show(context, sidecar).post({
        kind: "event",
        event: "newRequest",
        payload: null,
      });
    }),

    vscode.commands.registerCommand("apiClient.restartSidecar", async () => {
      try {
        await sidecar.restart();
        void vscode.window.showInformationMessage("API Client: request engine restarted.");
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        void vscode.window.showErrorMessage(`API Client: ${message}`);
      }
    }),

    vscode.window.registerWebviewViewProvider("apiClient.launcher", new LauncherView(), {
      webviewOptions: { retainContextWhenHidden: true },
    }),
  );
}

export function deactivate(): void {
  // The sidecar is disposed through context.subscriptions.
}

/**
 * The Activity Bar entry. The real UI belongs in an editor tab, so this view is
 * only a way in: it opens the panel as soon as it is revealed, and shows a
 * button for when the panel is closed again.
 */
class LauncherView implements vscode.WebviewViewProvider {
  resolveWebviewView(view: vscode.WebviewView): void {
    view.webview.options = { enableScripts: true };
    const nonce = Math.random().toString(36).slice(2);

    view.webview.html = `<!doctype html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}';" />
    <style>
      body { font-family: var(--vscode-font-family); padding: 12px; color: var(--vscode-foreground); }
      button {
        width: 100%; padding: 6px 10px; border: 0; cursor: pointer;
        background: var(--vscode-button-background); color: var(--vscode-button-foreground);
      }
      p { color: var(--vscode-descriptionForeground); font-size: 12px; }
    </style>
  </head>
  <body>
    <button id="open" type="button">Open API Client</button>
    <p>Type a URL, press Send. Nothing to set up.</p>
    <script nonce="${nonce}">
      const vscodeApi = acquireVsCodeApi();
      document.getElementById("open").addEventListener("click", () => vscodeApi.postMessage("open"));
      vscodeApi.postMessage("open");
    </script>
  </body>
</html>`;

    view.webview.onDidReceiveMessage((message: unknown) => {
      if (message === "open") void vscode.commands.executeCommand("apiClient.open");
    });
  }
}
