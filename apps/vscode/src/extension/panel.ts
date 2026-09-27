import * as os from "node:os";

import type { AppState, CodeTarget, ExportInput, HttpRequest, ServerMessage } from "@api-client/ui";
import * as vscode from "vscode";

import { type WebviewEvent, type WebviewResponse, isWebviewRequest } from "./protocol";
import type { Sidecar } from "./sidecar";

/**
 * The main UI: one editor-tab webview per window.
 *
 * The webview has no network access of its own. Every request is an RPC to the
 * extension host, which forwards it to the sidecar.
 */
export class ApiClientPanel {
  private static current: ApiClientPanel | undefined;

  private readonly disposables: vscode.Disposable[] = [];

  static show(context: vscode.ExtensionContext, sidecar: Sidecar): ApiClientPanel {
    if (ApiClientPanel.current) {
      ApiClientPanel.current.panel.reveal(vscode.ViewColumn.Active);
      return ApiClientPanel.current;
    }

    const panel = vscode.window.createWebviewPanel(
      "apiClient.panel",
      "API Client",
      vscode.ViewColumn.Active,
      {
        enableScripts: true,
        retainContextWhenHidden: true,
        localResourceRoots: [vscode.Uri.joinPath(context.extensionUri, "media")],
      },
    );

    ApiClientPanel.current = new ApiClientPanel(panel, context, sidecar);
    return ApiClientPanel.current;
  }

  private constructor(
    private readonly panel: vscode.WebviewPanel,
    private readonly context: vscode.ExtensionContext,
    private readonly sidecar: Sidecar,
  ) {
    panel.iconPath = vscode.Uri.joinPath(context.extensionUri, "media", "icon.svg");
    panel.webview.html = this.render();

    this.disposables.push(
      panel.webview.onDidReceiveMessage((message: unknown) => {
        void this.onMessage(message);
      }),
      panel.onDidDispose(() => this.dispose()),
    );
  }

  /** Nudges the webview, e.g. when the palette asks for a new request. */
  post(event: WebviewEvent): void {
    void this.panel.webview.postMessage(event);
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

  /**
   * Each case is one sidecar message. Nothing is decided here; the engine owns
   * the behaviour so the desktop app and this agree by construction.
   */
  private async dispatch(method: string, params: unknown): Promise<unknown> {
    switch (method) {
      case "sendRequest": {
        const { request } = params as { request: HttpRequest };
        const reply = await this.sidecar.call(
          { type: "sendRequest", request, variables: {} },
          request.id,
        );
        if (reply.type === "requestComplete") {
          return { response: reply.response, error: null, historyEntry: reply.historyEntry };
        }
        if (reply.type === "requestError") {
          return { response: null, error: reply.error, historyEntry: reply.historyEntry };
        }
        throw new Error(describe(reply));
      }

      case "generateCode": {
        const { request, target } = params as { request: HttpRequest; target: CodeTarget };
        const reply = await this.sidecar.call({
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
        this.sidecar.notify({ type: "cancelRequest", requestId });
        return null;
      }

      case "loadState": {
        const reply = await this.sidecar.call({ type: "loadState" });
        if (reply.type !== "stateLoaded") throw new Error(describe(reply));
        return {
          state: reply.state,
          recovery: reply.recovery,
          storagePath: reply.storagePath,
          version: reply.version,
        };
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

      case "exportSavedRequests": {
        const input = params as ExportInput;
        const folder = vscode.workspace.workspaceFolders?.[0]?.uri ?? vscode.Uri.file(os.homedir());
        const target = await vscode.window.showSaveDialog({
          title: "Export saved requests",
          defaultUri: vscode.Uri.joinPath(folder, input.fileName),
          filters: input.format === "http" ? { "HTTP requests": ["http"] } : { JSON: ["json"] },
        });
        if (!target) return null;

        const reply = await this.sidecar.call({
          type: "exportSavedRequests",
          savedRequestIds: [...input.savedRequestIds],
          format: input.format,
          includeSecrets: input.includeSecrets,
        });
        if (reply.type !== "savedRequestsExported") throw new Error(describe(reply));
        await vscode.workspace.fs.writeFile(target, new TextEncoder().encode(reply.contents));
        return target.fsPath;
      }

      case "saveState": {
        const { state } = params as { state: AppState };
        return this.saved(await this.sidecar.call({ type: "saveState", state }));
      }

      case "saveRequest": {
        const input = params as {
          savedRequestId: string | null;
          name: string;
          request: HttpRequest;
        };
        return this.saved(
          await this.sidecar.call({
            type: "saveRequest",
            savedRequestId: input.savedRequestId,
            name: input.name,
            request: input.request,
          }),
        );
      }

      case "deleteSavedRequest": {
        const { savedRequestId } = params as { savedRequestId: string };
        return this.saved(await this.sidecar.call({ type: "deleteSavedRequest", savedRequestId }));
      }

      case "clearHistory":
        return this.saved(await this.sidecar.call({ type: "clearHistory" }));

      default:
        throw new Error(`Unknown request: ${method}`);
    }
  }

  private saved(reply: ServerMessage): unknown {
    if (reply.type !== "stateSaved") throw new Error(describe(reply));
    return reply.state;
  }

  private reply(response: WebviewResponse): void {
    void this.panel.webview.postMessage(response);
  }

  /**
   * Strict CSP: scripts only from this bundle and only with the nonce, no
   * remote anything. CodeMirror injects its own stylesheet at runtime, which is
   * why inline styles are allowed and inline scripts are not.
   */
  private render(): string {
    const { webview } = this.panel;
    const nonce = makeNonce();
    const asset = (file: string): string =>
      webview
        .asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, "media", file))
        .toString();

    const csp = [
      `default-src 'none'`,
      `img-src ${webview.cspSource} data:`,
      `font-src ${webview.cspSource}`,
      `style-src ${webview.cspSource} 'unsafe-inline'`,
      `script-src 'nonce-${nonce}'`,
    ].join("; ");

    return `<!doctype html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <meta http-equiv="Content-Security-Policy" content="${csp}" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <link rel="stylesheet" href="${asset("webview.css")}" />
    <title>API Client</title>
  </head>
  <body>
    <div id="root"></div>
    <script type="module" nonce="${nonce}" src="${asset("webview.js")}"></script>
  </body>
</html>`;
  }

  private dispose(): void {
    ApiClientPanel.current = undefined;
    for (const disposable of this.disposables) disposable.dispose();
    this.panel.dispose();
  }
}

function describe(reply: ServerMessage): string {
  if ("message" in reply && typeof reply.message === "string") return reply.message;
  return `Unexpected reply from the request engine: ${reply.type}`;
}

function makeNonce(): string {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  let nonce = "";
  for (let index = 0; index < 32; index += 1) {
    nonce += alphabet[Math.floor(Math.random() * alphabet.length)];
  }
  return nonce;
}
