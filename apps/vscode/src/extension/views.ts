import * as vscode from "vscode";

import { webviewHtml, webviewOptions } from "./html";
import type { SidebarMessage, WebviewEvent } from "./protocol";
import type { Store } from "./store";

/**
 * The side bar: a New Request button, then history and saved requests as two
 * tabs of one pane. Saved requests live in the state file with everything
 * else, so their passwords and tokens are encrypted like the rest.
 *
 * Opening anything opens an editor tab; deleting, importing and exporting are
 * VS Code's own menus, which call the commands with the row's id.
 */
export class SidebarView implements vscode.WebviewViewProvider, vscode.Disposable {
  static readonly viewType = "pidge.sidebar";

  private view: vscode.WebviewView | undefined;
  private readonly subscriptions: vscode.Disposable[];

  constructor(
    private readonly extensionUri: vscode.Uri,
    private readonly store: Store,
    private readonly fail: (error: unknown) => void,
  ) {
    this.subscriptions = [
      store.onDidChangeHistory(() => this.update()),
      store.onDidChangeSaved(() => this.update()),
    ];
  }

  resolveWebviewView(view: vscode.WebviewView): void {
    this.view = view;
    view.webview.options = webviewOptions(this.extensionUri);
    view.webview.html = webviewHtml(view.webview, this.extensionUri, "sidebar.js");
    view.webview.onDidReceiveMessage((message: SidebarMessage) => {
      void this.onMessage(message).catch(this.fail);
    });
    view.onDidDispose(() => {
      if (this.view === view) this.view = undefined;
    });
  }

  dispose(): void {
    for (const subscription of this.subscriptions) subscription.dispose();
  }

  private async onMessage(message: SidebarMessage): Promise<void> {
    switch (message.type) {
      case "ready":
        await this.store.load();
        this.update();
        return;
      case "newRequest":
        await vscode.commands.executeCommand("pidge.newRequest");
        return;
      case "openHistory":
        await vscode.commands.executeCommand("pidge.openHistory", { id: message.id });
        return;
      case "openSaved":
        await vscode.commands.executeCommand("pidge.openSaved", { id: message.id });
        return;
    }
  }

  private update(): void {
    if (!this.view) return;
    const event: WebviewEvent = {
      kind: "event",
      event: "lists",
      payload: { history: this.store.history, savedRequests: this.store.savedRequests },
    };
    void this.view.webview.postMessage(event);
  }
}
